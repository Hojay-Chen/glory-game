using Arena.Core.Snapshot;
using Xunit;

namespace Arena.Tests.Snapshot;

/// ADR-0001 T1: Snapshot → Restore → Continue 一致性
public class SnapshotRoundTrip
{
    [Fact]
    public void BitwiseEquals_SameData_True()
    {
        var a = new SnapshotData();
        a.Set(100, 42);
        a.Set(200, 99);
        var b = new SnapshotData();
        b.Set(100, 42);
        b.Set(200, 99);
        Assert.True(a.BitwiseEquals(b));
    }

    [Fact]
    public void BitwiseEquals_DifferentValue_False()
    {
        var a = new SnapshotData();
        a.Set(100, 42);
        var b = new SnapshotData();
        b.Set(100, 43);
        Assert.False(a.BitwiseEquals(b));
    }

    [Fact]
    public void BitwiseEquals_DifferentKeyCount_False()
    {
        var a = new SnapshotData();
        a.Set(100, 42);
        var b = new SnapshotData();
        Assert.False(a.BitwiseEquals(b));
    }

    [Fact]
    public void Save_Load_RoundTrip()
    {
        var a = new SnapshotData();
        a.Set(1, 10); a.Set(2, 20); a.Set(3, 30);
        var (keys, values) = a.ToArrays();
        var b = new SnapshotData();
        b.Load(keys, values);
        Assert.True(a.BitwiseEquals(b));
    }
}
