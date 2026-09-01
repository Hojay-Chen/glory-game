using System.Linq;
using System;
using System.Collections.Generic;
// PRODUCTION - Arena.Core
// ADR-0001 §3/ADR-0009: SimWorld——确定性战斗循环编排器
// Step(tick, commands) 是唯一入口。Sim 是 Tick 的纯函数（ADR-0001 §9）。
using System.Text;
using Arena.Core.Calc;
using Arena.Core.Collision;
using Arena.Core.Rng;

namespace Arena.Core.Sim;

public sealed class SimWorld
{
    public const long TICK_RATE = 60;
    public const long GRAVITY_PER_TICK = 22 * Fixed.ONE / TICK_RATE;  // 22 m/s² 量化
    public const long ARENA_HALF_W = 30 * Fixed.ONE;
    public const long ARENA_HALF_D = 42 * Fixed.ONE;
    public const long BOUNCE_FACTOR_NUM = 6, BOUNCE_FACTOR_DEN = 10;   // ×0.6
    public const long KB_VEL_MULT = 9;                                   // 击退初速 = 距离×9
    public const int MAX_WALL_ITERATIONS = 2;
    public const long FIGHTER_RADIUS = 29491;  // 0.45m × 65536 = 29491         // r=0.45m (白盒 0.45)
    public const long FIGHTER_HEIGHT = 98304;  // 1.5m × 65536   // 1.5m 站高

    public long Tick { get; private set; }
    public long MatchSeed { get; }
    public string DataVersionHash { get; }
    public EventLog Events { get; } = new();
    public List<FighterStateData> Fighters { get; } = new();
    public List<SkillExecution> ActiveSkills { get; } = new();
    public Rng.SimRng Rng { get; }

    // SkillDef 数据（Phase 3D 从 Compiler 输出注入；Phase 3A 用硬编码最小集）
    private readonly Dictionary<ushort, SkillRuntimeData> _skills = new();
    public record SkillRuntimeData(
        ushort SkillId, int StartupTicks, int ActiveTicks, int RecoveryTicks,
        int Hits, int HitInterval, double DamageMult, int HitstunTicks,
        double KnockbackM, double LaunchV, bool IsSweep, bool IsLaunch,
        bool HasHitRegion, byte HitRegion, long HitboxShapeR, long MpCost);

    public SimWorld(long matchSeed, string dataVersionHash)
    {
        MatchSeed = matchSeed;
        DataVersionHash = dataVersionHash;
        Rng = new SimRng(matchSeed);
    }

    public void AddSkill(SkillRuntimeData data) => _skills[data.SkillId] = data;
    public SkillRuntimeData? GetSkill(ushort id) => _skills.GetValueOrDefault(id);

    public void AddFighter(int id, string classId, Fixed x, Fixed z, long atk = 1100)
    {
        Fighters.Add(new FighterStateData
        {
            Id = id, ClassId = classId,
            PosX = x, PosY = Fixed.Zero, PosZ = z,
            HeadingQuantum = z.Raw > 0 ? 0 : 32768,  // 朝原点
            Atk = atk,
        });
    }

    // ---- ADR-0001 Step：确定性状态转移 ----
    public void Step(int tick, ReadOnlySpan<Command> commands)
    {
        Tick = tick;
        Events.BeginTick(tick);

        // ① 指令处理（FighterId 升序）
        ProcessCommands(commands);

        // ② Sim 主动推进（技能时间轴）
        AdvanceSkills();

        // ③ 运动积分 + 碰撞（IntegrateMove：统一路径）
        IntegrateAll();

        // ④ 状态机 Tick 结算
        TickStates();

        // ⑤ 事件冻结
        // (EventLog 自动记录)

        // 清理
        foreach (var f in Fighters)
        {
            if (f.Hp <= 0 && f.State != FighterState.Dead)
            {
                f.State = FighterState.Dead;
                Events.Emit(new SimEvent { Kind = EventKind.Died, AttackerId = f.Id, VictimId = f.Id });
            }
        }
    }

    // ---- 指令处理 ----
    private void ProcessCommands(ReadOnlySpan<Command> commands)
    {
        foreach (var cmd in commands)
        {
            var f = Fighters.FirstOrDefault(x => x.Id == GetFighterIdForCommand(cmd));
            if (f is null || f.State == FighterState.Dead) continue;
            if (f.State != FighterState.Normal) continue;

            switch (cmd.Kind)
            {
                case CmdKind.Skill: TryCastSkill(f, cmd); break;
                case CmdKind.Basic: TryCastSkill(f, cmd); break;
                case CmdKind.Move: HandleMove(f, cmd); break;
                case CmdKind.Jump: break; // Phase 3D
            }
        }
    }

    private int GetFighterIdForCommand(Command cmd) => cmd.TargetTick >= 0 ? cmd.TargetTick % Fighters.Count : 0;

    private void HandleMove(FighterStateData f, Command cmd)
    {
        // 8 向移动：DirIndex → 方向向量
        if (f.State != FighterState.Normal || f.ActiveSkillId != "") return;
        long speed = 6 * Fixed.ONE + Fixed.ONE / 3; // 6.3 m/s
        long dx = DirIndexToDX(cmd.DirIndex);
        long dz = DirIndexToDZ(cmd.DirIndex);
        f.VelX = Fixed.FromRaw(dx * speed / 100);
        f.VelZ = Fixed.FromRaw(dz * speed / 100);
        if (dx != 0 || dz != 0) { f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero; } // 移动简化
    }

    private static long DirIndexToDX(byte idx) => idx switch { 1 => Fixed.ONE, 7 => -Fixed.ONE, _ => 0 };
    private static long DirIndexToDZ(byte idx) => idx switch { 0 => Fixed.ONE, 4 => -Fixed.ONE, _ => 0 };

    // ---- 技能施放 ----
    private void TryCastSkill(FighterStateData f, Command cmd)
    {
        if (cmd.Kind != CmdKind.Skill && cmd.Kind != CmdKind.Basic) return;
        var skill = GetSkill(cmd.SkillId);
        if (skill is null) return;

        // CD 检查
        if (f.Cooldowns.TryGetValue(cmd.SkillId, out var cd) && cd > 0) return;
        // MP 检查
        if (f.Mp < skill.MpCost) return;

        // 设置 CD
        f.Cooldowns[cmd.SkillId] = skill.ActiveTicks + skill.RecoveryTicks + (long)(6 * Fixed.ONE / Fixed.ONE); // 简化
        f.Mp -= skill.MpCost;
        f.State = FighterState.Act;
        f.ActiveSkillId = $"SKILL_{cmd.SkillId}";
        f.ActivePhaseTick = 0;
        f.HitConfirmed = false;

        // 创建 SkillExecution
        var exec = new SkillExecution
        {
            SkillId = f.ActiveSkillId,
            OwnerId = f.Id,
            CastTick = (int)Tick,
            StartupTicks = skill.StartupTicks,
            ActiveTicks = skill.ActiveTicks,
            RecoveryTicks = skill.RecoveryTicks,
            CurrentTick = 0,
            Phase = 0,
            MpCost = skill.MpCost,
            IsBasicAttack = cmd.Kind == CmdKind.Basic,
        };

        // 生成 hitSchedule 对应的 ActiveHitbox
        for (int seg = 0; seg < skill.Hits; seg++)
        {
            exec.Hitboxes.Add(new ActiveHitbox
            {
                OwnerId = f.Id, SkillId = cmd.SkillId, SegmentIndex = (byte)seg,
                StartupTick = skill.StartupTicks,
                ActiveStart = skill.StartupTicks,
                ActiveEnd = skill.StartupTicks + skill.ActiveTicks,
                DamageMultRaw = (long)(skill.DamageMult * Fixed.ONE),
                HitstunTicks = skill.HitstunTicks,
                KnockbackRaw = Fixed.FromInt((long)(skill.KnockbackM * 1000)).Raw / 1000,
                LaunchVRaw = Fixed.FromRaw((long)(skill.LaunchV * Fixed.ONE / TICK_RATE)).Raw,
                IsSweep = skill.IsSweep, IsLaunch = skill.IsLaunch,
                HasHitRegion = skill.HasHitRegion, HitRegion = skill.HitRegion,
                HitboxShapeR = skill.HitboxShapeR,
            });
        }

        ActiveSkills.Add(exec);
        Events.Emit(new SimEvent
        {
            Kind = EventKind.SkillCast, AttackerId = f.Id, SkillId = cmd.SkillId,
            VictimId = f.Id, DamageRaw = skill.MpCost,
        });
    }

    // ---- 技能推进 ----
    private void AdvanceSkills()
    {
        for (int i = ActiveSkills.Count - 1; i >= 0; i--)
        {
            var exec = ActiveSkills[i];
            exec.CurrentTick++;
            var owner = Fighters.FirstOrDefault(f => f.Id == exec.OwnerId);
            if (owner is null) { ActiveSkills.RemoveAt(i); continue; }

            if (exec.IsExpired)
            {
                owner.State = FighterState.Normal;
                owner.ActiveSkillId = "";
                Events.Emit(new SimEvent { Kind = EventKind.ActEnded, AttackerId = owner.Id, VictimId = owner.Id });
                ActiveSkills.RemoveAt(i);
                continue;
            }

            // 相位更新
            if (exec.InActive && owner.State == FighterState.Act)
                exec.Phase = 1;
            else if (exec.InRecovery)
                exec.Phase = 2;

            // 命中判定（active 阶段，每段 hitSchedule 到点时）
            if (exec.InActive && !exec.IsHold)
            {
                var skill = GetSkillForExecution(exec);
                if (skill is not null) TryHit(exec, owner, skill);
            }
        }
    }

    private SkillRuntimeData? GetSkillForExecution(SkillExecution exec)
    {
        // 从 ActiveSkillId 反查——简化实现
        foreach (var kv in _skills)
        {
            if (exec.SkillId.Contains(kv.Key.ToString()) || exec.SkillId == $"SKILL_{kv.Key}")
                return kv.Value;
        }
        return null;
    }

    private void TryHit(SkillExecution exec, FighterStateData owner, SkillRuntimeData skill)
    {
        // 对每个 Hitbox 检查
        foreach (var hb in exec.Hitboxes)
        {
            // 检查当前 Tick 是否在命中窗口
            int tickInWindow = exec.CurrentTick - exec.StartupTicks;
            if (tickInWindow < 0 || tickInWindow >= skill.ActiveTicks) continue;

            // 对每个敌方 Fighter 做碰撞检测
            foreach (var target in Fighters)
            {
                if (target.Id == owner.Id || target.State == FighterState.Dead) continue;
                if (target.State == FighterState.Getup) continue;  // 起身无敌
                if (target.State == FighterState.Break) continue;   // 免控

                // 简化距离判定（Circle hitbox）
                long dx = target.PosX.Raw - owner.PosX.Raw;
                long dz = target.PosZ.Raw - owner.PosZ.Raw;
                long distSq = dx * dx + dz * dz;
                long range = skill.HitboxShapeR;
                if (distSq > range * range) continue;

                // 已命中检查（同 segment 去重）
                long hitKey = (long)target.Id << 32 | (long)Tick << 8 | hb.SegmentIndex;
                if (owner.HitTargets.Contains(hitKey)) continue;
                owner.HitTargets.Add(hitKey);

                // 命中！
                ResolveHit(owner, target, skill, hb, exec);
            }
        }
    }

    // ---- HitResolve（Phase 3C 核心）----
    private void ResolveHit(FighterStateData attacker, FighterStateData victim, SkillRuntimeData skill, ActiveHitbox hb, SkillExecution exec)
    {
        if (victim.State == FighterState.Down && hb.IsSweep == false) return;  // 倒地保护
        if (victim.State == FighterState.Dead) return;

        victim.HitstunCount++;
        int hn = victim.HitstunCount;

        // 伤害计算（ADR-0001 §2.5 + SPEC-0006 HitRegion）
        long mult = (long)(skill.DamageMult * Fixed.ONE);
        long dmg = DeterministicMath.MulShift(mult, attacker.Atk);
        dmg = DeterministicMath.MulShift(dmg, 3 * Fixed.ONE / 5);  // ×0.6 防御

        // HitRegion 修正（GDD §4.6 弱点头部 / 巴雷特头部×2）
        var region = HitRegion.Torso;
        if (skill.HasHitRegion) region = (HitRegion)skill.HitRegion;
        else if (victim.PosY.Raw > Fixed.FromInt(1).Raw) region = HitRegion.Head; // 空中目标近头

        if (region == HitRegion.Head && skill.HitRegion > 0)
            dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.Modifiers.WeakPointX150);
        else if (region == HitRegion.Head)
            dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.Modifiers.WeakPointX200);

        // 连段递减（ADR-0001 §2 伤害递减表）
        if (hn >= 7)
        {
            int idx = Math.Min(hn, Calc.DeterministicTables.DamageDecay.Length - 1);
            dmg = DeterministicMath.MulShift(dmg, Calc.DeterministicTables.DamageDecay[idx]);
        }

        victim.Hp -= dmg / Fixed.ONE;

        // 浮空 vs 击退 vs 倒地
        if (skill.LaunchV > 0)
        {
            if (victim.State == FighterState.Launch)
            {
                // 浮空刷新（×0.8^n，下限 3.0）
                victim.LaunchCount++;
                int idx = Math.Min(victim.LaunchCount, Calc.DeterministicTables.LaunchDecay.Length - 1);
                long newV = DeterministicMath.MulShift(Fixed.FromRaw((long)(skill.LaunchV * 1000)).Raw,
                    Calc.DeterministicTables.LaunchDecay[idx]);
                if (newV < 3 * Fixed.ONE) newV = 3 * Fixed.ONE;
                victim.VelY = Fixed.FromRaw(newV);
                Events.Emit(new SimEvent { Kind = EventKind.Launched, AttackerId = attacker.Id, VictimId = victim.Id, SkillId = 0 });
            }
            else
            {
                victim.LaunchCount = 0;
                victim.AirTime = 0;
                victim.VelY = Fixed.FromRaw((long)(skill.LaunchV * 1000));
                victim.State = FighterState.Launch;
                Events.Emit(new SimEvent { Kind = EventKind.Launched, AttackerId = attacker.Id, VictimId = victim.Id });
            }
        }
        else if (skill.KnockbackM > 0)
        {
            long hs = Math.Max(skill.HitstunTicks, 12);
            hs = DeterministicMath.MulShift(hs, Calc.DeterministicTables.HitstunDecay[Math.Min(hn, 64)]);
            hs = Math.Max(hs, 6);
            victim.State = FighterState.Hitstun;
            victim.StateTicksRemaining = (int)hs;
            // 击退方向
            long dirX = victim.PosX.Raw > attacker.PosX.Raw ? 1 : -1;
            victim.VelX = Fixed.FromRaw(dirX * (long)(skill.KnockbackM * 1000) * DeterministicMath.FRAC / 1000);
            Events.Emit(new SimEvent { Kind = EventKind.WallBounced, AttackerId = attacker.Id, VictimId = victim.Id });
        }
        else if (victim.State != FighterState.Launch && victim.State != FighterState.Down)
        {
            victim.State = FighterState.Hitstun;
            victim.StateTicksRemaining = (int)Math.Max(DeterministicMath.MulShift(skill.HitstunTicks, Calc.DeterministicTables.HitstunDecay[Math.Min(hn, 64)]), 6);
        }

        Events.Emit(new SimEvent
        {
            Kind = EventKind.Hit, AttackerId = attacker.Id, VictimId = victim.Id,
            SkillId = 0, SegmentIndex = hb.SegmentIndex,
            DamageRaw = dmg, HitNumber = hn,
            VictimStateBefore = (byte)victim.State,
            HitRegion = (byte)region,
            HitPointX = victim.PosX.Raw, HitPointY = victim.PosY.Raw + FIGHTER_HEIGHT / 2,
            HitPointZ = victim.PosZ.Raw,
            SweepFlag = hb.IsSweep, AirMod = victim.State == FighterState.Launch,
        });

        // 控制值挣脱
        if (victim.ControlValue >= 100 && victim.State != FighterState.Break)
        {
            victim.ControlValue = 0;
            victim.State = FighterState.Break;
            victim.StateTicksRemaining = 90;
            Events.Emit(new SimEvent { Kind = EventKind.BreakTriggered, VictimId = victim.Id });
        }
    }

    // ---- 运动积分（IntegrateMove 统一路径）----
    private void IntegrateAll()
    {
        foreach (var f in Fighters)
        {
            if (f.State == FighterState.Dead) continue;
            if (f.State == FighterState.Launch) continue;

            // 水平运动
            if (f.VelX.Raw != 0 || f.VelZ.Raw != 0)
            {
                f.PosX = Fixed.FromRaw(f.PosX.Raw + f.VelX.Raw / TICK_RATE);
                f.PosZ = Fixed.FromRaw(f.PosZ.Raw + f.VelZ.Raw / TICK_RATE);

                // 边界碰撞（矩形 arena）
                bool bounced = false;
                if (f.PosX.Raw > ARENA_HALF_W) { f.PosX = Fixed.FromRaw(ARENA_HALF_W); f.VelX = Fixed.FromRaw(-f.VelX.Raw * BOUNCE_FACTOR_NUM / BOUNCE_FACTOR_DEN); bounced = true; }
                if (f.PosX.Raw < -ARENA_HALF_W) { f.PosX = Fixed.FromRaw(-ARENA_HALF_W); f.VelX = Fixed.FromRaw(-f.VelX.Raw * BOUNCE_FACTOR_NUM / BOUNCE_FACTOR_DEN); bounced = true; }
                if (f.PosZ.Raw > ARENA_HALF_D) { f.PosZ = Fixed.FromRaw(ARENA_HALF_D); f.VelZ = Fixed.FromRaw(-f.VelZ.Raw * BOUNCE_FACTOR_NUM / BOUNCE_FACTOR_DEN); bounced = true; }
                if (f.PosZ.Raw < -ARENA_HALF_D) { f.PosZ = Fixed.FromRaw(-ARENA_HALF_D); f.VelZ = Fixed.FromRaw(-f.VelZ.Raw * BOUNCE_FACTOR_NUM / BOUNCE_FACTOR_DEN); bounced = true; }
                if (bounced)
                {
                    if (f.State == FighterState.Hitstun) f.StateTicksRemaining += 10;
                    Events.Emit(new SimEvent { Kind = EventKind.WallBounced, VictimId = f.Id });
                }

                // 摩擦
                f.VelX = Fixed.FromRaw(f.VelX.Raw * 85 / 100);
                f.VelZ = Fixed.FromRaw(f.VelZ.Raw * 85 / 100);
                if (Math.Abs(f.VelX.Raw) < Fixed.ONE / 20 && Math.Abs(f.VelZ.Raw) < Fixed.ONE / 20)
                {
                    f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero;
                }
            }

            // 垂直运动（Launch）
            if (f.State == FighterState.Launch)
            {
                f.AirTime++;
                if (f.AirTime >= 180) { f.ForcedFall = true; f.VelY = Fixed.FromRaw(-12 * Fixed.ONE); }
                f.VelY = Fixed.FromRaw(f.VelY.Raw - GRAVITY_PER_TICK);
                f.PosY = Fixed.FromRaw(f.PosY.Raw + f.VelY.Raw / TICK_RATE);
                if (f.PosY.Raw <= 0)
                {
                    f.PosY = Fixed.Zero; f.VelY = Fixed.Zero;
                    f.State = FighterState.Down;
                    f.DownTicks = 0;
                    f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero;
                    Events.Emit(new SimEvent { Kind = EventKind.Landed, VictimId = f.Id });
                }
            }
            else if (f.PosY.Raw > 0 && f.State != FighterState.Down)
            {
                // 地面约束
                f.VelY = Fixed.FromRaw(f.VelY.Raw - GRAVITY_PER_TICK);
                f.PosY = Fixed.FromRaw(f.PosY.Raw + f.VelY.Raw / TICK_RATE);
                if (f.PosY.Raw <= 0) { f.PosY = Fixed.Zero; f.VelY = Fixed.Zero; }
            }
        }
    }

    // ---- 状态机 Tick 结算 ----
    private void TickStates()
    {
        foreach (var f in Fighters)
        {
            if (f.ProtectTicks > 0) f.ProtectTicks--;
            if (f.State == FighterState.Dead) continue;

            switch (f.State)
            {
                case FighterState.Hitstun:
                    if (--f.StateTicksRemaining <= 0) { f.State = FighterState.Normal; ResetVictim(f); }
                    break;
                case FighterState.Down:
                    f.DownTicks++;
                    if (f.DownTicks >= 45)  // 普通倒地
                    {
                        f.State = FighterState.Getup;
                        f.StateTicksRemaining = 24;
                    }
                    break;
                case FighterState.Getup:
                    if (--f.StateTicksRemaining <= 0)
                    {
                        f.State = FighterState.Normal;
                        f.ProtectTicks = 60;
                        ResetVictim(f);
                    }
                    break;
                case FighterState.Break:
                    if (--f.StateTicksRemaining <= 0) f.State = FighterState.Normal;
                    break;
                case FighterState.Normal:
                    f.ControlValue = Math.Max(0, f.ControlValue - 20 / 1);
                    break;
            }
        }
    }

    private void ResetVictim(FighterStateData f)
    {
        f.HitstunCount = 0; f.LaunchCount = 0; f.AirTime = 0;
        f.ForcedFall = false; f.NoUkemi = false;
    }

    private const FighterState LAUNCH_STATE = FighterState.Launch;
}

// 扩展 FighterStateData 辅助
public static class FighterExtensions
{
    public static long DistSqTo(this FighterStateData a, FighterStateData b)
    {
        long dx = a.PosX.Raw - b.PosX.Raw;
        long dz = a.PosZ.Raw - b.PosZ.Raw;
        return dx * dx + dz * dz;
    }
}
