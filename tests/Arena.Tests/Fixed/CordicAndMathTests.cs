using System;
using Arena.Core.Calc;
using Xunit;

namespace Arena.Tests.Calc;

/// E-6/E-7 基座: CORDIC 三角、负域 RHE 除法、Int128 二次求根
public class CordicAndMathTests
{
    // RHE(2π×65536)=411775；heading H → 角度 H/65536×360°
    [Theory]
    [InlineData(0, 0, 65536)]           // heading 0 = +Z → (x,z)=(0,1)
    [InlineData(16384, 65536, 0)]       // 90° 顺时针 = +X → (1,0)
    [InlineData(32768, 0, -65536)]      // 180° = −Z
    [InlineData(49152, -65536, 0)]      // 270° = −X
    public void CordicCosSin_Cardinal_Angles(long h, long expCos, long expSin)
    {
        DeterministicMath.CordicCosSin(h, out var c, out var s);
        Assert.True(Math.Abs(c - expCos) <= 8, $"cos({h}) = {c}, expected ≈ {expCos}");
        Assert.True(Math.Abs(s - expSin) <= 8, $"sin({h}) = {s}, expected ≈ {expSin}");
    }

    [Fact]
    public void CordicCosSin_FullCircle_Matches_HalfDegreeTable_WithinTolerance()
    {
        // 每 15° 对照构建期半度表（表精度 1 量子；CORDIC ≤3 量子）
        for (int deg = 0; deg < 360; deg += 15)
        {
            long h = (long)Math.Round((double)deg / 360.0 * 65536.0);
            DeterministicMath.CordicCosSin(h, out var c, out var s);
            // heading h 的方向向量 (x,z) = (sin d, cos d)——与表 (cos d, sin d) 交叉对照
            DeterministicTables.HalfDegTrig(deg * 2, out var tc, out var ts);
            Assert.True(Math.Abs(c - ts) <= 8, $"x({deg}°): cordic={c} sinTable={ts}");
            Assert.True(Math.Abs(s - tc) <= 8, $"z({deg}°): cordic={s} cosTable={tc}");
        }
    }

    [Fact]
    public void CordicCosSin_IsDeterministic_AcrossCalls()
    {
        DeterministicMath.CordicCosSin(12345, out var c1, out var s1);
        for (int i = 0; i < 100; i++)
        {
            DeterministicMath.CordicCosSin(12345, out var c2, out var s2);
            Assert.Equal(c1, c2);
            Assert.Equal(s1, s2);
        }
    }

    [Fact]
    public void RotateHalfDeg_Identity_Composes()
    {
        // heading=0 朝向 +Z；旋转 +45°（索引 90）→ +X+Z 对角
        DeterministicMath.RotateHalfDeg(0, 90, out var cP, out var sP);
        Assert.True(Math.Abs(cP - 46341) <= 8);
        Assert.True(Math.Abs(sP - 46341) <= 8);
        // 旋转 −45° → −X+Z 对角
        DeterministicMath.RotateHalfDeg(0, -90, out var cM, out var sM);
        Assert.True(Math.Abs(cM + 46341) <= 8);
        Assert.True(Math.Abs(sM - 46341) <= 8);
    }

    [Fact]
    public void DivRoundHalfEven_Negative_Divisors_And_Dividends()
    {
        Assert.Equal(-4, DeterministicMath.DivRoundHalfEven(-7, 2));   // RHE(-3.5) = -4（偶）
        Assert.Equal(4, DeterministicMath.DivRoundHalfEven(7, 2));     // RHE(3.5) = 4（偶）
        Assert.Equal(-4, DeterministicMath.DivRoundHalfEven(7, -2));   // RHE(-3.5) = -4
        Assert.Equal(4, DeterministicMath.DivRoundHalfEven(-7, -2));
        Assert.Equal(-2, DeterministicMath.DivRoundHalfEven(-5, 2));   // RHE(-2.5) = -2（偶）
        Assert.Equal(3, DeterministicMath.DivRoundHalfEven(6, 2));
    }

    [Fact]
    public void ISqrt128_Matches_Int64_ISqrt_On_Domain()
    {
        var rng = new System.Random(42);   // 测试域熵源（非战斗路径）
        for (int i = 0; i < 1000; i++)
        {
            long v = rng.NextInt64(1, long.MaxValue / 2);
            Assert.Equal(DeterministicMath.ISqrt(v), DeterministicMath.ISqrt128(v));
        }
        // 大数域（int64 平方根溢出区——B²≤8e24 判别式域）
        Int128 big = (Int128)2_800_000_000_000L * 2_800_000_000_000L;   // ~7.84e24
        Int128 r = DeterministicMath.ISqrt128(big);
        Assert.True(r * r <= big && (r + 1) * (r + 1) > big);
    }

    [Fact]
    public void SolveQuadraticIntervalQ_Tangent_Is_Contact()
    {
        // (2s−1)² ≤ 0 ⟺ 4s²−4s+1 ≤ 0 → 相切单根 s=1/2（PA-2.3 相切=接触）
        var ok = DeterministicMath.SolveQuadraticIntervalQ(4, -4, 1, out var tIn, out var tOut);
        Assert.True(ok);
        Assert.True(Math.Abs(tIn - 32768) <= 1, $"tIn={tIn}");
        Assert.True(Math.Abs(tOut - 32768) <= 1, $"tOut={tOut}");
    }

    [Fact]
    public void SolveQuadraticIntervalQ_NoRealRoots_ReturnsFalse()
    {
        Assert.False(DeterministicMath.SolveQuadraticIntervalQ(1, 0, 1, out _, out _));
    }
}
