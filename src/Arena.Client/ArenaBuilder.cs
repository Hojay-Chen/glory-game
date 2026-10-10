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
        Mat(new Color(0.175f, 0.190f, 0.215f), 0.88f, 0.00f);   // #4A4F59 哑光中灰（给深色靴/腿做对比）
    private static readonly StandardMaterial3D WallMat =
        Mat(new Color(0.227f, 0.247f, 0.290f), 0.70f, 0.10f);   // 结界墙 冷灰石（退让背景）
    private static readonly StandardMaterial3D PlatformMat =
        Mat(new Color(0.235f, 0.220f, 0.185f), 0.62f, 0.12f);   // 高台 略暖（高处=危险提示 §5.2）
    private static readonly StandardMaterial3D RampMat =
        Mat(new Color(0.295f, 0.278f, 0.235f), 0.72f, 0.08f);   // 坡道 同族压暗
    private static readonly StandardMaterial3D CoverMat =
        Mat(new Color(0.225f, 0.202f, 0.172f), 0.78f, 0.05f);   // 掩体墙 暖褐
    private static readonly StandardMaterial3D PillarMat =
        Mat(new Color(0.335f, 0.322f, 0.298f), 0.72f, 0.05f);   // 石柱 亮石
    private static readonly StandardMaterial3D WoodMat =
        Mat(new Color(0.350f, 0.260f, 0.160f), 0.88f, 0.00f);   // 木箱 暖棕
    private static readonly StandardMaterial3D RockMat =
        Mat(new Color(0.300f, 0.310f, 0.330f), 0.92f, 0.00f);   // 乱石 冷灰
    private static readonly StandardMaterial3D PotMat =
        Mat(new Color(0.420f, 0.300f, 0.220f), 0.80f, 0.00f);   // 陶罐 赤陶

    private static StandardMaterial3D Mat(Color c, float rough, float metal) => new()
    {
        AlbedoColor = c,
        Roughness = rough,
        Metallic = metal,
    };

    /// 静态环境（光照 + 地板 + 地物 + 装饰——与对局状态无关，只建一次）。
    /// 由 MatchRoot._Ready 调用；arena.csv 在此自行加载（MatchRoot 不再另建地物）。
    public void BuildEnvironment(Node parent)
    {
        var objects = LoadArenaObjects();

        // 地板：顶面 y=0，与 Sim 地面/角色脚底对齐（原实现顶面在 0.2，角色脚陷进地面）。
        // 尺寸外推至盖住界墙基座与观众席，避免装饰件悬空。
        var (hw, hd) = BoundaryExtent(objects);
        AddSlab(parent, hw + 6f, hd + 6f, FloorMat);

        Build(parent, objects);
        BuildLights(parent);
        BuildEnvironmentSettings(parent);
        ArenaDecor.Build(parent, objects);
    }

    // ---- 三灯方案（art-bible §5.1——转播可读性核心：主光造型 / 补光读姿态 / 轮廓光抠边）----

    private static void BuildLights(Node parent)
    {
        // 主光 Key：定义形体与投影方向（开阴影）
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-50, 35, 0),
            LightColor = new Color(1.000f, 0.965f, 0.910f),   // #FFF6E8
            LightEnergy = 0.78f,                              // 规格 1.0–1.2
            ShadowEnabled = true,
        });

        // 补光 Fill：从相机侧低位抬暗面，避免纯黑（关阴影）
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-20, -140, 0),
            LightColor = new Color(0.788f, 0.839f, 1.000f),   // #C9D6FF 冷调
            LightEnergy = 0.20f,                              // 规格 0.25–0.35
            ShadowEnabled = false,
        });

        // 轮廓光 Rim：从角色背后上方打，把剪影从背景里"抠"出来（关阴影）。
        // 相机在 −Z 侧朝 +Z 看，故"背后"= +Z；低掠角（−16°）在头顶/背缘形成最亮边线。
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-16, 0, 0),
            LightColor = new Color(0.900f, 0.930f, 1.000f),   // 近白微冷
            LightEnergy = 0.38f,                              // art-bible 规格在无色调用映射时需下调
            ShadowEnabled = false,
        });
    }

    private static void BuildEnvironmentSettings(Node parent)
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.086f, 0.098f, 0.129f),                 // #161A21 暗沉去饱和（§5.2 区间 #14171E~#1E2330）
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.549f, 0.588f, 0.671f),               // #8C96AB
            AmbientLightEnergy = 0.34f,                                          // 规格 0.5–0.7；Compat 渲染器无色调映射，压低防过曝
            FogEnabled = false,                                                  // §5.1: 关雾（否则吃掉 35m 外剪影）
        };
        // 注: Compatibility 渲染器不支持色调映射（已实测 tonemap_mode 无效果）→ 只能靠灯光/材质控制曝光
        parent.AddChild(new WorldEnvironment { Environment = env });
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
                    // （旧实现无脑用 o.R，rect 平台 R=0 会退化为不可见；此处按 shape 分流修复）
                    if (o.R > 0)
                        AddCylinder(parent, o.X, o.Z, o.R, (float)o.Height, PlatformMat);
                    else
                        AddBox(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, (float)o.Height, PlatformMat);
                    break;
                case ArenaObjectKind.Ramp:
                    // 坡道 v1: 斜置 Box 占位（30° 斜面碰撞由 Sim 高地带承载）
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
