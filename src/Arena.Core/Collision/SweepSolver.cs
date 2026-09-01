using System;
using System.Collections.Generic;
// PRODUCTION - Arena.Core
// SPEC-0005 §4/§5: ShapeLibrary + 解析 Sweep（Intra-Tick 线性公理 ⇒ 全解析可解）
using Arena.Core.Calc;

namespace Arena.Core.Collision;

// ---- ShapePrimitive ----
public enum ShapeKind : byte { Point = 0, Circle = 1, AABB = 2, ConvexPoly = 3, Sphere = 4, Segment = 5 }

public readonly struct Shape
{
    public ShapeKind Kind { get; }
    public long R { get; }          // Circle/Sphere 半径
    public long HalfW { get; }      // AABB 半宽
    public long HalfD { get; }      // AABB 半深
    public long Height { get; }     // 高度带顶（底=0 或 mover.y）
    public int VertCount { get; }   // ConvexPoly 顶点数

    public Shape(ShapeKind kind, long r = 0, long halfW = 0, long halfD = 0, long height = 0, int vertCount = 0)
    { Kind = kind; R = r; HalfW = halfW; HalfD = halfD; Height = height; VertCount = vertCount; }

    public static Shape Circle(long r) => new(ShapeKind.Circle, r: r);
    public static Shape Point => new(ShapeKind.Point);
    public static Shape AABB(long halfW, long halfD, long height) => new(ShapeKind.AABB, halfW: halfW, halfD: halfD, height: height);
    public static Shape Sphere(long r, long height) => new(ShapeKind.Sphere, r: r, height: height);
}

// ---- Contact ----
public readonly struct Contact
{
    public long ToiRaw { get; init; }           // Q32.16 ∈ [0, ONE]
    public byte LayerRank { get; init; }         // 0=terrain 1=push 2=combat
    public int OtherId { get; init; }           // 对方实体 Id（0=terrain）
    public long HitPointX { get; init; }
    public long HitPointZ { get; init; }
    public long NormalX { get; init; }          // 归一化 Q32.16
    public long NormalZ { get; init; }
    public byte CollisionKind { get; init; }    // 0=Stop 1=Bounce 2=Hit 3=Push

    public static readonly IComparer<Contact> Comparer = Comparer<Contact>.Create((a, b) =>
    {
        int c = a.ToiRaw.CompareTo(b.ToiRaw);
        if (c != 0) return c;
        c = a.LayerRank.CompareTo(b.LayerRank);
        if (c != 0) return c;
        c = a.OtherId.CompareTo(b.OtherId);
        return c != 0 ? c : a.CollisionKind.CompareTo(b.CollisionKind);
    });
}

// ---- SweepSolver（SPEC-0005 §5 解析求解——纯 int64，零 float）----
public static class SweepSolver
{
    /// Point/Circle(r) 从 from 到 to 的线性扫掠 vs 静态圆(Cx,Cz,R)。
    /// 解析二次方程：|P0+tD−C|² = (r+R)²，t ∈ [0,1]。
    /// 返回 TOI Raw（Q32.16 of [0,1]），-1 = 无接触。
    public static long SweepCircleVsCircle(
        long fromX, long fromZ, long dx, long dz,
        long cx, long cz, long r1, long r2)
    {
        long ex = fromX - cx;
        long ez = fromZ - cz;
        long a = dx * dx + dz * dz;
        if (a == 0) return -1;
        long b = -2 * (ex * dx + ez * dz);
        long c = ex * ex + ez * ez - (r1 + r2) * (r1 + r2);
        long disc = b * b - 4 * a * c;
        if (disc < 0) return -1;
        long sq = DeterministicMath.ISqrt(disc);
        sq = DeterministicMath.ISqrt(disc);
        long denom = 2 * a;
        // t_in = (−b − √Δ) / 2a
        long tInNum = -b - sq;
        if (tInNum < 0) tInNum = 0;
        // TOI = tInNum / denom，量化到 Q32.16 of [0,1]
        return DeterministicMath.DivRoundHalfEven(tInNum * Fixed.ONE, denom);
    }

    /// Point→Circle：简化的距离谓词版本（弹→Fighter 圆柱体）
    public static bool PointInCircle(long px, long pz, long cx, long cz, long r)
    {
        long dx = px - cx, dz = pz - cz;
        return dx * dx + dz * dz <= r * r;
    }

    /// Point→AABB：水平投影 + 高度带
    public static bool PointInAABB(long px, long py, long pz,
        long cx, long cz, long halfW, long halfD, long hBottom, long hTop)
    {
        return px >= cx - halfW && px <= cx + halfW
            && pz >= cz - halfD && pz <= cz + halfD
            && py >= hBottom && py <= hTop;
    }

    /// 线性扫掠：Point 从 (fromX,fromZ) 到 (toX,toZ) 是否穿过 Circle(cx,cz,R)。
    /// 返回 t ∈ [0,ONE]（Q32.16）或 -1。
    public static long SweepPointVsCircle(
        long fromX, long fromZ, long toX, long toZ,
        long cx, long cz, long r)
    {
        long dx = toX - fromX, dz = toZ - fromZ;
        long ex = fromX - cx, ez = fromZ - cz;
        long a = dx * dx + dz * dz;
        if (a == 0) return PointInCircle(fromX, fromZ, cx, cz, r) ? 0 : -1;
        long b = 2 * (ex * dx + ez * dz);
        long c = ex * ex + ez * ez - r * r;
        long disc = b * b - 4 * a * c;
        if (disc < 0) return -1;
        long sq = DeterministicMath.ISqrt(disc);
        long t1Num = -b - sq, t2Num = -b + sq;
        // 找到第一个进入 [0, ONE] 的根（TOI）
        long t1 = t1Num >= 0 ? DeterministicMath.DivRoundHalfEven(t1Num * Fixed.ONE, 2 * a) : -1;
        long t2 = DeterministicMath.DivRoundHalfEven(t2Num * Fixed.ONE, 2 * a);
        if (t1 >= 0 && t1 <= Fixed.ONE) return t1;
        if (t2 >= 0 && t2 <= Fixed.ONE) return t2;  // 起点已在内部
        return -1;
    }
}
