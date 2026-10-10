using System;
using System.Collections.Generic;
using System.Globalization;

// PRODUCTION - Arena.Infra.Data
// class-base.csv → 职业面板（身份 + 基础数值）。
// 身份字段供表现层做选人界面；**数值字段供装配期把职业面板接进战斗**——
// 此前这份 CSV 只被算进 dataVersionHash，atk/def/move_speed 从未应用到角色，
// 导致 25 个职业在战斗里共享同一套默认面板（1100/800/全局移速）。
namespace Arena.Infra.Data;

public static class ClassDefParser
{
    public sealed record ClassEntry(
        string ClassId, string Name, string Branch,
        long Atk, long Def, decimal MoveSpeedMps, string BodyWeight);

    public static List<ClassEntry> Parse(string classBaseCsvPath)
    {
        var list = new List<ClassEntry>();
        var lines = System.IO.File.ReadAllLines(classBaseCsvPath);
        if (lines.Length < 2) return list;
        var header = lines[0].Split(',');
        int iId = Array.IndexOf(header, "class_id");
        int iName = Array.IndexOf(header, "class_name");
        int iBranch = Array.IndexOf(header, "branch");
        int iAtk = Array.IndexOf(header, "atk");
        int iDef = Array.IndexOf(header, "def");
        int iMove = Array.IndexOf(header, "move_speed");
        int iWeight = Array.IndexOf(header, "body_weight");
        if (iId < 0 || iName < 0) return list;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var c = lines[i].Split(',');
            if (c.Length <= iId || c.Length <= iName) continue;
            list.Add(new ClassEntry(
                c[iId].Trim(),
                c[iName].Trim(),
                Field(c, iBranch),
                Long(c, iAtk),
                Long(c, iDef),
                Dec(c, iMove),
                Field(c, iWeight)));
        }
        return list;
    }

    private static string Field(string[] c, int i) => i >= 0 && i < c.Length ? c[i].Trim() : "";

    private static long Long(string[] c, int i) =>
        long.TryParse(Field(c, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal Dec(string[] c, int i) =>
        decimal.TryParse(Field(c, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
}
