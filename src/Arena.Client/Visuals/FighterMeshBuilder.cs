using System;
using System.Collections.Generic;
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
    private static PackedScene? _bodyScene;
    private static PackedScene? _weaponsScene;

    private static void EnsureLoaded()
    {
        _bodyScene ??= Godot.ResourceLoader.Load<PackedScene>("res://assets/body.glb");
        _weaponsScene ??= Godot.ResourceLoader.Load<PackedScene>("res://assets/weapons.glb");
    }

    private static void CollectMeshes(Node n, List<MeshInstance3D> outList)
    {
        if (n is MeshInstance3D mi) outList.Add(mi);
        foreach (var c in n.GetChildren()) CollectMeshes(c, outList);
    }

    /// 武器：从 weapons.glb 取该职业所属家族的子节点，挂到握把枢轴上。
    /// （评审 D2：此前**所有职业共用同一个盒子**。）
    public static void BuildWeapon(Node3D pivot, string meshName, Material weaponMat)
    {
        EnsureLoaded();
        if (_weaponsScene?.Instantiate() is not Node3D root) return;
        var want = root.GetNodeOrNull<Node3D>(meshName);
        if (want is null)
        {
            foreach (var c in root.GetChildren())
                if (c.Name.ToString().StartsWith("W_", StringComparison.Ordinal)) { want = c as Node3D; break; }
        }
        if (want is null) { root.QueueFree(); return; }
        want.GetParent()?.RemoveChild(want);
        var meshes = new List<MeshInstance3D>();
        CollectMeshes(want, meshes);
        foreach (var mi in meshes) mi.MaterialOverride = weaponMat;
        // 握把在原点、武器沿 +Y —— 与枢轴既有约定一致，无需再定位
        pivot.AddChild(want);
        root.QueueFree();
    }

    public static StandardMaterial3D Build(Node3D parent, FighterVisualProfile p, Color teamColor, byte team)
    {
        var bodyMat = Mat(teamColor);
        var darkMat = Mat(FighterVisualProfile.DarkPart);
        var skinMat = Mat(teamColor.Darkened(0.15f));
        var accentMat = Mat(p.Accent, emissive: true);

        // 人体：Blender 导出的 metaball 有机体块；材质替换为**可染色**实例（状态色/受击闪白仍生效）
        EnsureLoaded();
        if (_bodyScene?.Instantiate() is Node3D body)
        {
            var bodyMeshes = new List<MeshInstance3D>();
            CollectMeshes(body, bodyMeshes);
            foreach (var mi in bodyMeshes) mi.MaterialOverride = bodyMat;
            parent.AddChild(body);
        }

        // 12 系别徽记（背对镜头的一侧 +Z——越肩视角可见）
        Box(parent, accentMat, new Godot.Vector3(0.16f, 0.16f, 0.03f), new Godot.Vector3(0, 1.30f, 0.13f));

        BuildBranchParts(parent, p, darkMat, skinMat, accentMat);
        BuildTeamMarkers(parent, teamColor, team);   // D3: 阵营识别（形状 + 色，双通道）

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

    /// 阵营识别件（评审 D3：纯黑剪影下阵营必须可分——因此**形状**也要不同，不能只靠颜色）。
    /// 蓝方：方肩徽 + 方角地环；橙方：圆肩徽 + 圆环地环。
    private static void BuildTeamMarkers(Node3D parent, Color teamColor, byte team)
    {
        var mat = Mat(teamColor, emissive: true);
        // 脚下阵营环（贴地，越肩视角也看得到；非自发光过强——L2 明度近似，靠形状分）
        var ring = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.62f, BottomRadius = 0.62f, Height = 0.03f },
            MaterialOverride = Mat(teamColor.Darkened(0.25f), emissive: true),
            Position = new Godot.Vector3(0, 0.02f, 0),
        };
        parent.AddChild(ring);

        // 肩部阵营徽：蓝=方，橙=圆（形状冗余，灰度/色盲下也能分）
        foreach (float sx in new[] { 1f, -1f })
        {
            Mesh mesh = team == 0
                ? new BoxMesh { Size = new Godot.Vector3(0.12f, 0.10f, 0.12f) }
                : new SphereMesh { Radius = 0.07f, Height = 0.14f };
            parent.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = mat,
                Position = new Godot.Vector3(0.30f * sx, 1.52f, 0),
            });
        }
    }

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
