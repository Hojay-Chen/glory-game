using System;
using System.Collections.Generic;
using Arena.Core.Calc;
// PRODUCTION - Arena.Core
// 第一批真实职业签名（GDD §14 被动系——数据 special 列描述的不可泛化机制）。
// 全部无字段状态（ADR-0008）——副作用经 ISimContext 进 Sim 状态域。
namespace Arena.Core.Sim.Signatures;

/// BMG 斗者意志（GDD §14.1 passive）: 特定技能命中 → 获得炫纹（资源槽 Orb，上限 7）
/// Review 项#4: 炫纹触发关系由 Compiler 从 CSV special「炫纹:X」解析为 OrbTag 数据——
/// 签名只实现「获得资源」语义，不持有 skillId 集合（CSV 新增炫纹技无需改签名）
public sealed class BmgFightingSpirit : ISignature
{
    public string ClassId => "BMG";

    public void OnEvent(ISimContext ctx, in SimEvent e)
    {
        if (e.Kind != EventKind.Hit || e.AttackerId != ctx.FighterId) return;
        var def = ctx.GetSkillDef(e.SkillId);
        if (def is null || def.OrbTag == OrbTagKind.None) return;   // 数据驱动: OrbTag 由 Compiler 解析
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
        // Review 项#3: 阈值基准 = 权威 HpMax 状态域（非签名内硬编码 10000）
        long pct = f.Hp * 100 / Math.Max(1, f.HpMax);
        long buff = pct < 15 ? 15 : pct < 30 ? 10 : pct < 50 ? 5 : 0;
        if (buff > 0)
        {
            f.BuffAtkPctQ = DeterministicMath.DivRoundHalfEven(buff * Fixed.ONE, 100);
            f.BuffAtkPctTicks = 2;   // 每 Tick 刷新（持久 while 阈值成立）
        }
        else if (f.BuffAtkPctTicks > 0 && f.BuffAtkPctTicks <= 2)
        {
            // 仅清除自身短周期槽（ticks ≤ 2 = 本签名刷新节奏）——不踩长周期外部 buff（如阵内 ATK）
            f.BuffAtkPctQ = 0;
            f.BuffAtkPctTicks = 0;
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
/// Review 项#1: 旧实现以签名私有 _lastDef 跨 Tick 承担恢复基准——违反 ADR-0008
/// 「Signature 无字段战斗状态」。重设计: BuffDefPct 域（Fighter 权威状态域，可负、
/// Snapshot 携带、伤害链消费）——签名每 Tick 刷新域值，无跨 Tick 私有状态。
public sealed class QimBodyQi : ISignature
{
    public string ClassId => "QIM";

    public void OnEvent(ISimContext ctx, in SimEvent e) { }

    public void OnTick(ISimContext ctx)
    {
        var f = ctx.GetFighter(ctx.FighterId);
        if (f is null) return;
        // Review 项#3: 阈值基准 = 权威 MpMax 状态域
        long mpPct = f.Mp * 100 / Math.Max(1, f.MpMax);
        if (mpPct > 70)
        {
            f.BuffDefPctQ = DeterministicMath.DivRoundHalfEven(15 * Fixed.ONE, 100);
            f.BuffDefPctTicks = 2;
        }
        else if (mpPct < 30)
        {
            f.BuffDefPctQ = -DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100);
            f.BuffDefPctTicks = 2;
        }
        else if (f.BuffDefPctTicks > 0 && f.BuffDefPctTicks <= 2)
        {
            f.BuffDefPctQ = 0;
            f.BuffDefPctTicks = 0;
        }
    }
}
