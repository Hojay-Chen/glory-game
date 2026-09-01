using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
// PRODUCTION - Arena.Infra.Data
// ADR-0002 §3/§5: Data Compiler 管线 + dataVersionHash
using System.Security.Cryptography;
using System.Text;

namespace Arena.Infra.Data;

public sealed class DataCompiler
{
    public const string PipelineVersion = "ArenaCatalog:v1";
    public const string DeterministicConstVersion = "DC-2026-09-01";

    /// 全量编译入口（ADR-0002 §3 九段管线）
    public CompilerResult Compile(
        string skillsCsvPath, string weaponsCsvPath, string classBaseCsvPath)
    {
        var blockers = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var allDefs = new List<SkillDef>();

        // ① Read + Parse + Validate
        var skillsLines = File.ReadAllLines(skillsCsvPath);
        if (skillsLines.Length < 2) throw new InvalidDataException("skills.csv empty");
        var header = skillsLines[0].Split(',');
        int validCount = 0;

        for (int i = 1; i < skillsLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(skillsLines[i])) continue;
            var cells = skillsLines[i].Split(',');
            if (cells.Length != 36)
            {
                blockers.Add(new("L1", "COLUMN_COUNT", $"row {i}: {cells.Length} != 36 列"));
                continue;
            }
            var (def, issues) = SkillParser.ParseRow(cells, header);
            foreach (var issue in issues)
            {
                var entry = new ValidationIssue(issue.Severity, issue.Rule, $"row {i}: {issue.Detail}");
                if (issue.Severity == "L1") blockers.Add(entry);
                else warnings.Add(entry);
            }
            // ValidRows = 通过 L1（无 fail-fast blocker）的行；L2 警告不排除
            if (!issues.Any(issue => issue.Severity == "L1"))
            {
                allDefs.Add(def!);
                validCount++;
            }
        }

        // ② dataVersionHash（ADR-0002 §5.2 输入范围）
        var hashInput = new StringBuilder();
        hashInput.Append(PipelineVersion);
        hashInput.Append(HashBytes(File.ReadAllBytes(skillsCsvPath)));
        hashInput.Append(HashBytes(File.ReadAllBytes(weaponsCsvPath)));
        hashInput.Append(HashBytes(File.ReadAllBytes(classBaseCsvPath)));
        hashInput.Append(DeterministicConstVersion);
        var dataVersionHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()))).ToLowerInvariant();

        bool success = blockers.Count == 0;
        return new CompilerResult(success, validCount, blockers, warnings, dataVersionHash);
    }

    private static string HashBytes(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..16];
}
