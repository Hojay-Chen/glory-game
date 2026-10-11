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
    private float _flash;
    private float _cancelGlow;   // 取消成立时的青白闪光
    private float _attackTimer;  // 出招剩余时间（>0 时动作由攻击 clip 自持）
    private byte _comboStep;     // 当前普攻连段序号（ChainN；0=非普攻）
    private bool _frozen;        // Hit Stop 表现冻结
    private float _animTime;     // 表现层动画时钟（与 Sim tick 无关）
    private long _lastSpeedRaw;  // 最近一次同步的速度（决定呼吸/奔跑步频）
    private FighterState _lastState = FighterState.Normal;
    private float _recoil;       // 受击后仰（命中反馈的身体层）
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

    /// 技能的**实际时长**由调用方按帧数据给出（startup+active+recovery），
    /// 动作按此时长做速度缩放 → 视觉与判定窗口对齐（GDD §28.4 帧数据驱动）。
    public void OnAttackStarted(ushort skillId, Godot.Vector3 pos, Godot.Vector3 dir, float totalSec = 0.6f)
    {
        _attackTimer = MathF.Max(0.12f, totalSec);
        if (_rig.Anim is not null)
        {
            // 普攻按连段段号在两段剑击之间交替；技能统一用主段
            string clip = _comboStep > 0 && (_comboStep % 2 == 0) ? AttackClipAlt : AttackClip;
            if (_rig.Anim.GetAnimation(clip) is null) clip = AttackClip;
            var a = _rig.Anim.GetAnimation(clip);
            float len = a?.Length ?? 0f;
            _rig.Anim.SpeedScale = len > 0.01f ? Mathf.Clamp(len / totalSec, 0.35f, 3.5f) : 1f;
            _rig.Anim.Play(clip);
        }
        _trail.Arm(skillId);
    }

    /// 攻击动作用哪一段（按系别/武器家族挑，暂用剑击；连段在两段之间交替）
    private const string AttackClip = "Sword_Attack";
    private const string AttackClipAlt = "Sword_Attack_RM";

    /// 攻击生命周期结束（ActEnded/Cancelled/被打断）——停止采样，余点自然淡出
    public void OnAttackEnded(ushort skillId) { _trail.Disarm(skillId); _attackTimer = 0f; _comboStep = 0; }

    /// 终局——立即清空尾迹（不淡出，避免结算画面残留）
    public void OnMatchEnd() => _trail.Clear();

    /// Hit Stop 表现冻结：冻结期连动作播放一起停住（否则顿帧只有特效停、角色还在动）
    public void SetFrozen(bool frozen)
    {
        if (frozen == _frozen) return;
        _frozen = frozen;
        if (_rig.Anim is null) return;
        if (frozen) _rig.Anim.Pause();
        else _rig.Anim.Play();     // 无参 = 从当前处继续
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

    // ================= 荣耀高光反馈（此前 Sim 有事件、画面全无） =================

    /// 取消成立（技能取消普攻后摇 / 强制取消 / 跳跃取消）——已收尾迹 + 一记青白闪光。
    public void OnCancel()
    {
        _attackTimer = 0f;
        _flash = Mathf.Max(_flash, 0.55f);
        _cancelGlow = 1f;
    }

    /// 二段浮空（空中被再次打起）——浮空连招的关键节拍，给最亮的火花。
    public void OnRelaunched(Godot.Vector3 pos) => SpawnSpark(pos, new Color(1f, 0.78f, 0.35f), 18);

    /// 受身——落地瞬间的翻滚起身
    public void OnUkemi(Godot.Vector3 pos)
    {
        SpawnSpark(pos, new Color(0.65f, 0.95f, 1f), 10);
        _rig.Anim?.Play("Roll");
    }

    /// 击飞撞墙反弹
    public void OnWallBounce(Godot.Vector3 pos) => SpawnSpark(pos, new Color(1f, 0.55f, 0.25f), 16);

    /// 破防（格挡值被打光）
    public void OnGuardBroken(Godot.Vector3 pos) => SpawnSpark(pos, new Color(1f, 0.9f, 0.35f), 20);

    /// 起身完成——回到常规动作
    public void OnGetupDone() { }

    /// 普攻连段第 N 段——只记录段号，实际选哪段动作由随后的 OnAttackStarted 决定
    /// （两者同 Tick 发出；由一处统一播动作，避免互相覆盖）。
    public void OnBasicStep(byte step) => _comboStep = step;

    public void OnDeath()
    {
        _attackTimer = 0f;
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

    // ================= 动作驱动（AnimationPlayer 播放重定向好的 CC0 动作） =================

    /// 各档位动作的"设计步速"（m/s）——用来把播放速度缩放到实际移动速度，避免滑步。
    private const float WalkSpd = 1.7f, JogSpd = 3.6f, SprintSpd = 6.0f;

    /// 每表现帧：尾迹采样 + Hit Flash 衰减 + 整体位移 + 动作选择。
    /// 姿态全部来自动作 clip；本函数只决定"放哪一段、放多快"，是**纯表现**，不影响 Sim。
    private void ApplyMotion(float delta)
    {
        _animTime += delta;
        _recoil = Math.Max(0f, _recoil - delta * 3.2f);
        if (_attackTimer > 0f) _attackTimer = MathF.Max(0f, _attackTimer - delta);

        float speed = _lastSpeedRaw / 65536f;
        float run = Math.Min(1f, speed / SprintSpd);
        float bob = MathF.Sin(_animTime * (2.2f + run * 6.5f) * 6.2831853f) * (0.012f + run * 0.035f);

        if (_lastState == FighterState.Dead || _lastState == FighterState.Down)
        {
            _bodyRig.Position = Godot.Vector3.Zero;   // 倒地姿态由 Death01 动作给，这里不再人工摆
            _bodyRig.Rotation = Godot.Vector3.Zero;
        }
        else
        {
            _bodyRig.Position = new Godot.Vector3(0, bob, 0);
            _bodyRig.Rotation = new Godot.Vector3(-run * 0.10f + _recoil * 0.30f, 0, MathF.Sin(_animTime * 1.4f) * 0.012f);
        }
        DriveClips(speed);
    }

    /// 按状态/速度挑动作。攻击期由 `_attackTimer` 自持，不被打断。
    private void DriveClips(float speed)
    {
        var a = _rig.Anim;
        if (a is null) return;

        string want;
        float scale = 1f;

        if (_lastState is FighterState.Dead or FighterState.Down) want = "Death01";
        else if (_lastState == FighterState.Launch) want = "Jump";
        else if (_lastState == FighterState.Hitstun) want = "Hit_Chest";
        else if (_attackTimer > 0f) return;                       // 出招中：让攻击动作自己跑完
        else if (speed > 4.6f) { want = "Sprint"; scale = Mathf.Clamp(speed / SprintSpd, 0.7f, 1.5f); }
        else if (speed > 1.9f) { want = "Jog_Fwd"; scale = Mathf.Clamp(speed / JogSpd, 0.7f, 1.5f); }
        else if (speed > 0.22f) { want = "Walk"; scale = Mathf.Clamp(speed / WalkSpd, 0.7f, 1.5f); }
        else want = "Idle";

        if (a.CurrentAnimation == want) { a.SpeedScale = scale; return; }
        a.SpeedScale = scale;
        a.Play(want);
    }

    public override void _Process(double delta)
    {
        if (_frozen) return;
        _trail.Sample(delta);
        ApplyMotion((float)delta);

        if (_flash <= 0 && _cancelGlow <= 0) return;
        _flash = Math.Max(0, _flash - (float)delta * 6f);
        _cancelGlow = Math.Max(0, _cancelGlow - (float)delta * 4.5f);
        if (_bodyMat is not null)
            _bodyMat.Emission = new Color(
                _flash,                                        // 受击闪白（暖）
                _flash * 0.8f + _cancelGlow * 0.85f,           // 取消闪青（冷）——两路叠加
                _flash * 0.5f + _cancelGlow);
    }
}
