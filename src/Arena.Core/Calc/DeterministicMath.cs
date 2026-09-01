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

    /// RoundHalfEven 整数除法: RHE(a/b)。任意符号（RHE 符号对称: RHE(−x) = −RHE(x)）。
    /// 实现域: |a| < 2^62, |b| < 2^62（规范化取绝对值；int64 极值不在战斗量程内）。
    public static long DivRoundHalfEven(long a, long b)
    {
        long s = 1;
        if (a < 0) { s = -s; a = -a; }
        if (b < 0) { s = -s; b = -b; }
        long q = a / b;
        long r = a % b;
        long twiceR = r * 2;
        if (twiceR > b || (twiceR == b && (q & 1) != 0)) q++;
        return s * q;
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

    // ---- Errata E-6（提案）: 整数 CORDIC 三角 —— SPEC-0005 §4 朝向旋转的唯一实现 ----
    // 背景: fan/cone/box 类 hitbox 随 Owner HeadingQuantum（u16 全圆角）旋转，法线方向
    // 依赖运行时角度——静态半度表无法覆盖 65536 个朝向。CORDIC（Volder 1959）为
    // 纯 int64 移位/加减的固定迭代算法，逐位跨平台一致，与 ISqrt 同性质（无 float/libm）。
    // 精度: 17 次迭代，输出误差 ≤ 3 Q32.16 量子（约 4.6e-5 实值）——远小于 1 Fixed 量子空间语义。

    /// atan(2^-i) Q32.16 常量（i=0..16；i≥17 时 atan(2^-i) < 0.5 量子，迭代饱和）
    private static readonly long[] AtanQ =
    {
        51472, 30386, 16055, 8150, 4091, 2047, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2, 1
    };

    private const long CORDIC_GAIN_Q = 39797;   // 1/K = 0.607252935... × 65536
    public const long TWO_PI_Q = 411775;        // 2π × 65536（RHE）
    public const long PI_Q = 205887;            // π × 65536（RHE）
    private const long HALF_PI_Q = 102944;      // π/2 × 65536（RHE）

    /// HeadingQuantum（0..65535 全圆）→ 朝向向量 (cosQ, sinQ) = (x, z)，Q32.16。
    /// SPEC-0001 约定: heading 0 = +Z，顺时针为正 → 数学域角 θ = π/2 − h·2π/65536。
    /// 纯 int64，确定。输出误差 ≤ 8 Q32.16 量子（CORDIC 17 迭代 + 角度量化）。
    public static void CordicCosSin(long headingQuantum, out long cosQ, out long sinQ)
    {
        long h = ((headingQuantum % 65536) + 65536) % 65536;
        // 数学域角（CORDIC 收敛域需 [-π/2, π/2] 折叠）
        long theta = HALF_PI_Q - DivRoundHalfEven(h * TWO_PI_Q, 65536);   // ∈ [-3π/2, π/2]
        bool negate = false;   // 折叠 ±π 时 (cos, sin) 同时变号（cos(θ±π)=−cosθ, sin(θ±π)=−sinθ）
        if (theta > PI_Q) theta -= TWO_PI_Q;
        if (theta > HALF_PI_Q) { theta -= PI_Q; negate = true; }
        else if (theta < -HALF_PI_Q) { theta += PI_Q; negate = true; }

        long x = CORDIC_GAIN_Q, y = 0, z = theta;
        for (int i = 0; i < AtanQ.Length; i++)
        {
            long dx = x >> i, dy = y >> i;
            if (z >= 0) { x -= dy; y += dx; z -= AtanQ[i]; }
            else { x += dy; y -= dx; z += AtanQ[i]; }
        }
        cosQ = negate ? -x : x;
        sinQ = negate ? -y : y;
    }

    /// 角合成: heading H 的朝向旋转 φ（半度索引，φ>0 = 朝向顺时针转 φ 度）。
    /// 返回新朝向的 (x, z) 单位向量。恒等式在数学域合成: θ = θ_h − φ。
    public static void RotateHalfDeg(long headingQuantum, int halfDegIndex, out long cosQ, out long sinQ)
    {
        CordicCosSin(headingQuantum, out var ch, out var sh);
        DeterministicTables.HalfDegTrig(halfDegIndex, out var cf, out var sf);
        // cos(θh − φ) = cos·cos + sin·sin；sin(θh − φ) = sin·cos − cos·sin
        cosQ = MulShift(ch, cf) + MulShift(sh, sf);
        sinQ = MulShift(sh, cf) - MulShift(ch, sf);
    }

    /// 归一化 (dx,dz) → 单位向量 Q32.16（|n| ≈ ONE，误差 ≤1 量子）。零向量 → (1, 0)（构造确定）。
    public static void Normalize(long dx, long dz, out long nx, out long nz)
    {
        long len = ISqrt(dx * dx + dz * dz);
        if (len == 0) { nx = Fixed.ONE; nz = 0; return; }
        nx = DivRoundHalfEven(dx * Fixed.ONE, len);
        nz = DivRoundHalfEven(dz * Fixed.ONE, len);
    }

    // ---- Errata E-7（提案）: Int128 中间域二次求根 —— 判别式 b²−4ac 超出 int64 域 ----
    // 场景: SweepSolver 二次约束（圆/球/角圆），坐标 ~3.9e6 raw × 位移 ~2e5 raw ⇒ b² ≤ ~8e24。
    // Int128 为软件纯整数运算（无 float），逐位跨平台一致；仅判别式与求根进入 128 位域。

    /// Int128 整数平方根（floor）。Newton 固定迭代 + 精确回退——纯整数。
    public static Int128 ISqrt128(Int128 n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "ISqrt128: negative input");
        if (n < 2) return n;
        int bits = 0;
        Int128 t = n;
        while (t > 0) { bits++; t >>= 1; }
        Int128 x = (Int128)1 << ((bits + 1) / 2);   // 初值 ≥ √n（2^((bits+1)/2) ≥ 2^(bits/2) = √(2^bits) > √n）
        for (int i = 0; i < 128; i++)
        {
            Int128 nx = (x + n / x) >> 1;
            if (nx >= x) break;
            x = nx;
        }
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return x;
    }

    /// 二次 s²·a + s·b + c ≤ 0 在 s ∈ [0,1] 的解区间 → tRaw = RHE(s×ONE)。
    /// 返回 false = 区间空。a > 0 必须成立（凸二次）。系数进入 Int128 域防溢出。
    public static bool SolveQuadraticIntervalQ(Int128 a, Int128 b, Int128 c,
        out long tInRaw, out long tOutRaw)
    {
        tInRaw = -1; tOutRaw = -1;
        if (a <= 0) throw new ArgumentException("SolveQuadraticIntervalQ requires a > 0");
        Int128 disc = b * b - 4 * a * c;
        if (disc < 0) return false;
        Int128 sq = ISqrt128(disc);
        // s1 = (−b − sq)/(2a), s2 = (−b + sq)/(2a)；量化 t = RHE(s × ONE)
        Int128 num1 = -b - sq, num2 = -b + sq, den = 2 * a;
        tInRaw = DivRoundHalfEven128(num1 * Fixed.ONE, den);
        tOutRaw = DivRoundHalfEven128(num2 * Fixed.ONE, den);
        return true;
    }

    /// Int128 域 RoundHalfEven 除法（语义同 DivRoundHalfEven，符号对称规范化）。
    public static long DivRoundHalfEven128(Int128 a, Int128 b)
    {
        Int128 s = 1;
        if (a < 0) { s = -s; a = -a; }
        if (b < 0) { s = -s; b = -b; }
        Int128 q = a / b;
        Int128 r = a % b;
        Int128 twiceR = r * 2;
        if (twiceR > b || (twiceR == b && (q & 1) != 0)) q++;
        return (long)(s * q);
    }

    /// 确定性 floor 除法（BroadPhase 格索引——坐标可为负）。Math.DivRem 语义 floor。
    public static long DivFloor(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
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
