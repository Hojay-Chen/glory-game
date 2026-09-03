using System;
// PRODUCTION - Arena.Core
// ADR-0001 Errata E-4/E-5: Runtime Constants 中央表——全部「每 Tick 常量」唯一权威源。
// 语义原则: 全部常量以 GDD 秒制语义登记（Tick 率无关），per-Tick 值由本类静态派生（整数 RHE）。
// Tick 率变化时只改 TICK_RATE 重跑派生——碰撞算法/技能语义零改动（SPEC-0005 §12）。
// 来源标注: 每个常量必须引用 GDD 条款；白盒标定值显式标注「WB」（whitebox，非 GDD 直读）。
namespace Arena.Core.Sim;

public static class RuntimeConstants
{
    /// GDD §2.2.1: 逻辑固定 60Hz
    public const long TICK_RATE = 60;

    // ---- 重力与垂直运动（GDD §3.3/§5.3） ----
    /// 重力 22 m/s²（GDD §3.3 强重力）
    public const long GRAVITY_MPS2 = 22;
    /// 每 Tick 速度增量: 22/60 m/s（Q32.16 m/s 域）
    public static readonly long GRAVITY_PER_TICK = RhePerTickFromMps(GRAVITY_MPS2);
    /// 起跳初速 7.0 m/s（GDD §3.3）
    public const long JUMP_VELOCITY_MPS = 7;

    // ---- 移动（GDD §3.2） ----
    public const long WALK_MPS = 3;
    public const long RUN_MPS = 6;
    /// 空中操控加速度 18 m/s²，上限 5.0 m/s（GDD §3.2）
    public const long AIR_ACCEL_MPS2 = 18;
    public const long AIR_SPEED_CAP_MPS = 5;

    // ---- 击退/摩擦（GDD §5.1/§5.8；摩擦系数 WB 白盒标定） ----
    /// 击退初速 = 位移 × 9 m/s ⇒ 摩擦 0.85/tick 下总位移 = 初速/9（Σ0.85ⁿ=6.667 tick）
    public const long KNOCKBACK_VEL_MULT = 9;
    /// 摩擦每 Tick 保留 85%（WB 白盒标定——秒制等价 τ≈0.102s 指数衰减的 60Hz 离散化）
    public static readonly long FRICTION_KEEP_NUM = 85, FRICTION_KEEP_DEN = 100;
    /// 摩擦死区: |v| < 1/20 m/s 时清零（WB）
    public static readonly long FRICTION_STOP_EPSILON = Fixed.ONE / 20;

    // ---- 浮空（GDD §5.3） ----
    /// 浮空刷新下限 3.0 m/s（GDD §5.3 第一道闸下限）
    public const long LAUNCH_FLOOR_MPS = 3;
    /// 浮空连累计 3.0s → 强制落地（GDD §5.3 第二道闸）
    public static readonly int FLOAT_PROTECT_TICKS = (int)(3 * TICK_RATE);

    // ---- 受击状态（GDD §5.6/§5.7） ----
    public const int DOWN_TICKS_NORMAL = 45;        // 普通倒地 45f
    public const int DOWN_TICKS_LONG = 80;          // 长倒地（击飞/坠落）80f
    public const int GETUP_TICKS = 24;              // 起身 24f 全程无敌
    public const int UKEMI_WINDOW_TICKS = 20;       // 受身窗口 0–20f
    public const int UKEMI_WINDOW_EXTENDED = 30;    // 连续倒地保护: 第二次倒地窗口 30f
    /// 起身后保护 1s: 受伤 ×0.9、控制值积累 ×0.5（GDD §5.7）
    public static readonly int GETUP_PROTECT_TICKS = (int)(1 * TICK_RATE);
    /// 撞墙硬直延长 10f（GDD §5.8）
    public const int WALL_STUN_EXTEND_TICKS = 10;

    // ---- 控制值（GDD §7.4） ----
    public const long CONTROL_VALUE_MAX = 100;
    /// 未受控时 20/s 回落（GDD §7.4）
    public static readonly long CONTROL_DECAY_PER_TICK_NUM = 20, CONTROL_DECAY_PER_TICK_DEN = 60;
    /// Break 免控 1.5s（GDD §7.4）
    public static readonly int BREAK_TICKS = (int)(3 * TICK_RATE / 2);

    // ---- 资源（GDD §9） ----
    /// MP 自然回复 20/s（ADR-0003 §1: 连续量，不逐 Tick 发事件）
    public static readonly long MP_REGEN_PER_TICK_NUM = 20, MP_REGEN_PER_TICK_DEN = 60;

    // ---- 伤害公式（GDD §2.5.1） ----
    /// 防御系数 D = 1200/(1200+DEF)；基线 DEF=800 → D=0.6
    public const long DEFENSE_CONST = 1200;

    // ---- 投射物（GDD §4.5） ----
    public const int PROJECTILE_LIFETIME_TICKS = (int)(3 * TICK_RATE);   // 存活 3s
    public const int MAX_PROJECTILES_PER_FIGHTER = 8;                    // 每玩家同屏上限
    // ---- Flight 原语（DDQ-B5-6 裁定方案 B: 通用 FlightTicks 域+重力免除；触发入口仍 DDQ）----
    public const long FLIGHT_HEIGHT_CAP_M = 6;                           // 飞行高度上限 6m（GDD §14.4.3）
    public const long FLIGHT_HITDOWN_DMG_CAP = 1200;
    public const int PULL_MOVE_TICKS = 15;                               // 拉拽位移窗（15T 内按 PullVel 强制移动——与击退摩擦同律）                     // 飞行中被击中→击坠，所受该击伤害封顶（GDD §14.4.3）

    // ---- 输入缓冲（GDD §2.3.1/ADR-0010 §2） ----
    public const int INPUT_BUFFER_TICKS = 12;        // 通用缓冲 12f
    public const int BASIC_CHAIN_BUFFER_TICKS = 18;  // 普攻段间缓冲 18f
    public const int BASIC_CANCEL_TO_SKILL_TICKS = 4;// 普攻→技能取消: 生效帧后 4f 起
    public const int MAX_BUFFERED_COMMANDS = 8;      // ADR-0010: 队列上限（客户端 64，Sim 侧裁决槽 8）

    // ---- 取消/连招（GDD §8.2/§8.5） ----
    /// 强制中断代价: 60 MP + CD 4s（GDD §10.4）
    public const long FORCE_CANCEL_MP_COST = 60;
    public static readonly int FORCE_CANCEL_CD_TICKS = (int)(4 * TICK_RATE);

    // ---- 空间规格（GDD §2.1.2/SPEC-0005 §2） ----
    public static readonly long FIGHTER_RADIUS = RheM(0.45m);       // 站立碰撞体 r=0.45m
    public static readonly long FIGHTER_HEIGHT = RheM(1.8m);        // 站立碰撞体高 1.8m
    public static readonly long TORSO_HALF_W = RheM(0.45m);         // 躯干盒 0.9×1.6×0.6 → halfW 0.45
    public static readonly long TORSO_HALF_D = RheM(0.30m);         // halfD 0.30
    public static readonly long TORSO_TOP = RheM(1.6m);             // 高度带 [0, 1.6]
    public static readonly long HEAD_RADIUS = RheM(0.18m);          // 头部球 r=0.18（SPEC-0006 §1.1）
    public static readonly long HEAD_CENTER_Y = RheM(1.6m);         // 球心高度（PA-H1 GDD-GAP 约定: 躯干顶+0）
    /// 近战 hitbox 默认高度带（PA-H5: [0.2, 1.9]）
    public static readonly long MELEE_BAND_LOW = RheM(0.2m), MELEE_BAND_HIGH = RheM(1.9m);
    /// proj 默认 aimHeight 1.2m；弱点/头部标注 → 1.6m（PA-H5）
    public static readonly long PROJ_AIM_HEIGHT_DEFAULT = RheM(1.2m);
    public static readonly long PROJ_AIM_HEIGHT_HEAD = RheM(1.6m);
    /// 倒地 Hurtbox: 长条盒 1.7×0.4×0.8（GDD §2.1.2）——高度带 [0, 0.4]
    public static readonly long DOWN_TORSO_TOP = RheM(0.4m);
    /// 空中高度上限 12m 软性压回（GDD §2.1.1）
    public static readonly long CEILING_HEIGHT = RheM(12m);

    // ---- 格挡/盾值/完美格挡（GDD §6.2/§6.3；盾值/减伤来自技能 special 数据化） ----
    public const int GUARD_BLOCK_HALF_DEG = 60;          // 正面 120° 扇区 → 半角 60°（GDD §6.2）
    public static readonly long GUARD_HP_TAKE_NUM = 40, GUARD_HP_TAKE_DEN = 100;   // 减伤 60% → HP 承 40%
    public static readonly long GUARD_SHIELD_TAKE_NUM = 120, GUARD_SHIELD_TAKE_DEN = 100;  // 盾扣 = 伤害 ×1.2
    public const int PARRY_WINDOW_TICKS = 6;             // 完美格挡 6f（§6.3）
    public static readonly int PARRY_INTERVAL_TICKS = (int)(TICK_RATE / 2);   // 间隔 0.5s
    public const int PARRY_ATTACKER_STUN = 20;           // 弹刀: 攻击者强硬直 20f（§6.3）
    public const int PARRY_COUNTER_WINDOW = 15;          // 守方 15f 反击窗（§6.3）
    public const int GUARD_BREAK_STUN = 45;              // 破盾强硬直 45f（§6.2）
    public static readonly int SHIELD_REGEN_TICKS = (int)(8 * TICK_RATE);      // 盾 8s 恢复至满

    // ---- 反击（GDD §6.6；奖励封顶: 攻击者强硬直 20f） ----
    public const int COUNTER_ATTACKER_STUN = 20;

    // ---- 抓取（GDD §4.1/§7.2；Grabbed 完全受控+唯一免受其他伤害） ----
    public static readonly long GRAB_HOLD_DISTANCE = RheM(0.9m);   // 被抓者维持于抓取者身前

    // ---- 翻滚/耐力（GDD §10.1/§10.2 原著通用技） ----
    public const int ROLL_TICKS = 30;                    // 30f
    public static readonly long ROLL_DISTANCE = RheM(3m);          // 3m
    public const int ROLL_INVULN_START = 4;              // 无敌帧 4–18f
    public const int ROLL_INVULN_END = 18;
    public const long STAMINA_MAX = 100;
    public const long STAMINA_REGEN_PER_SEC = 10;        // 战斗中 10/s
    public const long STAMINA_ROLL_COST = 25;
    public const long STAMINA_UKEMI_COST = 15;
    public const long STAMINA_SPRINT_PER_SEC = 10;       // 疾跑 10/s——v1 无疾跑档（登记）

    // ---- 地形（GDD §3.3/§3.5/§19） ----
    public static readonly long STEP_UP_HEIGHT = RheM(0.5m);   // 台阶 ≤0.5m 自动踏上
    public const long FALL_DAMAGE_PER_M = 80;            // 坠落伤害 高度×80
    public const long FALL_DAMAGE_CAP = 1200;
    public static readonly long FALL_DAMAGE_MIN_DROP = RheM(2m);   // 高差 >2m 才结算
    public const int JUMP_LAND_LAG_TICKS = 6;            // 落地硬直 6f（受身则 0）

    // ---- Steer（SPEC-0001；GDD §4.1 追踪转向 ≤120°/s） ----
    public const int STEER_DEG_PER_SEC_DEFAULT = 120;

    // ---- BroadPhase（SPEC-0005 PA-6） ----
    public const long GRID_CELL_SIZE = 8 * Fixed.ONE;   // 8m cell

    // ---- 量化工具 ----
    private static long RheM(decimal meters) =>
        (long)Math.Round(meters * Fixed.ONE, MidpointRounding.ToEven);

    private static long Rhe(long n, long d)
    {
        long q = n / d, r = n % d, twice = r * 2;
        if (twice > d || (twice == d && (q & 1) != 0)) q++;
        return q;
    }

    /// m/s → 每 Tick 位移 raw（Q32.16 米/Tick）: RHE(mps × ONE / TICK_RATE)
    public static long RhePerTickFromMps(long mps) => Rhe(mps * Fixed.ONE, TICK_RATE);
}
