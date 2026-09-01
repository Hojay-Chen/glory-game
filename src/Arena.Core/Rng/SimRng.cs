using System;
using System.Collections.Generic;
using System.Linq;
// PRODUCTION - Arena.Core
// ADR-0001 §4: Per-Stream Counter RNG——确定性随机。
// value(streamKey) = SplitMix64(Mix64(matchSeed, streamKey, consumed[streamKey]))
// 流键隔离：新增一个技能的调用不改变其他流的任何 roll 结果（T7 回归测试锁定）。
namespace Arena.Core.Rng;

/// StreamClass 枚举（ADR-0001 §4.1 表）
public enum StreamClass : byte { SKILL_CHANCE = 0, UNIT_AI = 1, AMBIENT = 2 }

/// RollScope：流键的语义坐标（ADR-0001 §4.1）
public readonly record struct RollScope(StreamClass Class, int FighterId, ushort SkillId)
{
    public long ToStreamKey(long matchSeed) => RngInternal.Hash64(matchSeed, (long)Class << 48 | (long)(uint)FighterId << 16 | SkillId);
}

/// Per-Stream Counter RNG（ADR-0001 §4）
public sealed class SimRng
{
    private readonly long _matchSeed;
    private readonly SortedDictionary<long, long> _counters = new();

    public SimRng(long matchSeed) => _matchSeed = matchSeed;

    /// 在指定流上消费一次 roll，返回 [0, 100) 整数。调用即推进该流计数器。
    /// 隔离保证：不同流键的 (seed, key, counter) 三元组互不影响。
    public int Roll100(in RollScope scope)
    {
        long key = scope.ToStreamKey(_matchSeed);
        _counters.TryGetValue(key, out long counter);
        _counters[key] = counter + 1;
        long mixed = RngInternal.Mix64(_matchSeed, key, counter);
        return (int)((RngInternal.SplitMix64Value(mixed) & 0x7FFFFFFFFFFFFFFFL) % 100);
    }

    /// 当前全部流计数器（随 Snapshot 序列化，ADR-0001 §8）
    public IReadOnlyCollection<KeyValuePair<long, long>> Counters => _counters;

    /// 流计数器快照（确定性排序——键 Ordinal 升序的数组对）
    public (long[] keys, long[] values) CaptureCounters()
    {
        var keys = _counters.Keys.ToArray();
        var vals = new long[keys.Length];
        for (int i = 0; i < keys.Length; i++) vals[i] = _counters[keys[i]];
        return (keys, vals);
    }

    /// 从快照恢复
    public void RestoreCounters(long[] keys, long[] values)
    {
        _counters.Clear();
        for (int i = 0; i < keys.Length && i < values.Length; i++) _counters[keys[i]] = values[i];
    }
}

/// 内部哈希原语——纯 int64 算术（unchecked 上下文），逐位跨平台一致
internal static class RngInternal
{
    /// 三值混合 → 流键
    public static long Mix64(long a, long b, long c)
    {
        unchecked
        {
            long h = a;
            h ^= b + GOLDEN + (h << 6) + (h >> 2);
            h ^= c + GOLDEN + (h << 6) + (h >> 2);
            return h;
        }
    }

    private const long GOLDEN = unchecked((long)0x9E3779B97F4A7C15UL);

    /// SplitMix64 终结函数（Steele et al. 2014）——确定性双射
    public static long SplitMix64Value(long seed)
    {
        unchecked
        {
            ulong z = (ulong)(seed + GOLDEN);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (long)(z ^ (z >> 31));
        }
    }

    /// 二值 Hash64（流键生成用）
    public static long Hash64(long a, long b) => SplitMix64Value(Mix64(a, b, 0));
}
