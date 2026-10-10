using System.Collections.Generic;
using System.IO;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// arena.csv 对象 → 3D 竞技场（Vertical Slice 美术层）。
// 视觉规格: docs/art/art-bible-v1.md §5（三灯 / 地板色 / 背景 / 环境光）
//        + docs/PRODUCTION-PLAN.md §2.3（材质区分 / 光照主次 / 构图可读）。
// 本类只建**视觉网格**——Sim 地形独立来自 MatchAssembler（ArenaDefParser.BuildTerrain），
// 因此改外观零影响判定（碰撞只认 arena.csv 的 TerrainBody）。装饰件由 ArenaDecor 追加。
namespace Arena.Client;

public sealed class ArenaBuilder
{
    // ---- 材质分层（每类不同 Roughness/Metallic/Albedo，杜绝"一片同灰"——§2.3）----
    // 共用实例（非每件 new）: gl_compatibility 对 draw call 敏感，材质复用便于批处理与降内存。
    private static readonly StandardMaterial3D FloorMat =
        Mat(new Color(0.130f, 0.145f, 0.175f), 0.90f, 0.00f);   // 石地 冷石板（压暗——给角色做亮底衬）
    private static readonly StandardMaterial3D WallMat =
        Mat(new Color(0.318f, 0.345f, 0.405f), 0.72f, 0.08f);   // 结界墙 冷灰石（抬高以显剪影）
    private static readonly StandardMaterial3D PlatformMat =
        Mat(new Color(0.212f, 0.198f, 0.170f), 0.60f, 0.14f);   // 高台 略暖（高处=危险提示 §5.2）
    private static readonly StandardMaterial3D RampMat =
        Mat(new Color(0.255f, 0.240f, 0.205f), 0.74f, 0.08f);   // 坡道 同族压暗
    private static readonly StandardMaterial3D CoverMat =
        Mat(new Color(0.196f, 0.176f, 0.150f), 0.80f, 0.05f);   // 掩体墙 暖褐
    private static readonly StandardMaterial3D PillarMat =
        Mat(new Color(0.300f, 0.290f, 0.275f), 0.74f, 0.05f);   // 石柱 亮石
    private static readonly StandardMaterial3D WoodMat =
        Mat(new Color(0.330f, 0.235f, 0.140f), 0.88f, 0.00f);   // 木箱 暖棕
    private static readonly StandardMaterial3D RockMat =
        Mat(new Color(0.255f, 0.265f, 0.290f), 0.92f, 0.00f);   // 乱石 冷灰
    private static readonly StandardMaterial3D PotMat =
        Mat(new Color(0.420f, 0.290f, 0.205f), 0.80f, 0.00f);   // 陶罐 赤陶

    // 擂台盘面（中央圆台顶面——最靠近镜头的面，单独给一点暖金倾向的石材，
    // 用**环形刻线**而非整盘高光来提供近场焦点，避免"一块大煎饼"）
    private static readonly StandardMaterial3D ArenaDeckMat =
        Mat(new Color(0.238f, 0.222f, 0.190f), 0.66f, 0.10f);
    private static readonly StandardMaterial3D DeckLineMat =
        Emissive(new Color(0.30f, 0.245f, 0.140f), 0.10f, 0.45f, 0.15f);   // 刻线 暖金**微**自发光（压低——刻线是地面细节不是发光条）

    private static StandardMaterial3D Mat(Color c, float rough, float metal) => new()
    {
        AlbedoColor = c,
        Roughness = rough,
        Metallic = metal,
    };

    private static StandardMaterial3D Emissive(Color c, float energy, float rough, float metal) => new()
    {
        AlbedoColor = c,
        Roughness = rough,
        Metallic = metal,
        EmissionEnabled = true,
        Emission = c,
        EmissionEnergyMultiplier = energy,
    };

    /// 静态环境（光照 + 天空 + 地板 + 地物 + 装饰——与对局状态无关，只建一次）。
    /// 由 MatchRoot._Ready 调用；arena.csv 在此自行加载（MatchRoot 不再另建地物）。
    public void BuildEnvironment(Node parent)
    {
        var objects = LoadArenaObjects();

        // 地板：顶面 y=0，与 Sim 地面/角色脚底对齐（原实现顶面在 0.2，角色脚陷进地面）。
        // 尺寸外推至盖住界墙基座与观众席，避免装饰件悬空。
        var (hw, hd) = BoundaryExtent(objects);
        AddSlab(parent, hw + 6f, hd + 6f, FloorMat);

        Build(parent, objects);
        BuildArenaDeck(objects, parent);   // 中央擂台盘面 + 环形刻线（近场焦点，取代"大煎饼"）
        BuildLights(parent);
        BuildSkyAndEnclosure(parent, hw, hd);   // 天空 + 远景围合（消灭死黑 void）
        BuildEnvironmentSettings(parent);
        ArenaDecor.Build(parent, objects);
    }

    // ---- 三灯方案（art-bible §5.1——转播可读性核心：主光造型 / 补光读姿态 / 轮廓光抠边）----

    private static void BuildLights(Node parent)
    {
        // 主光 Key：定义形体与投影方向（开阴影）——暖白，能量给足（旧实现 0.78 太弱→画面无立体感）
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-46, 38, 0),
            LightColor = new Color(1.000f, 0.955f, 0.885f),   // #FFF4E2
            LightEnergy = 1.05f,
            ShadowEnabled = true,
            ShadowBias = 0.03f,
        });

        // 补光 Fill：从相机侧低位抬暗面，避免纯黑（关阴影）——冷调，与暖主光形成色温分离
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-18, -140, 0),
            LightColor = new Color(0.62f, 0.72f, 1.000f),     // 冷蓝补光（比旧实现更"冷"，拉开冷暖）
            LightEnergy = 0.38f,
            ShadowEnabled = false,
        });

        // 轮廓光 Rim：从角色背后上方低掠角打，把剪影从背景里"抠"出来（§5.1 核心）。
        // 相机在 −Z 侧朝 +Z 看，故"背后"= +Z；低掠角（−14°）在头顶/背缘形成最亮边线。
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-14, 0, 0),
            LightColor = new Color(0.850f, 0.900f, 1.000f),   // 近白微冷
            LightEnergy = 0.75f,                              // §5.1 规格 0.5–0.7；略高补偿兼容渲染器的平光
            ShadowEnabled = false,
        });
    }

    // ---- 天空 + 远景围合（消灭"死黑 void + 飘浮黑盒"）----

    private static void BuildSkyAndEnclosure(Node parent, float hw, float hd)
    {
        // 远景天幕环：一圈**高于界墙的圆柱内壁**，顶边渐隐到天空色。
        // 作用：给画面一个"地平线/远景"，把 void 顶掉；暗色低对比（§5.2 背景必须退让）。
        float r = Mathf.Max(hw, hd) + 16f;
        var ring = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = r,
                BottomRadius = r,
                Height = 46f,
                RadialSegments = 48,
                CapTop = false,
                CapBottom = false,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.118f, 0.133f, 0.176f),     // #1E2230 暗沉去饱和
                Roughness = 1.0f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,     // 双面（相机在环内）
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            Position = new Godot.Vector3(0, 20f, 0),
        };
        parent.AddChild(ring);
    }

    private static void BuildEnvironmentSettings(Node parent)
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.055f, 0.065f, 0.098f),      // 天顶深靛（比地面暗，形成上暗下亮的照明感）
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.560f, 0.600f, 0.690f),    // #8F99B0
            AmbientLightEnergy = 0.42f,
            FogEnabled = false,                                        // §5.1: 关雾（否则吃掉 35m 外剪影）
        };
        // 注: Compatibility 渲染器不支持色调映射（已实测 tonemap_mode 无效果）→ 只能靠灯光/材质控制曝光
        parent.AddChild(new WorldEnvironment { Environment = env });
    }

    /// 中央擂台盘面 + 同心环刻线——把 r=8m 的大圆盘从"一块煎饼"变成有层次的"擂台"。
    private static void BuildArenaDeck(IReadOnlyList<ArenaDefParser.ArenaObject> objects, Node parent)
    {
        float px = 0, pz = 0, pr = 8, ph = 1.2f;
        foreach (var o in objects)
            if (o.KindId == ArenaObjectKind.Platform && o.R > 0)
            { px = (float)o.X; pz = (float)o.Z; pr = (float)o.R; ph = (float)o.Height; }

        // 盘面顶皮（略高于台体，给独立的暖石材与刻线）
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = pr - 0.05f, BottomRadius = pr - 0.05f, Height = 0.06f, RadialSegments = 64 },
            MaterialOverride = ArenaDeckMat,
            Position = new Godot.Vector3(px, ph + 0.02f, pz),
        });
        // 同心环刻线（2 圈**细线**——给盘面一个焦点，不做发光条）
        for (int i = 0; i < 2; i++)
        {
            float rr = pr * (0.42f + 0.34f * i);
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = rr - 0.025f, OuterRadius = rr + 0.025f, Rings = 64, RingSegments = 6 },
                MaterialOverride = DeckLineMat,
                Position = new Godot.Vector3(px, ph + 0.05f, pz),
                Scale = new Godot.Vector3(1f, 0.12f, 1f),
            });
        }
        // 十字引导（盘面内的四条短刻线）
        foreach (var (dx, dz) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
        {
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Godot.Vector3(dx != 0 ? pr * 0.9f : 0.06f, 0.04f, dz != 0 ? pr * 0.9f : 0.06f) },
                MaterialOverride = DeckLineMat,
                Position = new Godot.Vector3(px + dx * pr * 0.45f, ph + 0.05f, pz + dz * pr * 0.45f),
            });
        }
    }

    /// 地物网格（强类型 ArenaObjectKind 完整映射——Registry 集中语义，零散落 switch）。
    public void Build(Node parent, IReadOnlyList<ArenaDefParser.ArenaObject> objects)
    {
        foreach (var o in objects)
        {
            if (!ArenaKindRegistry.NeedsVisual(o.KindId)) continue;
            switch (o.KindId)
            {
                case ArenaObjectKind.Boundary:
                    // 4 面结界墙（按 rect half_w/half_d 外推；作背景退让）
                    AddBox(parent, o.X, o.Z - o.HalfD - 0.5m, o.HalfW * 2 + 2, 1, 4, WallMat);
                    AddBox(parent, o.X, o.Z + o.HalfD + 0.5m, o.HalfW * 2 + 2, 1, 4, WallMat);
                    AddBox(parent, o.X - o.HalfW - 0.5m, o.Z, 1, o.HalfD * 2 + 2, 4, WallMat);
                    AddBox(parent, o.X + o.HalfW + 0.5m, o.Z, 1, o.HalfD * 2 + 2, 4, WallMat);
                    break;
                case ArenaObjectKind.Platform:
                    // 圆台（中央擂台 r8）→ 圆柱；方台（南北高台）→ 立方
                    if (o.R > 0)
                        AddCylinder(parent, o.X, o.Z, o.R, (float)o.Height, PlatformMat);
                    else
                        AddBox(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, (float)o.Height, PlatformMat);
                    break;
                case ArenaObjectKind.Ramp:
                    parent.AddChild(new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Godot.Vector3((float)(o.HalfW * 2), 0.4f, (float)(o.HalfD * 2)) },
                        MaterialOverride = RampMat,
                        Position = new Godot.Vector3((float)o.X, (float)(o.Height / 2), (float)o.Z),
                        RotationDegrees = new Godot.Vector3(-30, 0, 0),
                    });
                    break;
                case ArenaObjectKind.CoverWall:
                    AddBox(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, (float)o.Height, CoverMat);
                    break;
                case ArenaObjectKind.Pillar:
                    AddCylinder(parent, o.X, o.Z, o.R, (float)o.Height, PillarMat);
                    break;
                case ArenaObjectKind.PropWood:
                    AddBox(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, (float)o.Height, WoodMat);
                    break;
                case ArenaObjectKind.PropRock:
                    AddBox(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, (float)o.Height, RockMat);
                    break;
                case ArenaObjectKind.PropPot:
                    AddCylinder(parent, o.X, o.Z, 0.4m, 0.5f, PotMat);
                    break;
            }
        }
    }

    // ---- 装配辅助 ----

    /// 地板板（顶面 y=0；与脚底/Sim 地面同高）
    private static void AddSlab(Node parent, float halfW, float halfD, Material m)
    {
        const float thick = 0.3f;
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Godot.Vector3(halfW * 2, thick, halfD * 2) },
            MaterialOverride = m,
            Position = new Godot.Vector3(0, -thick / 2f, 0),   // 顶面 = 0
        });
    }

    private static void AddBox(Node parent, decimal x, decimal z, decimal w, decimal d, float h, Material m)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Godot.Vector3((float)w, h, (float)d) },
            MaterialOverride = m,
            Position = new Godot.Vector3((float)x, h / 2f, (float)z),
        });
    }

    private static void AddCylinder(Node parent, decimal x, decimal z, decimal r, float h, Material m)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = (float)r, BottomRadius = (float)r, Height = h },
            MaterialOverride = m,
            Position = new Godot.Vector3((float)x, h / 2f, (float)z),
        });
    }

    // ---- arena.csv 加载（从 cwd 向上找仓库根——与 MatchRoot.FindRepoRoot 同策略）----

    private static List<ArenaDefParser.ArenaObject> LoadArenaObjects()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/balance-sheet/arena.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir is null ? new List<ArenaDefParser.ArenaObject>()
                           : ArenaDefParser.Parse(Path.Combine(dir, "docs/balance-sheet/arena.csv"));
    }

    private static (float hw, float hd) BoundaryExtent(IReadOnlyList<ArenaDefParser.ArenaObject> objects)
    {
        foreach (var o in objects)
            if (o.KindId == ArenaObjectKind.Boundary)
                return ((float)o.HalfW, (float)o.HalfD);
        return (30f, 42f);
    }
}
