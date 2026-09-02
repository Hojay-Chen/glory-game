using System;
using System.Collections.Generic;
using Arena.Core.Calc;
using Arena.Core.Collision;
using Arena.Core.Rng;
// PRODUCTION - Arena.Core
// ADR-0001 §3.2/ADR-0009: SimWorld——确定性战斗循环编排器（Phase 4 SPEC 合规重建）。
// Step(tick, commands) 唯一入口；Sim 是 Tick 的纯函数。
// 结算总序（ADR-0001 §3.2）:
//   ① 指令处理（per-Fighter CmdStream，FighterId 升序；GDD §2.3.2 优先级）
//   ② Sim 主动推进（技能时间轴 hitbox spawn + 投射物推进，Uid 升序）
//   ③ 命中结算（ContactList 按 SPEC-0005 §6 总序 → HitResolve——零几何）
//   ④ 运动积分（IntegrateMove 统一路径 + 垂直物理 + L2 软推挤）
//   ⑤ 状态/闸门/资源 Tick（FighterId 升序）
//   ⑥ 死亡判定
namespace Arena.Core.Sim;

public sealed partial class SimWorld
{
    public const string ProtocolVersion = "sim-event-v2";

    public long Tick { get; private set; }
    public long MatchSeed { get; }
    public string DataVersionHash { get; }
    public EventLog Events { get; } = new();
    public Rng.SimRng Rng { get; }
    public CollisionSystem Collision { get; }

    // 实体容器（全部按 Id/Uid 升序维护——ADR-0001 §3.1 容器纪律）
    public List<FighterStateData> Fighters { get; } = new();
    public List<SkillExecution> Executions { get; } = new();
    public List<ActiveHitbox> Hitboxes { get; } = new();
    public List<ProjectileState> Projectiles { get; } = new();
    public readonly List<PendingContact> PendingContacts = new();

    private readonly Dictionary<ushort, SkillRuntimeData> _skills = new();
    private readonly Dictionary<int, List<Command>> _cmdBuf = new();
    private readonly Dictionary<int, List<(Command cmd, int expiry)>> _inputBuffers = new();
    private readonly Dictionary<int, byte> _teams = new();
    private int _nextExecUid = 1;
    private int _nextHitboxUid = 1;
    private int _nextProjUid = 1;

    public SimWorld(long matchSeed, string dataVersionHash)
    {
        MatchSeed = matchSeed;
        DataVersionHash = dataVersionHash;
        Rng = new SimRng(matchSeed);
        Collision = new CollisionSystem(RuntimeConstants.GRID_CELL_SIZE);
    }

    // ---- 装配（比赛开始前一次性；Step 后只读） ----

    public void AddSkill(SkillRuntimeData def) => _skills[def.RuntimeId] = def;
    public SkillRuntimeData? GetSkill(ushort id) => _skills.GetValueOrDefault(id);

    public void AddTerrain(TerrainBody body) => Collision.AddTerrain(body);
    public void SealWorld()
    {
        Collision.SealTerrain();
        Fighters.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var f in Fighters) _teams[f.Id] = f.Team;
    }

    public void AddFighter(int id, string classId, Fixed x, Fixed z, byte team = 0, long atk = 1100, long def = 800)
    {
        Fighters.Add(new FighterStateData
        {
            Id = id, ClassId = classId, Team = team,
            PosX = x, PosY = Fixed.Zero, PosZ = z,
            HeadingQuantum = z.Raw > 0 ? 32768 : 0,   // 朝向对方半场（0=+Z；z<0 面向 +Z）
            Atk = atk, Def = def,
        });
        _teams[id] = team;   // 阵营注册随装配即时生效（SealWorld 前后均可 AddFighter）
    }

    // ---- Step（唯一入口） ----

    public void Step(int tick, ReadOnlySpan<Command> commands)
    {
        Tick = tick;
        Events.BeginTick(tick);
        PendingContacts.Clear();

        // ① 指令处理（FighterId 升序）
        ProcessCommands(commands);

        // ② Sim 主动推进（executions Uid 升序 → hitbox spawn；projectiles Uid 升序）
        AdvanceExecutions();
        DrainBuffers();
        for (int i = 0; i < Projectiles.Count; i++)
            ProjectileSystem.Advance(this, Projectiles[i], tick, Fighters);

        // ③ 命中结算（ContactList 总序 → HitResolve）
        SweepCombat();
        ResolveContacts();

        // ④ 运动积分（IntegrateMove 统一路径；L2 软推挤）
        IntegrateFighters();

        // ⑤ 状态/闸门/资源 Tick（FighterId 升序）
        TickFighters();

        // ⑤' 签名钩子（ADR-0001 §3.2 ⑤——状态 Tick 后、死亡判定前）
        DispatchSignatures();

        // ⑥ 死亡判定
        for (int i = 0; i < Fighters.Count; i++)
        {
            var f = Fighters[i];
            if (f.Hp <= 0 && f.State != FighterState.Dead)
            {
                f.State = FighterState.Dead;
                if (f.ActiveSkillUid != 0) TerminateExecutionById(f.ActiveSkillUid, cancelled: false);
                Events.Emit(new SimEvent { Kind = EventKind.Died, VictimId = f.Id });
            }
            if (f.State == FighterState.Dead)
            {
                // 死亡的抓取者释放被擒者（GDD §7.2）
                foreach (var v in Fighters)
                {
                    if (v.GrabbedBy == f.Id)
                    {
                        v.GrabbedBy = -1;
                        v.GrabThrowSkill = 0;
                        if (v.State == FighterState.Grabbed) v.State = FighterState.Normal;
                        Events.Emit(new SimEvent { Kind = EventKind.GrabReleased, VictimId = v.Id, AttackerId = f.Id });
                    }
                }
            }
        }
    }

    // ---- ① 指令处理 ----

    private void ProcessCommands(ReadOnlySpan<Command> commands)
    {
        // per-Fighter 分组（FighterId 升序——Sort 稳定化）
        _cmdBuf.Clear();
        for (int i = 0; i < commands.Length; i++)
        {
            var cmd = commands[i];
            if (!_cmdBuf.TryGetValue(cmd.FighterId, out var list))
                _cmdBuf[cmd.FighterId] = list = new List<Command>();
            list.Add(cmd);
        }

        foreach (var f in Fighters)
        {
            if (f.State == FighterState.Dead) continue;
            if (!_cmdBuf.TryGetValue(f.Id, out var cmds)) continue;
            // GDD §2.3.2 优先级（高覆盖低）——稳定排序保持同优先级到达序
            cmds.Sort((a, b) => CommandPriority.Of(a.Kind).CompareTo(CommandPriority.Of(b.Kind)));
            for (int i = 0; i < cmds.Count; i++)
                Dispatch(f, cmds[i]);
        }
    }

    private void Dispatch(FighterStateData f, Command cmd)
    {
        switch (cmd.Kind)
        {
            case CmdKind.Roll:
                TryUkemi(f, cmd);
                TryStandingRoll(f, cmd);
                break;
            case CmdKind.ForceCancel:
                TryForceCancel(f);
                break;
            case CmdKind.Skill:
                TryCastSkill(f, cmd, fromBuffer: false);
                break;
            case CmdKind.Basic:
                TryCastBasic(f, cmd, fromBuffer: false);
                break;
            case CmdKind.Jump:
                TryJump(f, cmd, fromBuffer: false);
                break;
            case CmdKind.Move:
                HandleMove(f, cmd);
                break;
            case CmdKind.Steer:
                TrySteer(f, cmd);
                break;
        }
    }

    /// Steer（SPEC-0001: controlled 生效窗内朝向饱和步进，≤120°/s）
    private void TrySteer(FighterStateData f, Command cmd)
    {
        if (f.State != FighterState.Act || f.ActiveSkillUid == 0) return;
        var exec = GetExecution(f.ActiveSkillUid);
        if (exec?.Def is not { } def || def.SteerRateDegPerSec <= 0) return;
        // maxStep = rate × 65536 / 360 / 60（每 Tick 最大量子步）
        long maxStep = DeterministicMath.DivRoundHalfEven(
            (long)def.SteerRateDegPerSec * Fixed.ONE, 360 * RuntimeConstants.TICK_RATE);
        int diff = (int)(((cmd.AimQuantum - f.HeadingQuantum) % 65536 + 65536) % 65536);
        if (diff > 32768) diff -= 65536;   // 最短弧带符号差（SPEC-0001 §2）
        long step = Math.Clamp(diff, -maxStep, maxStep);
        f.HeadingQuantum = ((f.HeadingQuantum + step) % 65536 + 65536) % 65536;
    }

    /// 站立翻滚（GDD §10.1: 30f/3m/无敌 4–18f；耐力 25——Down 态走受身路径）
    private void TryStandingRoll(FighterStateData f, Command cmd)
    {
        if (f.State == FighterState.Down || f.State == FighterState.Roll) return;
        if (f.State == FighterState.Act && f.ActiveSkillUid != 0)
        {
            // hold 姿态可翻滚释放；其他技能不可翻滚取消（无【滚取消】数据）
            var exec = GetExecution(f.ActiveSkillUid);
            if (exec?.Def is not { } def || !def.IsHold) return;
            TerminateExecution(exec, cancelled: false);
        }
        else if (f.State != FighterState.Normal)
        {
            return;
        }
        if (f.Stamina < RuntimeConstants.STAMINA_ROLL_COST) return;   // 耗尽 → 翻滚不可用（§10.2）
        if (!StatusSystem.CanMove(f)) return;
        if (cmd.DirIndex > 7) return;
        f.Stamina -= RuntimeConstants.STAMINA_ROLL_COST;
        f.State = FighterState.Roll;
        f.RollTicksRemaining = RuntimeConstants.ROLL_TICKS;
        f.RollDirIndex = cmd.DirIndex;
        f.RollInvulnArmed = false;
        f.ActiveSkillUid = 0;
    }

    /// 8 向移动（GDD §3.2: 奔跑 6.0 m/s；攻击制动；状态门控）
    private void HandleMove(FighterStateData f, Command cmd)
    {
        // hold 姿态（格挡等）允许移动（GDD §6.2: 格挡移速 −60%）；其余 Act 制动
        bool guardWalk = false;
        if (f.State == FighterState.Act && f.ActiveSkillUid != 0)
        {
            var holdExec = GetExecution(f.ActiveSkillUid);
            if (holdExec?.Def is { } hd && hd.IsHold) guardWalk = true;
        }
        if (f.State != FighterState.Normal && !guardWalk) return;   // Act 制动
        if (!StatusSystem.CanMove(f)) return;
        if (f.GrabbedBy >= 0) return;
        if (cmd.DirIndex > 7) return;
        long speed = RuntimeConstants.RUN_MPS * Fixed.ONE;
        speed = DeterministicMath.MulShift(speed, StatusSystem.MoveSpeedMultQ(f));
        if (guardWalk) speed = DeterministicMath.MulShift(speed, DeterministicMath.DivRoundHalfEven(40 * Fixed.ONE, 100));
        if (cmd.DirIndex == 0 && cmd.AimQuantum == 0 && cmd.TargetTick == int.MinValue)
        { }   // 占位：v1 无「停走」区分——DirIndex=0 表示 +Z；停止 = 不发 Move
        (long dx, long dz) = DirVector(cmd.DirIndex);
        f.VelX = Fixed.FromRaw(DeterministicMath.MulShift(dx, speed));
        f.VelZ = Fixed.FromRaw(DeterministicMath.MulShift(dz, speed));
        // 朝向 = 移动方向（SPEC-0001 量化: DirIndex × 45°）
        f.HeadingQuantum = cmd.DirIndex * 8192;
    }

    internal static (long, long) DirVector(byte dirIndex) => dirIndex switch
    {
        0 => (0, Fixed.ONE),                    // +Z
        1 => (72405, 72405),                    // +X+Z（0.7071）
        2 => (Fixed.ONE, 0),                    // +X
        3 => (72405, -72405),                   // +X−Z
        4 => (0, -Fixed.ONE),                   // −Z
        5 => (-72405, -72405),                  // −X−Z
        6 => (-Fixed.ONE, 0),                   // −X
        7 => (-72405, 72405),                   // −X+Z
        _ => (0, 0),
    };

    // ---- 技能施放（含取消窗判定 GDD §8.2） ----

    private bool TryCastSkill(FighterStateData f, Command cmd, bool fromBuffer)
    {
        var def = GetSkill(cmd.SkillId);
        if (def is null) return false;
        if (!StatusSystem.CanAct(f) || !StatusSystem.CanCastSkill(f)) return false;
        if (f.State == FighterState.Dead) return false;

        // Act 中: 取消窗判定（GDD §8.2）——资源/CD 预检在终止当前技能之前（原子切换）
        if (f.State == FighterState.Act && f.ActiveSkillUid != 0)
        {
            var exec = GetExecution(f.ActiveSkillUid);
            if (exec is not null && !IsCancelable(f, exec, def))
            {
                if (!fromBuffer) BufferInput(f, cmd);   // 缓冲排水尝试不再重复入队
                return false;
            }
            if (!CanStartExecution(f, def)) return false;   // CD/MP 不足 → 取消不成立，当前技能不受影响
            if (exec is not null) TerminateExecution(exec, cancelled: true);
        }
        else if (f.State != FighterState.Normal)
        {
            return false;   // 受击不可出招（铁则——不缓冲）
        }

        return StartExecution(f, def, cmd);
    }

    /// 施放前置资源检查（MP/CD——不消耗）
    private bool CanStartExecution(FighterStateData f, SkillRuntimeData def)
    {
        if (def.Type.StartsWith("basic")) return true;
        if (f.Mp < def.MpCost) return false;
        if (f.Cooldowns.TryGetValue(def.RuntimeId, out var cd) && cd > 0) return false;
        return true;
    }

    /// 普攻（链式：生效帧后可取消下一段/技能，GDD §4.2）
    private bool TryCastBasic(FighterStateData f, Command cmd, bool fromBuffer)
    {
        if (!StatusSystem.CanAct(f)) return false;
        if (f.State == FighterState.Act && f.ActiveSkillUid != 0)
        {
            var exec = GetExecution(f.ActiveSkillUid);
            if (exec is null) return false;
            if (exec.Def is { } gdef && gdef.Guard is not null) return false;   // 格挡姿态无法普攻（§6.2）
            if (!exec.IsBasic)
            {
                // 技能执行中不可普攻取消——缓冲
                BufferInput(f, cmd);
                return false;
            }
            // 普攻段间: 生效帧后可衔接下一段（缓冲 18f 由缓冲机制承载）
            if (exec.Def is null || exec.CurrentOffset < exec.Def.StartupTicks || exec.Def.ChainNext == 0)
            {
                if (!fromBuffer) BufferInput(f, cmd);
                return false;
            }
            var nextDef = GetSkill(exec.Def.ChainNext);
            if (nextDef is null) return false;
            TerminateExecution(exec, cancelled: false);
            return StartExecution(f, nextDef, cmd);
        }
        if (f.State != FighterState.Normal) return false;

        // 链首: 职业第一条普攻（BAS 链 N=1）
        var first = GetFirstBasic(f.ClassId);
        if (first is null) return false;
        return StartExecution(f, first, cmd);
    }

    private SkillRuntimeData? GetFirstBasic(string classId)
    {
        // Catalog 遍历（确定性: RuntimeId 升序）——v1 直接线性扫描（487 规模可接受，索引化登记报告）
        for (ushort id = 1; ; id++)
        {
            var def = GetSkill(id);
            if (def is null) break;
            if (def.ClassId == classId && def.Type == "basic" && def.ChainN == 1) return def;
        }
        return null;
    }

    private bool IsCancelable(FighterStateData f, SkillExecution exec, SkillRuntimeData next)
    {
        if (exec.Def is null) return false;
        // 完美格挡/反击免费取消窗（GDD §6.3/§6.6: 可取消任意技能）
        if (f.CounterWindowTicks > 0) return true;
        // hold 姿态（格挡等）: 姿态建立后即可切换释放（GDD §6.2 格挡姿态衔接）
        if (exec.Def.IsHold) return exec.CurrentOffset >= exec.Def.StartupTicks;
        if (exec.IsBasic)
        {
            // 普攻→技能: 自生效帧后 4f 起（GDD §4.2）
            return exec.CurrentOffset >= exec.Def.StartupTicks + RuntimeConstants.BASIC_CANCEL_TO_SKILL_TICKS;
        }
        // 技能→技能: 命中确认 + 后摇取消窗 + 档位递进（GDD §8.2）
        if (!exec.HitConfirmed) return false;
        if (exec.CurrentOffset < exec.RecoveryStartOffset) return false;
        if (exec.Def.CancelMinTier == 255) return false;
        return next.Tier >= exec.Def.CancelMinTier;
    }

    private bool StartExecution(FighterStateData f, SkillRuntimeData def, Command cmd)
    {
        // MP/CD 消耗（普攻无消耗；调用方已 CanStartExecution 预检——此处直接消耗）
        if (!def.Type.StartsWith("basic"))
        {
            f.Mp -= def.MpCost;
            f.Cooldowns[def.RuntimeId] = def.CooldownTicks;
        }

        var exec = new SkillExecution
        {
            Uid = _nextExecUid++,
            SkillRuntimeId = def.RuntimeId,
            OwnerId = f.Id,
            CastTick = (int)Tick,
            Def = def,
            SegmentVictims = def.HitSchedule.Length > 0 ? new HashSet<int>[def.HitSchedule.Length] : Array.Empty<HashSet<int>>(),
        };
        for (int i = 0; i < exec.SegmentVictims.Length; i++) exec.SegmentVictims[i] = new HashSet<int>();
        Executions.Add(exec);
        f.State = FighterState.Act;
        f.ActiveSkillUid = exec.Uid;
        // 格挡姿态激活: 盾值置满（GDD §6.2 盾值1500；破碎/结束后 8s 恢复）
        if (def.Guard is { } g)
        {
            f.ShieldMax = g.ShieldMax;
            f.Shield = g.ShieldMax;
            f.ShieldRegenTicks = 0;
        }
        f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero;   // 攻击制动（GDD §3.2）
        // 施放朝向: Steer/Aim 量化（SPEC-0001）——Skill 指令的 AimQuantum 直接锁定朝向
        if (cmd.AimQuantum != 0 || cmd.Kind == CmdKind.Skill) f.HeadingQuantum = cmd.AimQuantum;

        // 投射物技: active 起点发射（hitSchedule[0] 偏移）
        if (def.IsProjectile)
        {
            _pendingProjectileSpawns.Add((exec, def.StartupTicks));
        }

        Events.Emit(new SimEvent
        {
            Kind = EventKind.SkillCast, AttackerId = f.Id, SkillId = def.RuntimeId,
            ValueRaw = def.MpCost,
        });
        return true;
    }

    private readonly List<(SkillExecution exec, int atOffset)> _pendingProjectileSpawns = new();

    private bool TryJump(FighterStateData f, Command cmd, bool fromBuffer)
    {
        if (f.State == FighterState.Act && f.ActiveSkillUid != 0)
        {
            // 跳跃取消（GDD §8.2: 标注【跳取消】技能命中后）
            var exec = GetExecution(f.ActiveSkillUid);
            if (exec is null || exec.Def is null || !exec.Def.JumpCancel || !exec.HitConfirmed ||
                exec.CurrentOffset < exec.RecoveryStartOffset)
            {
                if (!fromBuffer) BufferInput(f, cmd);
                return false;
            }
            TerminateExecution(exec, cancelled: true);
        }
        else if (f.State != FighterState.Normal)
        {
            return false;
        }
        if (f.PosY.Raw > 0 || !StatusSystem.CanAct(f)) return false;
        f.VelY = Fixed.FromRaw(RuntimeConstants.JUMP_VELOCITY_MPS * Fixed.ONE);
        return true;
    }

    /// 受身（GDD §5.6/§10.3: 倒地 0–20f（连续倒地 30f）+ 方向 ≤90°）
    private void TryUkemi(FighterStateData f, Command cmd)
    {
        if (f.State != FighterState.Down) return;
        if (f.UkemiIneffective) return;   // 【受身无效】（GDD §5.6）
        if (f.Stamina < RuntimeConstants.STAMINA_UKEMI_COST) return;   // 耐力耗尽 → 受身不可用（§10.2）
        int window = f.DownCount >= 2 ? RuntimeConstants.UKEMI_WINDOW_EXTENDED : RuntimeConstants.UKEMI_WINDOW_TICKS;
        if (f.DownTicks > window) return;
        // 方向判定: 输入方向 vs 摔倒方向 ≤90°（DirIndex 环距 ≤1）
        int diff = Math.Abs(cmd.DirIndex - f.FallDirIndex);
        diff = Math.Min(diff, 8 - diff);
        if (diff > 1) return;
        // 受身: 立即弹起 → Getup 24f 全程无敌（耐力 15，§10.2）
        f.Stamina -= RuntimeConstants.STAMINA_UKEMI_COST;
        f.State = FighterState.Getup;
        f.StateTicksRemaining = RuntimeConstants.GETUP_TICKS;
        f.InvulnTicks = RuntimeConstants.GETUP_TICKS;
        f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero;
        Events.Emit(new SimEvent { Kind = EventKind.Ukemi, VictimId = f.Id });
    }

    /// 强制中断（GDD §10.4: 后摇立即结束，60 MP + CD 4s）
    private void TryForceCancel(FighterStateData f)
    {
        if (f.State != FighterState.Act || f.ActiveSkillUid == 0) return;
        var exec = GetExecution(f.ActiveSkillUid);
        if (exec is null || !exec.InRecovery) return;   // 仅后摇（不含前摇/生效）
        if (f.Mp < RuntimeConstants.FORCE_CANCEL_MP_COST) return;
        if (f.Cooldowns.TryGetValue(0, out var fcd) && fcd > 0) return;
        f.Mp -= RuntimeConstants.FORCE_CANCEL_MP_COST;
        f.Cooldowns[0] = RuntimeConstants.FORCE_CANCEL_CD_TICKS;
        TerminateExecution(exec, cancelled: true);
    }

    // ---- 输入缓冲（ADR-0010 §2: Sim 裁决，12f 窗口） ----

    private void BufferInput(FighterStateData f, Command cmd)
    {
        if (!_inputBuffers.TryGetValue(f.Id, out var buf))
            _inputBuffers[f.Id] = buf = new List<(Command, int)>();
        if (buf.Count >= RuntimeConstants.MAX_BUFFERED_COMMANDS) buf.RemoveAt(0);   // 满则丢最旧
        buf.Add((cmd, (int)Tick + RuntimeConstants.INPUT_BUFFER_TICKS));
    }

    /// 缓冲排水: 最早合法指令在恢复可操作瞬间执行（GDD §2.3.1）
    private void DrainBuffers()
    {
        foreach (var f in Fighters)
        {
            if (!_inputBuffers.TryGetValue(f.Id, out var buf) || buf.Count == 0) continue;
            if (f.State == FighterState.Dead) { buf.Clear(); continue; }
            for (int i = 0; i < buf.Count; i++)
            {
                var (cmd, expiry) = buf[i];
                if (expiry < Tick) { buf.RemoveAt(i); i--; continue; }
                bool ok = cmd.Kind switch
                {
                    CmdKind.Skill => TryCastSkill(f, cmd, fromBuffer: true),
                    CmdKind.Basic => TryCastBasic(f, cmd, fromBuffer: true),
                    CmdKind.Jump => TryJump(f, cmd, fromBuffer: true),
                    _ => false,
                };
                if (ok)
                {
                    buf.RemoveAt(i);
                    break;   // 每 Tick 至多消费一条
                }
            }
        }
    }

    // ---- ② 技能时间轴推进 ----

    private void AdvanceExecutions()
    {
        _pendingProjectileSpawns.Clear();
        for (int i = Executions.Count - 1; i >= 0; i--)
        {
            var exec = Executions[i];
            if (exec.Terminated) { Executions.RemoveAt(i); continue; }
            var owner = GetFighter(exec.OwnerId);
            if (owner is null || owner.ActiveSkillUid != exec.Uid)
            {
                // Owner 已被打断/切换（防御——正常路径走 TerminateExecution）
                Executions.RemoveAt(i);
                continue;
            }
            exec.CurrentOffset++;
            var def = exec.Def!;

            // hitSchedule → hitbox spawn（P-2 编译期预计算偏移）
            while (exec.SpawnedSegments < def.HitSchedule.Length &&
                   exec.CurrentOffset >= SkillTimeline.SegmentWindow(def, exec.SpawnedSegments).start)
            {
                SpawnSegmentHitbox(exec, def, owner, exec.SpawnedSegments);
                exec.SpawnedSegments++;
            }

            // 投射物发射点
            if (def.IsProjectile && exec.CurrentOffset == def.StartupTicks)
            {
                ProjectileSystem.Spawn(this, owner, def, owner.HeadingQuantum, (int)Tick);
            }

            // 抓取投技结算（GDD §7.2: 抓取后有专属投技演出——执行结束帧投出）
            if (def.IsGrab && !exec.Terminated && exec.CurrentOffset >= def.StartupTicks + def.ActiveTicks)
            {
                ResolveGrabThrow(exec, def, owner);
            }

            // 结束（hold 姿态不自然结束——由取消/切换释放；其余恢复完毕即结束）
            if (!def.IsHold && exec.CurrentOffset >= exec.TotalTicks)
            {
                EndExecution(exec, owner);
                Executions.RemoveAt(i);
            }
        }
        // 过期 hitbox 回收
        for (int i = Hitboxes.Count - 1; i >= 0; i--)
            if (Tick >= Hitboxes[i].ExpireTick) Hitboxes.RemoveAt(i);
    }

    private void SpawnSegmentHitbox(SkillExecution exec, SkillRuntimeData def, FighterStateData owner, int segment)
    {
        if (def.Geo.Kind == GeoKind.None) return;
        var (start, end) = SkillTimeline.SegmentWindow(def, segment);
        var hb = new ActiveHitbox
        {
            Uid = _nextHitboxUid++,
            OwnerId = owner.Id,
            Def = def,
            SegmentIndex = (byte)segment,
            SpawnTick = (int)Tick,
            ExpireTick = (int)Tick + Math.Max(end - start, 1),
            AnchorX = owner.PosX.Raw,
            AnchorZ = owner.PosZ.Raw,
            AnchorHeading = owner.HeadingQuantum,
            AnchorVelX = owner.VelX.Raw,
            AnchorVelZ = owner.VelZ.Raw,
        };
        exec.SegmentVictims![segment] = hb.HitVictims;
        Hitboxes.Add(hb);
    }

    private void EndExecution(SkillExecution exec, FighterStateData owner)
    {
        if (!exec.HitConfirmed)
        {
            // 空技能 = 暴露破绽（GDD §4.4）——Whiff 事件（取消资格缺失的可观测形式）
            Events.Emit(new SimEvent { Kind = EventKind.Whiff, AttackerId = owner.Id, SkillId = exec.SkillRuntimeId, ReasonByte = (byte)WhiffReason.Range });
        }
        owner.State = FighterState.Normal;
        owner.ActiveSkillUid = 0;
        Events.Emit(new SimEvent { Kind = EventKind.ActEnded, AttackerId = owner.Id, SkillId = exec.SkillRuntimeId });
    }

    private void TerminateExecution(SkillExecution exec, bool cancelled) =>
        TerminateExecutionById(exec.Uid, cancelled);

    /// 终止执行（取消/打断/破盾）。抓取执行终止 → 释放被擒者（无投技结算）。
    internal void TerminateExecutionById(int uid, bool cancelled, bool interrupted = false)
    {
        var exec = GetExecution(uid);
        if (exec is null) return;
        var owner = GetFighter(exec.OwnerId);
        exec.Terminated = true;
        if (owner is not null && owner.ActiveSkillUid == exec.Uid)
        {
            if (owner.State == FighterState.Act) owner.State = FighterState.Normal;   // 打断路径: 状态已属受击
            owner.ActiveSkillUid = 0;
        }
        if (exec.Def is { } def && def.IsGrab)
        {
            // 释放全部被擒者（GDD §7.2 GrabReleased）
            foreach (var f in Fighters)
            {
                if (f.GrabbedBy != exec.OwnerId) continue;
                f.GrabbedBy = -1;
                f.GrabThrowSkill = 0;
                if (f.State == FighterState.Grabbed) { f.State = FighterState.Normal; f.StateTicksRemaining = 0; }
                Events.Emit(new SimEvent { Kind = EventKind.GrabReleased, VictimId = f.Id, AttackerId = exec.OwnerId });
            }
        }
        if (cancelled)
            Events.Emit(new SimEvent { Kind = EventKind.Cancelled, AttackerId = exec.OwnerId, SkillId = exec.SkillRuntimeId });
        else if (interrupted)
            Events.Emit(new SimEvent { Kind = EventKind.Interrupted, AttackerId = exec.OwnerId, SkillId = exec.SkillRuntimeId });
    }

    /// 抓取投技结算（执行结束帧: 伤害 + 落点反应 + 释放——GDD §7.2）
    private void ResolveGrabThrow(SkillExecution exec, SkillRuntimeData def, FighterStateData owner)
    {
        foreach (var vic in Fighters)
        {
            if (vic.GrabbedBy != owner.Id || vic.GrabThrowSkill != def.RuntimeId) continue;
            HitResolve.ResolveThrow(new HitResolve.HitContext
            {
                World = this,
                Attacker = owner,
                Victim = vic,
                Def = def,
                SegmentIndex = 0,
                HitRegion = (byte)HitRegion.Torso,
                HitPointX = vic.PosX.Raw, HitPointY = vic.PosY.Raw + RuntimeConstants.TORSO_TOP / 2, HitPointZ = vic.PosZ.Raw,
                HitNormalX = 0, HitNormalZ = 0,
            });
            vic.GrabbedBy = -1;
            vic.GrabThrowSkill = 0;
            Events.Emit(new SimEvent { Kind = EventKind.GrabReleased, VictimId = vic.Id, AttackerId = owner.Id });
        }
    }

    // ---- ③ 命中结算（SPEC-0005 §6 总序 → HitResolve 零几何） ----

    private void SweepCombat()
    {
        // 主动 hitbox × 敌方 Fighter（相对扫掠，PA-7）
        foreach (var hb in Hitboxes)   // Hitboxes 按 Uid 升序追加
        {
            if (Tick < hb.SpawnTick || Tick >= hb.ExpireTick) continue;
            var def = hb.Def;
            var owner = GetFighter(hb.OwnerId);
            if (owner is not { } ownerN || ownerN.State == FighterState.Dead) continue;
            var own = ownerN;

            var region = BuildHitboxRegion(def.Geo, hb.AnchorX, hb.AnchorZ, hb.AnchorHeading);
            long relBaseX = hb.AnchorVelX, relBaseZ = hb.AnchorVelZ;

            foreach (var vic in Fighters)   // Id 升序（SPEC-0005 §6.2 多目标 victimId 序）
            {
                if (vic.Id == hb.OwnerId || vic.State == FighterState.Dead) continue;
                if (SameTeam(hb.OwnerId, vic.Id)) continue;
                if (hb.HitVictims.Contains(vic.Id)) continue;
                if (vic.State == FighterState.Break) continue;   // 免控≠免伤，但 Break 源自挣脱保护窗

                // PA-7 相对扫掠: mover = victim 体圆，位移 = victim 位移 − owner 位移
                long dispX = DeterministicMath.DivRoundHalfEven(vic.VelX.Raw, RuntimeConstants.TICK_RATE) - DeterministicMath.DivRoundHalfEven(relBaseX, RuntimeConstants.TICK_RATE);
                long dispZ = DeterministicMath.DivRoundHalfEven(vic.VelZ.Raw, RuntimeConstants.TICK_RATE) - DeterministicMath.DivRoundHalfEven(relBaseZ, RuntimeConstants.TICK_RATE);
                if (!SweepSolver.SweepRegion(region, vic.PosX.Raw, vic.PosZ.Raw, dispX, dispZ,
                        RuntimeConstants.FIGHTER_RADIUS, out long toi, out _, out long nx, out long nz))
                    continue;

                // 垂直门控: hitbox 高度带（绝对 = Owner PosY + 相对带）× victim 体带
                long bandLo = own.PosY.Raw + def.Geo.BandLow;
                long bandHi = own.PosY.Raw + def.Geo.BandHigh;
                long vicLo = vic.PosY.Raw, vicHi = vic.PosY.Raw + (vic.State == FighterState.Down ? RuntimeConstants.DOWN_TORSO_TOP : RuntimeConstants.FIGHTER_HEIGHT);
                if (bandHi < vicLo || bandLo > vicHi) continue;   // 高度带不相交 → 无命中

                // 倒地保护: 仅【扫地】可打（几何接触后资格否决 → Whiff）
                if (vic.State == FighterState.Down && !def.Sweep)
                {
                    Events.Emit(new SimEvent { Kind = EventKind.Whiff, AttackerId = hb.OwnerId, SkillId = def.RuntimeId, ReasonByte = (byte)WhiffReason.DownProtected });
                    hb.HitVictims.Add(vic.Id);
                    continue;
                }

                // 部位选取（PA-H2: Head priority 20 > Torso 10——几何精判）
                byte regionSel = SelectHitRegion(def, region, hb, vic, bandLo, bandHi);
                (long px, long py, long pz) = ContactPoint(def, hb, vic, dispX, dispZ, toi, nx, nz, regionSel);

                PendingContacts.Add(new PendingContact
                {
                    ToiRaw = toi, LayerRank = 2,
                    AttackerId = hb.OwnerId, DefenderId = vic.Id,
                    HitboxUid = hb.Uid, Region = regionSel, Kind = (byte)ContactKind.CombatHit,
                    SkillRuntimeId = def.RuntimeId, SegmentIndex = hb.SegmentIndex,
                    HitPointX = px, HitPointY = py, HitPointZ = pz,
                    NormalX = nx, NormalZ = nz,
                });
                hb.HitVictims.Add(vic.Id);   // SemanticKey 幂等（同段同 victim 一次）
                MarkHitConfirmed(own, def.RuntimeId);
            }
        }

        // 排序（SPEC-0006 §2 稳定排序键；同键 = 同一离散时刻去重语义 PA-4.1）
        PendingContacts.Sort((a, b) =>
        {
            int c = a.ToiRaw.CompareTo(b.ToiRaw);
            if (c != 0) return c;
            c = a.LayerRank.CompareTo(b.LayerRank);
            if (c != 0) return c;
            c = a.AttackerId.CompareTo(b.AttackerId);
            if (c != 0) return c;
            c = a.DefenderId.CompareTo(b.DefenderId);
            if (c != 0) return c;
            c = a.HitboxUid.CompareTo(b.HitboxUid);
            if (c != 0) return c;
            c = a.Region.CompareTo(b.Region);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });
    }

    internal void MarkHitConfirmed(FighterStateData owner, ushort skillId)
    {
        var exec = GetExecution(owner.ActiveSkillUid);
        if (exec is not null && exec.SkillRuntimeId == skillId) exec.HitConfirmed = true;
    }

    /// 部位几何精判（SPEC-0006 PA-H1.2/PA-H2 + GDD §4.6 弱点门控）:
    /// 仅【弱点】类技能（HeadMultQ>0）使用头部 Hurtbox——其余技能命中一律 Torso。
    private byte SelectHitRegion(SkillRuntimeData def, ConvexRegion region, ActiveHitbox hb,
        FighterStateData vic, long bandLo, long bandHi)
    {
        if (def.HeadMultQ <= 0) return (byte)HitRegion.Torso;   // GDD §4.6: 非弱点技不使用头部判定
        long headCy = HurtboxModel.HeadCenterY(vic.PosY.Raw);
        // 头部球水平: 头心 vs hitbox 膨胀 headR（PA-H1.2 真 3D 由弹道路径承担；近战带语义走带交）
        bool head2D = SweepSolver.SweepRegion(region, vic.PosX.Raw, vic.PosZ.Raw, 0, 0,
            RuntimeConstants.HEAD_RADIUS, out _, out _, out _, out _);
        // 头部球垂直带 [headCy−r, headCy+r] ∩ hitbox 带
        bool headBand = bandHi >= headCy - RuntimeConstants.HEAD_RADIUS && bandLo <= headCy + RuntimeConstants.HEAD_RADIUS;
        if (head2D && headBand) return (byte)HitRegion.Head;
        return (byte)HitRegion.Torso;
    }

    /// 接触点（SPEC-0005 §5.4: Circle mover = TOI 位置 + 法线 × r 表面投影）
    private (long, long, long) ContactPoint(SkillRuntimeData def, ActiveHitbox hb, FighterStateData vic,
        long dispX, long dispZ, long toi, long nx, long nz, byte regionSel)
    {
        long cx = vic.PosX.Raw + DeterministicMath.MulShift(dispX, toi);
        long cz = vic.PosZ.Raw + DeterministicMath.MulShift(dispZ, toi);
        // HitPoint = victim 中心 TOI 位置 − normal × bodyR（体表面朝 hitbox 侧）
        long px = cx - DeterministicMath.MulShift(nx, RuntimeConstants.FIGHTER_RADIUS);
        long pz = cz - DeterministicMath.MulShift(nz, RuntimeConstants.FIGHTER_RADIUS);
        long py = regionSel == (byte)HitRegion.Head
            ? HurtboxModel.HeadCenterY(vic.PosY.Raw)
            : vic.PosY.Raw + RuntimeConstants.TORSO_TOP / 2;
        return (px, py, pz);
    }

    internal ConvexRegion BuildHitboxRegion(HitboxGeometry geo, long anchorX, long anchorZ, long heading)
    {
        return geo.Kind switch
        {
            GeoKind.Sector => ConvexRegion.Sector(anchorX, anchorZ, heading, geo.HalfDegIndex, geo.Radius),
            GeoKind.Circle => ConvexRegion.Circle(anchorX, anchorZ, geo.Radius),
            GeoKind.Obb => ObbForward(anchorX, anchorZ, heading, geo.HalfForward, geo.HalfAcross),
            GeoKind.Cylinder => ConvexRegion.Circle(anchorX, anchorZ, geo.Radius),
            _ => throw new InvalidOperationException("hitbox geo none"),
        };
    }

    private static ConvexRegion ObbForward(long x, long z, long heading, long halfForward, long halfAcross)
    {
        DeterministicMath.CordicCosSin(heading, out var fx, out var fz);
        // box 从 Owner 原点沿前向延伸: 中心 = owner + f × halfForward
        long cx = x + DeterministicMath.MulShift(fx, halfForward);
        long cz = z + DeterministicMath.MulShift(fz, halfForward);
        return ConvexRegion.Obb(cx, cz, fx, fz, halfForward, halfAcross);
    }

    // ---- 命中结算（PA-H3 时序） ----

    private void ResolveContacts()
    {
        for (int i = 0; i < PendingContacts.Count; i++)
        {
            var c = PendingContacts[i];
            var attacker = GetFighter(c.AttackerId);
            var victim = GetFighter(c.DefenderId);
            var def = GetSkill(c.SkillRuntimeId);
            if (attacker is null || victim is null || def is null) continue;

            HitResolve.Resolve(new HitResolve.HitContext
            {
                World = this,
                Attacker = attacker,
                Victim = victim,
                Def = def,
                SegmentIndex = c.SegmentIndex,
                HitRegion = c.Region,
                HitPointX = c.HitPointX, HitPointY = c.HitPointY, HitPointZ = c.HitPointZ,
                HitNormalX = c.NormalX, HitNormalZ = c.NormalZ,
            });

            // PA-H3: 投射物命中后处理
            if (c.FromProjectileUid != 0)
            {
                var proj = FindProjectile(c.FromProjectileUid);
                if (proj is not null && !proj.Expired)
                {
                    if (proj.PierceRemaining > 0) proj.PierceRemaining--;
                    else ProjectileSystem.Destroy(this, proj, ProjectileSystem.ProjectileEndReason.Hit);
                }
            }
        }
    }

    private ProjectileState? FindProjectile(int uid)
    {
        for (int i = 0; i < Projectiles.Count; i++)
            if (Projectiles[i].Uid == uid) return Projectiles[i];
        return null;
    }

    // ---- ④ 运动积分（IntegrateMove 统一路径——SPEC-0005 §2 禁止旁路） ----

    private void IntegrateFighters()
    {
        foreach (var f in Fighters)   // Id 升序
        {
            if (f.State == FighterState.Dead) continue;

            // 被抓取: 位置锁定于抓取者身前（GDD §7.2 完全受控）
            if (f.State == FighterState.Grabbed)
            {
                var grabber = GetFighter(f.GrabbedBy);
                if (grabber is not null)
                {
                    DeterministicMath.CordicCosSin(grabber.HeadingQuantum, out var gfx, out var gfz);
                    f.PosX = Fixed.FromRaw(grabber.PosX.Raw + DeterministicMath.MulShift(gfx, RuntimeConstants.GRAB_HOLD_DISTANCE));
                    f.PosZ = Fixed.FromRaw(grabber.PosZ.Raw + DeterministicMath.MulShift(gfz, RuntimeConstants.GRAB_HOLD_DISTANCE));
                }
                f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero; f.VelY = Fixed.Zero;
                f.PosY = Fixed.Zero;
                continue;
            }

            if (f.State == FighterState.Roll)
            {
                // 翻滚位移（GDD §10.1: 3m/30f 直线）
                f.RollTicksRemaining--;
                if (!f.RollInvulnArmed &&
                    RuntimeConstants.ROLL_TICKS - f.RollTicksRemaining >= RuntimeConstants.ROLL_INVULN_START)
                {
                    f.RollInvulnArmed = true;
                    f.InvulnTicks = RuntimeConstants.ROLL_INVULN_END - RuntimeConstants.ROLL_INVULN_START + 1;
                }
                (long rdx, long rdz) = DirVector(f.RollDirIndex);
                // 翻滚速度 = 3m/30f × 60 = 6 m/s（IntegrateMove 入参为 m/s 域，内部 ÷60 得 Tick 位移）
                long rollVel = DeterministicMath.DivRoundHalfEven(
                    RuntimeConstants.ROLL_DISTANCE * RuntimeConstants.TICK_RATE, RuntimeConstants.ROLL_TICKS);
                var rollMove = Collision.IntegrateMove(f.PosX.Raw, f.PosZ.Raw,
                    DeterministicMath.MulShift(rdx, rollVel),
                    DeterministicMath.MulShift(rdz, rollVel),
                    RuntimeConstants.FIGHTER_RADIUS, bounceEnabled: false);
                f.PosX = Fixed.FromRaw(rollMove.FinalX);
                f.PosZ = Fixed.FromRaw(rollMove.FinalZ);
                if (f.RollTicksRemaining <= 0)
                {
                    f.State = FighterState.Normal;
                    f.RollTicksRemaining = 0;
                }
                // 翻滚期间贴地（地面高度跟随——垂直分量独立处理）
            }

            // 地面高度（GDD §3.5 高台/台阶: ≤0.5m 自动踏上；平台=悬崖边走出即坠落）
            long ground = Collision.QueryGround(f.PosX.Raw, f.PosZ.Raw, f.PosY.Raw + RuntimeConstants.STEP_UP_HEIGHT);
            bool wasGrounded = f.VelY.Raw == 0 && f.State != FighterState.Launch;
            if (wasGrounded && ground > f.PosY.Raw && ground - f.PosY.Raw <= RuntimeConstants.STEP_UP_HEIGHT)
                f.PosY = Fixed.FromRaw(ground);   // 台阶吸附

            // 垂直运动（Launch/跳跃共用重力路径；GroundStop = 当前地面高度）
            bool wasAirborne = f.PosY.Raw > ground;
            if (f.PosY.Raw > ground || f.VelY.Raw > 0)
            {
                if (f.PeakY < f.PosY.Raw) f.PeakY = f.PosY.Raw;   // 空中峰值追踪（坠落伤害用）
                f.VelY = Fixed.FromRaw(f.VelY.Raw - RuntimeConstants.GRAVITY_PER_TICK);
                f.PosY = Fixed.FromRaw(f.PosY.Raw + DeterministicMath.DivRoundHalfEven(f.VelY.Raw, RuntimeConstants.TICK_RATE));
                if (f.PosY.Raw <= ground)
                {
                    long drop = f.PeakY - ground;
                    f.PosY = Fixed.FromRaw(ground);
                    f.VelY = Fixed.Zero;
                    f.PeakY = ground;
                    if (f.State == FighterState.Launch)
                    {
                        // 浮空落地 → 倒地（GDD §5.3）
                        f.State = FighterState.Down;
                        f.DownTicks = 0;
                        f.DownCount++;
                        Events.Emit(new SimEvent { Kind = EventKind.Landed, VictimId = f.Id, ValueRaw = f.FloatAirTicks });
                    }
                    else if (drop > RuntimeConstants.FALL_DAMAGE_MIN_DROP)
                    {
                        // 坠落（GDD §3.5: 高差 >2m → 高度×80 伤害（上限 1200）+ 强制长倒地）
                        long meters = DeterministicMath.DivRoundHalfEven(drop, Fixed.ONE);
                        long fallDmg = Math.Min(meters * RuntimeConstants.FALL_DAMAGE_PER_M, RuntimeConstants.FALL_DAMAGE_CAP);
                        f.Hp -= fallDmg;
                        f.State = FighterState.Down;
                        f.DownTicks = 0;
                        f.DownCount++;
                        Events.Emit(new SimEvent { Kind = EventKind.FallLanded, VictimId = f.Id, DamageRaw = fallDmg, ValueRaw = drop });
                    }
                    else if (drop > RuntimeConstants.STEP_UP_HEIGHT)
                    {
                        // 落地硬直 6f（GDD §3.3；坠落/浮空落地走各自分支）
                        Events.Emit(new SimEvent { Kind = EventKind.Landed, VictimId = f.Id });
                        if (f.State == FighterState.Normal)
                        {
                            f.State = FighterState.Hitstun;
                            f.StateTicksRemaining = RuntimeConstants.JUMP_LAND_LAG_TICKS;
                        }
                    }
                    else if (wasAirborne)
                    {
                        Events.Emit(new SimEvent { Kind = EventKind.Landed, VictimId = f.Id });
                    }
                }
            }
            else if (f.PosY.Raw < ground)
            {
                f.PosY = Fixed.FromRaw(ground);   // 贴地（台阶吸附后）
                f.PeakY = ground;
            }
            else
            {
                f.PeakY = ground;   // 地面行进——峰值复位
            }

            // 浮空连时钟（GDD §5.3 第二道闸: 累计 3s 强制落地）
            if (f.State == FighterState.Launch)
            {
                f.FloatAirTicks++;
                if (f.FloatAirTicks >= RuntimeConstants.FLOAT_PROTECT_TICKS && !f.ForcedFall)
                {
                    f.ForcedFall = true;
                    f.VelY = Fixed.FromRaw(-12 * Fixed.ONE);
                    Events.Emit(new SimEvent { Kind = EventKind.FloatProtect, VictimId = f.Id, ValueRaw = f.FloatAirTicks });
                }
            }

            // 水平运动（IntegrateMove: 走位 Stop / 击退 Bounce）
            bool bounce = f.State == FighterState.Hitstun || f.State == FighterState.Launch;
            var move = Collision.IntegrateMove(f.PosX.Raw, f.PosZ.Raw, f.VelX.Raw, f.VelZ.Raw,
                RuntimeConstants.FIGHTER_RADIUS, bounceEnabled: bounce);
            f.PosX = Fixed.FromRaw(move.FinalX);
            f.PosZ = Fixed.FromRaw(move.FinalZ);
            f.VelX = Fixed.FromRaw(move.FinalVelX);
            f.VelZ = Fixed.FromRaw(move.FinalVelZ);
            if (move.BounceCount > 0)
            {
                if (f.State == FighterState.Hitstun)
                    f.StateTicksRemaining += RuntimeConstants.WALL_STUN_EXTEND_TICKS;   // GDD §5.8 硬直延长 10f
                Events.Emit(new SimEvent { Kind = EventKind.WallBounced, VictimId = f.Id, HitNormalX = move.ContactNormalX, HitNormalZ = move.ContactNormalZ });
            }

            // 摩擦（击退衰减——仅 Hitstun 状态；走位速度每 Tick 由指令重设）
            if (f.State == FighterState.Hitstun)
            {
                f.VelX = Fixed.FromRaw(DeterministicMath.MulShift(f.VelX.Raw * RuntimeConstants.FRICTION_KEEP_NUM, Fixed.ONE) / RuntimeConstants.FRICTION_KEEP_DEN);
                f.VelZ = Fixed.FromRaw(DeterministicMath.MulShift(f.VelZ.Raw * RuntimeConstants.FRICTION_KEEP_NUM, Fixed.ONE) / RuntimeConstants.FRICTION_KEEP_DEN);
                if (Math.Abs(f.VelX.Raw) < RuntimeConstants.FRICTION_STOP_EPSILON && Math.Abs(f.VelZ.Raw) < RuntimeConstants.FRICTION_STOP_EPSILON)
                {
                    f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero;
                }
            }
        }

        // L2 SoftPush（击退/浮空位移驱动的重叠分离——(minId,maxId) 对序，PA-4.2）
        for (int i = 0; i < Fighters.Count; i++)
        {
            var a = Fighters[i];
            if (a.State is not (FighterState.Hitstun or FighterState.Launch)) continue;
            for (int j = i + 1; j < Fighters.Count; j++)
            {
                var b = Fighters[j];
                if (b.State == FighterState.Dead || a.State == FighterState.Dead) continue;
                var (pa, pa2, pb, pb2) = CollisionSystem.SoftPushPair(
                    a.PosX.Raw, a.PosZ.Raw, b.PosX.Raw, b.PosZ.Raw, RuntimeConstants.FIGHTER_RADIUS);
                a.PosX = Fixed.FromRaw(a.PosX.Raw + pa); a.PosZ = Fixed.FromRaw(a.PosZ.Raw + pa2);
                b.PosX = Fixed.FromRaw(b.PosX.Raw + pb); b.PosZ = Fixed.FromRaw(b.PosZ.Raw + pb2);
            }
        }
    }

    // ---- ⑤ 状态/闸门/资源 Tick（FighterId 升序） ----

    private void TickFighters()
    {
        foreach (var f in Fighters)
        {
            if (f.State == FighterState.Dead) continue;

            StatusSystem.Tick(f, this);

            // CD / 强制中断 CD
            var keys = new List<ushort>(f.Cooldowns.Keys);
            foreach (var k in keys)
            {
                long v = f.Cooldowns[k] - 1;
                if (v <= 0) f.Cooldowns.Remove(k); else f.Cooldowns[k] = v;
            }

            // 耐力回复（GDD §10.2: 战斗中 10/s——脱离战斗 20/s 需战斗状态追踪，v1 常值登记）
            if (f.Stamina < RuntimeConstants.STAMINA_MAX)
            {
                f.StaminaFrac += RuntimeConstants.STAMINA_REGEN_PER_SEC;
                if (f.StaminaFrac >= RuntimeConstants.TICK_RATE)
                {
                    long whole = f.StaminaFrac / RuntimeConstants.TICK_RATE;
                    f.StaminaFrac -= whole * RuntimeConstants.TICK_RATE;
                    f.Stamina = Math.Min(RuntimeConstants.STAMINA_MAX, f.Stamina + whole);
                }
            }
            // 盾回复（GDD §6.2: 8s 恢复至满——格挡结束/破盾起计）
            if (f.ShieldRegenTicks > 0)
            {
                f.ShieldRegenTicks--;
                if (f.ShieldRegenTicks == 0 && f.ShieldMax > 0) f.Shield = f.ShieldMax;
            }
            if (f.ParryCdTicks > 0) f.ParryCdTicks--;
            if (f.CounterWindowTicks > 0) f.CounterWindowTicks--;

            // MP 回复（20/s 连续量——分数累积，ADR-0003 §1）
            if (f.Mp < 1000)
            {
                f.MpFracNum += RuntimeConstants.MP_REGEN_PER_TICK_NUM;
                if (f.MpFracNum >= RuntimeConstants.MP_REGEN_PER_TICK_DEN)
                {
                    long whole = f.MpFracNum / RuntimeConstants.MP_REGEN_PER_TICK_DEN;
                    f.MpFracNum -= whole * RuntimeConstants.MP_REGEN_PER_TICK_DEN;
                    f.Mp = Math.Min(1000, f.Mp + whole);
                }
            }

            if (f.ProtectTicks > 0) f.ProtectTicks--;
            if (f.InvulnTicks > 0) f.InvulnTicks--;

            switch (f.State)
            {
                case FighterState.Hitstun:
                    f.StateTicksRemaining--;
                    // GDD §5.1: 击退解除 = 位移结束 + 硬直结束（双条件）——位移未结束维持击退状态
                    if (f.StateTicksRemaining > 0) break;
                    if (Math.Abs(f.VelX.Raw) > RuntimeConstants.FRICTION_STOP_EPSILON ||
                        Math.Abs(f.VelZ.Raw) > RuntimeConstants.FRICTION_STOP_EPSILON) break;
                    Recover(f);
                    break;
                case FighterState.Launch:
                    // 状态时长由落地驱动（④ 运动积分）
                    break;
                case FighterState.Down:
                    f.DownTicks++;
                    int downTotal = f.ForcedFall || f.DownCount >= 2 ? RuntimeConstants.DOWN_TICKS_LONG : RuntimeConstants.DOWN_TICKS_NORMAL;
                    if (f.DownTicks >= downTotal)
                    {
                        f.State = FighterState.Getup;
                        f.StateTicksRemaining = RuntimeConstants.GETUP_TICKS;
                        f.InvulnTicks = RuntimeConstants.GETUP_TICKS;   // 起身 24f 全程无敌（GDD §5.7）
                    }
                    break;
                case FighterState.Getup:
                    if (--f.StateTicksRemaining <= 0)
                    {
                        f.ProtectTicks = RuntimeConstants.GETUP_PROTECT_TICKS;
                        Recover(f);
                    }
                    break;
                case FighterState.Break:
                    if (--f.StateTicksRemaining <= 0) Recover(f);
                    break;
            }
        }
    }

    /// 恢复行动（GDD §8.4: 连段计数器恢复行动即清零）
    private void Recover(FighterStateData f)
    {
        f.State = FighterState.Normal;
        f.StateTicksRemaining = 0;
        f.HitstunCount = 0;
        f.LaunchCount = 0;
        f.FloatAirTicks = 0;
        f.ForcedFall = false;
        f.DownCount = 0;
        f.UkemiIneffective = false;
    }

    // ---- Sim 内部服务（HitResolve/StatusSystem/ProjectileSystem 消费） ----

    public FighterStateData? GetFighter(int id)
    {
        for (int i = 0; i < Fighters.Count; i++)
            if (Fighters[i].Id == id) return Fighters[i];
        return null;
    }

    public SkillExecution? GetExecution(int uid)
    {
        for (int i = 0; i < Executions.Count; i++)
            if (Executions[i].Uid == uid) return Executions[i];
        return null;
    }

    public bool SameTeam(int a, int b) =>
        _teams.TryGetValue(a, out var ta) && _teams.TryGetValue(b, out var tb) && ta == tb;

    public int NextProjectileUid() => _nextProjUid++;

    /// 控制值积累 + 挣脱（GDD §7.4: 满 100 → Break 1.5s 免控）
    public void AddControlValue(FighterStateData f, long amount)
    {
        if (f.State == FighterState.Break || f.State == FighterState.Dead) return;
        f.ControlValue += amount;
        if (f.ControlValue < RuntimeConstants.CONTROL_VALUE_MAX) return;
        // Break: 解除一切控制 + 清零（GDD §7.4）
        f.ControlValue = 0;
        for (int k = 1; k < f.Statuses.Length; k++)
            if (f.Statuses[k].Active) RemoveStatus(f, (StatusKind)k);
        f.State = FighterState.Break;
        f.StateTicksRemaining = RuntimeConstants.BREAK_TICKS;
        f.VelX = Fixed.Zero; f.VelZ = Fixed.Zero;
        Events.Emit(new SimEvent { Kind = EventKind.BreakTriggered, VictimId = f.Id });
    }

    public void ApplyStatus(FighterStateData f, StatusEffectDef eff, int sourceFighterId, ushort skillId) =>
        StatusSystem.Apply(f, eff, sourceFighterId, this);

    public void RemoveStatus(FighterStateData f, StatusKind kind) => StatusSystem.Remove(f, kind, this);
}
