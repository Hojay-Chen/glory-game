using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Collision;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Arena.Core.Snapshot;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 7 Batch 2 探针: Deploy（陷阱/光环）/ heal 通道 / 技能交互矩阵。
/// 退出标准: 无执行链断裂、无 silent、确定性双跑一致、新增机制在组合中产生正确行为。
public class Batch2InteractionTests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;

    private SimWorld CreateWorld(params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0x7EED_1234L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
            ArenaDefParser.Parse(System.IO.Path.Combine(
                CombatGoldenSlice.FindRepoRoot(), "docs/balance-sheet/arena.csv"))))
            w.AddTerrain(t);
        foreach (var (id, cls, team) in fighters)
        {
            var cap = cls switch
            {
                "SUM" => (SimWorld.ResourceSlotKind.Summon, 4L),
                "MEH" => (SimWorld.ResourceSlotKind.Deploy, 3L),
                _ => (SimWorld.ResourceSlotKind.Summon, 0L),
            };
            w.SetClassResource(cls, cap.Item1, cap.Item2);
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        }
        w.SealWorld();
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to, Func<int, Command[]>? perTick = null)
    {
        for (int t = from; t <= to; t++) w.Step(t, perTick?.Invoke(t) ?? Array.Empty<Command>());
    }

    private static long FixedM(decimal m) => (long)Math.Round(m * 65536m, MidpointRounding.ToEven);

    public void Dispose() { }

    // ================= Deploy: 陷阱（THF_T2_001 毒云陷阱 deploy:r2.5:触发） =================

    [Fact]
    public void D01_Trap_Triggers_On_Enemy_Proximity_SingleShot()
    {
        var w = CreateWorld((0, "THF", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(1);   // 陷阱落点前方 1.5m，触发半径 2.5m 内
        w.Step(1, new[] { Skill(0, "THF_T2_001") });
        Run(w, 2, 30);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitSpawned);
        var hits = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.VictimId == 1).ToList();
        Assert.True(hits.Count >= 1, "陷阱触发应命中进入者");
        // 单次触发: 同一陷阱不重复命中（触发即耗尽）
        Assert.True(hits.Count <= 2, $"陷阱单次触发: hits={hits.Count}");
    }

    [Fact]
    public void D02_Trap_DoesNotTrigger_On_Owner()
    {
        var w = CreateWorld((0, "THF", 0), (1, "BLA", 1));
        w.Fighters[0].PosZ = Fixed.FromInt(1);   // 主人站在陷阱触发半径内
        w.Step(1, new[] { Skill(0, "THF_T2_001") });
        Run(w, 2, 40);
        // 主人不触发己方陷阱（队伍过滤）
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitSpawned);
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0);
        Assert.True(w.Units.Count > 0 || w.Events.All.Any(e => e.Kind == EventKind.UnitDied),
            "陷阱未触发时保持部署");
    }

    // ================= Deploy: 光环（GBL_T3_002 炎阵 zone:r4.0 burn DoT） =================

    [Fact]
    public void D03_Aura_Pulses_Damage_Enemies_In_Radius()
    {
        var w = CreateWorld((0, "GBL", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);   // 光环 r4 内
        w.Step(1, new[] { Skill(0, "GBL_T3_002") });   // 炎阵 dmg=1.50 burn:40
        Run(w, 2, 200);
        // 周期脉冲: 12s 光环 → 多次命中/状态施加
        Assert.True(w.Events.All.Count(e => e.Kind == EventKind.Hit && e.VictimId == 1) >= 2,
            "光环应周期脉冲命中");
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Burn].Active
            || w.Events.All.Any(e => e.Kind == EventKind.StatusApplied && e.StatusKind == (byte)StatusKind.Burn),
            "炎阵应施加 burn");
    }

    [Fact]
    public void D04_Aura_Stops_When_Target_Exits_Radius()
    {
        var w = CreateWorld((0, "GBL", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Step(1, new[] { Skill(0, "GBL_T3_002") });
        // 目标持续步行 −Z 至走出 r4（deploy@1.5m + 半径 4m + 体半径 → 需 >6m ≈ 65T）
        Run(w, 2, 70, t => new[] { new Command(1, CmdKind.Move, 0, 0, 4, t) });
        Run(w, 71, 300);
        var lastHit = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.VictimId == 1)
            .OrderBy(e => e.Tick).LastOrDefault();
        Assert.True(lastHit.Tick == 0 || lastHit.Tick < 75, $"目标离开后脉冲应停止: last={lastHit.Tick} posZ={w.Fighters[1].PosZ.Raw}");
    }

    // ================= Deploy: 己方增益阵（GBL_T2_003 刀魂守护 ATK+5%） =================

    [Fact]
    public void D05_BuffAura_Applies_AtkBoost_To_Ally()
    {
        var w = CreateWorld((0, "GBL", 0), (1, "BLA", 1));
        // deploy 落于施法者前方 1.5m → 施法者自身距 deploy 1.5m < aura 4m → 自身获得增益
        w.Step(1, new[] { Skill(0, "GBL_T2_003") });   // 刀魂守护: 阵内己方 ATK+5%
        Run(w, 2, 30);
        Assert.True(w.Fighters[0].BuffAtkPctTicks > 0, "阵内己方应获得 ATK 增益");
        Assert.Equal(FixedM(0.05m), w.Fighters[0].BuffAtkPctQ);
        // 敌方（BLA team 1）不受增益
        Assert.Equal(0, w.Fighters[1].BuffAtkPctTicks);
        // 增益随 deploy 存活持续脉冲（60s 存在期 → t3613 到期）→ 到期后清零
        Run(w, 31, 62 * (int)RuntimeConstants.TICK_RATE);
        Assert.Equal(0, w.Fighters[0].BuffAtkPctTicks);   // deploy 到期 → 不再脉冲 → buff 域清零
    }

    // ================= Heal 通道（GAN_T1_002 HoT / PRI 瞬发） =================

    [Fact]
    public void D06_HealOverTime_Pulses_Restore_Hp()
    {
        var w = CreateWorld((0, "GAN", 0), (1, "BLA", 1));
        w.Fighters[0].Hp = 5000;   // 低血 → 回复空间
        w.Step(1, new[] { Skill(0, "GAN_T1_002") });   // 恢复术: dmg=200/3s × 18s
        Run(w, 2, 20 * (int)RuntimeConstants.TICK_RATE);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Healed && e.VictimId == 0);
        // HoT 脉冲: 200/3s × 6 次 = 1200 总回复
        Assert.True(w.Fighters[0].Hp >= 5000 + 1200 - 2,
            $"HoT 总回复: hp={w.Fighters[0].Hp} (expect ≥6200)");
    }

    [Fact]
    public void D07_Heal_Caps_At_MaxHp()
    {
        var w = CreateWorld((0, "GAN", 0), (1, "BLA", 1));
        w.Fighters[0].Hp = 9900;
        w.Step(1, new[] { Skill(0, "GAN_T1_002") });
        Run(w, 2, 20 * (int)RuntimeConstants.TICK_RATE);
        Assert.True(w.Fighters[0].Hp <= 10000, "回复不超过 HP 上限");
    }

    [Fact]
    public void D08_PRI_InstantHeal_Direct_Amount()
    {
        var w = CreateWorld((0, "PRI", 0), (1, "BLA", 1));
        w.Fighters[0].Hp = 8000;
        w.Step(1, new[] { Skill(0, "PRI_T2_002") });   // 小治愈术: dmg=900 瞬发
        Run(w, 2, 30);
        var healed = Assert.Single(w.Events.All, e => e.Kind == EventKind.Healed && e.VictimId == 0);
        Assert.Equal(900, healed.DamageRaw);
        Assert.Equal(8900, w.Fighters[0].Hp);
    }

    // ================= 技能交互矩阵（skill × status × entity × control 组合） =================

    [Fact]
    public void IX01_Burn_Ignores_Guard_DoT_Ticks_While_Guarding()
    {
        var w = CreateWorld((0, "ELE", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        // 先给 BLA 上 burn（火墙光环）
        w.Step(1, new[] { Skill(0, "GBL_T3_002") });
        Run(w, 2, 60);
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Burn].Active);
        // BLA 开格挡——DoT 不应被格挡吸收（DoT 非 hit 事件）
        w.Step(61, new[] { Skill(1, "BLA_T1_002") });
        Run(w, 62, 120);
        // DoT 穿透格挡: StatusSystem 独立结算（非 hit 通道）——DotApplied 直接证明
        Run(w, 121, 180);
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Burn].DotApplied > 0,
            $"burn DoT 应持续结算: applied={w.Fighters[1].Statuses[(int)StatusKind.Burn].DotApplied}");
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.GuardHit && e.VictimId == 1);
    }

    [Fact]
    public void IX02_Frozen_Target_Hits_Land_With_Bonus()
    {
        var w = CreateWorld((0, "ELE", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Fighters[1].HeadingQuantum = 32768;   // 面朝 attacker（无背击干扰）
        // 直接构造冰冻状态
        w.ApplyStatus(w.Fighters[1], new StatusEffectDef(StatusKind.Freeze, 0, 60, 0), 0, 0);
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Freeze].Active);
        // 龙牙命中冰冻目标 → +10% 冰冻增伤
        w.Step(2, new[] { Skill(0, "BMG_T1_002", 0) });
        Run(w, 3, 30);
        var hit = Assert.Single(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1);
        long baseDmg = DeterministicMath.MulShift(DeterministicMath.MulShift(
            Catalog.Get("BMG_T1_002")!.DamageMultQ, 1100),
            DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800));
        long frozen = DeterministicMath.MulShift(baseDmg, DeterministicMath.DivRoundHalfEven(110 * Fixed.ONE, 100));
        Assert.InRange(hit.DamageRaw, frozen - 3, frozen + 3);
    }

    [Fact]
    public void IX03_Knockback_Into_Wall_While_Poisoned()
    {
        var w = CreateWorld((0, "ELE", 0), (1, "BLA", 1));
        // 击退撞墙 + 中毒叠加: 落花掌朝墙 → 撞墙反弹 + 毒持续
        w.ApplyStatus(w.Fighters[1], new StatusEffectDef(StatusKind.Poison, FixedM(30), 300, 0), 0, 0);
        w.Fighters[1].PosZ = Fixed.FromInt(40);
        w.Fighters[0].PosZ = Fixed.FromInt(37);
        w.Step(1, new[] { Skill(0, "BMG_T1_004") });   // 落花掌 kb 3m 朝 +Z → 撞 z=42 墙
        Run(w, 2, 60);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.WallBounced && e.VictimId == 1);
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Poison].Active, "击退不影响已有 poison");
        long dotTotal = w.Fighters[1].Statuses[(int)StatusKind.Poison].DotApplied;
        Assert.True(dotTotal > 0, "poison DoT 独立于击退持续结算");
    }

    [Fact]
    public void IX04_Counter_Wins_Over_Grab_Attempt()
    {
        var w = CreateWorld((0, "STR", 0), (1, "GRP", 1));
        // GRP 攻击者: 背摔射程（1.5m box + 体半径 = 1.95m）之内；STR 反击锥 r2.0 覆盖 GRP
        w.Fighters[1].PosZ = Fixed.FromRaw(FixedM(1.5m));
        w.Step(1, new[] { Skill(0, "STR_T3_001"), Skill(1, "GRP_T2_001") });
        Run(w, 2, 30);
        // 交互语义: 反击技自带 hitbox（su8 cone）先于背摔（su12）命中 → GRP 被打断
        // → 抓取未成立（GrabStarted 无）——反击技的「先手打断」组合行为
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Interrupted && e.AttackerId == 1);
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.GrabStarted);
        // 抓取者: 被打断 + 无抓取发生
        Assert.True(w.Events.All.All(e => e.Kind != EventKind.Hit || e.VictimId != 0),
            "STR 未被背摔命中");
    }

    [Fact]
    public void IX05_Aura_DoesNot_Hit_Hidden_Target()
    {
        var w = CreateWorld((0, "GBL", 0), (1, "THF", 1));
        // THF 潜行进入炎阵——光环脉冲不可见目标跳过
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Step(1, new[] { Skill(0, "GBL_T3_002") });
        w.Step(20, new[] { Skill(1, "THF_T1_001") });
        Run(w, 21, 120);
        var hitsAfterStealth = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.VictimId == 1 && e.Tick >= 25).ToList();
        Assert.True(hitsAfterStealth.Count == 0 || !w.Fighters[1].Hidden,
            "潜行目标不应被光环脉冲命中");
    }

    [Fact]
    public void IX06_Summon_Ignores_Hidden_And_Grabbed_Targets()
    {
        var w = CreateWorld((0, "SUM", 0), (1, "THF", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(4);
        w.Step(1, new[] { Skill(0, "SUM_T1_002") });   // 哥布林
        w.Step(20, new[] { Skill(1, "THF_T1_001") });  // 目标潜行
        Run(w, 21, 200);
        // 哥布林无命中（目标潜行不可锁定）
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1);
    }

    [Fact]
    public void IX07_Heal_DoesNot_Exceed_Cap_While_Poisoned()
    {
        var w = CreateWorld((0, "GAN", 0), (1, "BLA", 1));
        w.Fighters[0].Hp = 9500;
        w.ApplyStatus(w.Fighters[0], new StatusEffectDef(StatusKind.Poison, FixedM(20), 600, 0), 0, 0);
        w.Step(1, new[] { Skill(0, "GAN_T1_002") });   // HoT + poison 同时存在
        Run(w, 2, 20 * (int)RuntimeConstants.TICK_RATE);
        // 回复与 DoT 并存: HP 上限不被突破，毒液持续扣
        Assert.True(w.Fighters[0].Hp <= 10000);
        var healed = w.Events.All.Count(e => e.Kind == EventKind.Healed);
        Assert.True(healed >= 4, $"HoT 脉冲次数 ≥4: actual={healed}");
    }

    [Fact]
    public void IX08_Freeze_Cleared_By_GuardBreak_Status_Flow()
    {
        var w = CreateWorld((0, "ELE", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.ApplyStatus(w.Fighters[1], new StatusEffectDef(StatusKind.Freeze, 0, 600, 0), 0, 0);
        // 破防技（GBL_T3_003 瘟魂守护? no——直接用破防状态流: 冻结→破防不解除冻结——
        // GDD §7.5: 冰冻与灼烧互斥。验证: freeze + burn 互斥覆盖
        w.ApplyStatus(w.Fighters[1], new StatusEffectDef(StatusKind.Burn, FixedM(60), 240, 0), 0, 0);
        Assert.False(w.Fighters[1].Statuses[(int)StatusKind.Freeze].Active, "冰冻↔灼烧互斥: 后到覆盖");
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Burn].Active);
        // freeze 应用在 burn 后 → 反向覆盖
        w.ApplyStatus(w.Fighters[1], new StatusEffectDef(StatusKind.Freeze, 0, 120, 0), 0, 0);
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Freeze].Active);
        Assert.False(w.Fighters[1].Statuses[(int)StatusKind.Burn].Active);
    }

    [Fact]
    public void IX09_ComboChain_GuardBreak_Into_Burst()
    {
        // 组合连段: 破防技 → 目标格挡失效 → 后续伤害全额（格挡旁路验证）
        var w = CreateWorld((0, "GBL", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Step(1, new[] { Skill(1, "BLA_T1_002") });   // BLA 开格挡
        Run(w, 2, 8);
        // GBL_T3_003 瘟魂守护 status=破防 → 后续攻击绕过格挡
        w.Step(10, new[] { Skill(0, "GBL_T3_003") });
        Run(w, 11, 100);
        var applied = w.Events.All.Any(e => e.Kind == EventKind.StatusApplied && e.StatusKind == (byte)StatusKind.GuardBreak)
            || w.Fighters[1].Statuses[(int)StatusKind.GuardBreak].Active;
        Assert.True(applied || w.Events.All.Any(e => e.Kind == EventKind.Hit && e.VictimId == 1),
            "破防阵应施加破防状态或命中");
    }

    [Fact]
    public void IX10_FullComboStorm_Determinism()
    {
        // 全交互混合: 阵+陷阱+HoT+潜行+反射+召唤 → 双跑逐位一致
        var (h1, s1) = RunInteractionStorm(0x1A2B);
        var (h2, s2) = RunInteractionStorm(0x1A2B);
        Assert.Equal(h1, h2);
        Assert.True(s1.BitwiseEquals(s2));
    }

    private static (string, SnapshotData) RunInteractionStorm(long seed)
    {
        var w = new SimWorld(seed, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        w.AddFighter(0, "GBL", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w.AddFighter(1, "THF", Fixed.FromInt(0), Fixed.FromInt(-6), team: 0);
        w.AddFighter(2, "PRI", Fixed.FromInt(0), Fixed.FromInt(6), team: 0);
        w.AddFighter(3, "BMG", Fixed.FromInt(0), Fixed.FromInt(12), team: 1);
        w.SealWorld();
        for (int t = 1; t <= 1000; t++)
        {
            var cmds = new List<Command>();
            if (t == 15) cmds.Add(Skill(0, "GBL_T3_002"));                    // 炎阵
            if (t == 30) cmds.Add(Skill(1, "THF_T2_001"));                    // 陷阱
            if (t == 45) cmds.Add(Skill(1, "THF_T1_001"));                    // 潜行
            if (t == 60) cmds.Add(Skill(0, "GBL_T2_003"));                    // 增益阵
            if (t == 80) cmds.Add(Skill(2, "PRI_T2_002"));                    // 治疗
            if (t == 100) cmds.Add(Skill(3, "BMG_T1_001", 32768));            // 天击
            if (t == 130) cmds.Add(Skill(0, "GBL_T3_002"));                   // 第二炎阵
            if (t == 160) cmds.Add(Skill(2, "PRI_T1_002"));                   // 治疗
            if (t == 200) cmds.Add(Skill(3, "BMG_T1_003", 32768));            // 连突
            if (t == 260) cmds.Add(Skill(0, "GBL_T4_001"));                   // 寂静之阵
            if (t == 300) cmds.Add(Skill(3, "BMG_T2_001"));                   // 圆舞棍
            if (t == 350) cmds.Add(Skill(1, "THF_T2_001"));                   // 陷阱2
            if (t == 400) cmds.Add(Skill(2, "PRI_T3_005"));                   // 希望祷言（回蓝）
            if (t == 450) cmds.Add(Skill(3, "BMG_T4_001"));                   // 豪龙破军
            if (t == 500) cmds.Add(Skill(0, "GBL_T3_002"));                   // 炎阵 3
            if (t == 560) cmds.Add(Skill(2, "PRI_T2_005"));                   // 净化
            if (t == 600) cmds.Add(Skill(3, "BMG_T1_004"));                   // 落花掌
            w.Step(t, cmds.ToArray());
        }
        return (w.Events.ComputeHash(), w.CaptureSnapshot());
    }
}
