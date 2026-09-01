using System;
using System.IO;
using Arena.Infra.Data;
using Xunit;

namespace Arena.Tests.Compiler;

/// ADR-0002 Phase 2 Gate：Compiler 对当前仓库真实数据的验证
public class DataCompiler_Gate
{
    private static string FindRepoRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "arena.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("arena.sln not found from " + AppDomain.CurrentDomain.BaseDirectory);
    }

    private readonly string _root = FindRepoRoot();

    [Fact]
    public void Compile_Parses_All487Rows_Reports3Blocked()
    {
        var compiler = new DataCompiler();
        var result = compiler.Compile(
            Path.Combine(_root, "docs/skill-spec/skills.csv"),
            Path.Combine(_root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(_root, "docs/balance-sheet/class-base.csv"));

        // 487 行中有 484 行通过 L1，3 行被阻塞（OQ-2/OQ-13）
        Assert.Equal(484, result.ValidRows);
        Assert.Equal(3, result.Blockers.Count);

        // 阻塞行具体核对
        Assert.Contains(result.Blockers, b => b.Detail.Contains("BER_T3_004") && b.Rule.Contains("ARMOR_UNIT_AMBIGUOUS"));
        Assert.Contains(result.Blockers, b => b.Detail.Contains("SBL_U_001") && b.Rule.Contains("HITBOX_KIND"));
        Assert.Contains(result.Blockers, b => b.Detail.Contains("SPF_U_001") && b.Rule.Contains("HITBOX_KIND"));
    }

    [Fact]
    public void Compile_DataVersionHash_Stable()
    {
        var compiler = new DataCompiler();
        var r1 = compiler.Compile(
            Path.Combine(_root, "docs/skill-spec/skills.csv"),
            Path.Combine(_root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(_root, "docs/balance-sheet/class-base.csv"));
        var r2 = compiler.Compile(
            Path.Combine(_root, "docs/skill-spec/skills.csv"),
            Path.Combine(_root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(_root, "docs/balance-sheet/class-base.csv"));
        Assert.Equal(r1.DataVersionHash, r2.DataVersionHash);
        Assert.NotEmpty(r1.DataVersionHash);
    }

    [Fact]
    public void Compile_DataVersionHash_Is64Hex()
    {
        var compiler = new DataCompiler();
        var result = compiler.Compile(
            Path.Combine(_root, "docs/skill-spec/skills.csv"),
            Path.Combine(_root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(_root, "docs/balance-sheet/class-base.csv"));
        Assert.Equal(64, result.DataVersionHash.Length);
        Assert.Matches("^[0-9a-f]+$", result.DataVersionHash);
    }
}
