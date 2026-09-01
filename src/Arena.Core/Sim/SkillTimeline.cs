using System.Collections.Generic;
// PRODUCTION - Arena.Core
// ADR-0003/0009: SkillTimeline——技能执行的确定性时间轴
// Startup → Active(含 hitSchedule) → Recovery，取消窗/无敌/霸体按 ADR-0001 语义独立表达
using Arena.Core.Calc;

namespace Arena.Core.Sim;

/// SkillRuntime 生成的 Hitbox 描述（激活窗口内每个 Tick 的空间语义）
public struct ActiveHitbox
{
    public long ToiHit;          // 未使用（预留）
    public int OwnerId;
    public ushort SkillId;
    public byte SegmentIndex;    // 0-based（多段）
    public int StartupTick;      // 相对 cast 的 Tick
    public int ActiveStart;      // 相对 cast
    public int ActiveEnd;        // 相对 cast（含）
    public long DamageMultRaw;   // Q32.16
    public long HitstunTicks;
    public long KnockbackRaw;    // Fixed Raw (米)
    public long LaunchVRaw;      // Fixed Raw (米/Tick)
    public bool IsSweep;         // 扫地
    public bool IsLaunch;
    public bool HasHitRegion;
    public byte HitRegion;
    public long HitboxShapeR;    // 简化：v1 所有 hitbox 视为圆（水平）
    public long HitboxHeight;    // 中心高度
}

/// 技能执行状态（SkillRuntime 输出，SimWorld 消费）
public sealed class SkillExecution
{
    public required string SkillId { get; set; }
    public int OwnerId { get; set; }
    public int CastTick { get; set; }
    public int StartupTicks { get; set; }
    public int ActiveTicks { get; set; }
    public int RecoveryTicks { get; set; }
    public int TotalTicks => StartupTicks + ActiveTicks + RecoveryTicks;
    public int CurrentTick { get; set; }   // 0-based since cast
    public byte Phase { get; set; }         // 0=startup 1=active 2=recovery 3=done
    public bool HitConfirmed { get; set; }
    public bool IsHold { get; set; }
    public bool IsControlled { get; set; }
    public List<ActiveHitbox> Hitboxes { get; set; } = new();
    public long MpCost { get; set; }
    public bool IsBasicAttack { get; set; }
    public int ChainN { get; set; }

    public bool IsExpired => CurrentTick >= TotalTicks;
    public bool InStartup => CurrentTick < StartupTicks;
    public bool InActive => CurrentTick >= StartupTicks && CurrentTick < StartupTicks + ActiveTicks;
    public bool InRecovery => CurrentTick >= StartupTicks + ActiveTicks && CurrentTick < TotalTicks;
}
