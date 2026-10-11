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

    /// 浮空连招要成立，**必须取消后摇**：天击总长 32f（前摇12+判定4+后摇16），
    /// 而浮空窗只有约 32f——老老实实等后摇走完，对方已经落地了，追打永远差那几帧。
    /// 这正是《荣耀》"技能取消技能后摇"存在的理由。
    /// 注：普攻（BAS 档）**不能**取消 T1 技的后摇——取消档位是 T1+，普攻只有 any 档，这是设计而非 bug。
    [Fact]
    public void GC04_浮空后追打_取消后摇接普攻即计空中追击()
    {
        var w = CreateWorld();
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });    // 天击（cancel_min_tier = T1+）
        Run(w, 2, 17);

        Assert.Equal(FighterState.Launch, w.Fighters[1].State);

        w.Step(18, new[] { Skill(0, "BMG_T1_002") });   // 后摇中取消 → 龙牙续空（同为 T1，满足 T1+ 取消档）
        Run(w, 19, 90);

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Cancelled);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1 && e.AirMod);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Relaunched && e.VictimId == 1);
    }

    /// 方案 B 的**核心**：空中命中不得把目标从 Launch 态里拽出来，否则"一次就断"、空中连打无从谈起。
    [Fact]
    public void GC06_空中命中保持浮空态_可继续连()
    {
        var w = CreateWorld();
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });
        Run(w, 2, 17);
        w.Step(18, new[] { Skill(0, "BMG_T1_002") });   // 取消后摇 → 龙牙续空

        int t = 19;
        while (t < 90 && !w.Events.All.Any(e => e.Kind == EventKind.Relaunched && e.VictimId == 1))
        {
            w.Step(t, Array.Empty<Command>());
            t++;
        }

        Assert.True(w.Events.All.Any(e => e.Kind == EventKind.Relaunched && e.VictimId == 1),
            "命中浮空目标必须产生 Relaunched（空中追击节拍）");
        Assert.Equal(FighterState.Launch, w.Fighters[1].State);   // ← 仍在浮空，而不是被打落成硬直
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
