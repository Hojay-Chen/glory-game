using System;
using System.Collections.Generic;
using Arena.Core.Collision;
// PRODUCTION - Arena.Core
// ADR-0002 §3 段7 RuntimeDef + ADR-0003/0009: SkillRuntimeData（Compiler 量化产物，Sim 唯一消费形态）
// SkillTimeline: 技能执行状态机——Startup → Active(hitSchedule 逐段 hitbox) → Recovery + 取消窗。
// 命中去重: per (execution, victimId, segmentIndex)——ADR-0003 SemanticKey 幂等。
namespace Arena.Core.Sim;

/// hitbox 几何（Compiler 量化；Q32.16）
public enum GeoKind : byte { None = 0, Sector = 1, Circle = 2, Obb = 3, Cylinder = 4 }

public readonly record struct HitboxGeometry(
    GeoKind Kind,
    long Radius,          // Sector/Circle/Cylinder
    int HalfDegIndex,     // Sector 半角的半度数（α=90° → 90）
    long HalfForward,     // Obb
    long HalfAcross,      // Obb
    long BandLow,         // 高度带下沿（绝对高度基点 = Owner PosY）
    long BandHigh)        // 高度带上沿
{
    public static readonly HitboxGeometry None = default;
}

/// 状态效果（GDD §7.3 路由集；Compiler 解析 status 列产物）
public readonly record struct StatusEffectDef(
    StatusKind Kind,
    long PotencyQ,        // Slow: 减速比例 Q32.16（0.30→19661）；DoT: 每秒伤害值（整数点数）
    int DurationTicks,
    int ChancePercent)    // 0 = 必定；>0 = Roll100 < chance
{
    public bool HasChance => ChancePercent > 0;
}

/// 霸体窗（GDD §6.4；armor 列解析产物）
public readonly record struct ArmorWindowDef(bool SuperArmor, int StartTick, int EndTick)
{
    public bool Covers(int tickOffset) => tickOffset >= StartTick && tickOffset < EndTick;
}

/// 无敌窗（GDD §6.5；invincible_f 列解析产物）
public readonly record struct InvulnWindowDef(int StartTick, int EndTick)
{
    public bool Covers(int tickOffset) => tickOffset >= StartTick && tickOffset < EndTick;
}

/// 技能运行时定义（全部数值已量化——Core 禁止再解析 CSV 原始语法）
public sealed class SkillRuntimeData
{
    public ushort RuntimeId { get; init; }        // Catalog 内稳定 u16（行序）
    public string SkillId { get; init; } = "";    // CSV 主键（诊断/映射）
    public string ClassId { get; init; } = "";
    public byte Tier { get; init; }               // 0=BAS 1=T1 2=T2 3=T3 4=T4 5=U
    public string Type { get; init; } = "";       // basic/active/grab/…
    public long MpCost { get; init; }
    public long CooldownTicks { get; init; }
    public int StartupTicks { get; init; }
    public int ActiveTicks { get; init; }
    public int RecoveryTicks { get; init; }
    public int[] HitSchedule { get; init; } = Array.Empty<int>();  // 各段激活偏移（相对 active 起点）
    public HitboxGeometry Geo { get; init; } = HitboxGeometry.None;
    public string DamageType { get; init; } = "phys";  // phys/magic（格挡物理门控消费）
    public long DamageMultQ { get; init; }        // Q32.16
    public long HeadMultQ { get; init; }          // 弱点头部倍率（PA-H5/SPEC-0006 §1.4: 近战 1.5 / 巴雷特类 2.0）
    public int HitstunTicks { get; init; }
    public long KnockbackVelQ { get; init; }      // 击退初速 Q32.16 m/s（= kb_m × 9）
    public long LaunchVelQ { get; init; }         // 浮空初速 Q32.16 m/s
    public StatusEffectDef[] Statuses { get; init; } = Array.Empty<StatusEffectDef>();
    public ArmorWindowDef? Armor { get; init; }
    public InvulnWindowDef? Invuln { get; init; }
    public bool Sweep { get; init; }              // 扫地（可打倒地）
    public bool ArmorBreak { get; init; }         // 破霸体（GDD §6.4）
    public bool IsProjectile { get; init; }       // proj/lob——spawn Projectile 而非自体 hitbox
    public bool IsLob { get; init; }
    public long ProjSpeedQ { get; init; }         // m/s Q32.16
    public long ProjRadius { get; init; }
    public int ProjRangeTicks { get; init; }      // 射程/存活
    public long AimHeightQ { get; init; }         // PA-H5: 1.2 默认 / 1.6 弱点
    public byte CancelMinTier { get; init; }      // 0=any 1=BAS..5=U 255=none（GDD §8.2）
    public bool JumpCancel { get; init; }
    public ushort ChainNext { get; set; }         // 普攻链下一段 RuntimeId（0=无；Catalog 装配期链接）
    public byte ChainN { get; init; }
    public bool ForcedDown { get; init; }         // 受身无效（GDD §5.6 圆舞棍/背摔/踏射）
    public string Special { get; init; } = "-";   // 签名路由预留（ADR-0008）
    // ---- Phase 5 原语（全部来自数据，禁止按 skillId 分支） ----
    public GuardDef? Guard { get; init; }         // 格挡姿态（BLA_T1_002 等）
    public bool IsGrab { get; init; }             // type=grab——抓取体系（GDD §4.1/§7.2）
    public bool IsCounter { get; init; }          // type=counter——反击架势（GDD §6.6）
    public bool IsHold { get; init; }             // active=hold——姿态持续至释放
    public int SteerRateDegPerSec { get; init; }  // controlled/可转向 → SPEC-0001 饱和步进
    public int ChargeTicks { get; init; }         // 蓄力 Ts → startup 追加
    public long ChargeBonusQ { get; init; }       // 蓄力 +P% → 伤害乘区（LAU_T3_001）
    public bool IsStealth { get; init; }          // 潜行（THF_T1_001 完全隐身）
    public long StealthSpeedPct { get; init; }    // 潜行移速百分比（数据: 移速60%）
    public bool IsReflect { get; init; }          // 法术反射（KNI_T3_003/WRK_T3_003）
    public int ReflectWindowTicks { get; init; }  // 反射窗（数据: 2s窗口）
    public bool FollowHeading { get; init; }      // 可控弹: 弹体跟随施法者朝向（念龙波）
    public bool IsSummon { get; init; }           // 召唤技（type=summon/unit hitbox/召唤位）
    public long SummonHp { get; init; }           // 单位 HP（数据 HP900/HP1200 或 600 基线）
    public int SummonLifetimeTicks { get; init; } // 存在期（存在90s/60s）
    public bool SummonFlying { get; init; }
    public bool SummonTank { get; init; }
    public int RequireBehindDeg { get; init; }    // MF-1: 需背身 N°（NJA_T3_001 背身缚首术 120°）
    public OrbTagKind OrbTag { get; init; }       // 炫纹触发标签（Compiler 从 special 炫纹:X 解析）
    // ---- Batch 4: 自增益通道（通用 B 类语义——数值全部来自 special 解析，零签名依赖） ----
    public string Name { get; init; } = "";       // CSV skill_name（家族分类数据源——SBL 波动剑系）
    public long SelfBuffAtkPctQ { get; init; }    // 施法自增益 ATK+P%（嗜血 20%/嗜血奋战 8%）
    public long SelfDrainPctQ { get; init; }      // 自伤脉率 P%/s ×HpMax（嗜血系 1.5%/s）
    public long LifestealPctQ { get; init; }      // 正嗜血: 造成伤害 P% 转回复（嗜血奋战 10%）
    // ---- Phase 7 Batch 2: Deploy / Heal 通道（统一实体载荷语义） ----
    public DeployKind DeployKind { get; init; }   // 部署变体（数据: 触发/悬浮/侦察/wall/zone）
    public long DeployHp { get; init; }           // 部署物 HP（HP300/600HP/HP200/HP150）
    public long TriggerRadius { get; init; }      // 陷阱触发半径（deploy:r1.5:触发）
    public long AuraRadius { get; init; }         // 光环半径（zone:r4.0）
    public int AuraPulseIntervalTicks { get; init; }  // 光环脉冲间隔（WB 基线 1s）
    public long HealAmountQ { get; init; }        // heal 数值（PRI 系 damage_mult 列=直接 HP 量）
    public bool HealIsMana { get; init; }         // 回蓝（PRI_T3_005 30%蓝）
    public long HealPulseIntervalTicks { get; init; } // HoT 脉冲间隔（GAN 每3s → 180T）
    public int HealPulseCount { get; init; }      // HoT 脉冲次数（18s/3s = 6）
}

/// 部署变体（GDD §14 数据推导——全部由 hitbox/special 文本结构决定，无 skillId 分支）
public enum DeployKind : byte { None = 0, Trap = 1, Aura = 2, Wall = 3, Scout = 4, Mirror = 5, Taunt = 6 }

/// 炫纹属性（GDD §9.3: 光/冰/火/暗/无属性——BMG 签名资源触发标签）
public enum OrbTagKind : byte { None = 0, Light = 1, Ice = 2, Fire = 3, Dark = 4, NonElemental = 5 }

/// 格挡姿态定义（GDD §6.2/§6.3；盾值/减伤率来自技能 special 数据）
public sealed record GuardDef(
    long ShieldMax,            // 盾值（数据: 盾值1500）
    long MitigateNum,          // 化解物理 70% → HP 承 30%（数据化覆盖 GDD 60% 基线）
    long MitigateDen,
    bool PhysicalOnly);        // 化解物理 = 仅 phys 伤害类型（magic 绕过）

/// active hitbox 实例（Tick 内瞬态语义锚——不入 Snapshot，由 execution 重建）
public sealed class ActiveHitbox
{
    public required int Uid { get; init; }
    public required int OwnerId { get; init; }
    public required SkillRuntimeData Def { get; init; }
    public required byte SegmentIndex { get; init; }
    public required int SpawnTick { get; init; }   // 绝对 Tick
    public required int ExpireTick { get; init; }  // 绝对 Tick（不含）
    // 锚定: Owner 在 SpawnTick 的位置/朝向（PA-7 相对扫掠基准）
    public required long AnchorX { get; init; }
    public required long AnchorZ { get; init; }
    public required long AnchorHeading { get; init; }
    public required long AnchorVelX { get; init; } // Owner 本 Tick 位移（相对扫掠）
    public required long AnchorVelZ { get; init; }
    public readonly HashSet<int> HitVictims = new();   // (victimId) per segment——SemanticKey 幂等
}

/// 技能执行（SkillTimeline 状态机实例——入 Snapshot）
public sealed class SkillExecution
{
    public int Uid { get; set; }
    public ushort SkillRuntimeId { get; set; }
    public int OwnerId { get; set; }
    public int CastTick { get; set; }
    public int CurrentOffset { get; set; }       // 自 cast 起 Tick 数
    public bool HitConfirmed { get; set; }       // 命中确认（取消资格，GDD §8.2）
    public bool Terminated { get; set; }         // 被取消/打断
    public bool IsBasic => SkillRuntimeId != 0 && Def?.Type == "basic";
    public byte SpawnedSegments;                 // 已 spawn 的段数（hitSchedule 推进指针）
    public HashSet<int>[]? SegmentVictims;       // per-segment 去重（Snapshot 序列化）
    public int StartupDeltaTicks;                // 签名前摇修正（SBL 波动共鸣 −1f/层——每 cast 独立）
    [System.Text.Json.Serialization.JsonIgnore]
    public SkillRuntimeData? Def;                // 运行时引用（由 Catalog 恢复，不入快照）

    public int EffectiveStartup => (Def?.StartupTicks ?? 0) + StartupDeltaTicks;
    public int TotalTicks => Def is null ? 0 : EffectiveStartup + Def.ActiveTicks + Def.RecoveryTicks;
    public bool InStartup => Def is not null && CurrentOffset < EffectiveStartup;
    /// hold 姿态: 生效窗开放至释放（取消/切换/打断）——无自然上界（GDD §6.2 格挡姿态）
    public bool InActive => Def is not null && CurrentOffset >= EffectiveStartup
        && (Def.IsHold || CurrentOffset < EffectiveStartup + Def.ActiveTicks);
    public bool InRecovery => Def is not null && !Def.IsHold
        && CurrentOffset >= EffectiveStartup + Def.ActiveTicks && CurrentOffset < TotalTicks;
    public int ActiveEndOffset => Def is null ? 0 : EffectiveStartup + Def.ActiveTicks;
    public int RecoveryStartOffset => ActiveEndOffset;
}

/// hitSchedule → 段激活窗（P-2 编译期预计算 + 运行时窗口推导）
public static class SkillTimeline
{
    /// 段 k 的 hitbox 存活窗（绝对偏移 [start, end)——至下一段激活或 active 结束）
    /// startupTicks: 生效前摇（exec.EffectiveStartup——签名前摇修正时传入覆盖 def 值）
    public static (int start, int end) SegmentWindow(SkillRuntimeData def, int segment, int? startupTicks = null)
    {
        int startup = startupTicks ?? def.StartupTicks;
        int activeStart = startup;
        int activeEnd = startup + def.ActiveTicks;
        int start = activeStart + (segment < def.HitSchedule.Length ? def.HitSchedule[segment] : 0);
        int end = segment + 1 < def.HitSchedule.Length
            ? Math.Min(activeStart + def.HitSchedule[segment + 1], activeEnd)
            : activeEnd;
        if (end <= start) end = start + 1;   // 至少 1T 判定窗
        return (start, end);
    }
}
