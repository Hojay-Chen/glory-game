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

    // ---- Body ----
    private readonly Node3D _bodyRig;          // 人形装配根（取代原单胶囊）
    private readonly StandardMaterial3D _bodyMat;

    // ---- Weapon ----
    private readonly Node3D _weaponPivot;
    private readonly StandardMaterial3D _weaponMat;

    // ---- EffectRoot ----
    private readonly Node3D _effectRoot;

    // ---- StateVisual ----
    private readonly Label3D _nameTag;

    // ---- Weapon Trail（VS-8）----
    private readonly WeaponTrail _trail;

    // ---- AnimationDriver ----
    private Tween? _swingTween;
    private float _flash;
    private bool _frozen;        // Hit Stop 表现冻结
    private bool _tweenPaused;   // 冻结时暂停挥摆、解冻时恢复（只恢复自己暂停的那次）

    public FighterView(int id, byte team, FighterVisualProfile profile)
    {
        _id = id;
        _team = team;
        _profile = profile;

        var teamCol = team == 0 ? _profile.BodyColor0 : _profile.BodyColor1;

        // Body —— 人形装配（14 件基本体 + 系别变体件；纯代码，零外部资产）
        _bodyRig = new Node3D();
        _bodyMat = FighterMeshBuilder.Build(_bodyRig, _profile, teamCol, _team);
        AddChild(_bodyRig);

        // Weapon（右手侧竖持——AnimationDriver 挥摆）
        _weaponPivot = new Node3D { Position = new Godot.Vector3(0.45f, 1.1f, 0) };
        _weaponMat = new StandardMaterial3D { AlbedoColor = _profile.WeaponColor, Metallic = 0.6f, Roughness = 0.3f };
        AddChild(_weaponPivot);
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
            FontSize = 40,
            Position = new Godot.Vector3(0, 2.1f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        AddChild(_nameTag);
    }

    public int Id => _id;

    /// 权威状态投影（每物理帧——P3: 表现跟随 Sim）
    public void Sync(FighterStateData f)
    {
        Position = new Godot.Vector3(
            (float)(f.PosX.Raw / 65536.0),
            (float)(f.PosY.Raw / 65536.0),
            (float)(f.PosZ.Raw / 65536.0));
        Rotation = new Godot.Vector3(0, -(float)(f.HeadingQuantum / 65536.0 * Math.PI * 2), 0);

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

        _bodyRig.RotationDegrees = f.State == FighterState.Down ? new Godot.Vector3(-90, 0, 0) : Godot.Vector3.Zero;
        _bodyRig.Position = f.State == FighterState.Down ? new Godot.Vector3(0, 0.4f, 0) : Godot.Vector3.Zero;
        _nameTag.Text = f.State == FighterState.Dead ? "KO" : _team == 0 ? $"P1 {f.Hp / 100}" : $"BOT {f.Hp / 100}";
    }

    // ================= AnimationDriver（PresentationEvent 驱动） =================

    public void OnAttackStarted(ushort skillId, Godot.Vector3 pos, Godot.Vector3 dir)
    {
        // 武器挥摆 tween（前摇反馈——出刀感）
        _swingTween?.Kill();
        _tweenPaused = false;
        _swingTween = CreateTween();
        _weaponPivot.RotationDegrees = new Godot.Vector3(0, 0, -60);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", new Godot.Vector3(0, 0, 60), 0.12f);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", Godot.Vector3.Zero, 0.15f);

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
    public override void _Process(double delta)
    {
        if (_frozen) return;
        _trail.Sample(delta);

        if (_flash <= 0) return;
        _flash = Math.Max(0, _flash - (float)delta * 6f);
        _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
    }

}
