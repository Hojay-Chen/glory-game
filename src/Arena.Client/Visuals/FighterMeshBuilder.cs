using System;
using System.Collections.Generic;
using Godot;

// PRODUCTION - Arena.Client
// 人形装配器：**关节化**人形（程序化骨架 + 分段网格），零外部资产。
// 依据 `docs/art/art-bible-v1.md` §4。
//
// 为什么不用静态网格/metaball：静态体块在越肩机位下读不出**四肢与动作**——评审 §2.2 判 FAIL 的主因。
// 本版改成 FK 骨架：Hips→Chest→Head，Chest→肩→上臂→前臂→手，Hips→大腿→小腿→脚；
// 每段是可独立旋转的 Node3D 枢轴，FighterView 逐帧写旋转 → 步态/待机/攻击/受击全部由代码驱动。
//
// 坐标约定: 原点在脚底、前方 −Z、背面 +Z、右手 +X。总高 ≈ 1.80m。
namespace Arena.Client.Visuals;

/// 关节化人形骨架引用（FighterView 逐帧写各关节 rotation 做程序化动作）。
public sealed class BodyRig
{
    public Node3D Root = null!;      // 整体（呼吸起伏 / 前倾 / 倒地）
    public Node3D Hips = null!;
    public Node3D Chest = null!;     // 胸/脊柱
    public Node3D Head = null!;
    public Node3D ArmL = null!, ForeL = null!, HandL = null!;
    public Node3D ArmR = null!, ForeR = null!, HandR = null!;
    public Node3D ThighL = null!, ShinL = null!, FootL = null!;
    public Node3D ThighR = null!, ShinR = null!, FootR = null!;
    public StandardMaterial3D TeamMaterial = null!;   // 状态染色 / 受击闪白的载体
    public StandardMaterial3D DarkMaterial = null!;
}

public static class FighterMeshBuilder
{
    private static PackedScene? _weaponsScene;

    private static void EnsureWeapons()
        => _weaponsScene ??= Godot.ResourceLoader.Load<PackedScene>("res://assets/weapons.glb");

    private static void CollectMeshes(Node n, List<MeshInstance3D> outList)
    {
        if (n is MeshInstance3D mi) outList.Add(mi);
        foreach (var c in n.GetChildren()) CollectMeshes(c, outList);
    }

    /// 武器：从 weapons.glb 取该职业所属家族的子节点，挂到握把枢轴上。
    public static void BuildWeapon(Node3D pivot, string meshName, Material weaponMat)
    {
        EnsureWeapons();
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
        pivot.AddChild(want);
        root.QueueFree();
    }

    // ---- 骨架尺寸（脚底 y=0，总高 ≈1.80m）----
    private const float HipY = 0.94f, ChestY = 1.12f, NeckY = 1.50f, HeadY = 1.64f, HeadR = 0.145f;
    private const float ThighLen = 0.44f, ShinLen = 0.40f;
    private const float UpperArmLen = 0.32f, ForeLen = 0.27f;

    public static BodyRig Build(Node3D parent, FighterVisualProfile p, Color teamColor, byte team)
    {
        var rig = new BodyRig();
        rig.TeamMaterial = Mat(teamColor);
        rig.DarkMaterial = Mat(FighterVisualProfile.DarkPart);
        rig.TeamMaterial.EmissionEnabled = true;                 // 预留：受击闪白写 Emission
        rig.TeamMaterial.Emission = Colors.Black;
        var teamMat = rig.TeamMaterial;
        var darkMat = rig.DarkMaterial;
        var accentMat = Mat(p.Accent, emissive: true);
        // 前臂/小腿：只比躯干暗一档——**必须在地板底色之上可读**（此前压到 0.28 使下半身与地板糊在一起，
        // 视觉上整个人"缩水"一半；这是本轮最重要的一处"看不清"根因）。
        var limbMat = Mat(teamColor.Darkened(0.12f));
        var gearMat = Mat(new Color(0.30f, 0.33f, 0.39f));       // 靴/手/骨盆：中灰护具（对比地板）

        rig.Root = Joint(parent, "Root", Godot.Vector3.Zero);

        // --- 躯干 ---
        rig.Hips = Joint(rig.Root, "Hips", new Godot.Vector3(0, HipY, 0));
        Box(rig.Hips, gearMat, new Godot.Vector3(0.34f, 0.20f, 0.22f), new Godot.Vector3(0, -0.02f, 0));   // 骨盆

        rig.Chest = Joint(rig.Hips, "Chest", new Godot.Vector3(0, ChestY - HipY, 0));
        // 胸廓：躯干箱 + 顶部"肩轭"箱 → 干净的宽肩剪影（棱柱在实机里读作几何碎片，已弃用）
        Box(rig.Chest, teamMat, new Godot.Vector3(0.34f, 0.44f, 0.24f), new Godot.Vector3(0, 0.20f, 0));
        Box(rig.Chest, teamMat, new Godot.Vector3(0.52f, 0.15f, 0.26f), new Godot.Vector3(0, 0.36f, 0));
        Box(rig.Chest, accentMat, new Godot.Vector3(0.15f, 0.11f, 0.03f), new Godot.Vector3(0, 0.24f, -0.13f)); // 胸徽

        rig.Head = Joint(rig.Chest, "Head", new Godot.Vector3(0, NeckY - ChestY + 0.06f, 0));
        Sphere(rig.Head, darkMat, HeadR, new Godot.Vector3(0, HeadY - NeckY - 0.06f, 0));                   // 头
        Box(rig.Head, accentMat, new Godot.Vector3(0.11f, 0.015f, 0.05f), new Godot.Vector3(0, HeadY - NeckY - 0.04f, -HeadR)); // 面甲缝

        // --- 手臂（肩枢轴在胸顶两侧）---
        (rig.ArmL, rig.ForeL, rig.HandL) = Arm(rig.Chest, teamMat, limbMat, gearMat, +1f);
        (rig.ArmR, rig.ForeR, rig.HandR) = Arm(rig.Chest, teamMat, limbMat, gearMat, -1f);

        // --- 腿 ---
        (rig.ThighL, rig.ShinL, rig.FootL) = Leg(rig.Hips, teamMat, limbMat, gearMat, +1f);
        (rig.ThighR, rig.ShinR, rig.FootR) = Leg(rig.Hips, teamMat, limbMat, gearMat, -1f);

        BuildBranchParts(rig, p, teamMat, darkMat, accentMat);
        BuildTeamMarkers(rig.Root, teamColor, team);   // 阵营识别（形状 + 色双通道）

        return rig;
    }

    private static (Node3D arm, Node3D fore, Node3D hand) Arm(Node3D chest, Material upper, Material lower, Material dark, float side)
    {
        var sh = Joint(chest, side > 0 ? "ArmL" : "ArmR", new Godot.Vector3(side * 0.235f, 0.34f, 0));
        CapsuleExt(sh, upper, 0.062f, UpperArmLen);
        var fore = Joint(sh, "Fore", new Godot.Vector3(0, -UpperArmLen, 0));
        CapsuleExt(fore, lower, 0.052f, ForeLen);
        var hand = Joint(fore, "Hand", new Godot.Vector3(0, -ForeLen, 0));
        Sphere(hand, dark, 0.062f, new Godot.Vector3(0, -0.03f, 0));
        return (sh, fore, hand);
    }

    private static (Node3D thigh, Node3D shin, Node3D foot) Leg(Node3D hips, Material upper, Material lower, Material dark, float side)
    {
        var th = Joint(hips, side > 0 ? "ThighL" : "ThighR", new Godot.Vector3(side * 0.105f, -0.06f, 0));
        CapsuleExt(th, upper, 0.078f, ThighLen);
        var sh = Joint(th, "Shin", new Godot.Vector3(0, -ThighLen, 0));
        CapsuleExt(sh, lower, 0.062f, ShinLen);
        var ft = Joint(sh, "Foot", new Godot.Vector3(0, -ShinLen, 0));
        Box(ft, dark, new Godot.Vector3(0.11f, 0.06f, 0.24f), new Godot.Vector3(0, -0.03f, -0.05f));   // 脚掌向前伸
        return (th, sh, ft);
    }

    /// 系别变体件：肩垫 / 头饰 / 背饰 / 下摆——挂在胸或髋上，保留六系剪影差异。
    private static void BuildBranchParts(BodyRig rig, FighterVisualProfile p,
        StandardMaterial3D teamMat, StandardMaterial3D darkMat, StandardMaterial3D accentMat)
    {
        void Shoulders(Godot.Vector3 size, float x, Material m)
        {
            Box(rig.Chest, m, size, new Godot.Vector3(x, 0.34f, 0));
            Box(rig.Chest, m, size, new Godot.Vector3(-x, 0.34f, 0));
        }

        switch (p.Shape)
        {
            case BranchShape.Mage:      // 大肩袍 + 尖帽 + A 字下摆
                Shoulders(new Godot.Vector3(0.17f, 0.11f, 0.17f), 0.25f, darkMat);
                Prism(rig.Head, accentMat, new Godot.Vector3(0.22f, 0.32f, 0.22f), new Godot.Vector3(0, HeadY - NeckY + 0.12f, 0));
                Cyl(rig.Hips, darkMat, 0.25f, 0.60f, new Godot.Vector3(0, -0.34f, 0), topR: 0.16f);   // 法袍下摆
                break;
            case BranchShape.Blade:     // 宽肩甲 + 短披风
                Shoulders(new Godot.Vector3(0.21f, 0.13f, 0.19f), 0.26f, darkMat);
                Box(rig.Chest, Mat(p.Accent.Darkened(0.35f)), new Godot.Vector3(0.32f, 0.52f, 0.05f), new Godot.Vector3(0, 0.18f, 0.15f));
                break;
            case BranchShape.Gunner:    // 扁肩甲 + 斜弹带 + 腰袋
                Shoulders(new Godot.Vector3(0.19f, 0.08f, 0.15f), 0.27f, darkMat);
                Box(rig.Chest, accentMat, new Godot.Vector3(0.10f, 0.44f, 0.06f), new Godot.Vector3(0.17f, 0.20f, 0.14f));
                Box(rig.Hips, darkMat, new Godot.Vector3(0.14f, 0.12f, 0.10f), new Godot.Vector3(-0.20f, 0.02f, 0.12f));
                break;
            case BranchShape.Brawler:   // 低重心 + 放大拳
                Shoulders(new Godot.Vector3(0.15f, 0.09f, 0.15f), 0.29f, darkMat);
                Sphere(rig.HandL, darkMat, 0.095f, new Godot.Vector3(0, -0.04f, 0));
                Sphere(rig.HandR, darkMat, 0.095f, new Godot.Vector3(0, -0.04f, 0));
                break;
            case BranchShape.Night:     // 兜帽收顶 + 背披流苏
                Prism(rig.Head, darkMat, new Godot.Vector3(0.26f, 0.30f, 0.30f), new Godot.Vector3(0, HeadY - NeckY + 0.06f, 0));
                Box(rig.Chest, accentMat, new Godot.Vector3(0.28f, 0.50f, 0.05f), new Godot.Vector3(0, 0.16f, 0.15f));
                break;
            case BranchShape.Cleric:    // 头盔 + 大肩甲 + 背盾
                Sphere(rig.Head, darkMat, 0.17f, new Godot.Vector3(0, HeadY - NeckY + 0.01f, 0));
                Shoulders(new Godot.Vector3(0.25f, 0.17f, 0.23f), 0.28f, darkMat);
                Box(rig.Chest, accentMat, new Godot.Vector3(0.12f, 0.58f, 0.42f), new Godot.Vector3(0, 0.14f, 0.20f));
                break;
            default:                    // 散人：中性 + 收拢的万象伞背在身后
                Shoulders(new Godot.Vector3(0.16f, 0.10f, 0.16f), 0.26f, darkMat);
                Cyl(rig.Chest, accentMat, 0.04f, 1.10f, new Godot.Vector3(0, 0.05f, 0.17f));
                break;
        }
    }

    /// 阵营识别件（蓝=方肩徽 + 方角地环；橙=圆肩徽 + 圆环地环）——形状冗余，色盲可分。
    public static void BuildTeamMarkers(Node3D root, Color teamColor, byte team)
    {
        var mat = Mat(teamColor, emissive: true);
        var ring = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.60f, BottomRadius = 0.60f, Height = 0.03f },
            MaterialOverride = Mat(teamColor.Darkened(0.25f), emissive: true),
            Position = new Godot.Vector3(0, 0.02f, 0),
        };
        root.AddChild(ring);

        foreach (float sx in new[] { 1f, -1f })
        {
            Mesh mesh = team == 0
                ? new BoxMesh { Size = new Godot.Vector3(0.13f, 0.11f, 0.13f) }
                : new SphereMesh { Radius = 0.075f, Height = 0.15f };
            root.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = mat,
                Position = new Godot.Vector3(0.305f * sx, 1.46f, 0),
            });
        }
    }

    // ---- 基本体工厂 ----

    private static Node3D Joint(Node3D parent, string name, Godot.Vector3 pos)
    {
        var n = new Node3D { Name = name, Position = pos };
        parent.AddChild(n);
        return n;
    }

    private static StandardMaterial3D Mat(Color c, bool emissive = false) => new()
    {
        AlbedoColor = c,
        Roughness = 0.72f,
        Metallic = 0.08f,
        EmissionEnabled = emissive,
        Emission = emissive ? c : Colors.Black,
        EmissionEnergyMultiplier = emissive ? 0.50f : 0f,
    };

    private static void Add(Node3D parent, MeshInstance3D mi, Material m, Godot.Vector3 pos)
    {
        mi.MaterialOverride = m;
        mi.Position = pos;
        parent.AddChild(mi);
    }

    /// 从关节沿 −Y 向下延伸的胶囊（骨骼段）。len 为段长。
    private static void CapsuleExt(Node3D joint, Material m, float r, float len)
    {
        var mi = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = r, Height = len } };
        Add(joint, mi, m, new Godot.Vector3(0, -len / 2f, 0));
    }

    private static void Box(Node3D parent, Material m, Godot.Vector3 size, Godot.Vector3 pos) =>
        Add(parent, new MeshInstance3D { Mesh = new BoxMesh { Size = size } }, m, pos);

    private static void Sphere(Node3D parent, Material m, float r, Godot.Vector3 pos) =>
        Add(parent, new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = r * 2f, RadialSegments = 20, Rings = 12 } }, m, pos);

    private static void Cyl(Node3D parent, Material m, float r, float h, Godot.Vector3 pos, float? topR = null) =>
        Add(parent, new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = topR ?? r, BottomRadius = r, Height = h, RadialSegments = 16 },
        }, m, pos);

    /// PrismMesh 默认尖端朝上；flip=true 则尖端朝下（做宽肩窄腰的胸廓/下摆）。
    private static void Prism(Node3D parent, Material m, Godot.Vector3 size, Godot.Vector3 pos, bool flip = false)
    {
        var mi = new MeshInstance3D { Mesh = new PrismMesh { Size = size } };
        if (flip) mi.RotationDegrees = new Godot.Vector3(180, 0, 0);
        Add(parent, mi, m, pos);
    }
}
