using System;
using System.Collections.Generic;
// PRODUCTION - Arena.Core
// SPEC-0006: Hurtbox 定义与 HitRegion 枚举
namespace Arena.Core.Collision;

public enum HitRegion : byte
{
    None = 0, Torso = 1, Head = 2,
    LeftArm = 3, RightArm = 4, LeftLeg = 5, RightLeg = 6, Weapon = 7
}

public static class HitRegionPriority
{
    // SPEC-0006 §1.2: Head=20 > Torso=10 > 四肢=5 > Weapon=3 > None=0
    private static readonly int[] Values = { 0, 10, 20, 5, 5, 5, 5, 3 };
    public static int Priority(HitRegion r) => Values[(int)r];
    /// 命中多 Region 时取 priority 最大者（PA-H2 唯一选取规则）
    public static HitRegion Select(HitRegion a, HitRegion b) =>
        Priority(a) >= Priority(b) ? a : b;
}

/// Hurtbox 静态定义（GDD §2.1 站立 profile，全体职业统一 v1）
public static class HurtboxProfile
{
    // Torso: AABB halfW=0.45m halfD=0.30m 高度带 [0, 1.6m]
    public const long TorsoHalfWRaw = 0_45_0000;  // 0.45m in 4-decimal fixed (简化白盒精度)
    public const long TorsoHalfDRaw = 0_30_0000;
    public const long TorsoTopRaw = 1_60_0000;    // 1.6m
    // Head: Sphere r=0.18m 球心高度 1.6m
    public const long HeadR = 0_18_0000;
    public const long HeadCenterY = 1_60_0000;
}
