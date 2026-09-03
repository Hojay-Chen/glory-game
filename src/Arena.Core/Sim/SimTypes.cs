using System;
using System.Collections.Generic;
using Arena.Core.Collision;
using Arena.Core.Calc;

namespace Arena.Core.Sim;

// ---- ADR-0001 §3/ADR-0003: 稳定 ID 与状态枚举 ----
// EVENT_PROTOCOL_VERSION = 4（v2: Hit 空间载荷；v3: +Parry/GuardHit/GuardBroken/GrabStarted/
// GrabReleased/Countered/Interrupted/FallLanded——Phase 5 格挡/抓取/反击/坠落体系；
// v4: +DrainPulse——Batch 4 自增益通道自伤脉冲）

public enum FighterState : byte
{
    Normal = 0, Act = 1, Hitstun = 2, Launch = 3, Down = 4,
    Getup = 5, Break = 6, Grabbed = 7, Dead = 8, Roll = 9
}

// ADR-0001 §7.1 优先级: Dead > Break > Grabbed > Down > Launch > Hitstun > Act > Normal
public static class FighterStatePriority
{
    private static readonly byte[] Rank = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 1 };
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
    Parry = 22, GuardHit = 23, GuardBroken = 24,
    GrabStarted = 25, GrabReleased = 26, Countered = 27,
    Interrupted = 28, FallLanded = 29,
    UnitSpawned = 30, UnitDied = 31, StealthBroken = 32, Reflected = 33,
    BuffApplied = 34, BuffExpired = 35, Healed = 36, DrainPulse = 37,
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
    public long HpMax { get; set; } = 10000;        // GDD §2.5.3 全职业统一（权威状态域）
    public long Mp { get; set; } = 1000;
    public long MpMax { get; set; } = 1000;
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
    public long InvulnTicks { get; set; }           // 状态性无敌（起身 24f / 翻滚 4-18f / 技能窗）
    public int CounterWindowTicks { get; set; }     // 完美格挡/反击免费取消窗（GDD §6.3/6.6）
    public int ParryCdTicks { get; set; }           // 完美格挡 0.5s 间隔（GDD §6.3）

    // 格挡/盾值（GDD §6.2；盾值/减伤来自技能数据）
    public long Shield { get; set; }
    public long ShieldMax { get; set; }
    public int ShieldRegenTicks { get; set; }       // 格挡结束后 8s 恢复至满

    // 抓取（GDD §7.2: Grabbed 完全受控+唯一免受其他伤害）
    public int GrabbedBy { get; set; } = -1;        // 抓取者 FighterId（-1 = 无）
    public int GrabThrowSkill { get; set; }         // 投技 RuntimeId（抓取执行结束时结算）

    public long PeakY { get; set; }                 // 空中峰值（坠落伤害: 高差 = 峰值 − 落点）

    // Visibility（GDD THF 潜行: 完全隐身、攻击/被击解除）
    public bool Hidden { get; set; }
    // 法术反射（KNI 法术反射/WRK 魔镜: 窗口内 magic 弹体反弹）
    public int ReflectTicks { get; set; }

    // Buff（GDD 阵内增益类: ATK/DEF 百分比+时限——Review 项#1: 域化而非直接改写基准）
    public long BuffAtkPctQ { get; set; }
    public int BuffAtkPctTicks { get; set; }
    public long BuffDefPctQ { get; set; }           // QIM 护体真气等 DEF 百分比增益（可负）
    public int BuffDefPctTicks { get; set; }

    // 自增益通道（通用 B 类: 嗜血 ATK+20% 等由施法数据驱动，Batch 4）
    public long BuffDrainHpPctQ { get; set; }       // 自伤脉率（Q32.16 每秒 ×HpMax——嗜血 1.5%/s）
    public int BuffDrainHpPctTicks { get; set; }
    public long LifestealPctQ { get; set; }         // 正嗜血: 命中造成伤害的 P% 转为自身回复
    public int LifestealTicks { get; set; }

    // Buff 霸体域（DDQ-B4-①解耦: 纯 buff 技动作窗 2T 后霸体由效果域承载——SSA 窗口数据化）
    public byte BuffArmorKind { get; set; }         // 0=无 1=SA 2=SSA（ArmorWindowDef.SuperArmor）
    public int BuffArmorDelayTicks { get; set; }    // 窗口起点延迟（armor start）
    public int BuffArmorTicks { get; set; }         // 霸体剩余（armor end−start）

    // 炫纹类型计数（BMG 资源闭环: Orb 槽计总数，类型分布供炫纹发射按型发射/增益；Σ==Orb 不变式）
    public readonly long[] OrbTypeCounts = new long[6];   // 下标 = OrbTagKind 枚举值（None/Light/Ice/Fire/Dark/NonElemental）

    // 复制技槽（ROG 以牙还牙: 最近命中自己的技能记录，每局 3 槽——动态施放判定域）
    public readonly ushort[] CopiedSkillUids = new ushort[3];
    public int CopiedSkillNext;                     // 环形写入指针（0..2）

    // 最近一次完成（结束/取消）施放的技能——SBL 波动共鸣「不同波动剑连放」判定域
    public ushort LastCastSkillUid { get; set; }

    // Heal 通道（GDD PRI 系/GAN 恢复术: 直接量/HoT 脉冲）
    public long HealPulseAmountQ { get; set; }
    public int HealPulseRemaining { get; set; }
    public int HealPulseTimer { get; set; }
    public int HealPulseInterval { get; set; }
    public bool HealIsMana { get; set; }

    // 职业资源槽（GDD §9.3: 炫纹/弹匣/召唤位/部署位/舍命HP——定长槽位确定性纪律）
    public readonly long[] ResourceCounts = new long[8];
    public readonly long[] ResourceCaps = new long[8];

    // 武器（GDD §16: 赛前选择 1 把；atk_mod 面板加成 + 规则级 trait 由 overlay 消费）
    public ushort WeaponId { get; set; }

    // 翻滚（GDD §10.1: 30f/3m/无敌 4-18f）
    public int RollTicksRemaining { get; set; }
    public byte RollDirIndex { get; set; }
    public bool RollInvulnArmed { get; set; }

    // 耐力（GDD §10.2: 上限 100 / 战斗中 10/s / 翻滚 25 / 受身 15）
    public long Stamina { get; set; } = 100;
    public long StaminaFrac { get; set; }

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
            CounterWindowTicks = CounterWindowTicks, ParryCdTicks = ParryCdTicks,
            Shield = Shield, ShieldMax = ShieldMax, ShieldRegenTicks = ShieldRegenTicks,
            GrabbedBy = GrabbedBy, GrabThrowSkill = GrabThrowSkill,
            RollTicksRemaining = RollTicksRemaining, RollDirIndex = RollDirIndex,
            RollInvulnArmed = RollInvulnArmed, PeakY = PeakY,
            HpMax = HpMax, MpMax = MpMax,
            BuffDefPctQ = BuffDefPctQ, BuffDefPctTicks = BuffDefPctTicks,
            BuffDrainHpPctQ = BuffDrainHpPctQ, BuffDrainHpPctTicks = BuffDrainHpPctTicks,
            LifestealPctQ = LifestealPctQ, LifestealTicks = LifestealTicks,
            LastCastSkillUid = LastCastSkillUid,
            BuffArmorKind = BuffArmorKind, BuffArmorDelayTicks = BuffArmorDelayTicks, BuffArmorTicks = BuffArmorTicks,
            CopiedSkillNext = CopiedSkillNext,
            BuffAtkPctQ = BuffAtkPctQ, BuffAtkPctTicks = BuffAtkPctTicks,
            HealPulseAmountQ = HealPulseAmountQ, HealPulseRemaining = HealPulseRemaining,
            HealPulseTimer = HealPulseTimer, HealPulseInterval = HealPulseInterval, HealIsMana = HealIsMana,
            Stamina = Stamina, StaminaFrac = StaminaFrac,
        };
        foreach (var kv in Cooldowns) c.Cooldowns[kv.Key] = kv.Value;
        Array.Copy(Statuses, c.Statuses, Statuses.Length);
        Array.Copy(OrbTypeCounts, c.OrbTypeCounts, OrbTypeCounts.Length);
        Array.Copy(CopiedSkillUids, c.CopiedSkillUids, CopiedSkillUids.Length);
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

/// 命中来源方向判定（格挡 120° 正面扇区 / 反击窗共用——纯整数点积）
public static class FacingRules
{
    /// 命中来自 victim 正面 ±halfDeg 扇区 ⟺ cos(victim→attacker, facing) > cos(halfDeg)
    /// 零除法: dot(v2a, f) / |v2a| > cosθ ⟺ dot·cosDen > |v2a|·cosNum（cos 来自半度表）
    public static bool IsFromFront(FighterStateData victim, long attackerX, long attackerZ, int halfDegIndex)
    {
        long ax = attackerX - victim.PosX.Raw;
        long az = attackerZ - victim.PosZ.Raw;
        if (ax == 0 && az == 0) return true;   // 同心: 构造确定（正面）
        DeterministicMath.CordicCosSin(victim.HeadingQuantum, out var fx, out var fz);
        DeterministicTables.HalfDegTrig(halfDegIndex * 2, out var c, out var _);
        long dot = DeterministicMath.MulShift(ax, fx) + DeterministicMath.MulShift(az, fz);
        long len = DeterministicMath.ISqrt(ax * ax + az * az);
        // dot/len > cosθ（Q32.16 恒等变形: dot×cosDen > len×cosNum, cosDen=65536）
        return dot > DeterministicMath.MulShift(len, c);
    }
}

/// Visibility 投影（pre-adr §3-2 Sim.Visibility v1: 潜行=完全隐身，攻击/被击/显形解除）
public static class Visibility
{
    /// 该 Fighter 对 observer 是否可见（v1: Hidden 全局不可见——队友可见性语义登记待扩展）
    public static bool IsVisible(FighterStateData target, int observerId) =>
        !(target.Hidden && target.Id != observerId);
}
