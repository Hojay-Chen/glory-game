using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena.Infra.Data;

public sealed record SkillDef
{
    public required string SkillId { get; init; }
    public required string SkillName { get; init; }
    public required string ClassId { get; init; }
    public required string Tier { get; init; }
    public required string Type { get; init; }
    public required int CostMp { get; init; }
    public required long CooldownTicks { get; init; }
    public required int StartupTicks { get; init; }
    public required int ActiveTicks { get; init; }
    public required int RecoveryTicks { get; init; }
    public required int HitIntervalTicks { get; init; }
    public required string ActiveRaw { get; init; }
    public required string HitboxRaw { get; init; }
    public required string RangeM { get; init; }
    public required string AngleDeg { get; init; }
    public required double DamageMult { get; init; }
    public required string DamageType { get; init; }
    public required int Hits { get; init; }
    public required int HitstunTicks { get; init; }
    public required double KnockbackM { get; init; }
    public required double LaunchV { get; init; }
    public required string StatusRaw { get; init; }
    public required string ArmorRaw { get; init; }
    public required string InvincibleRaw { get; init; }
    public required int Sweep { get; init; }
    public required int Intercept { get; init; }
    public required int Channel { get; init; }
    public required string CancelMinTier { get; init; }
    public required int JumpCancel { get; init; }
    public required string Special { get; init; }
    public required int[] HitSchedule { get; init; }
    public required List<string> PendingReviewFlags { get; init; }
}

public sealed record ValidationIssue(string Severity, string Rule, string Detail);
public sealed record CompilerResult(bool Success, int ValidRows, List<ValidationIssue> Blockers, List<ValidationIssue> Warnings, string DataVersionHash);
