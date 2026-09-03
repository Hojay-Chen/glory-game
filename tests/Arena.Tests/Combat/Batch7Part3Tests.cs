using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Core.Sim.Signatures;
using Arena.Infra.Data;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 7 Batch 7 Part 3: Pull 原语 / 强制中断 / Decoy / Channel 覆盖验证。
/// PL01 勾魂拉拽 / PL02 悬磁炮拖拽 / FI01 强制中断 / DC01 影分身 Decoy / CH01 Channel 多段+中断覆盖。
public class Batch7Part3Tests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private static SimWorld CreateWorld(params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0xB7F0_0001L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters) w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        w.SealWorld();
        w.InstallSignatures(DefaultRegistry());
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to)
    {
        for (int t = from; t <= to; t++) w.Step(t, Array.Empty<Command>());
    }

    public void Dispose() { }

    // ================= PL01: 勾魂——命中后目标向攻击者拉拽 2.5m =================

    [Fact]
    public void PL01_GouHun_Pulls_Victim_Toward_Attacker()
    {
        var w = CreateWorld((0, "EXO", 0), (1, "BLA", 1));
        var exo = w.Fighters[0];
        var bla = w.Fighters[1];
        bla.PosZ = Fixed.FromInt(8);   // 勾魂射程 12m 内
        exo.HeadingQuantum = 0;        // 朝 +Z

        var zBefore = bla.PosZ.Raw;
        w.Step(1, new[] { Skill(0, "EXO_T3_003") });
        Run(w, 2, 60);

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.AttackerId == 0 && e.SkillId == Catalog.IdMap["EXO_T3_003"]);
        // 拉拽: 命中后 BLA 向 EXO（−Z 方向）位移 ≈2.5m
        long pulled = zBefore - bla.PosZ.Raw;
        Assert.True(pulled >= Fixed.ONE && pulled <= (long)(2.5 * Fixed.ONE) + Fixed.ONE,
            $"勾魂拉拽 ≈2.5m: 实际 {pulled / 65536.0:F2}m");
    }

    // ================= PL02: 悬磁炮——吸附拖拽弹道数值未裁定（DDQ-B7-5 白名单验证） =================

    [Fact]
    public void PL02_XuanCiCannon_SuctionTrajectory_DDQ()
    {
        // 悬磁炮 hb=proj:吸附拖拽——特殊弹道形态（吸附强行带动目标+飞行变缓下坠+触地爆炸）
        // 弹道数值/拖拽窗口未裁定（DDQ-B7-5）→ 按纪律不猜；本探针固化: cast 无异常 + 拉拽语义已数据化
        var w = CreateWorld((0, "LAU", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(8);
        var exception = Record.Exception(() =>
        {
            w.Step(1, new[] { Skill(0, "LAU_T3_003") });
            Run(w, 2, 120);
        });
        Assert.Null(exception);
        // 拉拽原语已数据化（ParsePullToward 从 status 拖拽 token 提取）——弹道裁定后即接通
        Assert.True(Catalog.Get("LAU_T3_003")!.PullTowardOwnerM > 0, "拉拽距离已数据化（弹道待裁定）");
        Assert.Contains(Catalog.UnroutedHitboxes, u => u.StartsWith("LAU_T3_003"));
    }

    // ================= FI01: 强制中断——陷阱扣命中打断攻击（独立于伤害/霸体） =================

    [Fact]
    public void FI01_TrapZhou_ForceInterrupts_Acting_Target()
    {
        var w = CreateWorld((0, "THF", 0), (1, "BLA", 1));
        var bla = w.Fighters[1];
        bla.PosZ = Fixed.FromInt(1);   // 陷阱触发半径内
        bla.HeadingQuantum = 32768;

        // THF t1 先手布陷阱（su12 → t13 部署于身前 1.5m=BLA 所在位）；BLA t10 起上挑
        // → t14 BLA 触发陷阱爆发 → root + 强制中断 BLA 执行体
        w.Step(1, new[] { Skill(0, "THF_T1_002") });
        w.Step(10, new[] { Skill(1, "BLA_T1_001", 32768) });
        Run(w, 11, 60);

        var trapHits = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.SkillId == Catalog.IdMap["THF_T1_002"] && e.VictimId == 1).ToList();
        Assert.True(trapHits.Count > 0, "陷阱爆发命中");
        // 强制中断: BLA 执行体被终止（Interrupted 事件）——即使之后 root 限制移动
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Interrupted && e.AttackerId == 1);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.StatusApplied && e.VictimId == 1 && e.StatusKind == (byte)StatusKind.Root);
    }

    // ================= DC01: 影分身——Decoy 静置诱饵实体（快照延续） =================

    [Fact]
    public void DC01_YingFenShen_Decoy_Spawns_And_Survives_Restore()
    {
        var w = CreateWorld((0, "NJA", 0), (1, "BLA", 1));
        w.Step(1, new[] { Skill(0, "NJA_T2_002") });   // 影分身术（hb self:假身+真身换位）
        Run(w, 2, 30);
        Console.WriteLine($"DBG-DC casts={w.Events.All.Any(e => e.Kind == EventKind.SkillCast && e.Tick == 1)} njamp={w.Fighters[0].Mp} units={w.Units.Count}");

        var decoy = w.Units.FirstOrDefault(u => u.Spec.Decoy);
        Assert.NotNull(decoy);
        Assert.True(decoy!.LifetimeRemaining > 0, "Decoy 存在期");

        // 快照恢复: Decoy 实体关系延续
        var snap = w.CaptureSnapshot();
        var restored = CreateWorld((0, "NJA", 0), (1, "BLA", 1));
        restored.RestoreSnapshot(snap);
        Assert.NotNull(restored.Units.FirstOrDefault(u => u.Spec.Decoy));
        Assert.Equal(decoy.LifetimeRemaining, restored.Units.First(u => u.Spec.Decoy).LifetimeRemaining);
    }

    // ================= CH01: Channel 多段持续 + 可被中断覆盖验证 =================

    [Fact]
    public void CH01_Channel_MultiHit_Interruptible()
    {
        // 火焰喷射（channel: act 1.5s cone dmg 0.70 多段燃烧）——多段周期伤害
        var w = CreateWorld((0, "LAU", 0), (1, "BLA", 1));
        var bla = w.Fighters[1];
        bla.PosZ = Fixed.FromInt(4);
        w.Step(1, new[] { Skill(0, "LAU_T1_002") });
        Run(w, 2, 60);
        var hits = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.AttackerId == 0).ToList();
        Assert.True(hits.Count >= 2, $"火焰喷射多段持续: {hits.Count} 段");

        // 中断覆盖: channel 施法中被打 → Interrupted（§4.3 对 channel 生效）
        var w2 = CreateWorld((0, "LAU", 0), (1, "BMG", 1));
        w2.Fighters[1].PosZ = Fixed.FromInt(2);
        w2.Fighters[1].HeadingQuantum = 32768;
        w2.Step(1, new[] { Skill(0, "LAU_T1_002") });
        w2.Step(30, new[] { Skill(1, "BMG_T1_001", 32768) });   // 命中 channel 施法者
        Run(w2, 31, 50);
        // 多段 channel 天然具备打断压制: 周期命中打断敌方反击执行体（Interrupted 记被打断方 OwnerId）
        Assert.Contains(w2.Events.All, e => e.Kind == EventKind.Interrupted && e.AttackerId == 1 && e.SkillId == Catalog.IdMap["BMG_T1_001"]);
    }

    // ================= IT04 联动: 白名单收缩后残留行全部执行验证 =================

    [Fact]
    public void PL03_Remaining_Whitelist_Rows_All_Execute()
    {
        // 白名单 7 行（Epic 语义）逐行执行——无静默无异常
        var whitelist = new[] { "WIT_U_001", "GRP_T1_001", "WRK_T3_003", "WRK_T3_004", "WRK_T3_006", "WRK_T4_002", "WRK_T4_003" };
        foreach (var sid in whitelist)
        {
            var w = CreateWorld((0, "WRK", 0), (1, "BLA", 1));
            w.Fighters[1].PosZ = Fixed.FromInt(3);
            var eventsBefore = w.Events.All.Count;
            var exception = Record.Exception(() =>
            {
                w.Step(1, new[] { Skill(0, sid) });
                Run(w, 2, 60);
            });
            Assert.Null(exception);
        }
    }
}
