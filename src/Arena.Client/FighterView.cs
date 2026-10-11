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
//
// 身体现在是一具**骨骼化角色**（assets/fighter_knight.glb，19 骨骼，刚性壳片绑定）：
// 姿态由写骨骼 pose 驱动（待机呼吸 / 步态 / 受击后仰 / 倒地），武器挂在 hand.R 的 BoneAttachment3D 上。
namespace Arena.Client;

public sealed partial class FighterView : Node3D
{
    private readonly int _id;
    private readonly byte _team;
    private readonly FighterVisualProfile _profile;
    private readonly string _displayName;

    // ---- Body ----
    private readonly Node3D _bodyRig;          // 整体位移/前倾/倒地（模型与骨骼都在其下）
    private readonly BodyRig _rig;             // 骨骼化角色
    private readonly BaseMaterial3D _bodyMat;

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

        // Body —— 骨骼化角色（Blender 生成 + 刚性绑定；程序化动作写骨骼 pose）
        _bodyRig = new Node3D();
        _rig = FighterMeshBuilder.Build(_bodyRig, _profile, teamCol, _team);
        _bodyMat = _rig.BodyMaterial;
        AddChild(_bodyRig);

        // Weapon（挂在 hand.R 骨骼的挂点上——随手臂动作一起走）
        _weaponPivot = new Node3D
        {
            Position = new Godot.Vector3(0.02f, 0.02f, 0f),
            RotationDegrees = new Godot.Vector3(0, 0, 0),
        };
        _weaponMat = new StandardMaterial3D { AlbedoColor = _profile.WeaponColor, Metallic = 0.6f, Roughness = 0.3f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        if (_rig.HandAnchor is not null) _rig.HandAnchor.AddChild(_weaponPivot);
        else _bodyRig.AddChild(_weaponPivot);
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
            Position = new Godot.Vector3(0, 2.30f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            OutlineSize = 8,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
        };
        AddChild(_nameTag);

        // 世界空间血条（Billboard 四边形，永远面向相机）
        _hpBarBg = BillboardQuad(0.96f, 0.10f, new Color(0.05f, 0.05f, 0.06f, 0.85f));
        _hpBarBg.Position = new Godot.Vector3(0, 2.12f, 0);
        AddChild(_hpBarBg);
        _hpBarFill = BillboardQuad(0.92f, 0.07f, teamCol, renderPriority: 1);
        _hpBarFill.Position = new Godot.Vector3(0, 2.12f, 0);
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
        // 状态染色：在**阵营基础调制**之上做乘算（不能直接覆盖——那会把贴图压成一块纯色）
        Color col = _rig.BaseTint;
        switch (f.State)
        {
            case FighterState.Launch: col *= 1.35f; break;
            case FighterState.Down: col *= 0.55f; break;
            case FighterState.Hitstun: col = col.Lerp(new Color(1f, 0.72f, 0.38f), 0.45f); break;
            case FighterState.Act: col *= 1.15f; break;
            case FighterState.Dead: col = new Color(0.30f, 0.30f, 0.32f); break;
            case FighterState.Grabbed: col = col.Lerp(new Color(0.85f, 0.30f, 0.85f), 0.45f); break;
        }
        if (f.Hidden) col = col.Lerp(new Color(0.45f, 0.45f, 0.58f), 0.6f);
        if (_bodyMat is not null) _bodyMat.AlbedoColor = col;

        _nameTag.Text = f.State == FighterState.Dead
            ? (_displayName.Length > 0 ? _displayName + "  KO" : "KO")
            : (_displayName.Length > 0 ? _displayName : $"P{_team}");
        float ratio = f.HpMax <= 0 ? 0f : (float)f.Hp / f.HpMax;
        if (ratio < 0f) ratio = 0f; else if (ratio > 1f) ratio = 1f;
        _hpBarFill.Scale = new Godot.Vector3(ratio, 1f, 1f);
        _hpBarFill.Position = new Godot.Vector3(-(1f - ratio) * 0.46f, 2.12f, 0f);
    }

    // ================= AnimationDriver（PresentationEvent 驱动） =================

    public void OnAttackStarted(ushort skillId, Godot.Vector3 pos, Godot.Vector3 dir)
    {
        // 武器挥摆 tween（前摇反馈——出刀感）
        _swingTween?.Kill();
        _tweenPaused = false;
        _swingTween = CreateTween();
        _weaponPivot.RotationDegrees = new Godot.Vector3(-95, 0, 0);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", new Godot.Vector3(55, 0, 0), 0.12f);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", new Godot.Vector3(0, 0, 0), 0.15f);

        _trail.Arm(skillId);
    }

    /// 攻击生命周期结束（ActEnded/Cancelled/被打断）——停止采样，余点自然淡出
    public void OnAttackEnded(ushort skillId) => _trail.Disarm(skillId);

    /// 终局——立即清空尾迹（不淡出，避免结算画面残留）
    public void OnMatchEnd() => _trail.Clear();

    /// Hit Stop 表现冻结：冻结期暂停挥摆并停止采样
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
        _flash = 1.0f;
        _recoil = Math.Min(1f, _recoil + 0.85f);
        if (_bodyMat is not null) _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
        SpawnSpark(pos, _profile.HitSparkColor, 10);
    }

    public void OnHitSpark(Godot.Vector3 pos) => SpawnSpark(pos, _profile.HitSparkColor, 6);
    public void OnGuard(Godot.Vector3 pos) => SpawnSpark(pos, _profile.GuardSparkColor, 8);
    public void OnParry(Godot.Vector3 pos) => SpawnSpark(pos, _profile.ParrySparkColor, 14);
    public void OnLaunched() { }
    public void OnDown() { }

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

    // ================= 程序化动作（写骨骼 pose） =================

    private const float Rad2Deg = 57.29578f;

    /// 每表现帧：尾迹采样 + Hit Flash 衰减 + 骨骼姿态。
    /// 全部是**加在权威投影之上的偏移**——Sync 决定"在哪"，本函数决定"什么姿态"，不影响 Sim。
    private void ApplyMotion(float delta)
    {
        _animTime += delta;
        _recoil = Math.Max(0f, _recoil - delta * 3.2f);

        float speed = _lastSpeedRaw / 65536f;
        float run = Math.Min(1f, speed / 6f);
        float bob = MathF.Sin(_animTime * (2.2f + run * 6.5f) * 6.2831853f) * (0.012f + run * 0.035f);

        float leanX = -run * 0.16f;
        float recoilX = _recoil * 0.42f;
        float sway = MathF.Sin(_animTime * 1.4f) * 0.015f;

        if (_lastState == FighterState.Down || _lastState == FighterState.Dead)
        {
            _bodyRig.Position = new Godot.Vector3(0, 0.26f, 0.14f);
            _bodyRig.Rotation = new Godot.Vector3(-Mathf.Pi / 2f * 0.92f, 0, 0);
            CollapseLimbs();
            return;
        }

        _bodyRig.Position = new Godot.Vector3(0, bob, 0);
        _bodyRig.Rotation = new Godot.Vector3(leanX + recoilX, 0, sway);

        if (_rig.Skel is null) return;
        if (OS.GetEnvironment("ARENA_NOANIM") == "1") return;   // 诊断：只做整体位移，不写骨骼

        if (run > 0.05f)
        {
            // 步态：大腿前后摆 + 膝盖屈伸 + 手臂反摆（绕"左右轴"= 前后方向摆动）
            float phase = _animTime * (5.0f + run * 6.0f);
            float swing = 0.30f + run * 0.55f;
            float s = MathF.Sin(phase), c = MathF.Cos(phase);

            _rig.Pose("thigh.L", BodyRigAxes.Fwd, s * swing * Rad2Deg);
            _rig.Pose("thigh.R", BodyRigAxes.Fwd, -s * swing * Rad2Deg);
            _rig.Pose("shin.L", BodyRigAxes.Fwd, -MathF.Max(0f, -c) * (0.75f + run * 0.8f) * Rad2Deg);
            _rig.Pose("shin.R", BodyRigAxes.Fwd, -MathF.Max(0f, c) * (0.75f + run * 0.8f) * Rad2Deg);
            _rig.Pose("upperarm.L", BodyRigAxes.Fwd, -s * swing * 0.85f * Rad2Deg);
            _rig.Pose("upperarm.R", BodyRigAxes.Fwd, s * swing * 0.85f * Rad2Deg);
            _rig.Pose("forearm.L", BodyRigAxes.Fwd, (-0.35f - MathF.Max(0f, s) * 0.40f) * Rad2Deg);
            _rig.Pose("forearm.R", BodyRigAxes.Fwd, (-0.35f - MathF.Max(0f, -s) * 0.40f) * Rad2Deg);
            _rig.Pose("chest", BodyRigAxes.Up, s * 3.5f);
            _rig.Pose("head", BodyRigAxes.Up, 0f);
        }
        else
        {
            // 待机：呼吸起伏 + 双肩微沉 + 头轻点
            float br = MathF.Sin(_animTime * 1.6f) * 0.5f;
            _rig.Pose("thigh.L", BodyRigAxes.Fwd, 0f);
            _rig.Pose("thigh.R", BodyRigAxes.Fwd, 0f);
            _rig.Pose("shin.L", BodyRigAxes.Fwd, 0f);
            _rig.Pose("shin.R", BodyRigAxes.Fwd, 0f);
            _rig.Pose("upperarm.L", BodyRigAxes.Fwd, br * 2.5f);
            _rig.Pose("upperarm.R", BodyRigAxes.Fwd, -br * 2.5f);
            _rig.Pose("forearm.L", BodyRigAxes.Fwd, -14f + br * 3f);
            _rig.Pose("forearm.R", BodyRigAxes.Fwd, -14f - br * 3f);
            _rig.Pose("chest", BodyRigAxes.Up, br * 1.2f);
            _rig.Pose("head", BodyRigAxes.Fwd, br * 1.6f);
        }
    }

    /// 倒地/阵亡：四肢摊开（配合整体后仰平躺）。
    private void CollapseLimbs()
    {
        if (_rig.Skel is null) return;
        _rig.Pose("upperarm.L", BodyRigAxes.Side, 42f);
        _rig.Pose("upperarm.R", BodyRigAxes.Side, -42f);
        _rig.Pose("forearm.L", BodyRigAxes.Fwd, -18f);
        _rig.Pose("forearm.R", BodyRigAxes.Fwd, -18f);
        _rig.Pose("thigh.L", BodyRigAxes.Fwd, 16f);
        _rig.Pose("thigh.R", BodyRigAxes.Fwd, -8f);
        _rig.Pose("shin.L", BodyRigAxes.Fwd, -28f);
        _rig.Pose("shin.R", BodyRigAxes.Fwd, -14f);
        _rig.Pose("chest", BodyRigAxes.Up, 0f);
        _rig.Pose("head", BodyRigAxes.Fwd, 0f);
    }

    public override void _Process(double delta)
    {
        if (_frozen) return;
        _trail.Sample(delta);
        ApplyMotion((float)delta);

        if (_flash <= 0) return;
        _flash = Math.Max(0, _flash - (float)delta * 6f);
        if (_bodyMat is not null) _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
    }
}
