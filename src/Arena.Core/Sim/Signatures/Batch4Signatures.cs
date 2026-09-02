using System;
using Arena.Core.Calc;

namespace Arena.Core.Sim.Signatures;

/// THF 陷阱精通（GDD §14.17 转职被动）: 潜行状态下可设置任何陷阱而不解除潜行（原著）。
/// 施法破隐的例外门控——SimWorld.TryCastSkill 经 ShouldBreakStealth 查询本签名。
/// 判定数据化: def.DeployKind != None（THF 全部陷阱/部署技）——无 skillId 集合。
public sealed class ThfTrapMastery : ISignature
{
    public string ClassId => "THF";

    public void OnEvent(ISimContext ctx, in SimEvent e) { }

    public bool ShouldBreakStealth(ISimContext ctx, ushort skillId)
    {
        var def = ctx.GetSkillDef(skillId);
        return def is null || def.DeployKind == DeployKind.None;   // 部署技不破隐，其余照常
    }
}

/// SBL 杀意波动（GDD §14.7 转职被动）: 波动系技能伤害 +4%/档；连续释放不同波动剑
/// 叠加「波动共鸣」（每层波动系技能前摇 −1f，最多 3 层）。
/// 家族分类数据化: skill_name 含「波动剑」（CSV 语义键，无 skillId 集合——Review 项#4 惯例）。
/// 层数存储: ResourceSlotKind.Resonance（class-base resource「共鸣:3」，Snapshot 全携带）。
public sealed class SblWaveResonance : ISignature
{
    public string ClassId => "SBL";
    private const string WaveFamilyName = "波动剑";

    private static bool IsWave(SkillRuntimeData? def) => def is not null && def.Name.Contains(WaveFamilyName, StringComparison.Ordinal);

    public void OnEvent(ISimContext ctx, in SimEvent e)
    {
        if (e.Kind != EventKind.SkillCast || e.AttackerId != ctx.FighterId) return;
        var def = ctx.GetSkillDef(e.SkillId);
        if (def is null || !IsWave(def)) return;
        var waveDef = def;
        var f = ctx.GetFighter(ctx.FighterId);
        if (f is null) return;
        // 连放判定: 上一手完成施放为「不同波动剑」→ 层+1（AddResource 钳 class-base cap）；
        // 首放 / 上一手非波动技 / 同一把重放（打断「不同连放」链）→ 回到 1 档（DDQ-B4-4）
        var lastDef = f.LastCastSkillUid != 0 ? ctx.GetSkillDef(f.LastCastSkillUid) : null;
        bool chain = lastDef is not null && IsWave(lastDef) && lastDef.RuntimeId != waveDef.RuntimeId;
        long cur = ctx.GetResource(SimWorld.ResourceSlotKind.Resonance);
        ctx.AddResource(SimWorld.ResourceSlotKind.Resonance, (chain ? cur + 1 : 1) - cur);
    }

    public long ModifyDamage(DamageModStage stage, ISimContext ctx, int attackerId, int victimId, ushort skillId)
    {
        if (stage != DamageModStage.SignaturePassive) return Fixed2.ONE;
        var def = ctx.GetSkillDef(skillId);
        if (!IsWave(def)) return Fixed2.ONE;
        long stacks = ctx.GetResource(SimWorld.ResourceSlotKind.Resonance);
        if (stacks <= 0) return Fixed2.ONE;
        // +4%/档（GDD §14.7）——档数 × 4% 乘区
        return Fixed2.ONE + stacks * DeterministicMath.DivRoundHalfEven(4 * Fixed.ONE, 100);
    }

    public int ModifyStartupTicks(ISimContext ctx, ushort skillId)
    {
        var def = ctx.GetSkillDef(skillId);
        if (!IsWave(def)) return 0;
        long stacks = ctx.GetResource(SimWorld.ResourceSlotKind.Resonance);
        return -(int)stacks;   // 每层前摇 −1f（GDD §14.7；负增量由 SimWorld 钳 ≥0）
    }
}

/// KNI 骑士精神（GDD §14.23 觉醒）: 八美德强化全部技能 + 重置除本技外全部 CD。
/// CD 重置数据化实现；「八美德强化」数值 CSV 未给出（DDQ-B4-3）——强化乘区待数据后在此追加。
public sealed class KniKnightSpirit : ISignature
{
    public string ClassId => "KNI";
    private const string SelfSkillId = "KNI_U_001";   // 本签名实现的觉醒技（CSV 语义键）

    public void OnEvent(ISimContext ctx, in SimEvent e)
    {
        if (e.Kind != EventKind.SkillCast || e.AttackerId != ctx.FighterId) return;
        var def = ctx.GetSkillDef(e.SkillId);
        if (def is null || def.SkillId != SelfSkillId) return;
        ctx.ResetAllCooldowns(def.RuntimeId);   // 重置除骑士精神外全部 CD（GDD §14.23）
    }
}
