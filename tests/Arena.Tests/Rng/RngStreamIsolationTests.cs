using System.Linq;
using Arena.Core.Rng;
using Xunit;

namespace Arena.Tests.Rng;

/// ADR-0001 §4 T7 强制回归测试：新增一个技能的 RNG 调用，不得改变其他流的输出。
public class Rng_StreamIsolation
{
    private const long TestSeed = 0xDEADBEEF42L;

    [Fact]
    public void NewCall_InSkillA_DoesNotChange_SkillB_Or_FighterB_Streams()
    {
        // 基准：两个 Fighter 各自的 Skill 流
        var rngA = new SimRng(TestSeed);
        var scopeA = new RollScope(StreamClass.SKILL_CHANCE, 1, 100);  // Fighter 1, Skill 100
        var scopeB = new RollScope(StreamClass.SKILL_CHANCE, 2, 200);  // Fighter 2, Skill 200
        var scopeC = new RollScope(StreamClass.SKILL_CHANCE, 2, 300);  // Fighter 2, Skill 300

        // 消费一轮：记录 Skill A 和 B 的基准 roll
        int baselineA = rngA.Roll100(scopeA);
        int baselineB1 = rngA.Roll100(scopeB);
        int baselineB2 = rngA.Roll100(scopeB);
        int baselineC1 = rngA.Roll100(scopeC);
        int baselineC2 = rngA.Roll100(scopeC);

        // 新 RNG：多消费 Skill A 一次（模拟"新增一个调用"）
        var rngB = new SimRng(TestSeed);
        rngB.Roll100(scopeA);                     // 正常消费
        rngB.Roll100(scopeA);                     // ← 新增的调用
        int afterA = rngB.Roll100(scopeA);        // Skill A 后续
        int afterB1 = rngB.Roll100(scopeB);       // Skill B 首次
        int afterB2 = rngB.Roll100(scopeB);       // Skill B 第二次
        int afterC1 = rngB.Roll100(scopeC);       // Skill C 首次
        int afterC2 = rngB.Roll100(scopeC);       // Skill C 第二次

        // Skill B/C 的 roll 值不受 Skill A 多余调用影响
        Assert.Equal(baselineB1, afterB1);
        Assert.Equal(baselineB2, afterB2);
        Assert.Equal(baselineC1, afterC1);
        Assert.Equal(baselineC2, afterC2);
    }

    [Fact]
    public void SameSeed_SameScope_SameResult()
    {
        var rng1 = new SimRng(TestSeed);
        var rng2 = new SimRng(TestSeed);
        var scope = new RollScope(StreamClass.SKILL_CHANCE, 1, 100);
        Assert.Equal(rng1.Roll100(scope), rng2.Roll100(scope));
    }

    [Fact]
    public void DifferentSeed_DifferentResult()
    {
        var rng1 = new SimRng(0xAAAA);
        var rng2 = new SimRng(0xBBBB);
        var scope = new RollScope(StreamClass.SKILL_CHANCE, 1, 100);
        // 不是数学保证（可能偶尔相等），但 100 个 roll 全相等的概率 < 1%
        var r1 = Enumerable.Range(0, 100).Select(_ => rng1.Roll100(scope)).ToList();
        var r2 = Enumerable.Range(0, 100).Select(_ => rng2.Roll100(scope)).ToList();
        Assert.NotEqual(r1, r2);
    }

    [Fact]
    public void Roll100_ReturnsInRange()
    {
        var rng = new SimRng(TestSeed);
        var scope = new RollScope(StreamClass.SKILL_CHANCE, 1, 100);
        for (int i = 0; i < 1000; i++)
        {
            int roll = rng.Roll100(scope);
            Assert.InRange(roll, 0, 99);
        }
    }

    [Fact]
    public void SnapshotRestore_PreservesSequence()
    {
        var rng = new SimRng(TestSeed);
        var scope = new RollScope(StreamClass.SKILL_CHANCE, 1, 100);
        rng.Roll100(scope);
        var (keys, values) = rng.CaptureCounters();

        // 恢复后相同 scope 产生相同 roll
        var rngRestored = new SimRng(TestSeed);
        rngRestored.RestoreCounters(keys, values);
        int original = rng.Roll100(scope);
        int restored = rngRestored.Roll100(scope);
        Assert.Equal(original, restored);
    }
}
