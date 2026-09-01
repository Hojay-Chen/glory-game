using System;
using System.Collections.Generic;
// PRODUCTION - Arena.Core
// ADR-0001 §1.3/§1.5: 确定性数学运算——全 Core 唯一舍入与平方根实现。
// RoundHalfEven 唯一舍入规则；ISqrt 整数 Newton 固定迭代（Errata E-3）。
namespace Arena.Core.Calc;

public static class DeterministicMath
{
    public const int FRAC = Fixed.FRAC;
    public const long ONE = Fixed.ONE;
    public const long HALF = ONE >> 1;

    /// ADR-0001 §1.3: (x*m) >> 16 带 RoundHalfEven。单步界: |x|≤2^31 且 |m|≤2^17 ⇒ 乘积≤2^48 不溢出。
    public static long MulShift(long x, long m)
    {
        long p = x * m;
        long q = p >> FRAC;
        long r = p & (ONE - 1);
        if (r > HALF || (r == HALF && (q & 1) != 0)) q++;
        return q;
    }

    /// RoundHalfEven 整数除法: RHE(a/b)。
    public static long DivRoundHalfEven(long a, long b)
    {
        long q = a / b;
        long r = a % b;
        long twiceR = r * 2;
        if (twiceR > b || (twiceR == b && (q & 1) != 0)) q++;
        if (twiceR < -b || (twiceR == -b && (q & 1) != 0)) q--;
        return q;
    }

    /// ADR-0001 Errata E-3: n < 2^62 的整数平方根（floor）。Newton 固定迭代 + 精确回退——纯 int64。
    public static long ISqrt(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "ISqrt: negative input");
        if (n < 2) return n;
        long bits = 0;
        long t = n;
        while (t > 0) { bits++; t >>= 1; }
        long x = 1L << ((int)((bits + 1) / 2));
        for (int i = 0; i < 33; i++)
        {
            long nx = (x + n / x) >> 1;
            if (nx >= x) break;   // Newton 单调下降，首升即达 floor 邻域
            x = nx;
        }
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return x;
    }

    /// ADR-0001 Errata E-3: Q32.16 平方根（RoundHalfEven）。
    /// 输入 x.Raw < 2^46（实值 < 2^30）；输出 y = RHE(sqrt(x.Raw << 16))，即 y² ≈ x.Raw × ONE。
    public static long FSqrtFixed(Fixed x)
    {
        long n = x.Raw;
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(x), "FSqrtFixed: negative input");
        if (n > (1L << 46)) throw new ArgumentOutOfRangeException(nameof(x), "FSqrtFixed: input exceeds 2^46 domain");
        long target = n << FRAC;              // 目标域: y² ≈ n<<16
        long y = ISqrt(target);
        long loDiff = target - y * y;
        long hiDiff = (y + 1) * (y + 1) - target;
        if (hiDiff < loDiff || (hiDiff == loDiff && ((y + 1) & 1) == 0)) y++;
        return y;
    }
}

/// ADR-0001 §5: Runtime Tick 基础类型。Core 唯一时间表示——禁止 Design Frame 概念进入 Core。
public readonly struct Tick : IEquatable<Tick>, IComparable<Tick>
{
    public long Value { get; }
    public Tick(long v) => Value = v;
    public static readonly Tick Zero = new(0);
    public Tick Next() => new(Value + 1);
    public bool Equals(Tick other) => Value == other.Value;
    public override bool Equals(object? o) => o is Tick t && Equals(t);
    public override int GetHashCode() => Value.GetHashCode();
    public int CompareTo(Tick other) => Value.CompareTo(other.Value);
    public static bool operator ==(Tick a, Tick b) => a.Equals(b);
    public static bool operator !=(Tick a, Tick b) => !a.Equals(b);
    public static bool operator <(Tick a, Tick b) => a.Value < b.Value;
    public static bool operator >(Tick a, Tick b) => a.Value > b.Value;
}
