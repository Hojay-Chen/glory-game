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
    private static readonly Color FloorCol = new(0.28f, 0.30f, 0.34f);
    private static readonly Color WallCol = new(0.45f, 0.48f, 0.55f);
    private static readonly Color PlatformCol = new(0.55f, 0.52f, 0.44f);
    private static readonly Color CoverCol = new(0.40f, 0.36f, 0.30f);

    /// 静态环境（光照/地板——与对局状态无关，只建一次）
    public void BuildEnvironment(Node parent)
    {
        AddBox(parent, 0, 0, 58, 82, 0.2f, FloorCol);
        var sun = new DirectionalLight3D { RotationDegrees = new Godot.Vector3(-55, 30, 0) };
        sun.LightEnergy = 1.1f;
        parent.AddChild(sun);
        var env = new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.12f, 0.13f, 0.16f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.55f, 0.58f, 0.65f);
        env.AmbientLightEnergy = 0.8f;
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
