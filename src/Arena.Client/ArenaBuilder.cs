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

    public void Build(Node parent, IReadOnlyList<ArenaDefParser.ArenaObject> objects)
    {
        // 地板（GDD §19.2 60×84m 场内区——boundary 内接）
        AddBox(parent, 0, 0, 58, 82, 0.2f, FloorCol);
        // 环境光+平行光（gl_compatibility 需要光源）
        var sun = new DirectionalLight3D { RotationDegrees = new Godot.Vector3(-55, 30, 0) };
        sun.LightEnergy = 1.1f;
        parent.AddChild(sun);
        var env = new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.12f, 0.13f, 0.16f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.55f, 0.58f, 0.65f);
        env.AmbientLightEnergy = 0.8f;
        var ambient = new WorldEnvironment { Environment = env };
        parent.AddChild(ambient);

        foreach (var o in objects)
        {
            if (o.Kind == "spawn") continue;   // 出生点由 MatchRoot 标记
            switch (o.Kind)
            {
                case "boundary":
                    // 4 面墙（外场边界——按 rect half_w/half_d）
                    AddBox(parent, o.X, o.Z - o.HalfD - 0.5m, o.HalfW * 2 + 2, 1, 4, WallCol);
                    AddBox(parent, o.X, o.Z + o.HalfD + 0.5m, o.HalfW * 2 + 2, 1, 4, WallCol);
                    AddBox(parent, o.X - o.HalfW - 0.5m, o.Z, 1, o.HalfD * 2 + 2, 4, WallCol);
                    AddBox(parent, o.X + o.HalfW + 0.5m, o.Z, 1, o.HalfD * 2 + 2, 4, WallCol);
                    break;
                case "platform":
                    AddCylinder(parent, o.X, o.Z, o.R, (float)o.Height, PlatformCol);
                    break;
                case "cover":
                case "pillar":
                    AddBox(parent, o.X, o.Z, Math.Max(o.HalfW * 2, 0.6m), Math.Max(o.HalfD * 2, 0.6m), (float)o.Height + 0.2f, CoverCol);
                    break;
                case "pot":
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
