using System;
using System.Collections.Generic;

// PRODUCTION - Arena.Infra.Data
// class-base.csv → 职业清单（身份字段：id / 中文名 / 系别）。
// 仅供表现层做「选人界面」与显示名——**战斗数值仍只由 Compiler 消费**，本解析器不参与数值链路。
// 放在 Infra 而非 Client：客户端不直接读 CSV（ArenaDefParser 同律）。
namespace Arena.Infra.Data;

public static class ClassDefParser
{
    public sealed record ClassEntry(string ClassId, string Name, string Branch);

    public static List<ClassEntry> Parse(string classBaseCsvPath)
    {
        var list = new List<ClassEntry>();
        var lines = System.IO.File.ReadAllLines(classBaseCsvPath);
        if (lines.Length < 2) return list;
        var header = lines[0].Split(',');
        int iId = Array.IndexOf(header, "class_id");
        int iName = Array.IndexOf(header, "class_name");
        int iBranch = Array.IndexOf(header, "branch");
        if (iId < 0 || iName < 0) return list;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var c = lines[i].Split(',');
            if (c.Length <= iId || c.Length <= iName) continue;
            list.Add(new ClassEntry(
                c[iId].Trim(),
                c[iName].Trim(),
                iBranch >= 0 && iBranch < c.Length ? c[iBranch].Trim() : ""));
        }
        return list;
    }
}
