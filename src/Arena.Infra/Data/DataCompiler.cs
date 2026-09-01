using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Arena.Core.Sim;
// PRODUCTION - Arena.Infra.Data
// ADR-0002 §3/§5: Data Compiler 管线 + dataVersionHash + RuntimeDef 产出（Phase 4 扩展）。
// 九段管线: Read → Parse → Canonical → Schema → Semantic → Quantize → RuntimeDef → Sort → Hash。
// L1 阻塞行拒产 RuntimeDef（fail-fast 登记 OQ）；L2/L3 警告随行。
namespace Arena.Infra.Data;

public sealed class DataCompiler
{
    public const string PipelineVersion = "ArenaCatalog:v1";
    public const string DeterministicConstVersion = "DC-2026-09-01";

    /// 校验入口（保留：行级校验结果 + hash）
    public CompilerResult Compile(
        string skillsCsvPath, string weaponsCsvPath, string classBaseCsvPath)
    {
        var (result, _) = CompileWithCatalog(skillsCsvPath, weaponsCsvPath, classBaseCsvPath);
        return result;
    }

    /// 全量编译入口：产出 RuntimeCatalog（RuntimeId = 通过 L1 的行序 1..N）
    public (CompilerResult result, RuntimeCatalog? catalog) CompileWithCatalog(
        string skillsCsvPath, string weaponsCsvPath, string classBaseCsvPath)
    {
        var blockers = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var allDefs = new List<SkillDef>();
        var unroutedStatuses = new List<string>();
        var unroutedHitboxes = new List<string>();

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

        // ⑥⑦ Quantize → RuntimeDef（L1 行已排除；量化失败 = 新 L1——如 SPEC-0005 §4 扇角凸性）
        var skills = new List<SkillRuntimeData>();
        var quantizeBlockers = new List<int>();   // allDefs 内索引
        for (int i = 0; i < allDefs.Count; i++)
        {
            try
            {
                var (rt, urS, urH) = RuntimeSkillFactory.Build(allDefs[i], (ushort)(skills.Count + 1));
                unroutedStatuses.AddRange(urS);
                unroutedHitboxes.AddRange(urH);
                skills.Add(rt!);
            }
            catch (FormatException ex)
            {
                quantizeBlockers.Add(i);
                blockers.Add(new("L1", "RUNTIME_QUANTIZE", $"row {allDefs[i].SkillId}: {ex.Message}"));
            }
        }

        // 普攻链链接（ChainNext: 同职业 basic 段号 +1）
        ushort LinkChainNext(SkillRuntimeData cur)
        {
            foreach (var s in skills)
                if (s.ClassId == cur.ClassId && s.Type == "basic" && s.ChainN == cur.ChainN + 1)
                    return s.RuntimeId;
            return 0;
        }
        foreach (var s in skills)
            if (s.Type == "basic" && s.ChainN > 0)
                s.ChainNext = LinkChainNext(s);

        // ⑨ dataVersionHash（ADR-0002 §5.2 输入范围）
        var hashInput = new System.Text.StringBuilder();
        hashInput.Append(PipelineVersion);
        hashInput.Append(HashBytes(File.ReadAllBytes(skillsCsvPath)));
        hashInput.Append(HashBytes(File.ReadAllBytes(weaponsCsvPath)));
        hashInput.Append(HashBytes(File.ReadAllBytes(classBaseCsvPath)));
        hashInput.Append(DeterministicConstVersion);
        var dataVersionHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput.ToString()))).ToLowerInvariant();

        bool success = blockers.Count == 0;
        var result = new CompilerResult(success, skills.Count, blockers, warnings, dataVersionHash);
        var catalog = new RuntimeCatalog
        {
            Skills = skills,
            IdMap = skills.ToDictionary(s => s.SkillId, s => s.RuntimeId),
            Blockers = blockers,
            Warnings = warnings,
            UnroutedStatuses = unroutedStatuses,
            UnroutedHitboxes = unroutedHitboxes,
            DataVersionHash = dataVersionHash,
        };
        return (result, catalog);
    }

    private static string HashBytes(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant()[..16];
}
