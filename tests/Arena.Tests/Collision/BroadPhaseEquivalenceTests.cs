using System;
using System.Collections.Generic;
using Arena.Core.Collision;
using Xunit;

namespace Arena.Tests.Collision;

/// SPEC-0005 PA-6 等价性契约（T55）: BroadPhase+NarrowPhase ≡ 全量 NarrowPhase（逐位）
public class BroadPhaseEquivalenceTests
{
    private const long ONE = Arena.Core.Fixed.ONE;

    [Fact]
    public void BroadPhase_Candidates_Superset_Of_True_Contacts()
    {
        var bp = new BroadPhase(8 * ONE);
        var rnd = new Random(20260902);
        var bodies = new List<(int id, ConvexRegion region, long x, long z, long r)>();
        for (int i = 0; i < 50; i++)
        {
            long x = rnd.NextInt64(-40 * ONE, 40 * ONE);
            long z = rnd.NextInt64(-40 * ONE, 40 * ONE);
            long r = rnd.NextInt64(ONE / 4, ONE);
            bodies.Add((i + 1, ConvexRegion.Circle(x, z, r), x, z, r));
            bp.Insert(i + 1, x - r, z - r, x + r, z + r, 0);
        }

        // 200 个随机查询盒——候选集 ⊇ 盒与圆真实相交集
        for (int q = 0; q < 200; q++)
        {
            long qx = rnd.NextInt64(-40 * ONE, 40 * ONE);
            long qz = rnd.NextInt64(-40 * ONE, 40 * ONE);
            long qh = rnd.NextInt64(ONE, 4 * ONE);
            var candidates = bp.Query(qx - qh, qx + qh, qz - qh, qz + qh);
            var truth = new HashSet<int>();
            foreach (var b in bodies)
            {
                // 圆与查询盒精确相交（clamp 最近点）
                long cx = Math.Max(qx - qh, Math.Min(b.x, qx + qh));
                long cz = Math.Max(qz - qh, Math.Min(b.z, qz + qh));
                long dx = b.x - cx, dz = b.z - cz;
                if (dx * dx + dz * dz <= b.r * b.r) truth.Add(b.id);
            }
            foreach (var t in truth)
                Assert.Contains(t, candidates);   // 保守性: 候选 ⊇ 真实
        }
    }

    [Fact]
    public void BroadPhase_Query_Is_IdSorted_And_Deterministic()
    {
        var bp = new BroadPhase(8 * ONE);
        var rnd = new Random(7);
        for (int i = 0; i < 30; i++)
        {
            long x = rnd.NextInt64(-20 * ONE, 20 * ONE);
            long z = rnd.NextInt64(-20 * ONE, 20 * ONE);
            bp.Insert(rnd.Next(1, 1000), x, z, x, z, ONE);
        }
        var r1 = new List<int>(bp.Query(-30 * ONE, 30 * ONE, -30 * ONE, 30 * ONE));
        var r2 = new List<int>(bp.Query(-30 * ONE, 30 * ONE, -30 * ONE, 30 * ONE));
        Assert.Equal(r1, r2);
        for (int i = 1; i < r1.Count; i++) Assert.True(r1[i - 1] < r1[i]);
    }
}
