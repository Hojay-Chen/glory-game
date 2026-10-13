using System;
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
        BuildBraziers(parent);
        BuildArchitecture(parent, hw, hd);
        ArenaDecor.Build(parent, objects);
    }

    // ---- 三灯方案（art-bible §5.1——转播可读性核心：主光造型 / 补光读姿态 / 轮廓光抠边）----

    private static void BuildLights(Node parent)
    {
        // 主光 Key（月光位）：PSSM4 软阴影 + 体积光（上帝光）
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-46, 38, 0),
            LightColor = new Color(1.000f, 0.955f, 0.885f),
            LightEnergy = 1.45f,
            ShadowEnabled = true,
            ShadowBias = 0.03f,
            ShadowBlur = 1.6f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowBlendSplits = true,
            DirectionalShadowMaxDistance = 70f,
            LightVolumetricFogEnergy = 0.85f,
        });

        // 补光 Fill：冷蓝抬暗面
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-18, -140, 0),
            LightColor = new Color(0.62f, 0.72f, 1.000f),
            LightEnergy = 0.32f,
            ShadowEnabled = false,
        });

        // 轮廓光 Rim：背后低掠角抠剪影
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Godot.Vector3(-14, 0, 0),
            LightColor = new Color(0.850f, 0.900f, 1.000f),
            LightEnergy = 1.15f,
            ShadowEnabled = false,
        });
    }

    // ---- 天空 + 远景围合（消灭"死黑 void + 飘浮黑盒"）----

    private static void BuildSkyAndEnclosure(Node parent, float hw, float hd)
    {
        // 看台：四面逐级后退/升高的台阶 + 顶层人群带——给"竞技场↔观众"的语境（评审 R-3）。
        // 人群 = **一个 MultiMesh**（约 1600 人、随机衣色、顶点着色器逐人呼吸摆动）——
        // 单 draw call 撑起整座看台。Forward+ 下的基本功，gl_compatibility 时代做不到。
        const int Tiers = 3;
        for (int tier = 0; tier < Tiers; tier++)
        {
            float off = 3.6f * tier;
            float h = 2.4f + tier * 2.2f;
            float zc = hd + 3.6f + off, xc = hw + 3.6f + off;
            float spanX = hw * 2f + 34f, spanZ = hd * 2f + 34f;

            foreach (float sz in new[] { 1f, -1f })
            {
                ArenaDecor.Bench(parent, 0, sz * zc, new Godot.Vector3(spanX, h, 3.4f));
                CrowdFill(0, sz * zc + sz * 0.55f, h, spanX, alongX: true, tier);
                // 前挡板压到 0.36m（原来 1.0m 会把身后的"人群"整排遮住）
                ArenaDecor.Rail(parent, 0, h + 0.18f, sz * (zc - 1.62f), new Godot.Vector3(spanX, 0.36f, 0.14f));
            }
            foreach (float sx in new[] { 1f, -1f })
            {
                ArenaDecor.Bench(parent, sx * xc, 0, new Godot.Vector3(3.4f, h, spanZ));
                CrowdFill(sx * xc + sx * 0.55f, 0, h, spanZ, alongX: false, tier);
                ArenaDecor.Rail(parent, sx * (xc - 1.62f), h + 0.18f, 0, new Godot.Vector3(0.14f, 0.36f, spanZ));
            }
        }

        EmitCrowd(parent);

        // 背墙：顶层看台之后的**合拢碗壁**——把"观众浮在天上"变成"封闭竞技场"。
        // 高度 8m，同时挡住天空接缝；用砂岩贴图与看台同族。
        var backMat = Mat(new Color(0.36f, 0.37f, 0.42f), 0.80f, 0.04f);
        {
            float bz = hd + 3.6f + 3.6f * 2 + 2.4f, bx = hw + 3.6f + 3.6f * 2 + 2.4f;
            float sxSpan = hw * 2f + 44f, szSpan = hd * 2f + 44f;
            foreach (float sz in new[] { 1f, -1f })
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Godot.Vector3(sxSpan, 8f, 1.2f) },
                    MaterialOverride = backMat,
                    Position = new Godot.Vector3(0, 8.6f + 4f, sz * bz),
                });
            foreach (float sx in new[] { 1f, -1f })
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Godot.Vector3(1.2f, 8f, szSpan) },
                    MaterialOverride = backMat,
                    Position = new Godot.Vector3(sx * bx, 8.6f + 4f, 0),
                });
        }

        // 灯桅：四角高杆 + **真泛光灯**（SpotLight 体积光束——体育场夜场的标志）。
        // 真灯同时解决"看台死黑"（SpotLight 顺带照亮观众席）。
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
                // 泛光灯：对准擂台中心，宽角——转播照明 + 穿雾光柱
                var spot = new SpotLight3D
                {
                    Position = new Godot.Vector3(mx, MastH + 0.6f, mz),
                    LightColor = new Color(0.92f, 0.95f, 1.00f),
                    SpotRange = 90f,
                    SpotAttenuation = 1.25f,
                    SpotAngle = 47f,
                    SpotAngleAttenuation = 1.1f,
                    LightEnergy = 9.5f,
                    LightVolumetricFogEnergy = 0.55f,
                    LightSpecular = 0.7f,
                    ShadowEnabled = false,
                };
                spot.LookAtFromPosition(spot.Position, new Godot.Vector3(0, 1.2f, 0), Godot.Vector3.Up);
                parent.AddChild(spot);
            }
    }

    /// 火盆：擂台四角斜位的青铜盆 + 火焰 + 闪烁 OmniLight（暖火 vs 冷月光的对冲）。
    private static void BuildBraziers(Node parent)
    {
        var bowlMat = Mat(new Color(0.23f, 0.17f, 0.12f), 0.45f, 0.75f);
        var poleMat = Mat(new Color(0.14f, 0.13f, 0.12f), 0.55f, 0.60f);
        var fireMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.55f, 0.15f),
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.50f, 0.12f),
            EmissionEnergyMultiplier = 4.2f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        foreach (float sx in new[] { 1f, -1f })
            foreach (float sz in new[] { 1f, -1f })
            {
                var brazier = new Node3D { Position = new Godot.Vector3(sx * 10.6f, 0f, sz * 10.6f) };
                brazier.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.22f, Height = 1.5f, RadialSegments = 10 },
                    MaterialOverride = poleMat,
                    Position = new Godot.Vector3(0, 0.75f, 0),
                });
                brazier.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.52f, BottomRadius = 0.24f, Height = 0.42f, RadialSegments = 12 },
                    MaterialOverride = bowlMat,
                    Position = new Godot.Vector3(0, 1.66f, 0),
                });
                brazier.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 0.38f, Height = 0.75f, RadialSegments = 12, Rings = 8 },
                    MaterialOverride = fireMat,
                    Position = new Godot.Vector3(0, 1.95f, 0),
                });
                brazier.AddChild(new Visuals.FlickerLight { Position = new Godot.Vector3(0, 2.1f, 0) });
                parent.AddChild(brazier);
            }
    }

    // ---- 观众（MultiMesh：单 draw call 的"人海"）----
    private static readonly List<(Godot.Vector3 pos, Godot.Vector3 scale, float yaw, Color col)> _crowd = new();

    /// 收集一排观众（沿看台长度按 ~0.72m 间距落位；衣色取自 6 色夜场调色板 + 逐人抖动）。
    private static void CrowdFill(float cx, float cz, float topY, float span, bool alongX, int tier)
    {
        var palette = new[]
        {
            new Color(0.62f, 0.28f, 0.26f), new Color(0.24f, 0.32f, 0.52f),
            new Color(0.66f, 0.55f, 0.30f), new Color(0.26f, 0.42f, 0.38f),
            new Color(0.52f, 0.34f, 0.50f), new Color(0.72f, 0.70f, 0.66f),
        };
        float step = 0.72f;
        int n = (int)(span / step);
        // 逐人的确定性伪随机（位置哈希——同局同布局，Restart 不跳脸）
        float Hash(int i) { float x = MathF.Sin(i * 12.9898f + tier * 78.233f) * 43758.5453f; return x - MathF.Floor(x); }
        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.5f) * step - span / 2f;
            float h1 = Hash(i), h2 = Hash(i + 977), h3 = Hash(i + 311);
            var col = palette[(int)(h1 * palette.Length) % palette.Length].Lerp(Colors.White, h3 * 0.18f);
            float hgt = 0.86f + h2 * 0.20f;                       // 身高差异
            var scale = new Godot.Vector3(0.52f, hgt, 0.42f);
            var pos = alongX
                ? new Godot.Vector3(cx + t, topY + hgt * 0.5f, cz + (h2 - 0.5f) * 0.5f)
                : new Godot.Vector3(cx + (h2 - 0.5f) * 0.5f, topY + hgt * 0.5f, cz + t);
            float yaw = (h1 - 0.5f) * 0.5f;
            _crowd.Add((pos, scale, yaw, col));
        }
    }

    /// 把收集到的观众一次性发成一个 MultiMeshInstance3D（逐实例：变换 + 衣色 + 摆动相位）。
    private static void EmitCrowd(Node parent)
    {
        if (_crowd.Count == 0) return;
        var shader = GD.Load<Shader>("res://shaders/crowd.gdshader");
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh { Size = Godot.Vector3.One },
            InstanceCount = _crowd.Count,
        };
        for (int i = 0; i < _crowd.Count; i++)
        {
            var (pos, scale, yaw, col) = _crowd[i];
            mm.SetInstanceTransform(i, new Transform3D(
                Basis.FromEuler(new Godot.Vector3(0, yaw, 0)).Scaled(scale), pos));
            // COLOR.r 承载"摆动相位"（着色器用），gb+a 是衣色
            float phase = (i * 0.6180339f) % 1f;
            mm.SetInstanceColor(i, new Color(phase, col.R, col.G, col.B));
        }
        parent.AddChild(new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = shader is null ? new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.4f, 0.45f) }
                                              : new ShaderMaterial { Shader = shader },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,   // 1600 人投影不值当
        });
        _crowd.Clear();
    }

    private static void BuildEnvironmentSettings(Node parent)
    {
        // Poly Haven 实拍星空 HDRI（kloppenheim_02_puresky, CC0）——背景/环境光/反射三合一。
        var skyMat = new PanoramaSkyMaterial
        {
            Panorama = GD.Load<Texture2D>("res://assets/tex/night_sky_2k.hdr"),
            Filter = true,
            EnergyMultiplier = 0.85f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat, RadianceSize = Sky.RadianceSizeEnum.Size256 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.20f,
            AmbientLightSkyContribution = 0.75f,

            // ---- Forward+ 专属画质 ----
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapExposure = 0.92f,
            TonemapWhite = 6.0f,
            GlowEnabled = true,
            GlowIntensity = 0.45f,
            GlowBloom = 0.05f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
            GlowHdrThreshold = 1.02f,
            SsaoEnabled = true,
            SsaoRadius = 1.2f,
            SsaoIntensity = 1.7f,
            SsaoPower = 1.6f,
            SsaoDetail = 0.6f,
            AdjustmentEnabled = true,
            AdjustmentContrast = 1.18f,
            AdjustmentSaturation = 1.12f,

            // ---- 体积雾：夜场空气感（灯光锥/月光辉光都靠它承载）----
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.020f,
            VolumetricFogAlbedo = new Color(0.42f, 0.48f, 0.62f),
            VolumetricFogEmission = new Color(0.015f, 0.02f, 0.035f),
            VolumetricFogLength = 44f,
            VolumetricFogAmbientInject = 0.35f,
            VolumetricFogTemporalReprojectionEnabled = true,
        };
        for (int lvl = 1; lvl <= 6; lvl++)
            env.SetGlowLevel(lvl, lvl is 3 or 4 ? 0.9f : 0.35f);
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
                    // CC0 真资产（军械箱）——不再用盒子冒充木箱
                    PlaceModel(parent, "old_military_crate",
                        new Godot.Vector3((float)o.X, 0f, (float)o.Z), 0.95f,
                        yawDeg: (float)((double)o.X + (double)o.Z) * 3.1f % 360f);
                    break;
                case ArenaObjectKind.PropRock:
                    PlaceModel(parent, "moon_rock_01",
                        new Godot.Vector3((float)o.X, 0f, (float)o.Z), 1.5f,
                        yawDeg: (float)((double)o.Z * 7.0) % 360f);
                    break;
                case ArenaObjectKind.PropPot:
                    PlaceModel(parent, "barrel_03",
                        new Godot.Vector3((float)o.X, 0f, (float)o.Z), 1.05f,
                        yawDeg: (float)((double)o.X * 11.0) % 360f);
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



    // ---- CC0 真模型资产（Poly Haven，glTF；替代基本体堆叠）----

    /// 实例化一个 CC0 模型，**按目标高度自动缩放并把脚底对齐到 y=0**——
    /// 下载来的资产尺度各不相同，手工量尺寸不可维护，自动归一化是唯一可行做法。
    private static void PlaceModel(Node parent, string name, Godot.Vector3 pos,
        float targetHeight, float yawDeg = 0f)
    {
        var scene = ResourceLoader.Load<PackedScene>($"res://assets/models/{name}/{name}.gltf");
        if (scene?.Instantiate() is not Node3D inst) return;
        var holder = new Node3D { Position = pos, RotationDegrees = new Godot.Vector3(0, yawDeg, 0) };
        holder.AddChild(inst);
        parent.AddChild(holder);

        var (mn, mx) = LocalAabb(inst, Transform3D.Identity);
        float h = mx.Y - mn.Y;
        if (h > 0.001f)
        {
            float k = targetHeight / h;
            inst.Scale = new Godot.Vector3(k, k, k);
            inst.Position = new Godot.Vector3(-mn.X * k, -mn.Y * k, -mn.Z * k);
        }
    }

    /// 递归求子树在 root 局部空间中的包围盒（含各 MeshInstance3D 自带变换）
    private static (Godot.Vector3, Godot.Vector3) LocalAabb(Node n, Transform3D acc)
    {
        var mn = new Godot.Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Godot.Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        void Walk(Node node, Transform3D t)
        {
            if (node is MeshInstance3D mi)
            {
                var a = t * mi.GetAabb();
                mn = mn.Min(a.Position); mx = mx.Max(a.Position);
                mn = mn.Min(a.End); mx = mx.Max(a.End);
                any = true;
            }
            foreach (var c in node.GetChildren())
                Walk(c, node is Node3D n3 ? t * n3.Transform : t);
        }
        Walk(n, acc);
        return any ? (mn, mx) : (Godot.Vector3.Zero, Godot.Vector3.One);
    }

    /// 竞技场建筑层：背墙拱廊 + 壁柱 + 檐口 + 旗帜 + 角雕像。
    /// 单纯的四堵墙永远读作"盒子"——建筑构件（重复的拱、有节奏的柱、收头的檐口）
    /// 是让远景**有尺度参照**、不再像白盒的关键。
    private static void BuildArchitecture(Node parent, float hw, float hd)
    {
        var stone = Mat(new Color(0.40f, 0.41f, 0.46f), 0.78f, 0.05f);
        var trim = Mat(new Color(0.50f, 0.50f, 0.55f), 0.62f, 0.12f);
        var dark = Mat(new Color(0.16f, 0.16f, 0.19f), 0.85f, 0.02f);

        float bz = hd + 3.6f + 3.6f * 2 + 2.4f, bx = hw + 3.6f + 3.6f * 2 + 2.4f;
        const float WallY = 8.6f;      // 背墙底
        const float WallH = 9.5f;      // 拱廊层高度

        // ---- 四面拱廊：每 9m 一根壁柱，柱间一个半圆拱 ----
        void Arcade(bool alongX, float fixedCoord, float span, float sign)
        {
            int n = Mathf.Max(3, (int)(span / 9f));
            float step = span / n;
            for (int i = 0; i <= n; i++)
            {
                float t = -span / 2f + i * step;
                var p0 = alongX ? new Godot.Vector3(t, 0, fixedCoord) : new Godot.Vector3(fixedCoord, 0, t);
                // 壁柱
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = alongX ? new Godot.Vector3(1.1f, WallH, 1.5f) : new Godot.Vector3(1.5f, WallH, 1.1f) },
                    MaterialOverride = stone,
                    Position = p0 + new Godot.Vector3(0, WallY + WallH / 2f, 0),
                });
                // 柱头
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = alongX ? new Godot.Vector3(1.5f, 0.5f, 1.9f) : new Godot.Vector3(1.9f, 0.5f, 1.5f) },
                    MaterialOverride = trim,
                    Position = p0 + new Godot.Vector3(0, WallY + WallH - 0.25f, 0),
                });
                if (i == n) break;
                // 拱（半圆用 8 段折线近似）——柱间的开口上沿
                var archMat = trim;
                for (int k = 0; k < 8; k++)
                {
                    float a0 = Mathf.Pi * k / 8f;
                    float rr = step * 0.44f;
                    float ax = Mathf.Cos(a0) * rr, ay = Mathf.Sin(a0) * rr;
                    float a1 = Mathf.Pi * (k + 1) / 8f;
                    float bx2 = Mathf.Cos(a1) * rr, by2 = Mathf.Sin(a1) * rr;
                    var mid = new Godot.Vector2((ax + bx2) / 2f, (ay + by2) / 2f);
                    float seg = new Godot.Vector2(bx2 - ax, by2 - ay).Length();
                    float ang = Mathf.Atan2(by2 - ay, bx2 - ax);
                    var ap = alongX
                        ? new Godot.Vector3(t + step / 2f, WallY + WallH - 0.9f + mid.Y, fixedCoord)
                        : new Godot.Vector3(fixedCoord, WallY + WallH - 0.9f + mid.Y, t + step / 2f);
                    var rot = alongX
                        ? new Godot.Vector3(0, 0, ang)
                        : new Godot.Vector3(0, Mathf.Pi / 2f, ang);
                    parent.AddChild(new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Godot.Vector3(seg, 0.34f, 0.9f) },
                        MaterialOverride = archMat,
                        Position = ap,
                        Rotation = rot,
                    });
                }
                // 拱后的暗腔（让拱"有深度"，不是画在墙上的线）
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = alongX ? new Godot.Vector3(step * 0.78f, WallH - 1.2f, 0.6f)
                                                      : new Godot.Vector3(0.6f, WallH - 1.2f, step * 0.78f) },
                    MaterialOverride = dark,
                    Position = (alongX ? new Godot.Vector3(t + step / 2f, WallY + (WallH - 1.2f) / 2f, fixedCoord)
                                       : new Godot.Vector3(fixedCoord, WallY + (WallH - 1.2f) / 2f, t + step / 2f)),
                });
            }
            // 檐口（收头线——建筑"完成度"的一半）
            parent.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = alongX ? new Godot.Vector3(span + 2f, 0.7f, 2.6f) : new Godot.Vector3(2.6f, 0.7f, span + 2f) },
                MaterialOverride = trim,
                Position = alongX ? new Godot.Vector3(0, WallY + WallH + 0.35f, fixedCoord)
                                  : new Godot.Vector3(fixedCoord, WallY + WallH + 0.35f, 0),
            });
        }

        Arcade(true, bz, hw * 2f + 30f, 1f);
        Arcade(true, -bz, hw * 2f + 30f, -1f);
        Arcade(false, bx, hd * 2f + 30f, 1f);
        Arcade(false, -bx, hd * 2f + 30f, -1f);

        // ---- 悬挂旗帜（拱间垂布：绸面 + 队色纹章，给远景一抹色）----
        var bannerMat0 = Mat(new Color(0.22f, 0.34f, 0.62f), 0.85f, 0f);
        var bannerMat1 = Mat(new Color(0.62f, 0.42f, 0.20f), 0.85f, 0f);
        for (int i = 0; i < 6; i++)
            foreach (var (m, zz) in new[] { (bannerMat0, bz - 0.9f), (bannerMat1, -bz + 0.9f) })
            {
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Godot.Vector3(1.9f, 3.4f, 0.08f) },
                    MaterialOverride = m,
                    Position = new Godot.Vector3(-30f + i * 12f, WallY + WallH - 2.1f, zz),
                });
            }

        // ---- 四角雕像（CC0 哥特石像真模型 + 石基座）----
        var pedestal = Mat(new Color(0.34f, 0.35f, 0.39f), 0.80f, 0.04f);
        foreach (float sx in new[] { 1f, -1f })
            foreach (float sz in new[] { 1f, -1f })
            {
                float px = sx * (hw - 3.5f), pz = sz * (hd - 5f);
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Godot.Vector3(2.4f, 1.6f, 2.4f) },
                    MaterialOverride = pedestal,
                    Position = new Godot.Vector3(px, 0.8f, pz),
                });
                PlaceModel(parent, "gothic_statue", new Godot.Vector3(px, 1.6f, pz), 5.2f,
                    yawDeg: sx * sz > 0 ? 135f : -135f);
            }

        // ---- 北面铁门（场景中心件：给远景一个"入口"）----
        PlaceModel(parent, "large_iron_gate", new Godot.Vector3(0f, 0f, hd + 2.2f), 7.5f, yawDeg: 180f);
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
