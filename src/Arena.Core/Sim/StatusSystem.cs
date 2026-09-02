using System;
using System.Collections.Generic;
using Arena.Core.Calc;
// PRODUCTION - Arena.Core
// GDD §7.3/§7.4/§7.5: StatusSystem——控制类异常唯一结算点（数据驱动路由，禁止按 skillId 分支）。
// 共存规则（§7.5）: 同类刷新取较大剩余；冰冻↔灼烧互斥；沉睡↔眩晕互斥；不同类共存。
// 控制值（§7.4）: 受控积累 / 未受控 20/s 回落 / 满 100 → Break 1.5s 免控清零。
namespace Arena.Core.Sim;

public static class StatusSystem
{
    /// 施加状态（GDD §7.5 共存规则）。返回是否生效。
    public static bool Apply(FighterStateData f, in StatusEffectDef eff, int sourceFighterId, SimWorld w)
    {
        if (f.State == FighterState.Dead || f.State == FighterState.Break) return false;   // 免控
        ref var slot = ref f.Statuses[(int)eff.Kind];

        // 互斥覆盖（后到覆盖）
        for (int k = 1; k < f.Statuses.Length; k++)
        {
            if ((StatusKind)k == eff.Kind || !f.Statuses[k].Active) continue;
            if (StatusRules.MutuallyExclusive(eff.Kind, (StatusKind)k))
            {
                w.RemoveStatus(f, (StatusKind)k);
            }
        }

        if (slot.Active)
        {
            // 同类刷新: 取较大剩余（§7.5）
            if (eff.DurationTicks > slot.RemainingTicks)
            {
                slot.RemainingTicks = eff.DurationTicks;
                slot.TotalTicks = eff.DurationTicks;
                slot.PotencyQ = eff.PotencyQ;
            }
        }
        else
        {
            slot.Active = true;
            slot.RemainingTicks = eff.DurationTicks;
            slot.TotalTicks = eff.DurationTicks;
            slot.PotencyQ = eff.PotencyQ;
            slot.DotCarryQ = 0;
            slot.DotApplied = 0;
            slot.SourceFighterId = sourceFighterId;
            w.Events.Emit(new SimEvent
            {
                Kind = EventKind.StatusApplied, VictimId = f.Id,
                StatusKind = (byte)eff.Kind, DurationTicks = eff.DurationTicks,
            });
            // 控制值积累（§7.4，起身保护 ×0.5）
            long cv = StatusRules.ControlValueAdd(eff.Kind);
            if (cv > 0) w.AddControlValue(f, f.ProtectTicks > 0 ? cv / 2 : cv);
        }
        return true;
    }

    public static void Remove(FighterStateData f, StatusKind kind, SimWorld w)
    {
        ref var slot = ref f.Statuses[(int)kind];
        if (!slot.Active) return;
        slot.Active = false;
        w.Events.Emit(new SimEvent { Kind = EventKind.StatusExpired, VictimId = f.Id, StatusKind = (byte)kind });
    }

    /// 每 Tick 结算（FighterId 升序由 SimWorld 保证）：时长/DoT/控制值衰减
    public static void Tick(FighterStateData f, SimWorld w)
    {
        bool controlled = false;
        for (int k = 1; k < f.Statuses.Length; k++)
        {
            ref var slot = ref f.Statuses[k];
            if (!slot.Active) continue;
            controlled = true;

            // DoT（灼烧/出血/毒: 每秒 Potency 点——分数伤害累积 RHE，确定性）
            // PotencyQ 已是 Q32.16 的每秒伤害（如灼烧 60 → 3932160）；per-Tick = RHE(PotencyQ / 60)
            if ((StatusKind)k is StatusKind.Burn or StatusKind.Bleed or StatusKind.Poison)
            {
                long perTick = DeterministicMath.DivRoundHalfEven(slot.PotencyQ, RuntimeConstants.TICK_RATE);
                slot.DotCarryQ += perTick;
                long whole = slot.DotCarryQ / Fixed.ONE;
                if (whole > 0)
                {
                    slot.DotCarryQ -= whole * Fixed.ONE;
                    slot.DotApplied += whole;
                    f.Hp -= whole;
                }
            }

            if (--slot.RemainingTicks <= 0)
            {
                slot.Active = false;
                w.Events.Emit(new SimEvent { Kind = EventKind.StatusExpired, VictimId = f.Id, StatusKind = (byte)k });
            }
        }

        // 控制值衰减: 未处于控制状态时 20/s（§7.4）
        if (!controlled && f.ControlValue > 0 && f.State != FighterState.Break)
        {
            long dec = DeterministicMath.DivRoundHalfEven(
                RuntimeConstants.CONTROL_DECAY_PER_TICK_NUM * Fixed.ONE,
                RuntimeConstants.CONTROL_DECAY_PER_TICK_DEN);
            f.ControlValue = Math.Max(0, f.ControlValue - dec / Fixed.ONE);
        }
    }

    // ---- 行为查询（Sim 指令/移动路由消费） ----
    public static bool CanAct(FighterStateData f)
    {
        for (int k = 1; k < f.Statuses.Length; k++)
            if (f.Statuses[k].Active && StatusRules.BlocksAction((StatusKind)k)) return false;
        return true;
    }

    public static bool CanMove(FighterStateData f)
    {
        for (int k = 1; k < f.Statuses.Length; k++)
            if (f.Statuses[k].Active && StatusRules.BlocksMovement((StatusKind)k)) return false;
        return true;
    }

    public static bool CanCastSkill(FighterStateData f)
    {
        for (int k = 1; k < f.Statuses.Length; k++)
            if (f.Statuses[k].Active && StatusRules.BlocksSkill((StatusKind)k)) return false;
        return true;
    }

    /// 移速乘区（Slow: ×(1−potency)）
    public static long MoveSpeedMultQ(FighterStateData f)
    {
        long mult = Fixed.ONE;
        ref readonly var slow = ref f.Statuses[(int)StatusKind.Slow];
        if (slow.Active) mult -= Math.Min(slow.PotencyQ, Fixed.ONE);
        return mult;
    }

    /// 攻击乘区（Weakness: ATK −20%）
    public static long AtkMultQ(FighterStateData f)
    {
        ref readonly var weak = ref f.Statuses[(int)StatusKind.Weakness];
        return weak.Active ? DeterministicMath.DivRoundHalfEven(80 * Fixed.ONE, 100) : Fixed.ONE;
    }
}
