using System;
using System.Collections.Generic;
using Arena.Core.Calc;
// PRODUCTION - Arena.Core
// SPEC-0005 §4/§5: ConvexRegion——全部碰撞图元的统一约束表示。
// 凸区域 = 线性半平面约束集 ∩ 圆盘约束集。全部坐标/半径 Q32.16。
// 求解器只消费本表示（SweepSolver.SweepRegion）——新图元 = 新构造器，零新求解路径。
// 生命周期: 每 Tick 由 Sim 构造（hitbox/hurtbox 投影），瞬态不入 Snapshot（SPEC-0005 §9）。
namespace Arena.Core.Collision;

/// 线性约束: NX·x + NZ·z ≤ B（N 为 Q32.16 归一化法线，|N| ≈ ONE）
public readonly record struct HalfPlane(long NX, long NZ, long B);

/// 圆盘约束: (x−CX)² + (z−CZ)² ≤ R²
public readonly record struct Disk(long CX, long CZ, long R);

/// 凸区域（半平面 ∩ 圆盘集合）。瞬态对象——每 Tick 重建。
public sealed class ConvexRegion
{
    public readonly List<HalfPlane> HalfPlanes = new();
    public readonly List<Disk> Disks = new();
    // 包围盒（BroadPhase 入格用；构造时精确或保守计算）
    public long MinX { get; private set; }
    public long MaxX { get; private set; }
    public long MinZ { get; private set; }
    public long MaxZ { get; private set; }

    public bool IsEmpty => HalfPlanes.Count == 0 && Disks.Count == 0;

    private void SetBounds(long minX, long maxX, long minZ, long maxZ)
    { MinX = minX; MaxX = maxX; MinZ = minZ; MaxZ = maxZ; }

    private void AddPlane(long nx, long nz, long b)
    {
        HalfPlanes.Add(new HalfPlane(nx, nz, b));
        // 非轴对齐法线的半平面不收紧包围盒（保守方向——BroadPhase 候选集只会偏大，PA-6 保守性保证）
    }

    private void AddDisk(long cx, long cz, long r)
    {
        Disks.Add(new Disk(cx, cz, r));
        if (cx - r < MinX) MinX = cx - r;
        if (cx + r > MaxX) MaxX = cx + r;
        if (cz - r < MinZ) MinZ = cz - r;
        if (cz + r > MaxZ) MaxZ = cz + r;
    }

    private ConvexRegion() { }

    /// 圆盘区域（circle/aura/zone hitbox、Fighter 体、立柱）
    public static ConvexRegion Circle(long cx, long cz, long r)
    {
        var rg = new ConvexRegion();
        rg.SetBounds(cx - r, cx + r, cz - r, cz + r);
        rg.AddDisk(cx, cz, r);
        return rg;
    }

    /// 轴对齐矩形（掩体/技能墙/静态 AABB）——4 半平面精确表示，无角圆
    public static ConvexRegion Aabb(long cx, long cz, long halfW, long halfD)
    {
        var rg = new ConvexRegion();
        rg.SetBounds(cx - halfW, cx + halfW, cz - halfD, cz + halfD);
        // 轴对齐法线 (±1,0)/(0,±1) 精确无量化误差
        rg.AddPlane(Fixed.ONE, 0, cx + halfW);
        rg.AddPlane(-Fixed.ONE, 0, -(cx - halfW));
        rg.AddPlane(0, Fixed.ONE, cz + halfD);
        rg.AddPlane(0, -Fixed.ONE, -(cz - halfD));
        return rg;
    }

    /// 半空间（结界墙等无限延伸阻挡体）: n·p ≤ b（n 单位轴向量）
    public static ConvexRegion HalfSpace(long nx, long nz, long b)
    {
        var rg = new ConvexRegion
        {
            MinX = long.MinValue / 4, MaxX = long.MaxValue / 4,
            MinZ = long.MinValue / 4, MaxZ = long.MaxValue / 4,
        };
        rg.AddPlane(nx, nz, b);
        return rg;
    }

    /// 有向矩形（box/line hitbox——随 Owner 朝向旋转）。
    /// (fx,fz) 单位前向（Q32.16，|f|≈ONE）；几何中心 = 前向中点（调用方计算）。
    public static ConvexRegion Obb(long cx, long cz, long fx, long fz, long halfForward, long halfAcross)
    {
        // 侧向单位向量 r = f 顺时针旋转 90°: (fz, −fx)
        long rx = fz, rz = -fx;
        var rg = new ConvexRegion();
        // OBB 包围盒 = 4 角投影（|f·e|+|r·e| 保守半径圆）
        long boundR = DeterministicMath.MulShift(Math.Abs(fx) + Math.Abs(rx), halfForward)
                   + DeterministicMath.MulShift(Math.Abs(fz) + Math.Abs(rz), halfAcross);
        rg.SetBounds(cx - boundR, cx + boundR, cz - boundR, cz + boundR);
        // 前向面对: f·p ≤ f·C + halfForward
        long bF = DeterministicMath.MulShift(fx, cx) + DeterministicMath.MulShift(fz, cz);
        rg.AddPlane(fx, fz, bF + halfForward);
        rg.AddPlane(-fx, -fz, -(bF - halfForward));
        // 侧向面对: r·p ≤ r·C + halfAcross
        long bR = DeterministicMath.MulShift(rx, cx) + DeterministicMath.MulShift(rz, cz);
        rg.AddPlane(rx, rz, bR + halfAcross);
        rg.AddPlane(-rx, -rz, -(bR - halfAcross));
        return rg;
    }

    /// 圆形扇区（fan/cone hitbox）。heading: Owner 朝向 quantum（SPEC-0001 u16，0=+Z 顺时针）；
    /// halfDegIndex: 半角的半度数（α=90° → 半角 45° → 索引 90）。扇角 ≤180° 由 Compiler 强制。
    /// 精确表示: 楔形（2 半平面）∩ 外接圆盘——无弦近似（对比 SPEC §4 ConvexPoly 弦近似为更精路径）。
    public static ConvexRegion Sector(long cx, long cz, long headingQuantum, int halfDegIndex, long radius)
    {
        // 边界射线方向 d± = Rotate(heading, ±α/2)（E-6 CORDIC + 半度表恒等合成；数学域 (x,z)）
        DeterministicMath.RotateHalfDeg(headingQuantum, halfDegIndex, out var cPlus, out var sPlus);
        DeterministicMath.RotateHalfDeg(headingQuantum, -halfDegIndex, out var cMinus, out var sMinus);
        var rg = new ConvexRegion();
        rg.SetBounds(cx - radius, cx + radius, cz - radius, cz + radius);
        // 外向法线（指向扇区外部，验证: 内部方向 dir(heading) 满足 n·dir < 0）
        // +half 边界（math θ=90°−half）外向 n1 = (s+, −c+)（d+ 旋转 −90°）
        // −half 边界（math θ=90°+half）外向 n2 = (−s−, +c−)（d− 旋转 +90°）
        long n1x = sPlus, n1z = -cPlus;
        long n2x = -sMinus, n2z = cMinus;
        long b1 = DeterministicMath.MulShift(n1x, cx) + DeterministicMath.MulShift(n1z, cz);
        long b2 = DeterministicMath.MulShift(n2x, cx) + DeterministicMath.MulShift(n2z, cz);
        rg.AddPlane(n1x, n1z, b1);
        rg.AddPlane(n2x, n2z, b2);
        rg.AddDisk(cx, cz, radius);
        return rg;
    }

    /// 点含测试（静态谓词，Overlap 用——推挤/部署校验/部位精判）。moverRadius: Minkowski 膨胀。
    public bool Contains(long px, long pz)
    {
        for (int i = 0; i < HalfPlanes.Count; i++)
        {
            var hp = HalfPlanes[i];
            if (DeterministicMath.MulShift(hp.NX, px) + DeterministicMath.MulShift(hp.NZ, pz) > hp.B)
                return false;
        }
        for (int i = 0; i < Disks.Count; i++)
        {
            var d = Disks[i];
            long dx = px - d.CX, dz = pz - d.CZ;
            if (dx * dx + dz * dz > d.R * d.R) return false;
        }
        return true;
    }
}

// SPEC-0005 §6 + SPEC-0006 §2: 碰撞结果（Tick 内瞬态，结算即弃，不入 Snapshot）
public readonly record struct ContactResult
{
    public long ToiRaw { get; init; }            // Q32.16 ∈ [0, ONE]（PA-3: 决策一律用量化值）
    public long TOutRaw { get; init; }
    public byte LayerRank { get; init; }         // 0=Terrain 1=Push 2=Combat（SPEC-0005 §6）
    public int OtherId { get; init; }
    public byte CollisionKind { get; init; }     // ContactKind
    public long HitPointX { get; init; }
    public long HitPointY { get; init; }
    public long HitPointZ { get; init; }
    public long NormalX { get; init; }           // 指向 mover 一侧（SPEC-0005 §5.3）
    public long NormalZ { get; init; }
}

public enum ContactKind : byte { TerrainStop = 0, TerrainBounce = 1, CombatHit = 2, Push = 3 }
public enum TerrainAction : byte { Bounce = 0, Stop = 1, DestroyProjectile = 2, PassThrough = 3 }
