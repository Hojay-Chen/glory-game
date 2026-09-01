using System;
using Arena.Core.Calc;
using Arena.Core.Collision;
using Arena.Core.Rng;
// PRODUCTION - Arena.Core
// SPEC-0006 §3: HitResolve——命中裁决唯一实现点。输入 = CollisionResult + SkillDef + Fighter 投影。
// 【结构性断言】本类禁止任何几何计算——输入签名只含 TOI/HitRegion/HitPoint/HitNormal 已算好的字段。
// 伤害链（GDD §2.5）: mult × ATK × 防御系数 × 修正乘积（无暴击无浮动——D02 确定性）。
// 修正顺序（乘法可交换但固定顺序保证逐位一致）:
//   防御 → 部位(头部弱点) → 空中/扫地 → 背击 → 沉睡觉醒(+30%) → 冰冻(+10%) →
//   起身保护(×0.9) → 连段递减(第 7 击起, 下限 0.40)
// 四道闸门（GDD §8.5）: ①浮空衰减 ×0.8ⁿ 下限 3.0 ②硬直递减 ×0.97ⁿ ③伤害递减 ④控制值挣脱。
namespace Arena.Core.Sim;

public static class HitResolve
{
    /// 命中上下文（SimWorld 组装；几何字段由 CollisionSystem 产出）
    public readonly struct HitContext
    {
        public required SimWorld World { get; init; }
        public required FighterStateData Attacker { get; init; }
        public required FighterStateData Victim { get; init; }
        public required SkillRuntimeData Def { get; init; }
        public required byte SegmentIndex { get; init; }
        public required byte HitRegion { get; init; }        // HitRegion 枚举值
        public required long HitPointX { get; init; }
        public required long HitPointY { get; init; }
        public required long HitPointZ { get; init; }
        public required long HitNormalX { get; init; }
        public required long HitNormalZ { get; init; }
        public bool FromProjectile { get; init; }
    }

    public static void Resolve(in HitContext ctx)
    {
        var w = ctx.World;
        var atk = ctx.Attacker;
        var vic = ctx.Victim;
        var def = ctx.Def;

        // ---- 资格过滤（PA-H4: 豁免在结算前） ----
        if (vic.State == FighterState.Dead) return;
        var vicExec = w.GetExecution(vic.ActiveSkillUid);
        bool skillInvuln = vicExec is not null && vicExec.Def is not null &&
                           vicExec.Def.Invuln is { } inv && inv.Covers(vicExec.CurrentOffset);
        if (vic.IsInvulnerable || skillInvuln)
        {
            EmitWhiff(w, atk.Id, def.RuntimeId, WhiffReason.Invulnerable);
            return;
        }
        bool victimDown = vic.State == FighterState.Down;
        if (victimDown && !def.Sweep)
        {
            // 倒地保护（GDD §5.6: 倒地不受击，仅【扫地】可打）
            EmitWhiff(w, atk.Id, def.RuntimeId, WhiffReason.DownProtected);
            return;
        }

        // ---- 伤害公式（GDD §2.5.1） ----
        long dmg = def.DamageMultQ;                                        // Q32.16 倍率
        dmg = DeterministicMath.MulShift(dmg, atk.Atk);                    // × ATK → 点数域
        long defenseFactor = DeterministicMath.DivRoundHalfEven(
            RuntimeConstants.DEFENSE_CONST * Fixed.ONE,
            RuntimeConstants.DEFENSE_CONST + vic.Def);                     // D = 1200/(1200+DEF) Q32.16
        dmg = DeterministicMath.MulShift(dmg, defenseFactor);

        // ---- 部位修正（SPEC-0006 §1.4/PA-H2: HitRegion 由 Collision 几何选取） ----
        var region = (HitRegion)ctx.HitRegion;
        bool headHit = region == HitRegion.Head;
        if (headHit)
        {
            // 弱点头部倍率（GDD §4.6: ×1.5 基线；巴雷特类 ×2 由 special 数据化——SPEC-0006 §1.4）
            dmg = DeterministicMath.MulShift(dmg, def.HeadMultQ);
        }

        // ---- 空中/扫地修正（GDD §2.4.4/§2.5.2） ----
        bool airMod = vic.IsAirborne;
        if (airMod) dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.Modifiers.AirborneX105);
        if (victimDown && def.Sweep) dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.Modifiers.SweepX070);

        // ---- 背击（GDD §2.5.2: 命中来源与目标面朝夹角 >120°） ----
        if (IsBackstab(atk, vic)) dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.Modifiers.BackstabX120);

        // ---- 沉睡觉醒（GDD §7.3: 受击即醒，醒来那一击 +30%） ----
        bool sleepWakeup = vic.Statuses[(int)StatusKind.Sleep].Active;
        if (sleepWakeup) dmg = DeterministicMath.MulShift(dmg, DeterministicMath.DivRoundHalfEven(130 * Fixed.ONE, 100));

        // ---- 冰冻增伤（GDD §7.3: 受击伤害 +10%） ----
        if (vic.Statuses[(int)StatusKind.Freeze].Active)
            dmg = DeterministicMath.MulShift(dmg, DeterministicMath.DivRoundHalfEven(110 * Fixed.ONE, 100));

        // ---- 起身保护（GDD §5.7: 起身后 1s 第一次伤害 ×0.9） ----
        if (vic.ProtectTicks > 0) dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.Modifiers.GetupProtX090);

        // ---- 连段递减（GDD §8.5③: 第 7 击起 ×0.94ⁿ，下限 0.40 表内钳制） ----
        int hitNumber = vic.HitstunCount + 1;
        if (hitNumber >= 7)
        {
            int idx = Math.Min(hitNumber, Calc.DeterministicTables.DamageDecay.Length - 1);
            dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.DamageDecay[idx]);
        }
        if (dmg < 0) dmg = 0;

        // ---- HP 结算（护盾池 G-09 未实现——直扣 HP，登记于实施报告） ----
        vic.Hp -= dmg;

        // ---- 霸体判定（GDD §6.4） ----
        bool armored = false;
        bool superArmored = false;
        var exec = w.GetExecution(vic.ActiveSkillUid);
        if (exec is not null && exec.Def is not null && exec.Def.Armor is { } armor &&
            armor.Covers(exec.CurrentOffset))
        {
            if (def.ArmorBreak)
            {
                // 【破霸体】: 直接击破霸体（GDD §6.4）——霸体失效，正常受击反应
            }
            else
            {
                armored = true;
                superArmored = armor.SuperArmor;
            }
        }

        // ---- 控制值（霸体承伤积累——拆霸体手段，GDD §6.4；单次增量 OQ 登记 = 10） ----
        if (armored) w.AddControlValue(vic, 10);

        // ---- 状态注入（数据驱动路由；几率走 SKILL_CHANCE 流） ----
        for (int i = 0; i < def.Statuses.Length; i++)
        {
            var eff = def.Statuses[i];
            if (eff.HasChance)
            {
                int roll = w.Rng.Roll100(new RollScope(StreamClass.SKILL_CHANCE, atk.Id, def.RuntimeId));
                if (roll >= eff.ChancePercent) continue;
            }
            w.ApplyStatus(vic, eff, atk.Id, def.RuntimeId);
        }

        // ---- 受击反应（armored 时跳过硬直/浮空/击退——承伤不硬直） ----
        if (!armored)
        {
            if (def.ForcedDown && victimDown)
            {
                // 【受身无效】命中倒地: 只能躺满（GDD §5.6）
                vic.UkemiIneffective = true;
            }
            ApplyReaction(ctx, hitNumber, airMod);
        }

        // ---- 沉睡受击即醒 ----
        if (sleepWakeup) w.RemoveStatus(vic, StatusKind.Sleep);

        // ---- 连段计数（GDD §8.4: 连段时钟=受控状态刷新） ----
        vic.HitstunCount = hitNumber;

        // ---- Hit 事件（协议 v2: 空间载荷直写 Raw，ADR-0003 §2.2 不可变） ----
        w.Events.Emit(new SimEvent
        {
            Kind = EventKind.Hit,
            AttackerId = atk.Id, VictimId = vic.Id,
            SkillId = def.RuntimeId, SegmentIndex = ctx.SegmentIndex,
            DamageRaw = dmg, HitNumber = hitNumber,
            HitRegion = ctx.HitRegion,
            HitPointX = ctx.HitPointX, HitPointY = ctx.HitPointY, HitPointZ = ctx.HitPointZ,
            HitNormalX = ctx.HitNormalX, HitNormalZ = ctx.HitNormalZ,
            VictimStateBefore = (byte)vic.State,
            PosY = vic.PosY.Raw,
            SweepFlag = def.Sweep, AirMod = airMod,
        });
    }

    /// 受击反应: 浮空/击退/硬直/强制倒地（GDD §5）
    private static void ApplyReaction(in HitContext ctx, int hitNumber, bool airMod)
    {
        var w = ctx.World;
        var vic = ctx.Victim;
        var def = ctx.Def;

        // 强制倒地技（GDD §5.6【受身无效】: 圆舞棍/背摔/踏射）
        if (def.ForcedDown)
        {
            if (vic.State == FighterState.Down)
            {
                // 【受身无效】命中倒地: 只能躺满倒地时间
                vic.UkemiIneffective = true;
            }
            else
            {
                ForceDown(w, vic);
                // 技能标签【受身无效】: 本次强制倒地不可受身（GDD §5.6）
                vic.UkemiIneffective = true;
            }
            return;
        }

        // 扫地命中倒地: 仅结算伤害，状态不变（GDD §5.6 倒地保护）
        if (vic.State == FighterState.Down) return;

        // 浮空（GDD §5.3）
        if (def.LaunchVelQ > 0)
        {
            if (vic.ForcedFall)
            {
                // 落地保护: 浮空技能不再将其击起（GDD §5.3 第二道闸）
                ApplyHitstun(w, ctx, hitNumber);
                return;
            }
            if (vic.State == FighterState.Launch)
            {
                // 空中再命中刷新: v = 技能初速 × 0.8ⁿ，下限 3.0（第一道闸）
                vic.LaunchCount++;
                int idx = Math.Min(vic.LaunchCount, Calc.DeterministicTables.LaunchDecay.Length - 1);
                long v = DeterministicMath.MulShift(def.LaunchVelQ, Calc.DeterministicTables.LaunchDecay[idx]);
                long floor = RuntimeConstants.LAUNCH_FLOOR_MPS * Fixed.ONE;
                if (v < floor) v = floor;
                vic.VelY = Fixed.FromRaw(v);
                w.Events.Emit(new SimEvent { Kind = EventKind.Relaunched, AttackerId = ctx.Attacker.Id, VictimId = vic.Id, SkillId = def.RuntimeId, ValueRaw = v });
            }
            else
            {
                vic.LaunchCount = 0;
                vic.FloatAirTicks = 0;
                vic.VelY = Fixed.FromRaw(def.LaunchVelQ);
                SetState(w, vic, FighterState.Launch);
                w.Events.Emit(new SimEvent { Kind = EventKind.Launched, AttackerId = ctx.Attacker.Id, VictimId = vic.Id, SkillId = def.RuntimeId, ValueRaw = def.LaunchVelQ });
            }
            return;
        }

        // 击退（GDD §5.1/§5.8）
        if (def.KnockbackVelQ > 0)
        {
            int stun = CalcHitstunTicks(def.HitstunTicks, hitNumber);
            SetState(w, vic, FighterState.Hitstun, stun);
            // 击退方向 = attacker → victim 水平向（确定性归一化）
            long dx = vic.PosX.Raw - ctx.Attacker.PosX.Raw;
            long dz = vic.PosZ.Raw - ctx.Attacker.PosZ.Raw;
            DeterministicMath.Normalize(dx, dz, out var nx, out var nz);
            vic.VelX = Fixed.FromRaw(DeterministicMath.MulShift(nx, def.KnockbackVelQ));
            vic.VelZ = Fixed.FromRaw(DeterministicMath.MulShift(nz, def.KnockbackVelQ));
            vic.FallDirIndex = DirIndexFromVel(vic.VelX.Raw, vic.VelZ.Raw);
            w.Events.Emit(new SimEvent { Kind = EventKind.Knockback, AttackerId = ctx.Attacker.Id, VictimId = vic.Id, SkillId = def.RuntimeId, ValueRaw = def.KnockbackVelQ });
            return;
        }

        // 普通硬直
        ApplyHitstun(w, ctx, hitNumber);
    }

    private static void ApplyHitstun(SimWorld w, in HitContext ctx, int hitNumber)
    {
        var vic = ctx.Victim;
        if (vic.State == FighterState.Down) return;   // 扫地命中不改变倒地状态
        SetState(w, vic, FighterState.Hitstun, CalcHitstunTicks(ctx.Def.HitstunTicks, hitNumber));
        vic.VelX = Fixed.Zero; vic.VelZ = Fixed.Zero;  // 硬直制动（GDD §5.2）
    }

    /// 硬直时长 = 基准 × 0.97ⁿ（下限 ×0.5，GDD §5.2/§8.5②）
    public static int CalcHitstunTicks(int baseTicks, int hitNumber)
    {
        int idx = Math.Min(hitNumber, Calc.DeterministicTables.HitstunDecay.Length - 1);
        long scaled = DeterministicMath.MulShift(baseTicks * Fixed.ONE, Calc.DeterministicTables.HitstunDecay[idx]);
        long floor = baseTicks * Fixed.ONE / 2;
        long t = scaled < floor ? floor : scaled;
        return (int)(t / Fixed.ONE);
    }

    /// 强制倒地（GDD §5.6）
    public static void ForceDown(SimWorld w, FighterStateData vic)
    {
        vic.UkemiIneffective = false;
        SetState(w, vic, FighterState.Down, 0);
        vic.DownTicks = 0;
        vic.DownCount++;
        vic.VelX = Fixed.Zero; vic.VelZ = Fixed.Zero; vic.VelY = Fixed.Zero;
        vic.PosY = Fixed.Zero;
        vic.FallDirIndex = 0;
        w.Events.Emit(new SimEvent { Kind = EventKind.ForcedDown, VictimId = vic.Id });
    }

    /// 背击判定（GDD §2.5.2: 命中来源与目标面朝方向夹角 >120°）——纯 int64 点积，零除法。
    /// u = victim − attacker（攻击行进方向）；背击 ⟺ victim→attacker 与面朝夹角 >120°
    /// ⟺ cos(attacker−victim, facing) < −1/2 ⟺ 2·(u·f) > |u|（dot/len 同 raw 域，比值无量纲）。
    public static bool IsBackstab(FighterStateData attacker, FighterStateData victim)
    {
        long ux = victim.PosX.Raw - attacker.PosX.Raw;
        long uz = victim.PosZ.Raw - attacker.PosZ.Raw;
        if (ux == 0 && uz == 0) return false;
        DeterministicMath.CordicCosSin(victim.HeadingQuantum, out var fx, out var fz);
        long dot = DeterministicMath.MulShift(ux, fx) + DeterministicMath.MulShift(uz, fz);
        long len = DeterministicMath.ISqrt(ux * ux + uz * uz);
        return 2 * dot > len;
    }

    /// 速度方向 → 8 向 DirIndex（0=+Z 顺时针 45° 步进）——受身方向判定粒度（ADR-0010 §2）
    public static byte DirIndexFromVel(long vx, long vz)
    {
        if (vx == 0 && vz == 0) return 0;
        long ax = vx < 0 ? -vx : vx, az = vz < 0 ? -vz : vz;
        // tan(22.5°) ≈ 414/1000；主轴判定阈值 |主| > 2.414×|副|
        bool xDominant = ax * 1000 > az * 2414;
        bool zDominant = az * 1000 > ax * 2414;
        if (zDominant) return vz > 0 ? (byte)0 : (byte)4;                       // +Z / −Z
        if (xDominant) return vx > 0 ? (byte)2 : (byte)6;                       // +X / −X
        if (vx > 0) return vz > 0 ? (byte)1 : (byte)3;                          // +X+Z / +X−Z
        return vz > 0 ? (byte)7 : (byte)5;                                       // −X+Z / −X−Z
    }

    private static void SetState(SimWorld w, FighterStateData f, FighterState s, int ticks = 0)
    {
        f.State = s;
        f.StateTicksRemaining = ticks;
    }

    private static void EmitWhiff(SimWorld w, int attackerId, ushort skillId, WhiffReason reason)
    {
        w.Events.Emit(new SimEvent
        {
            Kind = EventKind.Whiff, AttackerId = attackerId, SkillId = skillId,
            ReasonByte = (byte)reason,
        });
    }
}
