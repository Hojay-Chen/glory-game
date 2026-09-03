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

/// Phase 7 Batch 3 探针: 第一批真实职业签名（BMG 炫纹 / BER 血气唤醒 / ASN 暗杀艺术 / QIM 护体真气）。
/// 验证 ISignature 三种模式族: 事件→资源 / OnTick 条件 buff / 伤害修正乘区。
/// 退出标准: 签名在真实战斗组合中产生正确行为 + 确定性/快照不退化。
public class SignatureBatch1Tests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;

    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private SimWorld CreateWorld(SignatureRegistry? reg, params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0x8EED_1234L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters)
        {
            var cap = cls switch
            {
                "BMG" => (SimWorld.ResourceSlotKind.Orb, 7L),
                _ => (SimWorld.ResourceSlotKind.Orb, 0L),
            };
            w.SetClassResource(cls, cap.Item1, cap.Item2);
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        }
        w.SealWorld();
        if (reg is not null) w.InstallSignatures(reg);
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to)
    {
        for (int t = from; t <= to; t++) w.Step(t, Array.Empty<Command>());
    }

    public void Dispose() { }

    // ================= SG01: BMG 炫纹（事件→资源模式） =================

    [Fact]
    public void SG01_BMG_OrbGain_On_Hit_With_OrbSkill()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "BMG", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        // 天击（OrbSkill）命中 → 炫纹 +1
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });
        Run(w, 2, 30);
        Assert.True(w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] >= 1,
            "天击命中后应获得炫纹");
        // Review 项#4: 龙牙（炫纹:无属性）数据驱动也触发炫纹——原硬编码 HashSet 遗漏了它
        long after1 = w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb];
        w.Fighters[1].State = FighterState.Normal;
        w.Fighters[1].Hp = 10000;
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Fighters[0].Cooldowns.Clear();
        w.Fighters[0].Mp = 1000;
        w.Step(50, new[] { Skill(0, "BMG_T1_002") });   // 龙牙（炫纹:无属性）
        Run(w, 51, 80);
        Assert.True(w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] > after1,
            "龙牙（炫纹:无属性）也应获得炫纹（数据驱动）");
        // 非 OrbSkill（豪龙破军 special 无炫纹:前缀）→ 炫纹不增
        long before2 = w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb];
        w.Fighters[1].State = FighterState.Normal;
        w.Fighters[1].Hp = 10000;
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Fighters[0].Cooldowns.Clear();
        w.Fighters[0].Mp = 1000;
        w.Step(100, new[] { Skill(0, "BMG_T4_001") });
        Run(w, 101, 200);
        Assert.Equal(before2, w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb]);
    }

    [Fact]
    public void SG02_BMG_Orb_Caps_At_Seven()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "BMG", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        // 手动打满 7 炫纹 → 天击命中 → 不超上限
        w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] = 7;
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });
        Run(w, 2, 30);
        Assert.True(w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] <= 7,
            $"炫纹上限 7: actual={w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb]}");
    }

    // ================= SG03: BER 血气唤醒（OnTick 条件 buff 模式） =================

    [Fact]
    public void SG03_BER_HpThreshold_AtkBuff_Tiered()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "BER", 0), (1, "BLA", 1));
        var ber = w.Fighters[0];
        // HP 45% → +5% 档
        ber.Hp = 4500;
        Run(w, 1, 3);
        Assert.True(ber.BuffAtkPctTicks > 0, "HP<50% 应激活 buff");
        long expected5 = DeterministicMath.DivRoundHalfEven(5 * Fixed.ONE, 100);
        Assert.Equal(expected5, ber.BuffAtkPctQ);
        // HP 25% → +10% 档
        ber.Hp = 2500;
        Run(w, 4, 6);
        long expected10 = DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100);
        Assert.Equal(expected10, ber.BuffAtkPctQ);
        // HP 10% → +15% 档
        ber.Hp = 1000;
        Run(w, 7, 9);
        long expected15 = DeterministicMath.DivRoundHalfEven(15 * Fixed.ONE, 100);
        Assert.Equal(expected15, ber.BuffAtkPctQ);
        // HP 回满 → buff 清除
        ber.Hp = 10000;
        Run(w, 10, 15);
        Assert.Equal(0, ber.BuffAtkPctQ);
    }

    [Fact]
    public void SG04_BER_Buff_Affects_Damage_Chain()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "BER", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Fighters[1].HeadingQuantum = 32768;
        var ber = w.Fighters[0];
        ber.Hp = 2500;   // 25% → +10% buff
        Run(w, 1, 3);
        // BER_T1_002? BER 没有 T1_002——用 BER_BAS_001 普攻验证伤害链 buff 消费
        w.Step(5, new[] { new Command(0, CmdKind.Basic, 0, 0, 0, 5) });
        Run(w, 6, 40);
        var hit = w.Events.All.FirstOrDefault(e => e.Kind == EventKind.Hit && e.AttackerId == 0);
        Assert.True(hit.Tick != 0 || hit.DamageRaw > 0, "BER 普攻应命中");
        if (hit.DamageRaw > 0)
        {
            // BuffAtkPct 消费顺序: effAtk = Atk + Atk×10% → dmg = mult × effAtk × 0.6
            long buffedAtk = 1100 + DeterministicMath.MulShift(1100, DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100));
            long expected = DeterministicMath.MulShift(DeterministicMath.MulShift(
                Catalog.Get("BER_BAS_001")!.DamageMultQ, buffedAtk),
                DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800));
            Assert.InRange(hit.DamageRaw, expected - 5, expected + 5);
        }
    }

    // ================= SG05: ASN 暗杀艺术（伤害修正乘区模式） =================

    [Fact]
    public void SG05_ASN_Backstab_Bonus_Total_X144()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "ASN", 0), (1, "BLA", 1));
        // ASN 从背后攻击（victim 面朝 +Z 远离 attacker）——box 1.5m + 体半径
        w.Fighters[1].PosZ = Fixed.FromInt(1);
        w.Step(1, new[] { Skill(0, "ASN_BAS_001") });   // 短剑一段（fan r? ASN_BAS_001）
        Run(w, 2, 30);
        var hit = Assert.Single(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1);
        // 暗杀艺术: 基线背击 ×1.2 × 签名追加 ×1.2 = ×1.44
        long baseDmg = DeterministicMath.MulShift(DeterministicMath.MulShift(
            Catalog.Get("ASN_BAS_001")!.DamageMultQ, 1100),
            DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800));
        long backstab = DeterministicMath.MulShift(baseDmg, Arena.Core.Calc.DeterministicTables.Modifiers.BackstabX120);
        long assassinated = DeterministicMath.MulShift(backstab, DeterministicMath.DivRoundHalfEven(120 * Fixed.ONE, 100));
        Assert.InRange(hit.DamageRaw, assassinated - 3, assassinated + 3);
    }

    // ================= SG06: QIM 护体真气（OnTick DEF 阈值模式） =================

    [Fact]
    public void SG06_QIM_MpThreshold_DefBuff_Domain()
    {
        // Review 项#1: QIM 重设计为 BuffDefPct 域（不直接改写 Def 基准）
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "QIM", 0), (1, "BLA", 1));
        var qim = w.Fighters[0];
        Assert.Equal(800, qim.Def);   // 基准不变
        qim.Mp = 900;   // 90% > 70% → DEF+15%
        Run(w, 1, 5);
        Assert.Equal(DeterministicMath.DivRoundHalfEven(15 * Fixed.ONE, 100), qim.BuffDefPctQ);
        // MP 降到 20% → DEF−10%
        qim.Mp = 200;
        Run(w, 6, 10);
        Assert.Equal(-DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100), qim.BuffDefPctQ);
    }

    // ================= SG07: 全签名确定性 + 快照恢复 =================

    [Fact]
    public void SG07_AllSignatures_ComplexMatch_Deterministic()
    {
        var (h1, s1) = RunSignatureStorm(0x516B7C);
        var (h2, s2) = RunSignatureStorm(0x516B7C);
        Assert.Equal(h1, h2);
        Assert.True(s1.BitwiseEquals(s2));
    }

    private static (string, SnapshotData) RunSignatureStorm(long seed)
    {
        var w = new SimWorld(seed, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        w.SetClassResource("BMG", SimWorld.ResourceSlotKind.Orb, 7);
        w.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w.AddFighter(1, "BER", Fixed.FromInt(0), Fixed.FromInt(4), team: 0);
        w.AddFighter(2, "ASN", Fixed.FromInt(0), Fixed.FromInt(8), team: 1);
        w.AddFighter(3, "QIM", Fixed.FromInt(0), Fixed.FromInt(12), team: 1);
        w.SealWorld();
        var registry = Batch3Shared.DefaultRegistry();
        w.InstallSignatures(registry);
        for (int t = 1; t <= 1000; t++)
        {
            var cmds = new List<Command>();
            if (t == 20) cmds.Add(Skill(0, "BMG_T1_001"));                                  // 天击（炫纹）
            if (t == 60) cmds.Add(Skill(0, "BMG_T1_003"));                                  // 连突（炫纹）
            if (t == 100) cmds.Add(Skill(2, "ASN_BAS_001"));                                // ASN 普攻
            if (t == 140) cmds.Add(Skill(0, "BMG_T1_004"));                                 // 落花掌
            if (t == 180) cmds.Add(new Command(3, CmdKind.Skill, Catalog.IdMap["QIM_T1_001"], 32768, 0, t));
            if (t == 220) cmds.Add(Skill(0, "BMG_T2_001"));                                 // 圆舞棍
            if (t == 260) cmds.Add(new Command(1, CmdKind.Skill, Catalog.IdMap["BMG_T1_001"], 32768, 0, t));
            if (t == 300) cmds.Add(new Command(1, CmdKind.Basic, 0, 0, 0, t));              // BER 普攻
            if (t == 340) cmds.Add(new Command(2, CmdKind.Skill, Catalog.IdMap["BMG_T1_001"], 32768, 0, t));
            if (t == 400) cmds.Add(Skill(0, "BMG_T1_002"));                                 // 龙牙（非 OrbSkill）
            if (t == 460) cmds.Add(new Command(0, CmdKind.Basic, 0, 0, 0, t));              // BMG 普攻
            w.Step(t, cmds.Where(c => c.Kind == CmdKind.Skill || c.Kind == CmdKind.Basic).ToArray());
        }
        return (w.Events.ComputeHash(), w.CaptureSnapshot());
    }
}

/// 共享注册表工厂
public static class Batch3Shared
{
    public static SignatureRegistry DefaultRegistry()
    {
        var reg = new SignatureRegistry();
        reg.Register(new Arena.Core.Sim.Signatures.BmgFightingSpirit());
        reg.Register(new Arena.Core.Sim.Signatures.BerBloodAwakening());
        reg.Register(new Arena.Core.Sim.Signatures.AsnAssassination());
        reg.Register(new Arena.Core.Sim.Signatures.QimBodyQi());
        reg.Register(new Arena.Core.Sim.Signatures.ThfTrapMastery());
        reg.Register(new Arena.Core.Sim.Signatures.SblWaveResonance());
        reg.Register(new Arena.Core.Sim.Signatures.KniKnightSpirit());
        reg.Register(new Arena.Core.Sim.Signatures.RogPayback());
        return reg;
    }
}
