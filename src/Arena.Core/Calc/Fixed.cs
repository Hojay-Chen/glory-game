using System;
using System.Collections.Generic;
// PRODUCTION - Arena.Core
// ADR-0001 §1: Fixed = Q32.16 定点类型（int64 容器）。Core 唯一实值表示。
// 禁止 float/double/Math.* 进入本程序集（CI 门禁强制）。
namespace Arena.Core;

public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
{
    public const int FRAC = 16;
    public const long ONE = 1L << 16;   // 65536

    public long Raw { get; }

    public Fixed(long raw) => Raw = raw;

    public static Fixed FromRaw(long raw) => new(raw);
    public static Fixed FromInt(long v) => new(v * ONE);

    public static readonly Fixed Zero = new(0);
    public static readonly Fixed One = new(ONE);

    public static Fixed operator +(Fixed a, Fixed b) => new(a.Raw + b.Raw);
    public static Fixed operator -(Fixed a, Fixed b) => new(a.Raw - b.Raw);
    public static Fixed operator -(Fixed a) => new(-a.Raw);
    // 乘法必须走 DeterministicMath.MulShift（RoundHalfEven 语义），operator* 禁用以防静默截断

    public bool Equals(Fixed other) => Raw == other.Raw;
    public override bool Equals(object? o) => o is Fixed f && Equals(f);
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);
    public static bool operator ==(Fixed a, Fixed b) => a.Equals(b);
    public static bool operator !=(Fixed a, Fixed b) => !a.Equals(b);
    public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;
    public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;
    public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;
    public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

    public override string ToString() => $"Fixed({Raw})";
}
