using System;
using System.Collections.Generic;
using Godot;

// PRODUCTION - Arena.Client
// 人形装配器：加载 **Blender 生成 + 骨骼绑定** 的角色资产（assets/fighter_knight.glb）。
//
// 资产来源（可复现）：Blender MCP → Hyper3D/Rodin 生成白模 → 刚性壳片绑定 19 骨骼 → 导出 GLB。
// 绑定策略见 docs/art/character-rig.md：
//   该 AI 网格是"壳片汤"（数千个不连通壳片），Blender 的**热扩散自动权重无解**（全 0 权重），
//   包络权重会把铠甲撕裂；因此改为**按连通壳片刚性绑到最近骨骼**——硬表面铠甲本就该刚性，
//   成品是"关节可动的盔甲人"观感，无撕裂。
//
// 坐标约定: 原点在脚底、前方 −Z（资产导出后前向为 +Z，此处整体转 180°）。
namespace Arena.Client.Visuals;

/// 骨骼化角色引用（FighterView 逐帧写骨骼 pose 做程序化动作）。
public sealed class BodyRig
{
    public Node3D Root = null!;                  // 实例根（整体位移/前倾/倒地）
    public Skeleton3D Skel = null!;              // 骨骼
    public MeshInstance3D Mesh = null!;
    public BaseMaterial3D BodyMaterial = null!;  // 状态染色 / 受击闪白的载体
    public Color BaseTint = Colors.White;        // 阵营基础调制（状态染色在其上做乘算）
    public BoneAttachment3D HandAnchor = null!;  // 右手挂点（武器/尾迹）
    private readonly Dictionary<string, int> _bones = new();

    public int Bone(string name) => _bones.TryGetValue(name, out var i) ? i : -1;
    internal void Index()
    {
        for (int i = 0; i < Skel.GetBoneCount(); i++) _bones[Skel.GetBoneName(i)] = i;
    }

    /// 把一次**世界轴**旋转翻译成该骨骼的 pose。
    ///
    /// ⚠️ 关键（踩坑记录）：Godot 的 `bone_pose` 是**骨骼相对父级的完整局部变换**，
    /// `SetBonePoseRotation(idx, Identity)` 会把骨骼自带的 rest 局部旋转一并抹掉——
    /// 实测把 upperarm 的 pose 置为 identity，手臂会立刻对齐到父级（shoulder）的朝向，
    /// 也就是"横着张开"。所以必须 `新 pose = (父空间下的世界旋转) × rest 局部旋转`。
    public Quaternion Around(int boneIdx, Vector3 worldAxis, float deg)
    {
        if (boneIdx < 0) return Quaternion.Identity;
        var rest = Skel.GetBoneRest(boneIdx).Basis.Orthonormalized();
        int parent = Skel.GetBoneParent(boneIdx);
        var p = parent >= 0 ? Skel.GetBoneGlobalRest(parent).Basis.Orthonormalized() : Basis.Identity;
        var r = new Basis(worldAxis.Normalized(), Mathf.DegToRad(deg));
        // 把世界旋转搬到父级坐标系：parent⁻¹ · R · parent
        var delta = p.Inverse() * r * p;
        return (delta * rest).GetRotationQuaternion();
    }

    public void Pose(string bone, Vector3 worldAxis, float deg)
    {
        int i = Bone(bone);
        if (i >= 0) Skel.SetBonePoseRotation(i, Around(i, worldAxis, deg));
    }
}

/// 骨骼空间的三条语义轴——动作代码只关心"前后摆 / 左右张合 / 扭转"，
/// 由 BodyRig.Around() 负责翻译成每根骨骼的局部轴。
public static class BodyRigAxes
{
    public static readonly Vector3 Fwd = Vector3.Right;     // 绕它转 = 前后摆（腿/臂）
    public static readonly Vector3 Side = Vector3.Forward;  // 绕它转 = 左右张合
    public static readonly Vector3 Up = Vector3.Up;         // 绕它转 = 扭转
}

public static class FighterMeshBuilder
{
    public const string ModelPath = "res://assets/fighter_knight.glb";

    private static PackedScene? _bodyScene;
    private static PackedScene? _weaponsScene;

    private static PackedScene? Load(string path, ref PackedScene? cache)
        => cache ??= Godot.ResourceLoader.Load<PackedScene>(path);

    private static void Collect(Node n, List<MeshInstance3D> meshes, ref Skeleton3D? skel)
    {
        if (n is Skeleton3D s && skel is null) skel = s;
        if (n is MeshInstance3D mi) meshes.Add(mi);
        foreach (var c in n.GetChildren()) Collect(c, meshes, ref skel);
    }

    /// 武器：从 weapons.glb 取该职业所属家族的子节点，挂到握把枢轴上。
    public static void BuildWeapon(Node3D pivot, string meshName, Material weaponMat)
    {
        Load("res://assets/weapons.glb", ref _weaponsScene);
        if (_weaponsScene?.Instantiate() is not Node3D root) return;
        var want = root.GetNodeOrNull<Node3D>(meshName);
        if (want is null)
            foreach (var c in root.GetChildren())
                if (c.Name.ToString().StartsWith("W_", StringComparison.Ordinal)) { want = c as Node3D; break; }
        if (want is null) { root.QueueFree(); return; }
        want.GetParent()?.RemoveChild(want);
        var meshes = new List<MeshInstance3D>();
        Skeleton3D? _ = null;
        Collect(want, meshes, ref _);
        foreach (var mi in meshes) mi.MaterialOverride = weaponMat;
        pivot.AddChild(want);
        root.QueueFree();
    }

    public static BodyRig Build(Node3D parent, FighterVisualProfile p, Color teamColor, byte team)
    {
        var rig = new BodyRig();
        Load(ModelPath, ref _bodyScene);
        if (_bodyScene?.Instantiate() is not Node3D inst)
        {
            GD.PushError($"角色资产缺失: {ModelPath}");
            return rig;
        }

        // glTF 导出后前向为 +Z（Blender 前向 −Y → glTF +Z），Godot 前向为 −Z → 整体转 180°
        var holder = new Node3D { Name = "BodyModel", RotationDegrees = new Vector3(0, 180, 0) };
        holder.AddChild(inst);
        parent.AddChild(holder);
        rig.Root = holder;

        var meshes = new List<MeshInstance3D>();
        Skeleton3D? skel = null;
        Collect(inst, meshes, ref skel);
        rig.Skel = skel!;
        rig.Mesh = meshes.Count > 0 ? meshes[0] : null!;
        rig.Index();

        // 材质：复制资产自带材质（保留 PBR 贴图），再按阵营/状态做**调制**染色
        if (rig.Mesh is not null && rig.Mesh.Mesh.GetSurfaceCount() > 0 &&
            rig.Mesh.Mesh.SurfaceGetMaterial(0) is BaseMaterial3D src)
        {
            var mat = (BaseMaterial3D)src.Duplicate();
            rig.BaseTint = Tint(teamColor);
            mat.AlbedoColor = rig.BaseTint;
            rig.Mesh.MaterialOverride = mat;
            rig.BodyMaterial = mat;
        }
        else
        {
            rig.BaseTint = Tint(teamColor);
            rig.BodyMaterial = new StandardMaterial3D { AlbedoColor = rig.BaseTint };
            if (rig.Mesh is not null) rig.Mesh.MaterialOverride = rig.BodyMaterial;
        }

        // 右手挂点：BoneAttachment3D 跟随 hand.R 骨骼（武器随手臂动作一起走）
        if (rig.Skel is not null && rig.Bone("hand.R") >= 0)
        {
            var att = new BoneAttachment3D { Name = "HandR", BoneName = "hand.R" };
            rig.Skel.AddChild(att);
            rig.HandAnchor = att;
        }

        BuildTeamMarkers(rig.Root, teamColor, team);   // 阵营识别（形状 + 色双通道）
        _ = p;
        if (OS.GetEnvironment("ARENA_DEBUG") == "1")
        {
            GD.Print($"[RIG] bones={rig.Skel?.GetBoneCount()} surf={rig.Mesh?.Mesh.GetSurfaceCount()} " +
                     $"mat={rig.BodyMaterial?.GetType().Name} albedo={rig.BodyMaterial?.AlbedoColor} " +
                     $"tex={rig.BodyMaterial?.AlbedoTexture?.GetType().Name}");
            GD.Print($"[RIG] meshAabb={rig.Mesh?.GetAabb()}");
            foreach (var n in new[] { "hand.L", "hand.R", "head", "thigh.L" })
            {
                int i = rig.Bone(n);
                if (i >= 0 && rig.Skel is not null) GD.Print($"[RIG] {n} rest={rig.Skel.GetBoneGlobalRest(i).Origin}");
            }
            GD.Print($"[RIG] skelScale={rig.Skel?.Scale} rootScale={rig.Root?.Scale} holderRot={rig.Root?.RotationDegrees}");
            int ul = rig.Bone("upperarm.L");
            if (ul >= 0 && rig.Skel is not null)
            {
                var rb = rig.Skel.GetBoneGlobalRest(ul).Basis;
                GD.Print($"[RIG] upperarm.L basisX={rb.X} basisY={rb.Y} basisZ={rb.Z}");
                GD.Print($"[RIG] local(Fwd=+X)={rb.Inverse() * Vector3.Right}  local(Side=+Z)={rb.Inverse() * Vector3.Forward}");
            }
        }
        return rig;
    }

    /// 阵营染色：只做**轻度**调制（保留铠甲材质本身的金属/布料层次，不做纯色覆盖）。
    /// 注意先按**最大通道**归一化——若按亮度归一化会产生 >1 的分量，被钳制后既偏色又过曝。
    private static Color Tint(Color teamColor)
    {
        float m = Mathf.Max(teamColor.R, Mathf.Max(teamColor.G, teamColor.B));
        var hue = m > 0.0001f ? new Color(teamColor.R / m, teamColor.G / m, teamColor.B / m) : Colors.White;
        const float k = 0.30f;
        return new Color(
            Mathf.Lerp(1f, hue.R, k) * 0.90f,
            Mathf.Lerp(1f, hue.G, k) * 0.90f,
            Mathf.Lerp(1f, hue.B, k) * 0.90f);
    }

    /// 阵营识别件（蓝=方肩徽 + 方角地环；橙=圆肩徽 + 圆环地环）——形状冗余，色盲可分。
    public static void BuildTeamMarkers(Node3D root, Color teamColor, byte team)
    {
        var mat = Mat(teamColor, emissive: true);
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.62f, BottomRadius = 0.62f, Height = 0.03f },
            MaterialOverride = Mat(teamColor.Darkened(0.25f), emissive: true),
            Position = new Godot.Vector3(0, 0.03f, 0),
        });
        // 肩甲上的小徽记（贴在肩后侧——大部分机位都看得到，又不挡脸）
        foreach (float sx in new[] { 1f, -1f })
        {
            Mesh mesh = team == 0
                ? new BoxMesh { Size = new Godot.Vector3(0.085f, 0.075f, 0.085f) }
                : new SphereMesh { Radius = 0.05f, Height = 0.10f };
            root.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = mat,
                Position = new Godot.Vector3(0.215f * sx, 1.47f, -0.115f),
            });
        }
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
}
