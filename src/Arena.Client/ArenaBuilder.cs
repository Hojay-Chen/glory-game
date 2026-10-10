using System;
using System.Collections.Generic;
using System.Globalization;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// arena.csv 对象 → 3D 占位网格（Vertical Slice 白盒视觉——GDD §28.2 灰盒标准）。
// 地板=灰 Plane、边界墙=Box、平台=Cylinder、cover/pillar=Box；颜色按 kind 区分。
namespace Arena.Client;

public sealed class ArenaBuilder
{
    // 配色与照明取自 docs/art/art-bible-v1.md §5（地板提亮以衬深色腿部；三灯把角色从背景抠出）
    private static readonly Color FloorCol = new(0.290f, 0.310f, 0.349f);      // #4A4F59 中性中灰
    private static readonly Color WallCol = new(0.34f, 0.37f, 0.43f);
    private static readonly Color PlatformCol = new(0.46f, 0.44f, 0.38f);
    private static readonly Color CoverCol = new(0.32f, 0.29f, 0.25f);

    /// 静态环境（光照/地板——与对局状态无关，只建一次）
    public void BuildEnvironment(Node parent)
    {
        AddBox(parent, 0, 0, 58, 82, 0.2f, FloorCol);

        // 主光 Key：定义形体与投影方向
        var key = new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-50, 35, 0),
            LightColor = new Color(1.0f, 0.965f, 0.910f),   // #FFF6E8
            LightEnergy = 1.15f,
            ShadowEnabled = true,
        };
        parent.AddChild(key);

        // 补光 Fill：抬暗面，避免纯黑（读姿态）
        var fill = new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-20, -140, 0),
            LightColor = new Color(0.788f, 0.839f, 1.0f),   // #C9D6FF 冷调
            LightEnergy = 0.30f,
            ShadowEnabled = false,
        };
        parent.AddChild(fill);

        // 轮廓光 Rim：从背后打，把角色剪影从背景里抠出来
        var rim = new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(18, 200, 0),
            LightColor = new Color(0.85f, 0.90f, 1.0f),
            LightEnergy = 0.60f,
            ShadowEnabled = false,
        };
        parent.AddChild(rim);

        var env = new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.078f, 0.090f, 0.118f);   // #14171E 去饱和
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.549f, 0.588f, 0.671f); // #8C96AB
        env.AmbientLightEnergy = 0.60f;
        parent.AddChild(new WorldEnvironment { Environment = env });
    }

    /// 地物网格（强类型 ArenaObjectKind 完整映射——Registry 集中语义，零散落 switch）
    public void Build(Node parent, IReadOnlyList<ArenaDefParser.ArenaObject> objects)
    {
        foreach (var o in objects)
        {
            if (!ArenaKindRegistry.NeedsVisual(o.KindId)) continue;
            switch (o.KindId)
            {
                case ArenaObjectKind.Boundary:
                    // 4 面结界墙（按 rect half_w/half_d 外推）
                    AddBox(parent, o.X, o.Z - o.HalfD - 0.5m, o.HalfW * 2 + 2, 1, 4, WallCol);
                    AddBox(parent, o.X, o.Z + o.HalfD + 0.5m, o.HalfW * 2 + 2, 1, 4, WallCol);
                    AddBox(parent, o.X - o.HalfW - 0.5m, o.Z, 1, o.HalfD * 2 + 2, 4, WallCol);
                    AddBox(parent, o.X + o.HalfW + 0.5m, o.Z, 1, o.HalfD * 2 + 2, 4, WallCol);
                    break;
                case ArenaObjectKind.Platform:
                    AddCylinder(parent, o.X, o.Z, o.R, (float)o.Height, PlatformCol);
                    break;
                case ArenaObjectKind.Ramp:
                    // 坡道 v1: 斜置 Box 占位（30° 斜面碰撞由 Sim 高地带承载）
                    var ramp = new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Godot.Vector3((float)(o.HalfW * 2), 0.4f, (float)(o.HalfD * 2)) },
                        MaterialOverride = new StandardMaterial3D { AlbedoColor = PlatformCol.Darkened(0.15f) },
                        Position = new Godot.Vector3((float)o.X, (float)(o.Height / 2), (float)o.Z),
                        RotationDegrees = new Godot.Vector3(-30, 0, 0),
                    };
                    parent.AddChild(ramp);
                    break;
                case ArenaObjectKind.CoverWall:
                case ArenaObjectKind.Pillar:
                case ArenaObjectKind.PropWood:
                case ArenaObjectKind.PropRock:
                    var w = Math.Max(Math.Max(o.HalfW * 2, o.R * 2), 0.6m);
                    var d = Math.Max(Math.Max(o.HalfD * 2, o.R * 2), 0.6m);
                    var col = o.KindId switch
                    {
                        ArenaObjectKind.Pillar => new Color(0.62f, 0.60f, 0.56f),
                        ArenaObjectKind.PropRock => new Color(0.42f, 0.42f, 0.44f),
                        _ => CoverCol,
                    };
                    AddBox(parent, o.X, o.Z, w, d, (float)o.Height + 0.2f, col);
                    break;
                case ArenaObjectKind.PropPot:
                    AddCylinder(parent, o.X, o.Z, 0.4m, 0.5f, CoverCol);
                    break;
            }
        }
    }

    private static void AddBox(Node parent, decimal x, decimal z, decimal w, decimal d, float h, Color col)
    {
        var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Godot.Vector3((float)w, h, (float)d) } };
        mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = col };
        mi.Position = new Godot.Vector3((float)x, h / 2, (float)z);
        parent.AddChild(mi);
    }

    private static void AddCylinder(Node parent, decimal x, decimal z, decimal r, float h, Color col)
    {
        var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = (float)r, BottomRadius = (float)r, Height = h } };
        mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = col };
        mi.Position = new Godot.Vector3((float)x, h / 2, (float)z);
        parent.AddChild(mi);
    }
}
