using System;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Presentation;
using Arena.Client.Visuals;
using Godot;

// PRODUCTION - Arena.Client
// FighterView 六段结构: Body / Weapon / WeaponTrail(VS-8) / EffectRoot / StateVisual / AnimationDriver。
// 表现资源由 FighterVisualProfile 驱动（未来按职业替换，Sim 零修改）。
// 事件驱动反馈: 由 MatchRoot 从表现线索翻译为节点动作——禁止轮询 Sim 状态猜测事件。
namespace Arena.Client;

public sealed partial class FighterView : Node3D
{
    private readonly int _id;
    private readonly byte _team;
    private readonly FighterVisualProfile _profile;
    private readonly string _displayName;

    // ---- Body ----
    private readonly Node3D _bodyRig;          // 人形装配根（整体位移/前倾/倒地）
    private readonly BodyRig _rig;             // 关节化骨架（逐帧写四肢 rotation）
    private readonly StandardMaterial3D _bodyMat;

    // ---- Weapon ----
    private readonly Node3D _weaponPivot;
    private readonly StandardMaterial3D _weaponMat;

    // ---- EffectRoot ----
    private readonly Node3D _effectRoot;

    // ---- StateVisual ----
    private readonly Label3D _nameTag;
    private MeshInstance3D _hpBarBg = null!;
    private MeshInstance3D _hpBarFill = null!;

    // ---- Weapon Trail（VS-8）----
    private readonly WeaponTrail _trail;

    // ---- AnimationDriver ----
    private Tween? _swingTween;
    private float _flash;
    private bool _frozen;        // Hit Stop 表现冻结
    private float _animTime;     // 表现层动画时钟（与 Sim tick 无关）
    private long _lastSpeedRaw;  // 最近一次同步的速度（决定呼吸/奔跑步频）
    private FighterState _lastState = FighterState.Normal;
    private float _recoil;       // 受击后仰（命中反馈的身体层）
    private bool _tweenPaused;   // 冻结时暂停挥摆、解冻时恢复（只恢复自己暂停的那次）

    public FighterView(int id, byte team, FighterVisualProfile profile, string displayName = "")
    {
        _id = id;
        _team = team;
        _profile = profile;
        _displayName = displayName;

        var teamCol = team == 0 ? _profile.BodyColor0 : _profile.BodyColor1;

        // Body —— 关节化人形骨架（FK：Hips→Chest→Head / 肩→臂→手 / 髋→腿→脚；纯代码，零外部资产）
        _bodyRig = new Node3D();
        _rig = FighterMeshBuilder.Build(_bodyRig, _profile, teamCol, _team);
        _bodyMat = _rig.TeamMaterial;
        AddChild(_bodyRig);

        // Weapon（**挂在右手**上——随手臂动作一起走，不再是脱手的悬浮物）
        _weaponPivot = new Node3D
        {
            Position = new Godot.Vector3(0, -0.06f, 0),
            RotationDegrees = new Godot.Vector3(-18, 0, 0),   // 持械基准角（略前倾）
        };
        _weaponMat = new StandardMaterial3D { AlbedoColor = _profile.WeaponColor, Metallic = 0.6f, Roughness = 0.3f };
        _rig.HandR.AddChild(_weaponPivot);
        // 武器来自 assets/weapons.glb 的**职业所属家族**（此前所有职业共用同一个 BoxMesh）
        FighterMeshBuilder.BuildWeapon(_weaponPivot, _profile.WeaponMesh, _weaponMat);

        // Weapon Trail（尖端世界轨迹——挂在 FighterView 下随视图释放，TopLevel 用世界坐标）
        _trail = new WeaponTrail(_weaponPivot, _profile.WeaponLength, _profile.WeaponWidth * 1.5f, _profile.SlashColor);
        AddChild(_trail);

        // EffectRoot（命中特效/粒子生成点——胸口高度）
        _effectRoot = new Node3D { Position = new Godot.Vector3(0, _profile.BodyHeight * 0.7f, 0) };
        AddChild(_effectRoot);

        // StateVisual（名牌）
        _nameTag = new Label3D
        {
            FontSize = 34,
            Position = new Godot.Vector3(0, 2.34f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            OutlineSize = 8,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
        };
        AddChild(_nameTag);

        // 世界空间血条（Billboard 四边形，永远面向相机）——远处也能一眼看出谁快死了
        _hpBarBg = BillboardQuad(0.96f, 0.10f, new Color(0.05f, 0.05f, 0.06f, 0.85f));
        _hpBarBg.Position = new Godot.Vector3(0, 2.16f, 0);
        AddChild(_hpBarBg);
        _hpBarFill = BillboardQuad(0.92f, 0.07f, teamCol, renderPriority: 1);
        _hpBarFill.Position = new Godot.Vector3(0, 2.16f, 0);
        AddChild(_hpBarFill);
    }

    private static MeshInstance3D BillboardQuad(float w, float h, Color col, int renderPriority = 0) => new()
    {
        Mesh = new QuadMesh { Size = new Godot.Vector2(w, h) },
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = col,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // 底板与填充同位置同深度 → 必须显式排定绘制顺序，否则填充会被底板盖掉（实测黑条）
            RenderPriority = renderPriority,
        },
    };

    public int Id => _id;

    /// 权威状态投影（每物理帧——P3: 表现跟随 Sim）
    public void Sync(FighterStateData f)
    {
        Position = new Godot.Vector3(
            (float)(f.PosX.Raw / 65536.0),
            (float)(f.PosY.Raw / 65536.0),
            (float)(f.PosZ.Raw / 65536.0));
        Rotation = new Godot.Vector3(0, -(float)(f.HeadingQuantum / 65536.0 * Math.PI * 2), 0);

        _lastSpeedRaw = Math.Abs(f.VelX.Raw) + Math.Abs(f.VelZ.Raw);
        _lastState = f.State;
        var col = _team == 0 ? _profile.BodyColor0 : _profile.BodyColor1;
        switch (f.State)
        {
            case FighterState.Launch: col = col.Lightened(0.35f); break;
            case FighterState.Down: col = col.Darkened(0.5f); break;
            case FighterState.Hitstun: col = new Color(1f, 0.75f, 0.4f); break;
            case FighterState.Act: col = col.Lightened(0.2f); break;
            case FighterState.Dead: col = new Color(0.15f, 0.15f, 0.15f); break;
            case FighterState.Grabbed: col = new Color(0.8f, 0.2f, 0.8f); break;
        }
        if (f.Hidden) col = new Color(0.35f, 0.35f, 0.45f);
        _bodyMat.AlbedoColor = col;

        // 身体姿态由 _Process 的 ApplyMotion 统一决定（含倒地），此处只投影权威位置/朝向
        _nameTag.Text = f.State == FighterState.Dead
            ? (_displayName.Length > 0 ? _displayName + "  KO" : "KO")
            : (_displayName.Length > 0 ? _displayName : $"P{_team}");
        float ratio = f.HpMax <= 0 ? 0f : (float)f.Hp / f.HpMax;
        if (ratio < 0f) ratio = 0f; else if (ratio > 1f) ratio = 1f;
        _hpBarFill.Scale = new Godot.Vector3(ratio, 1f, 1f);
        _hpBarFill.Position = new Godot.Vector3(-(1f - ratio) * 0.46f, 2.16f, 0f);
    }

    // ================= AnimationDriver（PresentationEvent 驱动） =================

    public void OnAttackStarted(ushort skillId, Godot.Vector3 pos, Godot.Vector3 dir)
    {
        // 武器挥摆 tween（前摇反馈——出刀感）
        _swingTween?.Kill();
        _tweenPaused = false;
        _swingTween = CreateTween();
        // 前摇后举 → 前劈 → 回持械角（绕 X = 前后劈砍，武器挂在手上故这是"腕+臂"的复合动作）
        _weaponPivot.RotationDegrees = new Godot.Vector3(-95, 0, 0);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", new Godot.Vector3(55, 0, 0), 0.12f);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", new Godot.Vector3(-18, 0, 0), 0.15f);

        // 尾迹武装（清掉上一击余点——连续攻击不跨技连线）；实际轨迹在 _Process 逐帧采样
        _trail.Arm(skillId);
    }

    /// 攻击生命周期结束（ActEnded/Cancelled/被打断）——停止采样，余点自然淡出
    public void OnAttackEnded(ushort skillId) => _trail.Disarm(skillId);

    /// 终局——立即清空尾迹（不淡出，避免结算画面残留）
    public void OnMatchEnd() => _trail.Clear();

    /// Hit Stop 表现冻结：冻结期暂停挥摆并停止采样（否则解冻瞬间尖端跳变会画出长直线残留）
    public void SetFrozen(bool frozen)
    {
        if (frozen == _frozen) return;
        _frozen = frozen;
        if (_swingTween is null || !_swingTween.IsValid()) { _tweenPaused = false; return; }
        if (frozen && !_tweenPaused && _swingTween.IsRunning()) { _swingTween.Pause(); _tweenPaused = true; }
        else if (!frozen && _tweenPaused) { if (_swingTween.IsValid()) _swingTween.Play(); _tweenPaused = false; }
    }

    public void OnHitFlash(Godot.Vector3 pos)
    {
        // Hit Flash: 材质瞬时提亮（衰减在 _Process）
        _flash = 1.0f;
        _recoil = Math.Min(1f, _recoil + 0.85f);   // 身体后仰（命中反馈的第三层：位置之外还有姿态）
        _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
        SpawnSpark(pos, _profile.HitSparkColor, 10);
    }

    /// 判定命中但无伤害（纯接触）——仅火花，不闪白
    public void OnHitSpark(Godot.Vector3 pos) => SpawnSpark(pos, _profile.HitSparkColor, 6);

    /// 格挡化解——钢青色火花
    public void OnGuard(Godot.Vector3 pos) => SpawnSpark(pos, _profile.GuardSparkColor, 8);

    /// 完美格挡 / 弹刀——明亮火花
    public void OnParry(Godot.Vector3 pos) => SpawnSpark(pos, _profile.ParrySparkColor, 14);

    public void OnLaunched() { /* 击飞表现: Sync 已覆盖 Launch 提亮 + 抬升姿态 */ }

    public void OnDown() { /* 倒地姿态由 Sync 覆盖 */ }

    /// 阵亡——KO 灰色由 Sync 覆盖；同时收掉挥摆与尾迹（不留残影）
    public void OnDeath()
    {
        _swingTween?.Kill();
        _tweenPaused = false;
        _trail.Clear();
    }

    /// Hit 特效（命中点——简易扩散球体渐隐）
    private void SpawnSpark(Godot.Vector3 worldPos, Color col, int count)
    {
        var mi = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = col,
                EmissionEnabled = true,
                Emission = col,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
            Position = worldPos,
        };
        GetParent().AddChild(mi);
        var tw = CreateTween().SetParallel();
        tw.TweenProperty(mi, "scale", Godot.Vector3.One * 3f, 0.25f);
        tw.TweenProperty(mi.MaterialOverride, "albedo_color:a", 0.0f, 0.25f);
        tw.TweenProperty(mi.MaterialOverride, "emission_energy_multiplier", 0.0f, 0.25f);
        tw.Chain().TweenCallback(Callable.From(mi.QueueFree));
    }

    /// 每表现帧：尾迹采样 + Hit Flash 衰减（Hit Stop 冻结期整体跳过）
    /// 程序化动作（表现层）: 站立呼吸 / 移动前倾+步频 / 受击后仰 / 倒地。
    /// 全部是**加在权威投影之上的偏移**——Sync 决定"在哪"，本函数决定"什么姿态"，
    /// 因此不影响 Sim（P3 表现-仿真分离）。
    private void ApplyMotion(float delta)
    {
        _animTime += delta;
        _recoil = Math.Max(0f, _recoil - delta * 3.2f);

        float speed = _lastSpeedRaw / 65536f;           // m/s
        float run = Math.Min(1f, speed / 6f);           // 0=站 1=全速
        float freq = 2.2f + run * 6.5f;                  // 呼吸 2.2Hz → 奔跑 8.7Hz
        float bob = MathF.Sin(_animTime * freq * 6.2831853f) * (0.012f + run * 0.035f);

        float leanX = -run * 0.16f;                      // 移动前倾
        float recoilX = _recoil * 0.42f;                 // 受击后仰（与前倾叠加）
        float sway = MathF.Sin(_animTime * 1.4f) * 0.015f;   // 极轻的重心摆动

        if (_lastState == FighterState.Down || _lastState == FighterState.Dead)
        {
            _bodyRig.Position = new Godot.Vector3(0, 0.34f, 0.22f);
            _bodyRig.Rotation = new Godot.Vector3(-Mathf.Pi / 2f * 0.92f, 0, 0);
            CollapseLimbs();
            return;
        }

        _bodyRig.Position = new Godot.Vector3(0, bob, 0);
        _bodyRig.Rotation = new Godot.Vector3(leanX + recoilX, 0, sway);

        // ---- 四肢驱动（程序化动作——评审 §2.2 的"静态网格"整改）----
        if (run > 0.05f)
        {
            // 步态：大腿前后摆 + 膝盖屈伸 + 手臂反摆（A 字摆臂）
            float phase = _animTime * (5.0f + run * 6.0f);
            float swing = 0.28f + run * 0.55f;
            float s = MathF.Sin(phase), c = MathF.Cos(phase);
            _rig.ThighL.Rotation = new Godot.Vector3(s * swing, 0, 0);
            _rig.ThighR.Rotation = new Godot.Vector3(-s * swing, 0, 0);
            _rig.ShinL.Rotation = new Godot.Vector3(-MathF.Max(0f, -c) * (0.7f + run * 0.8f), 0, 0);
            _rig.ShinR.Rotation = new Godot.Vector3(-MathF.Max(0f, c) * (0.7f + run * 0.8f), 0, 0);
            _rig.ArmL.Rotation = new Godot.Vector3(-s * swing * 0.85f, 0, 0.12f);
            _rig.ArmR.Rotation = new Godot.Vector3(s * swing * 0.85f, 0, -0.12f);
            _rig.ForeL.Rotation = new Godot.Vector3(-0.40f - MathF.Max(0f, s) * 0.45f, 0, 0);
            _rig.ForeR.Rotation = new Godot.Vector3(-0.40f - MathF.Max(0f, -s) * 0.45f, 0, 0);
            _rig.Chest.Rotation = new Godot.Vector3(0, 0, s * 0.05f);
            _rig.Head.Rotation = Godot.Vector3.Zero;
        }
        else
        {
            // 待机：呼吸起伏 + 双肩微沉 + 头轻点
            float br = MathF.Sin(_animTime * 1.6f) * 0.5f;
            _rig.ThighL.Rotation = Godot.Vector3.Zero;
            _rig.ThighR.Rotation = Godot.Vector3.Zero;
            _rig.ShinL.Rotation = Godot.Vector3.Zero;
            _rig.ShinR.Rotation = Godot.Vector3.Zero;
            _rig.ArmL.Rotation = new Godot.Vector3(br * 0.05f, 0, 0.12f + br * 0.03f);
            _rig.ArmR.Rotation = new Godot.Vector3(-br * 0.05f, 0, -0.12f - br * 0.03f);
            _rig.ForeL.Rotation = new Godot.Vector3(-0.34f + br * 0.05f, 0, 0);
            _rig.ForeR.Rotation = new Godot.Vector3(-0.34f - br * 0.05f, 0, 0);
            _rig.Chest.Rotation = new Godot.Vector3(0, 0, br * 0.012f);
            _rig.Head.Rotation = new Godot.Vector3(br * 0.02f, 0, 0);
        }
    }

    /// 倒地/阵亡：四肢摊开（配合整体后仰平躺）。
    private void CollapseLimbs()
    {
        _rig.ArmL.Rotation = new Godot.Vector3(0.2f, 0, 0.55f);
        _rig.ArmR.Rotation = new Godot.Vector3(-0.1f, 0, -0.55f);
        _rig.ForeL.Rotation = new Godot.Vector3(-0.3f, 0, 0);
        _rig.ForeR.Rotation = new Godot.Vector3(-0.3f, 0, 0);
        _rig.ThighL.Rotation = new Godot.Vector3(0.40f, 0, 0.06f);
        _rig.ThighR.Rotation = new Godot.Vector3(-0.18f, 0, -0.06f);
        _rig.ShinL.Rotation = new Godot.Vector3(-0.60f, 0, 0);
        _rig.ShinR.Rotation = new Godot.Vector3(-0.30f, 0, 0);
        _rig.Chest.Rotation = Godot.Vector3.Zero;
        _rig.Head.Rotation = Godot.Vector3.Zero;
    }

    public override void _Process(double delta)
    {
        if (_frozen) return;
        _trail.Sample(delta);
        ApplyMotion((float)delta);

        if (_flash <= 0) return;
        _flash = Math.Max(0, _flash - (float)delta * 6f);
        _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
    }

}
