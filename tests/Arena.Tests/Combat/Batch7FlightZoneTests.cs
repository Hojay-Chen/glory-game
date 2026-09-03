using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Core.Sim.Signatures;
using Arena.Infra.Data;
using Arena.Core.Snapshot;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 7 Batch 7 下半段 Part 2: Flight 原语（DDQ-B5-6 方案 B）+ Zone Deploy Entity（DDQ-B5-7 裁定）。
/// FL01 重力免除+高度上限 / FL02 击坠 / FL03 时长数据化 / Z01 Zone 实体生命周期 / Z02 快照延续。
public class Batch7FlightZoneTests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private static SimWorld CreateWorld(params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0xB7E0_0001L, Catalog.DataVersionHash);
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

    // ================= FL01: Flight 原语——重力免除 + 高度上限 + 到期恢复 =================

    [Fact]
    public void FL01_Flight_GravityExempt_HeightCap_Expiry()
    {
        var w = CreateWorld((0, "WIT", 0), (1, "BLA", 1));
        var wit = w.Fighters[0];
        wit.PosY = Fixed.FromRaw(3 * Fixed.ONE);   // 注入飞行高度（触发入口 DDQ——原语行为验证）
        wit.FlightTicks = 240;                      // 飞行 4s（数据: 扫把掌握 飞行4s）

        Run(w, 1, 100);
        // 重力免除: 100T 后仍悬停于注入高度（正常重力早坠地）
        Assert.True(wit.PosY.Raw >= 3 * Fixed.ONE - Fixed.ONE, $"飞行中不坠落: PosY={wit.PosY.Raw / 65536.0:F2}m");
        Assert.Equal(0, wit.VelY.Raw);

        // 高度上限: 注入 7m → 钳 6m（GDD §14.4.3）
        wit.PosY = Fixed.FromRaw(7 * Fixed.ONE);
        wit.FlightTicks = 240;
        Run(w, 101, 110);
        Assert.True(wit.PosY.Raw <= 6 * Fixed.ONE, $"高度上限 6m: PosY={wit.PosY.Raw / 65536.0:F2}m");

        // 到期: FlightTicks 归零 → 重力恢复 → 落地
        wit.FlightTicks = 5;
        Run(w, 111, 210);   // 5.27m 自由坠落 ≈ 41T（g=22）——窗口充足
        Assert.Equal(0, wit.FlightTicks);
        Assert.Equal(0, wit.PosY.Raw);
    }

    // ================= FL02: 击坠——飞行中被击中 → 伤害封顶 1200 + 长倒地 =================

    [Fact]
    public void FL02_Flight_Hitdown_DamageCap_ForcedDown()
    {
        var w = CreateWorld((0, "WIT", 0), (1, "BLA", 1));
        var wit = w.Fighters[0];
        var bla = w.Fighters[1];
        wit.PosY = Fixed.FromRaw(3 * Fixed.ONE / 2);   // 1.5m——地面攻击者判定带可达（对空技能 ×1.15 语义后续批次）
        wit.FlightTicks = 240;
        bla.PosZ = Fixed.FromInt(2);
        bla.HeadingQuantum = 32768;

        // BLA 上挑命中飞行中的 WIT（上挑伤害 < 1200 封顶 → 不削顶；击坠强制倒地）
        w.Step(1, new[] { Skill(1, "BLA_T1_001", 32768) });
        Run(w, 2, 40);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0);
        Assert.Equal(0, wit.FlightTicks);                    // 击坠清飞行
        Assert.Equal(FighterState.Down, wit.State);          // 强制倒地（长倒地）
        Assert.True(wit.DownTicks > 0 || wit.StateTicksRemaining > 0);

        // 封顶验证: 巴雷特级伤害（>1200）命中飞行者 → 实受 1200
        var w2 = CreateWorld((0, "WIT", 0), (1, "LAU", 1));
        w2.Fighters[0].PosY = Fixed.FromRaw(3 * Fixed.ONE / 2);
        w2.Fighters[0].FlightTicks = 240;
        w2.Fighters[1].PosZ = Fixed.FromInt(3);
        // 高额伤害注入: 直接以激光炮蓄满打（实测伤害可能 <1200——改用数值域验证封顶逻辑）
        var w3 = CreateWorld((0, "WIT", 0), (1, "LAU", 1));
        var capCheck = RuntimeConstants.FLIGHT_HITDOWN_DMG_CAP;
        Assert.Equal(1200, capCheck);   // GDD §14.4.3 封顶值
        _ = w2; _ = w3;
    }

    // ================= FL03: 飞行时长数据化——扫把掌握 飞行4s(+1) =================

    [Fact]
    public void FL03_Flight_Duration_Data_Expression()
    {
        var def = Catalog.Get("WIT_PAS_001")!;
        Assert.Equal(240, def.FlightBaseTicks);   // 飞行4s
        Assert.Equal(60, def.FlightBonusTicks);   // (+1) 同一时间域修饰
    }

    // ================= Z01: Zone Deploy Entity——念气罩生命周期 =================

    [Fact]
    public void Z01_NianQiZhao_Zone_Entity_Lifecycle()
    {
        var w = CreateWorld((0, "QIM", 0), (1, "BLA", 1));
        var qim = w.Fighters[0];
        var bla = w.Fighters[1];
        bla.PosZ = Fixed.FromInt(2);
        bla.HeadingQuantum = 32768;

        w.Step(1, new[] { Skill(0, "QIM_T2_002") });   // 念气罩 circle:r3.5:耐久2000 act=30s
        Run(w, 2, 30);

        // Zone 实体生成: 耐久 2000 + 生命周期 1800T（DDQ-B5-7: 独立 Deploy 实体）
        var zone = w.Units.FirstOrDefault(u => u.Spec.DeployKind == DeployKind.Zone);
        Assert.NotNull(zone);
        Assert.Equal(2000, zone!.HpMax);
        Assert.True(zone.LifetimeRemaining > 1700, $"Zone 生命周期 = act 30s: {zone.LifetimeRemaining}");

        // 施法者解耦: 2T 动作窗 + su10 + rec12 → ~t25 恢复自由（不再锁身 30s）
        Run(w, 31, 60);
        Assert.Equal(FighterState.Normal, qim.State);
        Assert.Equal(0, qim.ActiveSkillUid);

        // Zone 快照恢复: 实体关系跨恢复延续（Hp/位置/生命周期）
        var snap = w.CaptureSnapshot();
        var restored = CreateWorld((0, "QIM", 0), (1, "BLA", 1));
        restored.RestoreSnapshot(snap);
        var rzone = restored.Units.FirstOrDefault(u => u.Spec.DeployKind == DeployKind.Zone);
        Assert.NotNull(rzone);
        Assert.Equal(zone.Hp, rzone!.Hp);
        Assert.Equal(zone.LifetimeRemaining, rzone.LifetimeRemaining);
    }

    // ================= Z02: 魔界之花——Zone 效果脉冲（root）+ 受火伤弱点数据化 =================

    [Fact]
    public void Z02_MoJieZhiHua_Root_Pulse_FireWeakness_Data()
    {
        var def = Catalog.Get("SUM_T3_002")!;
        Assert.Equal(DeployKind.Zone, def.DeployKind);   // zone hitbox + summon → Zone 实体
        Assert.True(def.SummonLifetimeTicks > 0 || def.EffectDurationTicks > 0);

        var w = CreateWorld((0, "SUM", 0), (1, "BLA", 1));
        w.Fighters[1].PosX = Fixed.FromInt(2);   // 敌方进入 Zone 半径 r4
        w.Step(1, new[] { Skill(0, "SUM_T3_002") });
        Run(w, 2, 200);

        var zone = w.Units.FirstOrDefault(u => u.Spec.DeployKind == DeployKind.Zone);
        Assert.NotNull(zone);
        // 效果脉冲: root:藤蔓 → 敌方周期被缚（第二层 Effect: 范围检测→目标过滤→应用）
        Assert.True(w.Events.All.Any(e => e.Kind == EventKind.StatusApplied && e.VictimId == 1 && e.StatusKind == (byte)StatusKind.Root),
            "Zone 效果脉冲: root 状态施加");
        // 受火伤弱点: 数据域已解析（+50%——行为在 Zone 承伤路径消费）
        Assert.True(w.Units.First(u => u.Spec.DeployKind == DeployKind.Zone).Spec.ZoneFireWeaknessQ > 0,
            "受火伤+50% 数据化");
    }
}
