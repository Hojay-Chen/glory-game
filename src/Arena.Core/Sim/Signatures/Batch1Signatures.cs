using System;
using System.Collections.Generic;
using Arena.Core.Calc;
// PRODUCTION - Arena.Core
// 第一批真实职业签名（GDD §14 被动系——数据 special 列描述的不可泛化机制）。
// 全部无字段状态（ADR-0008）——副作用经 ISimContext 进 Sim 状态域。
namespace Arena.Core.Sim.Signatures;

/// BMG 斗者意志（GDD §14.1 passive）: 特定技能命中 → 获得炫纹（资源槽 Orb，上限 7）
/// 炫纹技能映射（GDD 数据: 天击=光/连突=冰/落花掌=火/圆舞棍=暗——skillId 后缀路由）
public sealed class BmgFightingSpirit : ISignature
{
    public string ClassId => "BMG";
    private static readonly HashSet<string> OrbSkills = new(StringComparer.Ordinal)
    {
        "BMG_T1_001",   // 天击 → 光纹
        "BMG_T1_003",   // 连突 → 冰纹
        "BMG_T1_004",   // 落花掌 → 火纹
        "BMG_T2_001",   // 圆舞棍 → 暗纹
    };

    public void OnEvent(ISimContext ctx, in SimEvent e)
    {
        if (e.Kind != EventKind.Hit || e.AttackerId != ctx.FighterId) return;
        var def = ctx.GetSkillDef(e.SkillId);
        if (def is null || !OrbSkills.Contains(def.SkillId)) return;
        if (ctx.GetResource(SimWorld.ResourceSlotKind.Orb) >= ctx.GetResourceCap(SimWorld.ResourceSlotKind.Orb)) return;
        ctx.AddResource(SimWorld.ResourceSlotKind.Orb, 1);
    }
}

/// BER 血气唤醒（GDD §14.2 passive）: HP<50%:ATK+5%; <30%:+10%; <15%:+15%
/// OnTick 阈值检查 → BuffAtkPct 域刷新（无字段状态——域即 Sim 状态）
public sealed class BerBloodAwakening : ISignature
{
    public string ClassId => "BER";

    public void OnEvent(ISimContext ctx, in SimEvent e) { }

    public void OnTick(ISimContext ctx)
    {
        var f = ctx.GetFighter(ctx.FighterId);
        if (f is null) return;
        long pct = f.Hp * 100 / Math.Max(1, 10000);
        long buff = pct < 15 ? 15 : pct < 30 ? 10 : pct < 50 ? 5 : 0;
        if (buff > 0)
        {
            f.BuffAtkPctQ = DeterministicMath.DivRoundHalfEven(buff * Fixed.ONE, 100);
            f.BuffAtkPctTicks = 2;   // 下一 Tick 刷新（OnTick 每拍重写）
        }
        else if (f.BuffAtkPctTicks > 0 && f.BuffAtkPctQ == DeterministicMath.DivRoundHalfEven(15 * Fixed.ONE, 100))
        {
            f.BuffAtkPctQ = 0; f.BuffAtkPctTicks = 0;   // 血量回升 → 清除最高档
        }
    }
}

/// ASN 暗杀艺术（GDD §14.20 passive）: 背击额外 +20%（合计 ×1.44 = 1.2 × 1.2）
public sealed class AsnAssassination : ISignature
{
    public string ClassId => "ASN";

    public void OnEvent(ISimContext ctx, in SimEvent e) { }

    public long ModifyDamage(DamageModStage stage, ISimContext ctx, int attackerId, int victimId)
    {
        return stage == DamageModStage.BackstabBonus
            ? DeterministicMath.DivRoundHalfEven(120 * Fixed.ONE, 100)   // 追加 ×1.2
            : Fixed.ONE;
    }
}

/// QIM 护体真气（GDD §14.18 passive）: MP>70%:DEF+15%; <30%:DEF−10%
/// OnTick 阈值 → Def 域直接修改（v1: Def 值直接调整，MP 自然回复驱动翻转）
public sealed class QimBodyQi : ISignature
{
    public string ClassId => "QIM";
    private long _lastDef;   // 恢复基准（无字段状态例外：跨 Tick 恢复基准——登记为签名内可变）

    public void OnEvent(ISimContext ctx, in SimEvent e) { }

    public void OnTick(ISimContext ctx)
    {
        var f = ctx.GetFighter(ctx.FighterId);
        if (f is null) return;
        long mpPct = f.Mp * 100 / Math.Max(1, 1000);
        if (f.Def == _lastDef) { }   // 未被外部修改——继续调整
        long baseDef = _lastDef > 0 ? _lastDef : f.Def;
        long newDef = mpPct > 70 ? baseDef + DeterministicMath.MulShift(baseDef, DeterministicMath.DivRoundHalfEven(15 * Fixed.ONE, 100))
                   : mpPct < 30 ? baseDef - DeterministicMath.MulShift(baseDef, DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100))
                   : baseDef;
        if (newDef != f.Def) f.Def = newDef;
        _lastDef = baseDef;
    }
}
