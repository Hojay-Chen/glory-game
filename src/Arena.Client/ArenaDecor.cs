using System.Collections.Generic;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// 竞技场**非功能性装饰**——提升观感，不入 Sim 注册表、不参与判定
// （Sim 碰撞只认 arena.csv 的 TerrainBody；本类只在 _envRoot 下加 MeshInstance3D）。
// 规格出自 docs/PRODUCTION-PLAN.md §2.3 与 docs/art/art-bible-v1.md §5.2：
//   擂台高光描边 / 地面引导纹 / 立柱顶部构件 / 观众席·围栏意象。
// 性能：gl_compatibility 对 draw call 敏感——只加 ~25 个大件（非大量小件），
//   且所有件共用少数几个**静态材质实例**（不含每件 new）。
// 设计约束（art-bible A2/§5.2）：背景必须退让——装饰以暗色/低自发光为主，
//   仅擂台描边与立柱顶灯用暖色点缀，绝不在角色身后铺高频花纹或亮色。
namespace Arena.Client;

public static class ArenaDecor
{
    // ---- 共用材质（少量实例覆盖全部装饰件）----
    private static readonly StandardMaterial3D GuideMat =
        Emissive(new Color(0.26f, 0.34f, 0.41f), 0.16f, 0.30f, 0.20f);   // 地面引导纹（暗青、低自发光）
    private static readonly StandardMaterial3D RimMat =
        Emissive(new Color(0.52f, 0.40f, 0.17f), 0.14f, 0.35f, 0.25f);   // 擂台描边 / 顶灯（暖金）
    private static readonly StandardMaterial3D StoneMat =
        new() { AlbedoColor = new Color(0.34f, 0.33f, 0.31f), Roughness = 0.72f, Metallic = 0.05f };  // 与立柱同族
    private static readonly StandardMaterial3D BleacherMat =
        new() { AlbedoColor = new Color(0.137f, 0.149f, 0.180f), Roughness = 0.92f, Metallic = 0.00f };  // 观众席（暗、退让）
    private static readonly StandardMaterial3D RailMat =
        new() { AlbedoColor = new Color(0.353f, 0.376f, 0.431f), Roughness = 0.45f, Metallic = 0.35f };  // 围栏金属

    private static StandardMaterial3D Emissive(Color c, float energy, float rough, float metal) => new()
    {
        AlbedoColor = c,
        Roughness = rough,
        Metallic = metal,
        EmissionEnabled = true,
        Emission = c,
        EmissionEnergyMultiplier = energy,
    };

    /// 依 arena.csv 派生布局（中央擂台 / 四角立柱 / 结界范围），再摆装饰。
    public static void Build(Node parent, IReadOnlyList<ArenaDefParser.ArenaObject> objects)
    {
        // ---- 派生布局 ----
        float hw = 30f, hd = 42f;                 // 结界 half_w / half_d
        float px = 0f, pz = 0f, pr = 8f, ph = 1.2f;   // 中央擂台
        var pillars = new List<(float x, float z, float h)>();
        foreach (var o in objects)
        {
            switch (o.KindId)
            {
                case ArenaObjectKind.Boundary:
                    hw = (float)o.HalfW; hd = (float)o.HalfD;
                    break;
                case ArenaObjectKind.Platform:
                    if (o.R > 0) { px = (float)o.X; pz = (float)o.Z; pr = (float)o.R; ph = (float)o.Height; }
                    break;
                case ArenaObjectKind.Pillar:
                    pillars.Add(((float)o.X, (float)o.Z, (float)o.Height));
                    break;
            }
        }

        // ============ 1. 擂台边缘高光描边（可见的近场焦点——暖金自发光环）============
        AddTorus(parent, px, ph + 0.06f, pz, pr - 0.05f, pr + 0.05f, RimMat, flattenY: 1f, segments: 64);
        // 擂台基座轮廓线（地面上一圈冷色细线，把台体从地板上"切"出来）
        AddTorus(parent, px, 0.04f, pz, pr + 0.55f, pr + 0.75f, GuideMat, flattenY: 0.4f, segments: 64);

        // ============ 2. 地面引导纹（细长 Box 拼——球场式构图线）============
        // 2a. 外框（贴结界内侧 2m）
        float fwx = hw - 2f, fwz = hd - 2f;
        Mark(parent, 0f, fwz, new Godot.Vector3(fwx * 2f, 0.05f, 0.16f));      // N
        Mark(parent, 0f, -fwz, new Godot.Vector3(fwx * 2f, 0.05f, 0.16f));     // S
        Mark(parent, fwx, 0f, new Godot.Vector3(0.16f, 0.05f, fwz * 2f));      // E
        Mark(parent, -fwx, 0f, new Godot.Vector3(0.16f, 0.05f, fwz * 2f));     // W
        // 2b. 中轴十字（从擂台下方穿过——擂台遮挡中段，露出四向引导）
        Mark(parent, 0f, 0f, new Godot.Vector3(0.14f, 0.05f, fwz * 2f));       // Z 轴
        Mark(parent, 0f, 0f, new Godot.Vector3(fwx * 2f, 0.05f, 0.14f));       // X 轴

        // ============ 3. 立柱顶部构件（柱头 + 暖色顶灯——把四角点亮成"竞技场灯塔"）============
        foreach (var (x, z, h) in pillars)
        {
            Piece(parent, x, h + 0.22f, z, new Godot.Vector3(2.2f, 0.45f, 2.2f), StoneMat);   // 柱头
            Piece(parent, x, h + 0.70f, z, new Godot.Vector3(0.5f, 0.5f, 0.5f), RimMat);       // 顶灯
        }

        // ============ 4. 观众席 + 围栏意象（结界外的抬升看台环，暗色退让）============
        float bx = hw + 3.5f, bz = hd + 3.5f;           // 看台中心（墙外 3.5m）
        float bw = (hw + 6f) * 2f, bd = (hd + 6f) * 2f; // 看台长度（对齐地板外缘）
        Bench(parent, 0f, bz, new Godot.Vector3(bw, 2.2f, 5f));
        Bench(parent, 0f, -bz, new Godot.Vector3(bw, 2.2f, 5f));
        Bench(parent, bx, 0f, new Godot.Vector3(5f, 2.2f, bd));
        Bench(parent, -bx, 0f, new Godot.Vector3(5f, 2.2f, bd));
        // 围栏（看台顶沿的细挡板——长条大件，非小件堆砌）
        RailX(parent, 0f, bz, bw);     // N：沿 X 长
        RailX(parent, 0f, -bz, bw);    // S
        RailZ(parent, bx, 0f, bd);     // E：沿 Z 长
        RailZ(parent, -bx, 0f, bd);    // W
    }

    // ---- 装配辅助 ----

    private static void AddTorus(Node parent, float x, float y, float z,
        float inner, float outer, Material m, float flattenY, int segments)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = inner, OuterRadius = outer, Rings = segments, RingSegments = 8 },
            MaterialOverride = m,
            Position = new Godot.Vector3(x, y, z),
            Scale = new Godot.Vector3(1f, flattenY, 1f),   // 压扁成地面/边缘环带
        });
    }

    /// 地面刻线（细长 Box 平贴在 y=0.03）
    private static void Mark(Node parent, float x, float z, Godot.Vector3 size) =>
        Piece(parent, x, 0.03f, z, size, GuideMat);

    /// 看台体（底面贴地，中心抬到 size.y/2）
    private static void Bench(Node parent, float x, float z, Godot.Vector3 size) =>
        Piece(parent, x, size.Y / 2f, z, size, BleacherMat);

    /// 围栏挡板（坐在看台顶 y=2.2 之上；沿 X 长）
    private static void RailX(Node parent, float x, float z, float length) =>
        Piece(parent, x, 2.7f, z, new Godot.Vector3(length, 1.0f, 0.16f), RailMat);

    /// 围栏挡板（沿 Z 长）
    private static void RailZ(Node parent, float x, float z, float length) =>
        Piece(parent, x, 2.7f, z, new Godot.Vector3(0.16f, 1.0f, length), RailMat);

    private static void Piece(Node parent, float x, float y, float z, Godot.Vector3 size, Material m) =>
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = m,
            Position = new Godot.Vector3(x, y, z),
        });
}
