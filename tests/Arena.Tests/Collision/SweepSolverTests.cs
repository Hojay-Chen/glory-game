using System;
using Arena.Core.Collision;
using Xunit;

namespace Arena.Tests.Collision;

/// SPEC-0005 §5 解析 Sweep + PA-2 边界语义 + T54 谓词级用例
public class SweepSolverTests
{
    private const long ONE = Arena.Core.Fixed.ONE;

    // ---- T54b 型: 点扫掠穿越 AABB（两端皆在盒外——终点判定会漏，sweep 必须命中） ----
    [Fact]
    public void Sweep_Point_Crossing_Aabb_Hits_At_Front_Face()
    {
        // 盒: 中心 (0,0) halfW=0.45 halfD=0.30；弹从 (0,−1) → (0,+1)（一个 Tick 内 2m 位移）
        var box = ConvexRegion.Aabb(0, 0, FixedM(0.45m), FixedM(0.30m));
        var hit = SweepSolver.SweepRegion(box, 0, -FixedM(1m), 0, FixedM(2m), FixedM(0m),
            out var toi, out var tOut, out var nx, out var nz);
        Assert.True(hit);
        // 前面 z = −0.30: t = (−0.30 −(−1))/2 = 0.35
        Assert.True(Math.Abs(toi - (long)(0.35 * ONE)) <= 2, $"toi={toi}");
        // 法线指向 mover 一侧（−Z）
        Assert.True(nz < 0, $"nz={nz}");
    }

    // ---- T54c: 近失（横向 0.19m > 头部半径 0.18） ----
    [Fact]
    public void Sweep_NearMiss_OutsideRadius_Misses()
    {
        var head = ConvexRegion.Circle(0, 0, FixedM(0.18m));
        var hit = SweepSolver.SweepRegion(head, FixedM(0.19m), -FixedM(1m), 0, FixedM(2m), 0,
            out _, out _, out _, out _);
        Assert.False(hit);
    }

    // ---- T54d: 恰好边界 = 相切接触（PA-2.3 ≤ 语义） ----
    [Fact]
    public void Sweep_ExactBoundary_Tangent_Is_Contact()
    {
        var head = ConvexRegion.Circle(0, 0, FixedM(0.18m));
        var hit = SweepSolver.SweepRegion(head, FixedM(0.18m), -FixedM(1m), 0, FixedM(2m), 0,
            out var toi, out _, out _, out _);
        // 路径 x=0.18=R 与圆相切于路径中点——相切=接触（PA-2.3），TOI = 路径中点
        Assert.True(hit);
        Assert.True(Math.Abs(toi - ONE / 2) <= 2, $"toi={toi}");
    }

    // ---- PA-2.4: 接触发生在 [0,1] 之前 → 无接触 ----
    [Fact]
    public void Sweep_ContactBeforeWindow_NoContact()
    {
        var disk = ConvexRegion.Circle(0, -FixedM(5m), FixedM(0.5m));
        // mover 从 (0,0) 向 +Z 走 1m——接触在身后，不追溯
        var hit = SweepSolver.SweepRegion(disk, 0, 0, 0, FixedM(1m), 0, out _, out _, out _, out _);
        Assert.False(hit);
    }

    // ---- PA-2.2: 起点重叠 → TOI=0 立即结算 ----
    [Fact]
    public void Sweep_SpawnOverlap_ToiZero()
    {
        var disk = ConvexRegion.Circle(0, 0, FixedM(1m));
        var hit = SweepSolver.SweepRegion(disk, 0, 0, FixedM(3m), 0, 0, out var toi, out _, out var nx, out var nz);
        Assert.True(hit);
        Assert.Equal(0, toi);
        // 起点重叠法线 = 运动反方向（SPEC-0005 §5.3 构造确定）
        Assert.Equal(-ONE, nx);
        Assert.Equal(0, nz);
    }

    // ---- 扇形: 楔形 ∩ 圆盘（BMG_T1_001 天击 fan:r2.6:a100） ----
    [Fact]
    public void Sector_Contains_Frontal_Not_Rear()
    {
        var sector = ConvexRegion.Sector(0, 0, 0, 100, FixedM(2.6m));
        // heading 0 = +Z: 正前 2m 在内
        Assert.True(sector.Contains(0, FixedM(2m)));
        // 正后 2m 在外
        Assert.False(sector.Contains(0, -FixedM(2m)));
        // 侧向 2m（60° 偏转 > 半角 50°）在外
        Assert.False(sector.Contains(FixedM(2m) * 866 / 1000, FixedM(2m) / 2));
        // 侧向 1.2m（25° 偏转 < 50°）在内
        Assert.True(sector.Contains(FixedM(1.2m) * 423 / 1000, FixedM(1.2m) * 906 / 1000));
    }

    [Fact]
    public void Sector_Sweep_FromOutside_Frontal_Hits()
    {
        var sector = ConvexRegion.Sector(0, 0, 0, 100, FixedM(2.6m));
        // mover 从 (0, 3.5) 向 −Z 进入扇区
        var hit = SweepSolver.SweepRegion(sector, 0, FixedM(3.5m), 0, -FixedM(2m), FixedM(0.45m),
            out var toi, out _, out _, out _);
        Assert.True(hit);
        Assert.InRange(toi, 0, ONE);
    }

    // ---- mover 半径膨胀: 0.45m 体圆 vs 薄墙（0.1m 厚）——擦边接触 ----
    [Fact]
    public void Sweep_MoverRadius_Inflation_Catches_Brush()
    {
        var wall = ConvexRegion.Aabb(0, 0, FixedM(0.05m), FixedM(2m));
        // mover 中心距墙面 0.30m（> 0.05）但 < 0.05+0.45 → 体圆接触
        var hit = SweepSolver.SweepRegion(wall, FixedM(0.30m), 0, 0, 0, FixedM(0.45m),
            out _, out _, out _, out _);
        Assert.True(hit);
        // 中心距 0.55m（= 0.05+0.45+ε）→ 不接触
        var miss = SweepSolver.SweepRegion(wall, FixedM(0.55m), 0, 0, 0, FixedM(0.45m),
            out _, out _, out _, out _);
        Assert.False(miss);
    }

    // ---- 3D 球（PA-H1.2: 头部命中唯一路径——T54a 型终点双侧 miss） ----
    [Fact]
    public void Sphere3D_HeadCrossing_BothEndsOutside_Still_Hits()
    {
        // 头部球心 (0, 1.6, 0) r=0.18（弹体半径已并入 → r=0.18）
        // 弹从 (0, 1.6, −1.0) → +Z 1.333m/Tick（巴雷特）: 终点 (0, 1.6, 0.333)
        // 两端 |P−C| = 1.0 / 0.333 均 > 0.18 → 朴素终点判定 miss；解析 sweep 必须命中
        var hit = SweepSolver.SweepPointVsSphere3D(
            0, FixedM(1.6m), -FixedM(1m), 0, 0, FixedM(1.333m),
            0, FixedM(1.6m), 0, FixedM(0.18m),
            out var toi, out _, out _, out _);
        Assert.True(hit);
        // t_in = (1.0 − 0.18)/1.333 = 0.615
        var expect = (long)Math.Round(0.615 * ONE);
        Assert.True(Math.Abs(toi - expect) <= 16, $"toi={toi} expect≈{expect}");
    }

    [Fact]
    public void Sphere3D_HeightBand_Miss_Below()
    {
        // 弹高度 1.2m（躯干带）从头部球下方穿过——头部无接触
        var hit = SweepSolver.SweepPointVsSphere3D(
            0, FixedM(1.2m), -FixedM(1m), 0, 0, FixedM(1.333m),
            0, FixedM(1.6m), 0, FixedM(0.18m),
            out _, out _, out _, out _);
        Assert.False(hit);
    }

    // ---- 解析 vs K=32 oracle 对照（SPEC-0005 §5.2 裁定 2） ----
    [Fact]
    public void Analytic_Matches_Bisection_Oracle_Within_Quantum()
    {
        var sector = ConvexRegion.Sector(0, 0, 30000, 120, FixedM(2.2m));
        var rnd = new Random(1234);
        for (int i = 0; i < 200; i++)
        {
            long fx = rnd.NextInt64(-FixedM(4m), FixedM(4m));
            long fz = rnd.NextInt64(-FixedM(4m), FixedM(4m));
            long dx = rnd.NextInt64(-FixedM(2m), FixedM(2m));
            long dz = rnd.NextInt64(-FixedM(2m), FixedM(2m));
            bool a = SweepSolver.SweepRegion(sector, fx, fz, dx, dz, FixedM(0.45m), out var ta, out var tao, out _, out _);
            // 零测度接触（相切，区间 ≤2 量子）低于 K=32 前缀谓词采样的结构分辨率——oracle 不覆盖（SPEC §5.2 注 3）
            if (a && tao - ta <= 2) continue;
            bool o = SweepSolver.SweepRegionOracle(sector, fx, fz, dx, dz, FixedM(0.45m), out var to);
            if (!a && !o) continue;
            // 量化容差: oracle 分辨率 1/2^32 + 双方 RHE 量子 → ≤ 2 量子
            Assert.True(a == o, $"hit/miss mismatch: ({fx},{fz})+({dx},{dz}) analytic={a} oracle={o}");
            if (a && o && to > 0)
                Assert.True(Math.Abs(ta - to) <= 64, $"toi mismatch: analytic={ta} oracle={to}");
        }
    }

    private static long FixedM(decimal m) =>
        (long)Math.Round(m * 65536m, MidpointRounding.ToEven);
}
