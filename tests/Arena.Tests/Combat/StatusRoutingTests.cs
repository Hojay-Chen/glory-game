using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
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

    // ================= SR01: 7 行不再登记为未路由 =================

    [Fact]
    public void SR01_BatchA_Rows_Are_No_Longer_Unrouted()
    {
        var still = BatchA.Where(Unrouted.Contains).ToList();
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
        "ASN_T4_002:status:禁疗:10s",
        "WRK_T2_003:status:吸血:治疗自身×1.1",
        "WRK_T3_004:status:操纵目标移动",
        "WRK_T3_006:status:附身DoT",
        "WRK_T4_001:status:出神:操作无效:1.2s",
        "WRK_T4_002:status:拉拽入门内",
        "WRK_T4_003:status:禁闪现",
        "WRK_U_001:status:禁足:完全禁足3.5s:无法解除",
        "PRI_T4_001:status:目标所受伤害+30%:6s",
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
}
