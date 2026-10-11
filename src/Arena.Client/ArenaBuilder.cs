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
        Textured(Mat(new Color(0.088f, 0.098f, 0.126f), 0.90f, 0.00f), 0.14f);   // 石地 冷石板
    private static readonly StandardMaterial3D WallMat =
        Textured(Mat(new Color(0.318f, 0.345f, 0.405f), 0.72f, 0.08f), 0.10f);   // 结界墙 冷灰石
    private static readonly StandardMaterial3D PlatformMat =
        Textured(Mat(new Color(0.212f, 0.198f, 0.170f), 0.60f, 0.14f), 0.22f);   // 高台 略暖
    private static readonly StandardMaterial3D RampMat =
        Mat(new Color(0.255f, 0.240f, 0.205f), 0.74f, 0.08f);   // 坡道 同族压暗
    private static readonly StandardMaterial3D CoverMat =
        Textured(Mat(new Color(0.196f, 0.176f, 0.150f), 0.80f, 0.05f), 0.30f);   // 掩体墙 暖褐
    private static readonly StandardMaterial3D PillarMat =
        Textured(Mat(new Color(0.300f, 0.290f, 0.275f), 0.74f, 0.05f), 0.35f);   // 石柱 亮石
    private static readonly StandardMaterial3D WoodMat =
        Textured(Mat(new Color(0.330f, 0.235f, 0.140f), 0.88f, 0.00f), 0.55f);   // 木箱 暖棕
    private static readonly StandardMaterial3D RockMat =
        Textured(Mat(new Color(0.255f, 0.265f, 0.290f), 0.92f, 0.00f), 0.45f);   // 乱石 冷灰
    private static readonly StandardMaterial3D PotMat =
        Mat(new Color(0.420f, 0.290f, 0.205f), 0.80f, 0.00f);   // 陶罐 赤陶

    // 擂台盘面（中央圆台顶面——最靠近镜头的面，单独给一点暖金倾向的石材，
    // 用**环形刻线**而非整盘高光来提供近场焦点，避免"一块大煎饼"）
    private static readonly StandardMaterial3D ArenaDeckMat =
        Textured(Mat(new Color(0.238f, 0.222f, 0.190f), 0.66f, 0.10f), 0.20f);
    // 擂台包边（低矮金属压条——给台面一个"边界"，同时绝不遮挡角色）
    private static readonly StandardMaterial3D CurbMat =
        Mat(new Color(0.155f, 0.140f, 0.115f), 0.42f, 0.55f);
    private static readonly StandardMaterial3D DeckLineMat =
        Emissive(new Color(0.30f, 0.245f, 0.140f), 0.10f, 0.45f, 0.15f);   // 刻线 暖金**微**自发光（压低——刻线是地面细节不是发光条）

    private static StandardMaterial3D Mat(Color c, float rough, float metal) => new()
    {
        AlbedoColor = c,
        Roughness = rough,
        Metallic = metal,
    };

    /// 程序化石材细纹（零素材）：一张低对比噪声当反照率相乘，配合**三平面映射**给地板/墙体
    /// 提供不依赖 UV 的表面细节——消除"一整片纯色塑料板"的白盒感。
    /// 噪声经 ColorRamp 压到 0.74~1.0 → 只加细纹，不改变整体明度关系、不产生云雾感。
    private static NoiseTexture2D? _stoneTex;
    private static Texture2D StoneTexture()
    {
        if (_stoneTex is not null) return _stoneTex;
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.84f, 0.84f, 0.84f));
        ramp.SetColor(1, new Color(1f, 1f, 1f));
        _stoneTex = new NoiseTexture2D
        {
            Noise = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
                Frequency = 0.045f,
                FractalOctaves = 4,
                FractalLacunarity = 2.1f,
                FractalGain = 0.5f,
            },
            ColorRamp = ramp,   // NoiseTexture2D.ColorRamp 取 Gradient（把噪声压到 0.74~1.0）
            Width = 256,
            Height = 256,
            Seamless = true,
            GenerateMipmaps = true,
        };
        return _stoneTex;
    }

    /// 给材质挂上石材细纹（三平面映射——顺便规避 Box/Cylinder 的 UV 拉伸畸变）。
    private static StandardMaterial3D Textured(StandardMaterial3D m, float scale = 0.16f)
    {
        m.AlbedoTexture = StoneTexture();
        m.Uv1Triplanar = true;
        m.Uv1WorldTriplanar = true;
        m.Uv1Scale = new Godot.Vector3(scale, scale, scale);
        return m;
    }

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
            LightColor = new Color(0.62f, 0.72f, 1.000f),     // 冷蓝补光
            LightEnergy = 0.22f,                              // 压低（原 0.38 把场面提得太平、无对比）
            ShadowEnabled = false,
        });

        // 轮廓光 Rim：从角色背后上方低掠角打，把剪影从背景里"抠"出来（§5.1 核心）。
        // 相机在 −Z 侧朝 +Z 看，故"背后"= +Z；低掠角（−14°）在头顶/背缘形成最亮边线。
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-14, 0, 0),
            LightColor = new Color(0.850f, 0.900f, 1.000f),   // 近白微冷
            LightEnergy = 0.95f,                              // 提高——轮廓光是"从背景里抠出角色"的关键
            ShadowEnabled = false,
        });
    }

    // ---- 天空 + 远景围合（消灭"死黑 void + 飘浮黑盒"）----

    private static void BuildSkyAndEnclosure(Node parent, float hw, float hd)
    {
        // 看台：四面逐级后退/升高的台阶 + 顶层人群带——给"竞技场↔观众"的语境（评审 R-3）。
        // 人群用**沿看台等分的色块带**表现（几块一组、共用少量材质），而非逐个人形小盒：
        // gl_compatibility 对 draw call 敏感，这样几十个大件即可读出"坐满的看台"。
        const int Tiers = 3;
        var seatMat = new[] { Mat(new Color(0.40f, 0.19f, 0.18f), 0.94f, 0f),
                              Mat(new Color(0.19f, 0.25f, 0.40f), 0.94f, 0f),
                              Mat(new Color(0.43f, 0.36f, 0.20f), 0.94f, 0f),
                              Mat(new Color(0.22f, 0.32f, 0.29f), 0.94f, 0f) };
        for (int tier = 0; tier < Tiers; tier++)
        {
            float off = 3.6f * tier;
            float h = 2.4f + tier * 2.2f;
            float zc = hd + 3.6f + off, xc = hw + 3.6f + off;
            float spanX = hw * 2f + 34f, spanZ = hd * 2f + 34f;

            foreach (float sz in new[] { 1f, -1f })
            {
                ArenaDecor.Bench(parent, 0, sz * zc, new Godot.Vector3(spanX, h, 3.4f));
                CrowdRow(parent, 0, sz * zc + sz * 0.4f, h, 2.6f, spanX, alongX: true, tier, seatMat);
                // 前挡板压到 0.36m（原来 1.0m 会把身后的"人群"整排遮住）
                ArenaDecor.Rail(parent, 0, h + 0.18f, sz * (zc - 1.62f), new Godot.Vector3(spanX, 0.36f, 0.14f));
            }
            foreach (float sx in new[] { 1f, -1f })
            {
                ArenaDecor.Bench(parent, sx * xc, 0, new Godot.Vector3(3.4f, h, spanZ));
                CrowdRow(parent, sx * xc + sx * 0.4f, 0, h, 2.6f, spanZ, alongX: false, tier, seatMat);
                ArenaDecor.Rail(parent, sx * (xc - 1.62f), h + 0.18f, 0, new Godot.Vector3(0.14f, 0.36f, spanZ));
            }
        }

        // 灯桅：四角高杆 + 发光灯组——给上方空天补纵向结构（竞技场转播感），同时是远景纵深锚点。
        var mastMat = Mat(new Color(0.10f, 0.11f, 0.13f), 0.55f, 0.45f);
        var lampMat = Emissive(new Color(1.00f, 0.95f, 0.82f), 1.6f, 0.30f, 0.00f);
        foreach (float sx in new[] { 1f, -1f })
            foreach (float sz in new[] { 1f, -1f })
            {
                float mx = sx * (hw + 14f), mz = sz * (hd + 14f);
                const float MastH = 19f;
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Godot.Vector3(0.55f, MastH, 0.55f) },
                    MaterialOverride = mastMat,
                    Position = new Godot.Vector3(mx, MastH / 2f, mz),
                });
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Godot.Vector3(3.6f, 1.1f, 1.4f) },
                    MaterialOverride = lampMat,
                    Position = new Godot.Vector3(mx, MastH + 0.7f, mz),
                });
            }
    }

    /// 看台顶层的一排"观众"色块（沿长度等分 14 组，共用 4 个材质实例）。
    private static void CrowdRow(Node parent, float cx, float cz, float topY, float depth,
        float span, bool alongX, int tier, StandardMaterial3D[] mats)
    {
        const int N = 14;
        float step = span / N;
        for (int i = 0; i < N; i++)
        {
            float t = (i + 0.5f) * step - span / 2f;
            // 用 tier 错开配色，避免整排同色
            var m = mats[(i + tier * 3) % mats.Length];
            float hgt = 0.80f + ((i * 7 + tier * 5) % 3) * 0.16f;
            var size = alongX
                ? new Godot.Vector3(step * 0.82f, hgt, depth * 0.55f)
                : new Godot.Vector3(depth * 0.55f, hgt, step * 0.82f);
            var pos = alongX
                ? new Godot.Vector3(cx + t, topY + hgt / 2f, cz)
                : new Godot.Vector3(cx, topY + hgt / 2f, cz + t);
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = m,
                Position = pos,
            });
        }
    }

    private static void BuildEnvironmentSettings(Node parent)
    {
        // 程序化天空：给出真正的"天顶→地平线"渐变（取代此前一块扁平灰墙），
        // 让远景有纵深；室内剧场式配色（暗天顶 / 亮地平线 / 暗地面）。
        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.085f, 0.105f, 0.170f),
            SkyHorizonColor = new Color(0.310f, 0.345f, 0.430f),
            SkyCurve = 0.24f,
            GroundBottomColor = new Color(0.040f, 0.046f, 0.062f),
            GroundHorizonColor = new Color(0.150f, 0.170f, 0.215f),
            SunAngleMax = 40f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat, RadianceSize = Sky.RadianceSizeEnum.Size128 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,   // 环境光取自天空（更自然）
            AmbientLightEnergy = 0.30f,
            AmbientLightSkyContribution = 0.75f,
            FogEnabled = false,                                          // §5.1: 关雾（否则吃掉 35m 外剪影）
        };
        // 注: Compatibility 渲染器不支持色调映射（已实测 tonemap_mode 无效果）→ 只能靠灯光/材质控制曝光
        parent.AddChild(new WorldEnvironment { Environment = env });
    }

    /// 中央擂台盘面 + 同心环刻线——把 r=8m 的大圆盘从"一块煎饼"变成有层次的"擂台"。
    private static void BuildArenaDeck(IReadOnlyList<ArenaDefParser.ArenaObject> objects, Node parent)
    {
        float px = 0, pz = 0, pr = 8;
        foreach (var o in objects)
            if (o.KindId == ArenaObjectKind.Platform && o.R > 0)
            { px = (float)o.X; pz = (float)o.Z; pr = (float)o.R; }

        // 盘面顶皮（与地面近乎齐平——薄片，避免遮挡站在其上的角色；给独立的暖石材与刻线）
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = pr - 0.12f, BottomRadius = pr - 0.12f, Height = 0.06f, RadialSegments = 64 },
            MaterialOverride = ArenaDeckMat,
            Position = new Godot.Vector3(px, 0.05f, pz),
        });
        // 同心环刻线（2 圈**细线**——给盘面一个焦点，不做发光条）
        for (int i = 0; i < 2; i++)
        {
            float rr = pr * (0.42f + 0.34f * i);
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = rr - 0.025f, OuterRadius = rr + 0.025f, Rings = 64, RingSegments = 6 },
                MaterialOverride = DeckLineMat,
                Position = new Godot.Vector3(px, 0.09f, pz),
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
                Position = new Godot.Vector3(px + dx * pr * 0.45f, 0.09f, pz + dz * pr * 0.45f),
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
                    // ⚠️ 关键：Sim 侧 BuildTerrain 把 platform/ramp 视为**穿透**（战斗是 XZ 平面判定，
                    // PosY 只用于击飞/浮空），所以角色恒在 y=0 地面上移动。若这里把中央擂台画成
                    // 1.2m 实体块，它就会把站在同一坐标的角色**齐胸埋掉**（此前"看不到下半身 / 整个人
                    // 像一团蹲着的方块"的真凶）。故此处画**与地面齐平的涂装台面**（顶面 y≈0.04），
                    // 再用低矮包边（0.10m，远低于 1.8m 身高）给出"擂台边界"的读感。
                    if (o.R > 0)
                    {
                        AddFlatDisc(parent, o.X, o.Z, o.R, PlatformMat);
                        AddCurbRing(parent, o.X, o.Z, o.R);
                    }
                    else
                    {
                        AddFlatRect(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, PlatformMat);
                        AddCurbRingRect(parent, o.X, o.Z, o.HalfW, o.HalfD);
                    }
                    break;
                case ArenaObjectKind.Ramp:
                    // 坡道同样与地面齐平（Sim 明确穿透）——只作为色带提示连通关系。
                    AddFlatRect(parent, o.X, o.Z, o.HalfW * 2, o.HalfD * 2, RampMat);
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

    /// 与地面齐平的圆形涂装台面（顶面 y=0.04，绝不遮挡站在其上的角色）。
    private static void AddFlatDisc(Node parent, decimal x, decimal z, decimal r, Material m)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = (float)r, BottomRadius = (float)r, Height = 0.04f, RadialSegments = 64 },
            MaterialOverride = m,
            Position = new Godot.Vector3((float)x, 0.02f, (float)z),
        });
    }

    /// 与地面齐平的矩形涂装台面。
    private static void AddFlatRect(Node parent, decimal x, decimal z, decimal w, decimal d, Material m)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Godot.Vector3((float)w, 0.04f, (float)d) },
            MaterialOverride = m,
            Position = new Godot.Vector3((float)x, 0.02f, (float)z),
        });
    }

    /// 擂台圆形包边（低矮金属圈——只做"边界"读感，不构成遮挡）。
    private static void AddCurbRing(Node parent, decimal x, decimal z, decimal r)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = (float)r - 0.16f, OuterRadius = (float)r + 0.16f, Rings = 72, RingSegments = 8 },
            MaterialOverride = CurbMat,
            Position = new Godot.Vector3((float)x, 0.10f, (float)z),
            Scale = new Godot.Vector3(1f, 0.6f, 1f),
        });
    }

    /// 高台矩形包边（四条低矮边条）。
    private static void AddCurbRingRect(Node parent, decimal x, decimal z, decimal hw, decimal hd)
    {
        foreach (float sz in new[] { 1f, -1f })
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Godot.Vector3((float)(hw * 2) + 0.32f, 0.20f, 0.32f) },
                MaterialOverride = CurbMat,
                Position = new Godot.Vector3((float)x, 0.10f, (float)z + sz * (float)hd),
            });
        foreach (float sx in new[] { 1f, -1f })
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Godot.Vector3(0.32f, 0.20f, (float)(hd * 2) + 0.32f) },
                MaterialOverride = CurbMat,
                Position = new Godot.Vector3((float)x + sx * (float)hw, 0.10f, (float)z),
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
