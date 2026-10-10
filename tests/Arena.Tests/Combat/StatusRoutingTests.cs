using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Xunit;

namespace Arena.Tests.Combat;

/// 批次 A status 路由收口（2026-10-11）——把「语义已由既有原语承载、但 token 未登记」的行接上。
/// 断言的是**真实解析结果**（ForceInterrupt/状态表/Cleanse 等），不是「枚举存在」。
/// SR06 把剩余未路由集合**逐字固化**：任何新出现的未路由 status 都会让测试失败（防静默吞掉新语义）。
public class StatusRoutingTests
{
    private static IReadOnlyList<SkillRuntimeData> Skills => CombatGoldenSlice.Catalog.Skills;
    private static HashSet<string> Unrouted => CombatGoldenSlice.Catalog.UnroutedStatuses.ToHashSet();
    private static SkillRuntimeData Def(string id) => Skills.First(d => d.SkillId == id);

    /// 批次 A 收口的 7 行（token → 承载它的既有原语）
    private static readonly string[] BatchA =
    {
        "WRK_T3_003", "KNI_T3_003",   // status「反射法术」→ IsReflect（special∪技名）
        "ROG_T3_001",                 // status「破霸体」→ ArmorBreak（special）
        "WRK_T3_001",                 // status「打断吟唱」→ ForceInterrupt
        "PRI_T2_003",                 // status「sleep」→ StatusKind.Sleep
        "GAN_T2_002",                 // status「驱散」→ Cleanse
        "KNI_T3_006",                 // status「解除免疫挑衅/恐惧/混乱」→ Cleanse
    };

    /// 批次 B1 收口的 2 行（新增状态域）
    private static readonly string[] BatchB1 = { "PRI_T4_001", "ASN_T4_002" };

    // ================= SR01: 批次 A/B1 各行不再登记为未路由 =================

    [Fact]
    public void SR01_BatchA_Rows_Are_No_Longer_Unrouted()
    {
        var still = BatchA.Concat(BatchB1).Where(Unrouted.Contains).ToList();
        Assert.True(still.Count == 0, "仍有未路由登记: " + string.Join(",", still));
    }

    // ================= SR02: WRK_T3_001「打断吟唱」→ ForceInterrupt（真实修复） =================

    [Fact]
    public void SR02_InterruptChant_From_Status_Column_Sets_ForceInterrupt()
    {
        // 该行 special=「暗为主火为辅」不含关键词，关键词只在 status 列——
        // 收口前 ForceInterrupt=false（技能实际不打断），收口后必须为 true。
        Assert.True(Def("WRK_T3_001").ForceInterrupt,
            "WRK_T3_001 的「打断吟唱」写在 status 列，必须被提取为 ForceInterrupt");
    }

    // ================= SR03: PRI_T2_003「sleep:2.5s」→ Sleep 2.5s =================

    [Fact]
    public void SR03_Sleep_Token_Yields_Sleep_Status_With_Parsed_Duration()
    {
        var st = Def("PRI_T2_003").Statuses.FirstOrDefault(s => s.Kind == StatusKind.Sleep);
        Assert.Equal(StatusKind.Sleep, st.Kind);
        Assert.Equal(150, st.DurationTicks);   // 2.5s × 60Tick/s
    }

    // ================= SR04: 驱散 / 解除免疫 → CleanseDebuffs =================

    [Theory]
    [InlineData("GAN_T2_002")]   // status「驱散:初阶1个→满阶3个」
    [InlineData("KNI_T3_006")]   // status「解除免疫挑衅/恐惧/混乱」
    public void SR04_Dispel_Tokens_Set_Cleanse(string skillId)
    {
        Assert.True(Def(skillId).CleanseDebuffs, $"{skillId} 应置 CleanseDebuffs");
    }

    // ================= SR05: 反射/破霸体 token 被消费且既有语义不变 =================

    [Fact]
    public void SR05_Reflect_And_ArmorBreak_Tokens_Consumed_Without_Semantic_Change()
    {
        // 这两个机制由 special / 技名承载（已生效）；status 里的同义 token 只做消费，不重复施加。
        Assert.True(Def("WRK_T3_003").IsReflect);      // 技名「魔镜」+ special「反弹…」
        Assert.True(Def("KNI_T3_003").IsReflect);      // 技名「法术反射」
        Assert.True(Def("ROG_T3_001").ArmorBreak);     // special「破霸体」

        // 消费掉之后不应在状态表里凭空多出反射/破防项（不重复施加）
        Assert.DoesNotContain(Def("WRK_T3_003").Statuses, s => s.Kind == StatusKind.GuardBreak);
        Assert.DoesNotContain(Def("KNI_T3_003").Statuses, s => s.Kind == StatusKind.GuardBreak);
    }

    // ================= SR06: 剩余未路由集合逐字固化（回归闸） =================

    private static readonly string[] ExpectedRemaining =
    {
        "ELE_T2_001:status:forcespin:180°",
        "WIT_T2_003:status:dispel:1",
        "WIT_U_001:status:全异常满值",
        "SBL_T3_001:status:结界:封锁1.2s",
        "GBL_T3_004:status:weight:x2",
        "LAU_T1_003:status:三发:①击退②僵直③击倒",
        "MEH_T3_001:status:weight:x1.5",
        "MEH_T4_001:status:amplify:下一击×2",
        "GRP_T1_001:status:震地波:r2.5:0.30",
        "GRP_U_001:status:落点AOE:r3.0",
        "ROG_T2_001:status:锁喉:目标攻防-50%",
        "ROG_T3_004:status:压制:目标倒地期间持续",
        "WRK_T2_003:status:吸血:治疗自身×1.1",
        "WRK_T3_004:status:操纵目标移动",
        "WRK_T3_006:status:附身DoT",
        "WRK_T4_001:status:出神:操作无效:1.2s",
        "WRK_T4_002:status:拉拽入门内",
        "WRK_T4_003:status:禁闪现",
        "WRK_U_001:status:禁足:完全禁足3.5s:无法解除",
        "KNI_T3_004:status:反噬:伤害+效果",
        "KNI_T3_007:status:免疫挑衅/吼叫",
        "EXO_U_001:status:技能封禁:指定一技能8s(U档4s)",
    };

    [Fact]
    public void SR06_Remaining_Unrouted_Set_Is_Frozen()
    {
        var expected = ExpectedRemaining.ToHashSet();
        var actual = Unrouted;

        var added = actual.Except(expected).OrderBy(x => x).ToList();
        var removed = expected.Except(actual).OrderBy(x => x).ToList();

        Assert.True(added.Count == 0, "新出现未路由（须显式裁定后再登记）: " + string.Join(" | ", added));
        Assert.True(removed.Count == 0, "白名单已过期（行被路由后须同步更新）: " + string.Join(" | ", removed));
    }

    // ================= SR07: 批次 B1 的两行产出了新的状态域 =================

    [Fact]
    public void SR07_BatchB1_Tokens_Yield_New_Status_Domains()
    {
        var up = Def("PRI_T4_001").Statuses.FirstOrDefault(s => s.Kind == StatusKind.DamageTakenUp);
        Assert.Equal(StatusKind.DamageTakenUp, up.Kind);
        Assert.Equal(6 * 60, up.DurationTicks);      // 「…+30%:6s」
        Assert.Equal(FixedM(0.30m), up.PotencyQ);    // +30% → Q32.16

        var hb = Def("ASN_T4_002").Statuses.FirstOrDefault(s => s.Kind == StatusKind.HealBlock);
        Assert.Equal(StatusKind.HealBlock, hb.Kind);
        Assert.Equal(10 * 60, hb.DurationTicks);     // 「禁疗:10s」
    }

    // ================= SR08: 易伤真的提高伤害（行为验证，非枚举断言） =================

    [Fact]
    public void SR08_DamageTakenUp_Multiplies_Incoming_Damage()
    {
        long plain = HitDamage(up: false);
        long boosted = HitDamage(up: true);

        Assert.True(plain > 0, "基线必须真的命中");
        Assert.True(boosted > plain, $"易伤应提高所受伤害: {plain} → {boosted}");
        double ratio = (double)boosted / plain;
        Assert.InRange(ratio, 1.25, 1.35);   // 数据是 +30%
    }

    private static long HitDamage(bool up)
    {
        var w = CreateWorld(0, "BLA", 1, "BLA");
        w.Fighters[1].PosZ = Fixed.FromInt(1);          // 贴身（fan r2.2）
        if (up)
            w.ApplyStatus(w.Fighters[1], new StatusEffectDef(StatusKind.DamageTakenUp, FixedM(0.30m), 600, 0), 0, 0);
        w.Step(1, new[] { Skill(0, "BLA_T1_001") });    // aim=0 → 朝 +Z，正对受害者
        Run(w, 2, 60);
        return w.Events.All.Where(e => e.Kind == EventKind.Hit && e.VictimId == 1).Sum(e => e.DamageRaw);
    }

    // ================= SR09: 禁疗真的封锁回复（行为验证） =================

    [Fact]
    public void SR09_HealBlock_Prevents_Healing()
    {
        Assert.True(HealGain(blocked: false) > 0, "基线治疗必须真的生效");
        Assert.Equal(0, HealGain(blocked: true));
    }

    private static long HealGain(bool blocked)
    {
        var w = CreateWorld(0, "PRI", 1, "BLA");
        var me = w.Fighters[0];
        me.Hp = 5000;                                   // 先受伤，留出回复空间
        long before = me.Hp;
        if (blocked)
            w.ApplyStatus(me, new StatusEffectDef(StatusKind.HealBlock, 0, 600, 0), 1, 0);
        w.Step(1, new[] { Skill(0, "PRI_T1_002") });     // 治疗 800（v1 ally=自体）
        Run(w, 2, 120);
        return me.Hp - before;
    }

    // ================= 测试世界 / 指令助手 =================

    private static SimWorld CreateWorld(int id0, string cls0, int id1, string cls1)
    {
        var w = new SimWorld(0x7EED_1234L, CombatGoldenSlice.Catalog.DataVersionHash);
        foreach (var s in CombatGoldenSlice.Catalog.Skills) w.AddSkill(s);
        w.AddFighter(id0, cls0, Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w.AddFighter(id1, cls1, Fixed.FromInt(0), Fixed.FromInt(0), team: 1);
        w.SealWorld();
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) =>
        new(f, CmdKind.Skill, CombatGoldenSlice.Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to)
    {
        for (int t = from; t <= to; t++) w.Step(t, Array.Empty<Command>());
    }

    private static long FixedM(decimal m) => (long)Math.Round(m * 65536m, MidpointRounding.ToEven);
}
