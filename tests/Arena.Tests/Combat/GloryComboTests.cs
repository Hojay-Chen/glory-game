using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Xunit;

namespace Arena.Tests.Combat;

/// 《荣耀》核心操作机制的**确定性验证**——此前这套机制代码里写了、实战里却几乎不触发，
/// 而且**一条测试都没有**（BasicStep / cancel_min_tier / jump_cancel / 浮空追打 全无覆盖）。
/// 本组用例把它们钉在确定性指令流上：只要机制退化成"写了但触发不了"，CI 立刻红。
///
/// 用全身高带宽判定为背景：普攻连段 / 技能取消后摇 / 跳跃取消 / 浮空连招（二段浮空）。
public class GloryCombo : IDisposable
{
    private readonly string _root = CombatGoldenSlice.FindRepoRoot();
    private SimWorld? _world;

    private static Command Skill(int id, string skillId) =>
        new(id, CmdKind.Skill, CombatGoldenSlice.Catalog.IdMap[skillId], 0, 0, 0);
    private static Command Basic(int id) => new(id, CmdKind.Basic, 0, 0, 0, 0);
    private static Command Jump(int id) => new(id, CmdKind.Jump, 0, 0, 0, 0);

    private static void Run(SimWorld w, int from, int to, Func<int, Command[]>? perTick = null)
    {
        for (int t = from; t <= to; t++)
            w.Step(t, perTick?.Invoke(t) ?? Array.Empty<Command>());
    }

    /// 标准对局: BMG(战斗法师) @origin 朝 +Z；victim @z=2
    private SimWorld CreateWorld(string victimClass = "BLA")
    {
        var cat = CombatGoldenSlice.Catalog;
        var world = new SimWorld(0x5EED_2468L, cat.DataVersionHash);
        foreach (var s in cat.Skills) world.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
                     ArenaDefParser.Parse(Path.Combine(_root, "docs/balance-sheet/arena.csv"))))
            world.AddTerrain(t);
        world.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        world.AddFighter(1, victimClass, Fixed.FromInt(0), Fixed.FromInt(2), team: 1);
        world.SealWorld();
        _world = world;
        return world;
    }

    public void Dispose() => _world = null;

    // ================= 1. 普攻连段（荣耀最基础的节奏） =================

    [Fact]
    public void GC01_普攻连按_沿链推进出多段()
    {
        var w = CreateWorld();
        Run(w, 1, 170, _ => new[] { Basic(0) });   // 玩家式连按普攻

        var used = w.Events.All
            .Where(e => e.Kind == EventKind.Hit && e.AttackerId == 0)
            .Select(e => e.SkillId).Distinct().ToList();

        Assert.True(used.Count >= 3,
            $"普攻应沿 BAS 链连出多段（实测只有 {used.Count} 段: {string.Join(",", used)}）");
    }

    // ================= 2. 技能取消普攻后摇（荣耀的命门） =================

    [Fact]
    public void GC02_普攻后摇中按技能_产生取消()
    {
        var w = CreateWorld();
        w.Step(1, new[] { Basic(0) });         // BAS_001: 前摇9 + 判定3 + 后摇12
        Run(w, 2, 12);                          // 推进到后摇段
        w.Step(13, new[] { Skill(0, "BMG_T1_002") });   // 后摇中接技能 → 取消
        Run(w, 14, 45);

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Cancelled);
        Assert.Contains(w.Events.All, e =>
            e.Kind == EventKind.SkillCast && e.SkillId == CombatGoldenSlice.Catalog.IdMap["BMG_T1_002"]);
    }

    // ================= 3. 跳跃取消（jump_cancel=1 的技） =================

    [Fact]
    public void GC03_可跳跃取消的技能_后摇起跳产生取消()
    {
        var w = CreateWorld();
        // BMG_T2_001 圆舞棍: jump_cancel=1（skills.csv）
        w.Step(1, new[] { Skill(0, "BMG_T2_001") });
        Run(w, 2, 26);                          // 推进到后摇
        w.Step(27, new[] { Jump(0) });
        Run(w, 28, 60);

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Cancelled);
        Assert.True(w.Fighters[0].PosY.Raw > 0 || w.Fighters[0].State == FighterState.Launch,
            "跳跃取消后攻击者应当离地");
    }

    // ================= 4. 浮空连招（荣耀的高光） =================

    [Fact]
    public void GC04_天击起手_空中追打产生二段浮空()
    {
        var w = CreateWorld();
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });    // 天击 launch_v=9.0
        Run(w, 2, 33);

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Launched && e.VictimId == 1);
        Assert.Equal(FighterState.Launch, w.Fighters[1].State);   // 33t 时应仍在空中（峰值 ~t35）

        // 空中追打：前摇结束后立刻接一发，趁对方还在空中再补一刀。
        // 注：这里**追打成功是硬要求**——一旦台面顶被误判成地面（见 AddFighter 的出生点高度），
        // 天击刚起飞就"落地"，这条断言会立刻红。
        w.Step(33, new[] { Skill(0, "BMG_T2_001") });
        Run(w, 34, 110);

        var airHit = w.Events.All.FirstOrDefault(e =>
            e.Kind == EventKind.Hit && e.VictimId == 1 && e.AirMod);
        Assert.True(airHit.Tick > 0, "浮空后必须能追打命中（AirMod 空中修正命中）");

        // ⚠️ 已知缺口（见 docs/OPEN-ISSUES.md EN-10）：各职业**只有 1 个浮空技且 CD 5~10s**，
        //    因此"二段浮空（Relaunched）"在 1v1 实战里几乎不可能触发——是数据缺口，不是本用例该掩盖的。
    }

    [Fact]
    public void GC05_空中命中_受击者离地时有Hit()
    {
        var w = CreateWorld();
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });
        Run(w, 2, 33);
        int airHitsBefore = w.Events.All.Count(e => e.Kind == EventKind.Hit && e.VictimId == 1);

        w.Step(34, new[] { Skill(0, "BMG_T2_001") });   // 圆舞棍（另一段，非浮空）
        Run(w, 35, 95);

        Assert.True(w.Events.All.Count(e => e.Kind == EventKind.Hit && e.VictimId == 1) > airHitsBefore,
            "浮空中应当能继续追打命中");
    }
}
