using Arena.Core;
using Arena.Core.Calc;
using Xunit;

namespace Arena.Tests.Calc;

public class Fixed_Operations
{
    [Fact]
    public void MulShift_HalfEven_RoundsDown_WhenQuotientEven()
    {
        // 3/2 = 1.5 → RHE → 2 (round up to even)
        Assert.Equal(2L, DeterministicMath.DivRoundHalfEven(3, 2));
    }

    [Fact]
    public void MulShift_HalfEven_RoundsDown_WhenQuotientOdd()
    {
        // 5/2 = 2.5 → RHE → 2 (round down to even)
        Assert.Equal(2L, DeterministicMath.DivRoundHalfEven(5, 2));
    }

    [Fact]
    public void MulShift_ExactDivision_NoRounding()
    {
        Assert.Equal(3L, DeterministicMath.DivRoundHalfEven(6, 2));
    }

    [Fact]
    public void Fixed_One_Is65536()
    {
        Assert.Equal(65536L, Fixed.ONE);
    }

    [Fact]
    public void Fixed_Addition_RawLinear()
    {
        var a = Fixed.FromInt(3);
        var b = Fixed.FromInt(2);
        Assert.Equal(Fixed.FromInt(5), a + b);
    }

    [Fact]
    public void Fixed_Negative_Works()
    {
        var a = Fixed.FromInt(-5);
        Assert.Equal(Fixed.FromInt(5), -a);
    }
}

public class ISqrt_Boundary
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(2L, 1L)]
    [InlineData(3L, 1L)]
    [InlineData(4L, 2L)]
    [InlineData(8L, 2L)]
    [InlineData(9L, 3L)]
    [InlineData(15L, 3L)]
    [InlineData(16L, 4L)]
    [InlineData(2_000_000_000L, 44721L)]  // sqrt(2e9) ≈ 44721.36
    public void ISqrt_KnownValues(long input, long expected)
    {
        Assert.Equal(expected, DeterministicMath.ISqrt(input));
    }

    [Fact]
    public void ISqrt_LargeNumber_Deterministic()
    {
        long n = 4_611_686_018_427_387_904L; // 2^62 - close to max
        long result = DeterministicMath.ISqrt(n);
        Assert.True(result * result <= n);
        Assert.True((result + 1) * (result + 1) > n);
    }
}

public class FSqrtFixed_Boundary
{
    [Fact]
    public void FSqrtFixed_One_ReturnsOne()
    {
        Assert.Equal(Fixed.ONE, DeterministicMath.FSqrtFixed(Fixed.One));
    }

    [Fact]
    public void FSqrtFixed_Zero_ReturnsZero()
    {
        Assert.Equal(0L, DeterministicMath.FSqrtFixed(Fixed.Zero));
    }

    [Fact]
    public void FSqrtFixed_4_Returns_2xONE()
    {
        // sqrt(4.0) = 2.0 → 2 × 65536 = 131072
        Assert.Equal(131072L, DeterministicMath.FSqrtFixed(Fixed.FromRaw(4 * 65536)));
    }
}
