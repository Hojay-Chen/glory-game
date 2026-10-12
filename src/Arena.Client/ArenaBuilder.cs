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
    // ---- 材质分层（Poly Haven CC0 实拍 PBR：diff/nor/rough 三件套 + 世界三平面映射）----
    // 共用实例（非每件 new）。AlbedoColor 现在充当**调色乘数**（贴图本色 × 调色）——
    // 夜场氛围靠它压暗/偏色，而表面细节全部来自实拍贴图（凹凸由法线贴图承载）。
    private static readonly StandardMaterial3D FloorMat =
        Pbr("floor", new Color(0.88f, 0.89f, 0.94f), 0.10f);          // 石板地（贴图本色较深，调色提亮）          // 石板地 冷调压暗
    private static readonly StandardMaterial3D WallMat =
        Pbr("sandstone", new Color(0.72f, 0.73f, 0.78f), 0.06f);      // 看台/结界墙 砂岩
    private static readonly StandardMaterial3D PlatformMat =
        Pbr("sandstone", new Color(0.58f, 0.55f, 0.50f), 0.09f);      // 高台 暖砂
    private static readonly StandardMaterial3D RampMat =
        Pbr("sandstone", new Color(0.52f, 0.50f, 0.46f), 0.12f);      // 坡道
    private static readonly StandardMaterial3D CoverMat =
        Pbr("sandstone", new Color(0.46f, 0.42f, 0.38f), 0.10f);      // 掩体
    private static readonly StandardMaterial3D PillarMat =
        Pbr("rock", new Color(0.78f, 0.78f, 0.80f), 0.10f);           // 石柱
    private static readonly StandardMaterial3D WoodMat =
        Pbr("planks", new Color(0.72f, 0.62f, 0.52f), 0.35f);         // 木箱
    private static readonly StandardMaterial3D RockMat =
        Pbr("rock", new Color(0.80f, 0.81f, 0.84f), 0.16f);           // 乱石
    private static readonly StandardMaterial3D PotMat =
        Pbr("rock", new Color(0.62f, 0.45f, 0.34f), 0.18f);           // 陶罐 赤陶调色

    // 擂台盘面：大理石 + 微暖调色——近场主角面
    private static readonly StandardMaterial3D ArenaDeckMat =
        Pbr("marble", new Color(0.44f, 0.47f, 0.55f), 0.07f);          // 蓝灰大理石（暖调会读成篮球场）
    // 擂台包边（低矮金属压条）
    private static readonly StandardMaterial3D CurbMat =
        Mat(new Color(0.155f, 0.140f, 0.115f), 0.42f, 0.55f);
    private static readonly StandardMaterial3D DeckLineMat =
        Emissive(new Color(0.55f, 0.80f, 1.00f), 0.60f, 0.45f, 0.15f);// 刻线：冷青辉光（竞技场符文线，远离"篮球场"）

    /// Poly Haven 三件套材质（res://assets/tex/<name>_{diff,nor,rough}.jpg）
    private static StandardMaterial3D Pbr(string name, Color tint, float metal)
    {
        var m = new StandardMaterial3D
        {
            AlbedoTexture = GD.Load<Texture2D>($"res://assets/tex/{name}_diff.jpg"),
            NormalTexture = GD.Load<Texture2D>($"res://assets/tex/{name}_nor.jpg"),
            RoughnessTexture = GD.Load<Texture2D>($"res://assets/tex/{name}_rough.jpg"),
            NormalEnabled = true,
            NormalScale = 1.1f,
            Metallic = metal,
            Roughness = 1.0f,               // 由贴图承载
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
        };
        m.AlbedoColor = tint;               // 乘在实拍贴图上的调色
        return m;
    }

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
        // 主光 Key：定义形体与投影方向（PSSM4 软阴影）——暖白
        var key = new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-46, 38, 0),
            LightColor = new Color(1.000f, 0.955f, 0.885f),   // #FFF4E2
            LightEnergy = 1.45f,                              // ACES 吃掉约 25% 亮度，相应上调
            ShadowEnabled = true,
            ShadowBias = 0.03f,
            ShadowBlur = 1.6f,                                // 软影（Forward+ 支持）
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowBlendSplits = true,
            DirectionalShadowMaxDistance = 70f,
        };
        parent.AddChild(key);

        // 补光 Fill：从相机侧低位抬暗面，避免纯黑（关阴影）——冷调，与暖主光形成色温分离
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-18, -140, 0),
            LightColor = new Color(0.62f, 0.72f, 1.000f),     // 冷蓝补光
            LightEnergy = 0.30f,
            ShadowEnabled = false,
        });

        // 轮廓光 Rim：从角色背后上方低掠角打，把剪影从背景里"抠"出来（§5.1 核心）。
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-14, 0, 0),
            LightColor = new Color(0.850f, 0.900f, 1.000f),   // 近白微冷
            LightEnergy = 1.15f,
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
        // 程序化天空：真正的"天顶→地平线"渐变 + 夜场竞技场配色。
        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.045f, 0.055f, 0.095f),
            SkyHorizonColor = new Color(0.235f, 0.260f, 0.330f),
            SkyCurve = 0.22f,
            GroundBottomColor = new Color(0.030f, 0.034f, 0.046f),
            GroundHorizonColor = new Color(0.120f, 0.135f, 0.170f),
            SunAngleMax = 40f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat, RadianceSize = Sky.RadianceSizeEnum.Size256 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.55f,
            AmbientLightSkyContribution = 0.85f,
            FogEnabled = false,                                          // §5.1: 关雾（否则吃掉 35m 外剪影）

            // ---- Forward+ 专属画质（gl_compatibility 无这些能力——切渲染器的理由）----
            TonemapMode = Godot.Environment.ToneMapper.Aces,             // 电影级色调映射
            TonemapExposure = 1.12f,
            TonemapWhite = 6.0f,
            GlowEnabled = true,                                          // 刻线/灯组/技能特效真正泛光
            GlowIntensity = 0.38f,
            GlowBloom = 0.06f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
            GlowHdrThreshold = 1.05f,                                    // 只让 HDR>1 的像素溢出
            SsaoEnabled = true,                                          // 接触阴影——接地感的关键
            SsaoRadius = 1.2f,
            SsaoIntensity = 1.6f,
            SsaoPower = 1.6f,
            SsaoDetail = 0.6f,
            AdjustmentEnabled = true,
            AdjustmentContrast = 1.06f,
            AdjustmentSaturation = 1.08f,
        };
        for (int lvl = 1; lvl <= 6; lvl++)
            env.SetGlowLevel(lvl, lvl is 3 or 4 ? 0.9f : 0.35f);         // 中频泛光为主（柔和不糊）
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
