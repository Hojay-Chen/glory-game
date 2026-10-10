using Godot;

// PRODUCTION - Arena.Client
// 人形装配器（程序化基本体，零外部资产）。
// 依据 `docs/art/art-bible-v1.md` §4 的 14 件基础体 + 按系变体件——**纯代码构造**，
// 与判定体无关（Sim 的碰撞是独立几何模型），因此外形可自由替换为将来的真模型。
// 坐标约定（美术规格）: 原点在脚底、前方 −Z、背面 +Z、右手 +X。
namespace Arena.Client.Visuals;

public static class FighterMeshBuilder
{
    /// 把 14 件基础体 + 系别变体件挂到 parent 下。
    /// 返回躯干材质（FighterView 用它做受击闪白与状态染色的载体）。
    public static StandardMaterial3D Build(Node3D parent, FighterVisualProfile p, Color teamColor)
    {
        var bodyMat = Mat(teamColor);
        var darkMat = Mat(FighterVisualProfile.DarkPart);
        var skinMat = Mat(teamColor.Darkened(0.15f));
        var accentMat = Mat(p.Accent, emissive: true);

        // 1 脚 ×2
        Box(parent, darkMat, new Godot.Vector3(0.13f, 0.10f, 0.30f), new Godot.Vector3(0.12f, 0.05f, -0.03f));
        Box(parent, darkMat, new Godot.Vector3(0.13f, 0.10f, 0.30f), new Godot.Vector3(-0.12f, 0.05f, -0.03f));
        // 2 小腿 ×2
        Capsule(parent, darkMat, 0.065f, 0.42f, new Godot.Vector3(0.12f, 0.30f, 0));
        Capsule(parent, darkMat, 0.065f, 0.42f, new Godot.Vector3(-0.12f, 0.30f, 0));
        // 3 大腿 ×2
        Capsule(parent, darkMat, 0.090f, 0.44f, new Godot.Vector3(0.12f, 0.72f, 0));
        Capsule(parent, darkMat, 0.090f, 0.44f, new Godot.Vector3(-0.12f, 0.72f, 0));
        // 4 骨盆
        Box(parent, darkMat, new Godot.Vector3(0.34f, 0.22f, 0.22f), new Godot.Vector3(0, 0.98f, 0));
        // 5 躯干（阵营主色——状态染色载体）
        Box(parent, bodyMat, new Godot.Vector3(0.42f, 0.42f, 0.24f), new Godot.Vector3(0, 1.28f, 0));
        // 6 颈 / 7 头
        Cyl(parent, darkMat, 0.055f, 0.08f, new Godot.Vector3(0, 1.52f, 0));
        Sphere(parent, skinMat, 0.11f, new Godot.Vector3(0, 1.64f, 0));
        // 9/10/11 上肢 ×2
        foreach (float sx in new[] { 1f, -1f })
        {
            Capsule(parent, skinMat, 0.060f, 0.30f, new Godot.Vector3(0.27f * sx, 1.32f, 0));
            Capsule(parent, skinMat, 0.055f, 0.28f, new Godot.Vector3(0.28f * sx, 1.06f, 0));
            Sphere(parent, darkMat, 0.075f, new Godot.Vector3(0.285f * sx, 0.91f, 0));
        }
        // 12 系别徽记（背对镜头的一侧 +Z——越肩视角可见）
        Box(parent, accentMat, new Godot.Vector3(0.16f, 0.16f, 0.03f), new Godot.Vector3(0, 1.30f, 0.13f));

        BuildBranchParts(parent, p, darkMat, skinMat, accentMat);

        return bodyMat;
    }

    /// 变体件: 肩垫形状（8）+ 背饰（13）。六系只靠「肩垫形状 + 背饰 + 徽记色 + 体型」区分。
    private static void BuildBranchParts(Node3D parent, FighterVisualProfile p,
        StandardMaterial3D darkMat, StandardMaterial3D skinMat, StandardMaterial3D accentMat)
    {
        void Shoulders(Godot.Vector3 size, float y, float x, StandardMaterial3D m)
        {
            Box(parent, m, size, new Godot.Vector3(x, y, 0));
            Box(parent, m, size, new Godot.Vector3(-x, y, 0));
        }

        switch (p.Shape)
        {
            case BranchShape.Mage:      // 下摆 A 字外扩 + 顶部尖
                Cyl(parent, darkMat, 0.18f, 0.70f, new Godot.Vector3(0, 0.65f, 0), topR: 0.34f);
                Prism(parent, accentMat, new Godot.Vector3(0.24f, 0.24f, 0.28f), new Godot.Vector3(0, 1.78f, 0));
                Shoulders(new Godot.Vector3(0.16f, 0.10f, 0.16f), 1.47f, 0.24f, darkMat);
                break;
            case BranchShape.Blade:     // 宽肩窄腰梯形 + 短披风
                Prism(parent, darkMat, new Godot.Vector3(0.20f, 0.12f, 0.18f), new Godot.Vector3(0.26f, 1.47f, 0));
                Prism(parent, darkMat, new Godot.Vector3(0.20f, 0.12f, 0.18f), new Godot.Vector3(-0.26f, 1.47f, 0));
                Box(parent, Mat(p.Accent.Darkened(0.35f)), new Godot.Vector3(0.30f, 0.50f, 0.06f), new Godot.Vector3(0, 1.05f, 0.14f));
                break;
            case BranchShape.Gunner:    // 扁肩甲 + 斜弹带 + 腰袋
                Shoulders(new Godot.Vector3(0.18f, 0.08f, 0.14f), 1.47f, 0.26f, darkMat);
                Box(parent, accentMat, new Godot.Vector3(0.10f, 0.42f, 0.06f), new Godot.Vector3(0.20f, 1.20f, 0.14f));
                Box(parent, darkMat, new Godot.Vector3(0.14f, 0.12f, 0.10f), new Godot.Vector3(-0.22f, 0.98f, 0.12f));
                break;
            case BranchShape.Brawler:   // 低重心 + 放大拳/前臂（无甲、"最空"的背）
                Shoulders(new Godot.Vector3(0.14f, 0.09f, 0.14f), 1.44f, 0.28f, skinMat);
                Sphere(parent, darkMat, 0.095f, new Godot.Vector3(0.29f, 0.90f, 0));
                Sphere(parent, darkMat, 0.095f, new Godot.Vector3(-0.29f, 0.90f, 0));
                break;
            case BranchShape.Night:     // 兜帽收顶 + 背披流苏
                Prism(parent, darkMat, new Godot.Vector3(0.24f, 0.24f, 0.28f), new Godot.Vector3(0, 1.66f, 0));
                Box(parent, accentMat, new Godot.Vector3(0.26f, 0.46f, 0.05f), new Godot.Vector3(0, 1.16f, 0.13f));
                break;
            case BranchShape.Cleric:    // 方正厚重: 头盔 + 大肩甲 + 盾
                Sphere(parent, darkMat, 0.13f, new Godot.Vector3(0, 1.66f, 0));
                Shoulders(new Godot.Vector3(0.24f, 0.16f, 0.22f), 1.48f, 0.27f, darkMat);
                Box(parent, accentMat, new Godot.Vector3(0.10f, 0.55f, 0.40f), new Godot.Vector3(0.34f, 1.15f, -0.10f));
                break;
            default:                    // 散人: 中性人形 + 万象伞（收拢态背在身后）
                Shoulders(new Godot.Vector3(0.15f, 0.09f, 0.15f), 1.47f, 0.25f, darkMat);
                Cyl(parent, accentMat, 0.04f, 1.05f, new Godot.Vector3(0, 1.05f, 0.16f));
                break;
        }
    }

    // ---- 基本体工厂 ----

    private static StandardMaterial3D Mat(Color c, bool emissive = false) => new()
    {
        AlbedoColor = c,
        Roughness = 0.75f,
        Metallic = 0.05f,
        EmissionEnabled = emissive,
        Emission = emissive ? c : Colors.Black,
        EmissionEnergyMultiplier = emissive ? 0.45f : 0f,
    };

    private static void Add(Node3D parent, MeshInstance3D mi, Material m, Godot.Vector3 pos)
    {
        mi.MaterialOverride = m;
        mi.Position = pos;
        parent.AddChild(mi);
    }

    private static void Box(Node3D parent, Material m, Godot.Vector3 size, Godot.Vector3 pos, Material? overrideMat = null) =>
        Add(parent, new MeshInstance3D { Mesh = new BoxMesh { Size = size } }, overrideMat ?? m, pos);

    private static void Capsule(Node3D parent, Material m, float r, float h, Godot.Vector3 pos) =>
        Add(parent, new MeshInstance3D { Mesh = new CapsuleMesh { Radius = r, Height = h } }, m, pos);

    private static void Sphere(Node3D parent, Material m, float r, Godot.Vector3 pos) =>
        Add(parent, new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = r * 2f } }, m, pos);

    private static void Cyl(Node3D parent, Material m, float r, float h, Godot.Vector3 pos, float? topR = null) =>
        Add(parent, new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = topR ?? r, BottomRadius = r, Height = h },
        }, m, pos);

    private static void Prism(Node3D parent, Material m, Godot.Vector3 size, Godot.Vector3 pos) =>
        Add(parent, new MeshInstance3D { Mesh = new PrismMesh { Size = size } }, m, pos);
}
