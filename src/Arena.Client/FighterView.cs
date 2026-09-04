using System;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Presentation;
using Arena.Client.Visuals;
using Godot;

// PRODUCTION - Arena.Client
// VS-6.5 FighterView 五段结构: Body / Weapon / EffectRoot / StateVisual / AnimationDriver。
// 表现资源由 FighterVisualProfile 驱动（未来按职业替换，Sim 零修改）。
// 事件驱动反馈: OnPresentationEvent 统一入口——禁止轮询 Sim 状态猜测事件。
namespace Arena.Client;

public sealed partial class FighterView : Node3D
{
    private readonly int _id;
    private readonly byte _team;
    private readonly FighterVisualProfile _profile;

    // ---- Body ----
    private readonly MeshInstance3D _body;
    private readonly StandardMaterial3D _bodyMat;

    // ---- Weapon ----
    private readonly Node3D _weaponPivot;
    private readonly MeshInstance3D _weapon;
    private readonly StandardMaterial3D _weaponMat;

    // ---- EffectRoot ----
    private readonly Node3D _effectRoot;

    // ---- StateVisual ----
    private readonly Label3D _nameTag;

    // ---- AnimationDriver ----
    private Tween? _swingTween;
    private float _flash;

    public FighterView(int id, byte team, FighterVisualProfile profile)
    {
        _id = id;
        _team = team;
        _profile = profile;

        var teamCol = team == 0 ? _profile.BodyColor0 : _profile.BodyColor1;

        // Body
        _bodyMat = new StandardMaterial3D { AlbedoColor = teamCol, EmissionEnabled = true, Emission = new Color(0.05f, 0.05f, 0.05f) };
        _body = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = _profile.BodyRadius, Height = _profile.BodyHeight },
            MaterialOverride = _bodyMat,
            Position = new Godot.Vector3(0, _profile.BodyHeight / 2, 0),
        };
        AddChild(_body);

        // Weapon（右手侧竖持——AnimationDriver 挥摆）
        _weaponPivot = new Node3D { Position = new Godot.Vector3(0.45f, 1.1f, 0) };
        _weaponMat = new StandardMaterial3D { AlbedoColor = _profile.WeaponColor, Metallic = 0.6f, Roughness = 0.3f };
        _weapon = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Godot.Vector3(_profile.WeaponWidth, _profile.WeaponLength, _profile.WeaponWidth) },
            MaterialOverride = _weaponMat,
            Position = new Godot.Vector3(0, _profile.WeaponLength / 2, 0),
        };
        _weaponPivot.AddChild(_weapon);
        AddChild(_weaponPivot);

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

        _body.RotationDegrees = f.State == FighterState.Down ? new Godot.Vector3(-90, 0, 0) : Godot.Vector3.Zero;
        _body.Position = f.State == FighterState.Down ? new Godot.Vector3(0, 0.4f, 0) : new Godot.Vector3(0, _profile.BodyHeight / 2, 0);
        _nameTag.Text = f.State == FighterState.Dead ? "KO" : _team == 0 ? $"P1 {f.Hp / 100}" : $"BOT {f.Hp / 100}";
    }

    // ================= AnimationDriver（PresentationEvent 驱动） =================

    public void OnAttackStarted(ushort skillId, Godot.Vector3 pos, Godot.Vector3 dir)
    {
        // 武器挥摆 tween（前摇反馈——出刀感）
        _swingTween?.Kill();
        _swingTween = CreateTween();
        _weaponPivot.RotationDegrees = new Godot.Vector3(0, 0, -60);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", new Godot.Vector3(0, 0, 60), 0.12f);
        _swingTween.TweenProperty(_weaponPivot, "rotation_degrees", Godot.Vector3.Zero, 0.15f);

        // 技能轨迹（简单武器轨迹——技能起手侧颜色弧）
        SpawnSlashArc(pos + dir * 0.6f + Godot.Vector3.Up * 1.1f, dir, 0.5f, 0.18f, _profile.SlashColor);
    }

    public void OnHitFlash(Godot.Vector3 pos)
    {
        // Hit Flash: 材质瞬时提亮（衰减在 _Process）
        _flash = 1.0f;
        _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
        SpawnSpark(pos, _profile.HitSparkColor, 10);
    }

    public void OnLaunched() { /* 击飞表现: Sync 已覆盖 Launch 提亮 + 抬升姿态 */ }

    public void OnDown() { /* 倒地姿态由 Sync 覆盖 */ }

    public void OnDeath() { /* KO 灰色由 Sync 覆盖 */ }

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

    /// 武器轨迹（简易弧——技能起手/普攻共用；tween alpha 渐隐）
    private void SpawnSlashArc(Godot.Vector3 worldPos, Godot.Vector3 dir, float len, float width, Color col)
    {
        var mi = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Godot.Vector3(width, 0.04f, len) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(col.R, col.G, col.B, 0.7f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                EmissionEnabled = true,
                Emission = col,
            },
            Position = worldPos + dir * len / 2,
        };
        GetParent().AddChild(mi);
        var tw = CreateTween();
        tw.TweenProperty(mi.MaterialOverride, "albedo_color:a", 0.0f, 0.2f);
        tw.TweenCallback(Callable.From(() => mi.QueueFree()));
    }

    /// Hit Flash 衰减（_Process 表现帧驱动）
    public override void _Process(double delta)
    {
        if (_flash <= 0) return;
        _flash = Math.Max(0, _flash - (float)delta * 6f);
        _bodyMat.Emission = new Color(_flash, _flash * 0.8f, _flash * 0.5f);
    }

}
