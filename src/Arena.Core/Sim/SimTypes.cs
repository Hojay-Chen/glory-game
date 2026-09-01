using System;
using System.Collections.Generic;
using Arena.Core.Collision;
using Arena.Core.Calc;

namespace Arena.Core.Sim;

// ---- ADR-0001 §3/ADR-0003: 稳定 ID 与状态枚举 ----
// EVENT_PROTOCOL_VERSION = 2（SPEC-0006 §2: Hit 携带 hitRegion/hitPoint/hitNormal）

public enum FighterState : byte
{
    Normal = 0, Act = 1, Hitstun = 2, Launch = 3, Down = 4,
    Getup = 5, Break = 6, Grabbed = 7, Dead = 8
}

// ADR-0001 §7.1 优先级: Dead > Break > Grabbed > Down > Launch > Hitstun > Act > Normal
public static class FighterStatePriority
{
    private static readonly byte[] Rank = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
    public static bool CanOverride(FighterState current, FighterState next) => Rank[(int)next] >= Rank[(int)current];
}

// ---- Command（ADR-0001/0010/SPEC-0001）----
// FighterId 显式入 Command——per-Fighter CmdStream 路由（combat-fidelity-review BUG-2 修正）。
// 传输层 CommandPacket 按通道携带归属；Core 内统一显式字段（Headless/Replay 同构）。

public enum CmdKind : byte { Move = 0, Jump = 1, Roll = 2, Skill = 3, Basic = 4, ForceCancel = 5, Steer = 6 }

public readonly record struct Command(
    int FighterId,
    CmdKind Kind,
    ushort SkillId,
    ushort AimQuantum,
    byte DirIndex,
    int TargetTick)
{
    public static readonly Command None = default;
}

/// 同 Tick 指令优先级（GDD §2.3.2: 受身/挣脱 > 强制中断 > 技能 > 翻滚 > 跳跃 > 普攻 > 移动）
public static class CommandPriority
{
    public static int Of(CmdKind kind) => kind switch
    {
        CmdKind.Roll => 0,        // 受身（Down 态）/ 翻滚
        CmdKind.ForceCancel => 1,
        CmdKind.Skill => 2,
        CmdKind.Jump => 3,
        CmdKind.Basic => 4,
        CmdKind.Move => 5,
        CmdKind.Steer => 6,
        _ => 7,
    };
}

// ---- SimEvent（ADR-0003 封闭枚举 + SPEC-0006 §2 空间载荷，协议 v2）----

public enum EventKind : byte
{
    SkillCast = 0, Hit = 1, Launched = 2, Landed = 3, ForcedDown = 4,
    Ukemi = 5, WallBounced = 6, BreakTriggered = 7, Died = 8,
    FloatProtect = 9, Whiff = 10, Cancelled = 11, ActEnded = 12,
    GetupDone = 13, Knockback = 14, StatusApplied = 15, StatusExpired = 16,
    ProjectileSpawned = 17, ProjectileDestroyed = 18, Relaunched = 19,
    BasicStep = 20, ControlValueNearFull = 21,
}

/// Whiff 原因（ADR-0003 §3.2）
public enum WhiffReason : byte { Range = 0, DownProtected = 1, Angle = 2, Invulnerable = 3 }

public readonly record struct SimEvent(
    long Tick, ushort SeqInTick, EventKind Kind,
    int AttackerId, int VictimId,
    ushort SkillId, byte SegmentIndex,
    long DamageRaw, int HitNumber,
    byte HitRegion,
    long HitPointX, long HitPointY, long HitPointZ,
    long HitNormalX, long HitNormalZ,
    byte VictimStateBefore, long PosY,
    bool SweepFlag, bool AirMod,
    byte StatusKind, int DurationTicks, byte ReasonByte, long ValueRaw)
{
    public long EventId => Tick << 16 | SeqInTick;
}

// ---- HitRegion（SPEC-0006 §1.2 可扩展枚举）----
// 委托至 Collision 命名空间定义（HitRegion），此处 using 透明引用。

// ---- StatusKind（GDD §7.3 控制类异常 v1 路由集）----
public enum StatusKind : byte
{
    None = 0, Slow = 1, Root = 2, Stun = 3, Freeze = 4, Sleep = 5,
    Silence = 6, Blind = 7, Burn = 8, Bleed = 9, Poison = 10,
    Weakness = 11, GuardBreak = 12, Paralysis = 13, Taunt = 14,
    Fear = 15, Confuse = 16, Curse = 17, Shock = 18,
}

/// GDD §7.3 控制值增量 + 行为路由表（数据驱动消费）
public static class StatusRules
{
    /// 各异常控制值增量（GDD §7.3 表）
    public static long ControlValueAdd(StatusKind k) => k switch
    {
        StatusKind.Stun or StatusKind.Freeze or StatusKind.Paralysis => 35,
        StatusKind.Root => 25,
        StatusKind.Sleep or StatusKind.Fear or StatusKind.Confuse or StatusKind.Taunt => 30,
        StatusKind.Silence or StatusKind.Blind or StatusKind.GuardBreak => 15,
        StatusKind.Slow or StatusKind.Weakness => 10,
        _ => 0,
    };

    /// 硬控/软控行为位（v1 行为路由；未路由 = 仅事件可见——实施报告登记）
    public static bool BlocksAction(StatusKind k) =>
        k is StatusKind.Stun or StatusKind.Freeze or StatusKind.Sleep or StatusKind.Paralysis;
    public static bool BlocksMovement(StatusKind k) =>
        k is StatusKind.Stun or StatusKind.Freeze or StatusKind.Sleep or StatusKind.Paralysis or StatusKind.Root;
    public static bool BlocksSkill(StatusKind k) =>
        k is StatusKind.Stun or StatusKind.Freeze or StatusKind.Sleep or StatusKind.Paralysis or StatusKind.Silence;
    /// 是否控制类（控制值衰减暂停判定）
    public static bool IsControl(StatusKind k) => ControlValueAdd(k) > 0;
    /// 互斥对（GDD §7.5: 冰冻↔灼烧；沉睡↔眩晕）
    public static bool MutuallyExclusive(StatusKind a, StatusKind b) =>
        (a == StatusKind.Freeze && b == StatusKind.Burn) || (a == StatusKind.Burn && b == StatusKind.Freeze) ||
        (a == StatusKind.Sleep && b == StatusKind.Stun) || (a == StatusKind.Stun && b == StatusKind.Sleep);
}

/// 持久化状态实例（定长槽位 = StatusKind 索引——ADR-0001 §3.1 确定性容器纪律）
public struct StatusInstance
{
    public bool Active;
    public int RemainingTicks;
    public int TotalTicks;
    public long PotencyQ;      // 慢速比例/DoT 每秒伤害（Q32.16 语义依 Kind）
    public long DotCarryQ;     // DoT 累积余数（确定性分数伤害）
    public long DotApplied;
    public int SourceFighterId;
}

// ---- Fighter（ADR-0001 §8.2 Fighter 域，v2 扩展）----

public sealed class FighterStateData
{
    public int Id { get; set; }
    public string ClassId { get; set; } = "";
    public byte Team { get; set; }

    public Fixed PosX { get; set; } = Fixed.Zero;
    public Fixed PosY { get; set; } = Fixed.Zero;
    public Fixed PosZ { get; set; } = Fixed.Zero;
    public Fixed VelX { get; set; } = Fixed.Zero;   // Q32.16 m/s
    public Fixed VelY { get; set; } = Fixed.Zero;
    public Fixed VelZ { get; set; } = Fixed.Zero;
    public long HeadingQuantum { get; set; }        // SPEC-0001 u16 语义（0=+Z 顺时针）

    public FighterState State { get; set; } = FighterState.Normal;
    public int StateTicksRemaining { get; set; }

    public long Hp { get; set; } = 10000;
    public long Mp { get; set; } = 1000;
    public long MpFracNum { get; set; }             // MP 回复分数累积（20/s，ADR-0003 §1 连续量）
    public long Atk { get; set; } = 1100;
    public long Def { get; set; } = 800;
    public long ControlValue { get; set; }

    // 连段纪元（GDD §8.4: 连段计数器——恢复行动即清零）
    public int HitstunCount { get; set; }           // 本连段被命中次数（=hitNumber）
    public int LaunchCount { get; set; }            // 本连段浮空刷新次数
    public int FloatAirTicks { get; set; }          // 浮空连累计（落地保护 3s）
    public bool ForcedFall { get; set; }            // 落地保护触发后不再被击起
    public bool UkemiIneffective { get; set; }      // 受身无效（圆舞棍类）
    public int DownCount { get; set; }              // 本连段倒地次数（第二次→受身窗 30f）
    public long DownTicks { get; set; }
    public byte FallDirIndex { get; set; }          // 摔倒方向（8 向 DirIndex，受身判定）

    public long ProtectTicks { get; set; }          // 起身保护（×0.9 / 控制值×0.5）
    public long InvulnTicks { get; set; }           // 状态性无敌（起身 24f 全程）

    // Act（技能执行中）
    public int ActiveSkillUid { get; set; }         // SkillExecution.Uid（0=无）
    public ushort PendingChainSkill { get; set; }   // 普攻段间缓冲的下一段

    public readonly SortedDictionary<ushort, long> Cooldowns = new();

    /// 状态槽（StatusKind 定长槽位——确定性遍历）
    public readonly StatusInstance[] Statuses = new StatusInstance[32];

    public bool IsAirborne => PosY.Raw > 0 || State == FighterState.Launch;
    public bool IsInvulnerable => InvulnTicks > 0;

    public FighterStateData Clone()
    {
        var c = new FighterStateData
        {
            Id = Id, ClassId = ClassId, Team = Team,
            PosX = PosX, PosY = PosY, PosZ = PosZ,
            VelX = VelX, VelY = VelY, VelZ = VelZ,
            HeadingQuantum = HeadingQuantum,
            State = State, StateTicksRemaining = StateTicksRemaining,
            Hp = Hp, Mp = Mp, MpFracNum = MpFracNum, Atk = Atk, Def = Def,
            ControlValue = ControlValue,
            HitstunCount = HitstunCount, LaunchCount = LaunchCount,
            FloatAirTicks = FloatAirTicks, ForcedFall = ForcedFall,
            UkemiIneffective = UkemiIneffective, DownCount = DownCount,
            DownTicks = DownTicks, FallDirIndex = FallDirIndex,
            ProtectTicks = ProtectTicks, InvulnTicks = InvulnTicks,
            ActiveSkillUid = ActiveSkillUid, PendingChainSkill = PendingChainSkill,
        };
        foreach (var kv in Cooldowns) c.Cooldowns[kv.Key] = kv.Value;
        Array.Copy(Statuses, c.Statuses, Statuses.Length);
        return c;
    }
}

// ---- Hurtbox 投影辅助（SPEC-0006 §1.3: 运行时投影，非独立实体）----
public static class HurtboxModel
{
    /// 躯干 OBB（0.9 宽 × 0.6 深，随朝向旋转；高度带 [posY, posY+1.6]）
    public static ConvexRegion TorsoRegion(long posX, long posZ, long headingQuantum)
    {
        DeterministicMath.CordicCosSin(headingQuantum, out var fx, out var fz);
        // 前向半深 0.3m，横向半宽 0.45m；OBB 中心 = Fighter 原点（躯干盒以脚底原点为中心对称——GDD §2.1.2 0.9×0.6 水平投影）
        return ConvexRegion.Obb(posX, posZ, fx, fz, Sim.RuntimeConstants.TORSO_HALF_D, Sim.RuntimeConstants.TORSO_HALF_W);
    }

    /// 头部球心高度（PA-H1 GDD-GAP 约定: 躯干顶 + 0，随 PosY 平移）
    public static long HeadCenterY(long posY) => posY + Sim.RuntimeConstants.HEAD_CENTER_Y;
}
