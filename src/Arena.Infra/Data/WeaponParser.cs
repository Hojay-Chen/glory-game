
using System;
using System.Collections.Generic;
using System.Globalization;
// PRODUCTION - Arena.Infra.Data
// GDD §16 武器规格: 武器=赛前选择 1 把；数值级禁止（D12）、规则级允许（距离/CD/段数/半径/角度/耐久/解锁）。
// v1 overlay 实装: atk_mod（面板加成）+ 结构化 trait_rules（{skill_id}: 字段 旧→新 / 浮空类:launch_v +P）；
// 未匹配 trait 文本 → UnroutedTraits 显式登记（无静默丢弃）。
namespace Arena.Infra.Data;

public sealed record WeaponTraitRule(string TargetSkillId, string Field, decimal NewValue, decimal? Cap);

public sealed record WeaponDef(
    string WeaponId, string ClassId, decimal AtkMod, decimal RangeM,
    IReadOnlyList<WeaponTraitRule> Rules, IReadOnlyList<string> UnroutedTraits);

public static class WeaponParser
{
    public static List<WeaponDef> Parse(string weaponsCsvPath, List<string> unrouted)
    {
        var lines = System.IO.File.ReadAllLines(weaponsCsvPath);
        var list = new List<WeaponDef>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = lines[i].Split(',');
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var header = lines[0].Split(',');
            for (int k = 0; k < header.Length && k < cells.Length; k++) d[header[k]] = cells[k].Trim();
            var rules = new List<WeaponTraitRule>();
            var trait = d.GetValueOrDefault("trait", "-");
            var tr = d.GetValueOrDefault("trait_rules", "-");
            if (trait != "-" && tr != "-")
            {
                // 模式 1: {skill_id}:...{old}→{new}
                var m = System.Text.RegularExpressions.Regex.Match(tr, @"([A-Z]{3}_[A-Z0-9_]+[0-9]{3})[^:]*:.*?(\d+(?:\.\d+)?)→(\d+(?:\.\d+)?)");
                if (m.Success)
                {
                    var field = System.Text.RegularExpressions.Regex.Match(tr, @"[:]\s*([^0-9→]+)");
                    rules.Add(new WeaponTraitRule(m.Groups[1].Value, field.Success ? field.Groups[1].Value.Trim() : "?",
                        decimal.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), null));
                }
                else
                {
                    // 模式 2: 浮空类技能:launch_v +0.5(上限 9.5)
                    var m2 = System.Text.RegularExpressions.Regex.Match(tr, @"launch_v\s*\+(\d+(?:\.\d+)?)(?:\(上限\s*(\d+(?:\.\d+)?)\))?");
                    if (m2.Success)
                    {
                        decimal? cap = m2.Groups[2].Success ? decimal.Parse(m2.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                        rules.Add(new WeaponTraitRule("*launch", "launch_v", decimal.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture), cap));
                    }
                    else
                    {
                        unrouted.Add($"{d.GetValueOrDefault("weapon_id", "?")}:trait:{trait}");
                    }
                }
            }
            list.Add(new WeaponDef(
                d.GetValueOrDefault("weapon_id", ""),
                d.GetValueOrDefault("class_id", ""),
                decimal.TryParse(d.GetValueOrDefault("atk_mod", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var am) ? am : 0m,
                decimal.TryParse(d.GetValueOrDefault("range_m", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rm) ? rm : 0m,
                rules, Array.Empty<string>()));
        }
        return list;
    }
}
