using System;
using System.Collections.Generic;
using System.Globalization;
using Arena.Core.Collision;
using Arena.Core;
using Arena.Core.Calc;
// PRODUCTION - Arena.Infra.Data
// SPEC-0004: ArenaDef 事实源——arena.csv → TerrainBody 装配（SPEC-0005 §1 世界实体注册表-静态部分）。
// interaction 语义: bounce=结界墙反弹 / block=阻挡（Stop）+ 弹体摧毁 / none=装饰。
namespace Arena.Infra.Data;

public static class ArenaDefParser
{
    public sealed record ArenaObject(
        string ArenaId, string ObjectId, string Kind, string Shape,
        decimal X, decimal Z, decimal R, decimal HalfW, decimal HalfD,
        decimal Height, long Hp, string Interaction);

    public static List<ArenaObject> Parse(string arenaCsvPath)
    {
        var lines = System.IO.File.ReadAllLines(arenaCsvPath);
        var list = new List<ArenaObject>();
        if (lines.Length < 2) return list;
        var header = lines[0].Split(',');
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = lines[i].Split(',');
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int k = 0; k < header.Length && k < cells.Length; k++) d[header[k]] = cells[k].Trim();
            list.Add(new ArenaObject(
                d.GetValueOrDefault("arena_id", ""),
                d.GetValueOrDefault("object_id", ""),
                d.GetValueOrDefault("kind", ""),
                d.GetValueOrDefault("shape", ""),
                Dec(d.GetValueOrDefault("x_m", "0")),
                Dec(d.GetValueOrDefault("z_m", "0")),
                Dec(d.GetValueOrDefault("r_m", "0")),
                Dec(d.GetValueOrDefault("half_w_m", "0")),
                Dec(d.GetValueOrDefault("half_d_m", "0")),
                Dec(d.GetValueOrDefault("height_m", "0")),
                long.TryParse(d.GetValueOrDefault("hp", "0"), out var hp) ? hp : 0,
                d.GetValueOrDefault("interaction", "none")));
        }
        return list;
    }

    /// arena.csv → TerrainBody（v1: boundary=bounce；cover/pillar/prop=Stop+弹体摧毁；platform/ramp/spawn/pot=穿透）
    public static List<TerrainBody> BuildTerrain(IEnumerable<ArenaObject> objects)
    {
        var bodies = new List<TerrainBody>();
        int id = 1;
        foreach (var o in objects)
        {
            // spawn/pot 不入碰撞世界；platform/ramp = 高地（QueryGround 消费，不阻挡）
            if (o.Kind is "spawn" or "prop_pot") continue;

            var action = o.Kind switch
            {
                "boundary" => TerrainAction.Bounce,
                "cover_wall" or "pillar" or "prop_wood" or "prop_rock" => TerrainAction.DestroyProjectile,
                _ => TerrainAction.PassThrough,   // platform/ramp: 高地（不阻挡水平运动）
            };
            if (action == TerrainAction.PassThrough && o.Height == 0) continue;

            ConvexRegion? region;
            if (o.Kind == "boundary")
            {
                // 结界墙 = 场外半空间×4（rect 描述的是场内有效区——实体不得整体入碰撞世界）
                // GDD §2.1.1: 60×84m 有效战斗区，四周结界墙撞反弹
                region = null;   // 展开为 4 体，见下
            }
            else
            {
                region = o.Shape switch
                {
                    "rect" => ConvexRegion.Aabb(Q(o.X), Q(o.Z), Q(o.HalfW), Q(o.HalfD)),
                    "circle" => ConvexRegion.Circle(Q(o.X), Q(o.Z), Q(o.R)),
                    _ => ConvexRegion.Aabb(Q(o.X), Q(o.Z), Q(o.HalfW), Q(o.HalfD)),
                };
            }

            if (o.Kind == "boundary")
            {
                long hw = Q(o.HalfW), hd = Q(o.HalfD);
                bodies.Add(new TerrainBody(id++, ConvexRegion.HalfSpace(-Fixed.ONE, 0, -hw), action, 0));   // x ≥ +hw
                bodies.Add(new TerrainBody(id++, ConvexRegion.HalfSpace(Fixed.ONE, 0, -hw), action, 0));    // x ≤ −hw
                bodies.Add(new TerrainBody(id++, ConvexRegion.HalfSpace(0, -Fixed.ONE, -hd), action, 0));   // z ≥ +hd
                bodies.Add(new TerrainBody(id++, ConvexRegion.HalfSpace(0, Fixed.ONE, -hd), action, 0));    // z ≤ −hd
                continue;
            }
            bodies.Add(new TerrainBody(id++, region!, action, Q(o.Height)));
        }
        return bodies;
    }

    private static long Q(decimal m) => (long)Math.Round(m * 65536m, MidpointRounding.ToEven);
    private static decimal Dec(string s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
}
