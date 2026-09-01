using System;
using System.Collections.Generic;
using System.Linq;
// PRODUCTION - Arena.Core
// ADR-0001 §8: Snapshot 基础结构——确定性序列化骨架。
// 完备性原则：从同一 Snapshot 出发 + 相同 Command Stream ⇒ 相同后续 Snapshot + Events。
namespace Arena.Core.Snapshot;

/// 确定性序列化框架：有序键值对（键 Ordinal 升序），值 = int64 Raw。
/// Phase 0 骨架：仅 RNG 计数器 + Tick；Phase 3 按 ADR-0001 §8.2 完整清单扩展。
public sealed class SnapshotData
{
    private readonly SortedDictionary<long, long> _entries = new();

    public void Set(long key, long value) => _entries[key] = value;
    public long Get(long key, long defaultValue = 0) => _entries.TryGetValue(key, out var v) ? v : defaultValue;
    public bool Has(long key) => _entries.ContainsKey(key);

    /// 有序键值对数组（序列化/比对用；键 Ordinal 升序）
    public (long[] keys, long[] values) ToArrays()
    {
        var keys = _entries.Keys.ToArray();
        var values = new long[keys.Length];
        for (int i = 0; i < keys.Length; i++) values[i] = _entries[keys[i]];
        return (keys, values);
    }

    public void Load(long[] keys, long[] values)
    {
        _entries.Clear();
        for (int i = 0; i < keys.Length && i < values.Length; i++) _entries[keys[i]] = values[i];
    }

    /// 逐位比对（确定性测试 T1/T2/T3 的核心断言）
    public bool BitwiseEquals(SnapshotData other)
    {
        if (_entries.Count != other._entries.Count) return false;
        using var e1 = _entries.GetEnumerator();
        using var e2 = other._entries.GetEnumerator();
        while (e1.MoveNext() && e2.MoveNext())
        {
            if (e1.Current.Key != e2.Current.Key || e1.Current.Value != e2.Current.Value) return false;
        }
        return true;
    }

    public int Count => _entries.Count;
}
