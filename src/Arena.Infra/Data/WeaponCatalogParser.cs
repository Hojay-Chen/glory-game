using System;
using System.Collections.Generic;
using System.Globalization;

// PRODUCTION - Arena.Infra.Data
// weapons.csv → 武器展示目录（选武器界面用）。数值仅供展示与装配取 atk_mod；
// 规则级 trait 的解释权在 Compiler/WeaponOverlay，此处不重复解释（避免两处口径）。
namespace Arena.Infra.Data;

public static class WeaponCatalogParser
{
    public sealed record WeaponEntry(
        string WeaponId, string Name, string ClassId, string Type, string Grade,
        decimal AtkMod, decimal AtkSpd, decimal RangeM, string Trait, string Notes);

    public static List<WeaponEntry> Parse(string weaponsCsvPath)
    {
        var list = new List<WeaponEntry>();
        var lines = System.IO.File.ReadAllLines(weaponsCsvPath);
        if (lines.Length < 2) return list;
        var header = lines[0].Split(',');
        int I(string n) => Array.IndexOf(header, n);
        int iId = I("weapon_id"), iName = I("weapon_name"), iCls = I("class_id"), iType = I("type"),
            iGrade = I("grade"), iAtkMod = I("atk_mod"), iSpd = I("atk_spd"), iRange = I("range_m"),
            iTrait = I("trait"), iNotes = I("notes");
        if (iId < 0 || iCls < 0) return list;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var c = lines[i].Split(',');
            if (c.Length <= iId || c.Length <= iCls) continue;
            list.Add(new WeaponEntry(
                F(c, iId), F(c, iName), F(c, iCls), F(c, iType), F(c, iGrade),
                D(c, iAtkMod), D(c, iSpd), D(c, iRange), F(c, iTrait), F(c, iNotes)));
        }
        return list;
    }

    private static string F(string[] c, int i) => i >= 0 && i < c.Length ? c[i].Trim() : "";

    private static decimal D(string[] c, int i) =>
        decimal.TryParse(F(c, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
}
