using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Ai;
using Arena.Infra.Data;
using Arena.Infra.Input;
using Arena.Infra.Match;
using Arena.Infra.Presentation;
using Godot;

// PRODUCTION - Arena.Client
// Vertical Slice 主入口（ADR-0009 §5 场景树落地）:
//   StartMatch 装配（数据编译→SimWorld→地形→Fighters→AI）→ 60Hz 权威步进 → 视觉/HUD/相机同步
//   → 胜负判定 → R 重开（RestartMatch: 完整重装配，零状态残留）。
//   VS-7/VS-8: 步进后经 PresentationDirector 消费 Sim 事件流 → DispatchCue 翻译为节点动作
//   （挥摆/尾迹/火花/闪白/弹刀/HitStop 冻结/相机 trauma）——判定全在 Arena.Infra，本层只做节点操作。
//   Hit Stop 冻结同时下发到各视图（暂停挥摆 tween + 停止尾迹采样），避免解冻时尖端跳变画出残留直线。
// 架构约束（ADR-0001/0007/0009）: Godot 层只产 Input + Presentation；
//   SimWorld 是唯一战斗权威；AI 与玩家经同一条 Command 流（AiBot 在 Arena.Infra.Ai——与
//   Headless MatchDriver 共用同一 bot 实现）。表现层不预测 Sim。
// 环境变量 ARENA_AUTOPILOT=1 → 玩家位也由 AI 驱动（headless 冒烟/回归用）。
namespace Arena.Client;

public partial class MatchRoot : Node
{
    private RuntimeCatalog? _catalog;
    private MatchSetup? _setup;
    private SimWorld? _world;
    private AiBot? _bot;                 // Fighter 1 的 AI 对手（同权 Command——ADR-0007）
    private AiBot? _botP0;               // autopilot: 玩家位 AI
    private InputMapper? _inputMapper;
    private int _tick;
    private bool _matchOver;
    private bool _autopilot;

    private readonly Node3D _envRoot = new();      // 静态环境（光照/地板/建筑——只建一次）
    private Node3D _matchRoot = new();             // 每局动态节点（FighterView/出生点标记——Restart 时整体重建）
    private readonly Dictionary<int, FighterView> _views = new();
    private Camera3D? _camera;
    private int _restartCooldown;
    private Hud? _hud;
    private readonly Arena.Client.Audio.AudioDirector _audio = new();

    // VS-7 表现调度（纯逻辑——只读 Sim 事件流，产出引擎无关的表现线索）
    private readonly PresentationDirector _director = new();
    private Godot.Vector3 _lastPlayer;    // 相机跟随的玩家位（越肩机位用）
    private Godot.Vector3 _lastEnemy;
    private bool _camValid;
    private const int IntroTicks = 90;   // 开场运镜时长（60 Tick/s → 1.5s）
    private float _finishT;              // 终局特写进度 0→1
    private Godot.Vector3 _finishFocus;  // 终局特写焦点（胜者）

    // ---- 鼠标视角（2026-10-13 手感重做）----
    // _camYaw 语义与 Sim HeadingQuantum 一致（0=+Z，顺时针为正；单位=弧度），
    // 移动方向 = 相机相对（W=视角前方），攻击朝向 = 软锁定敌人 / 相机前方。
    private float _camYaw;               // 视线方位角
    private float _camPitch = 0.30f;     // 俯仰（0.30 ≈ 17°俯角起手）
    private float _camDist = 5.2f;       // 视距（滚轮 3.2..9）
    private MoveCtx? _moveCtx;           // 相机相对换算（随 _camYaw 逐 tick 刷新）
    private float _mouseIdle;            // 距上次鼠标移动的秒数（软锁定回正用）

    private MoveCtx MoveCtxFor()
    {
        if (_moveCtx is null)
            _moveCtx = new MoveCtx { ToWorld = ToWorldDir, AimQuantum = SoftLockAim() };
        _moveCtx.AimQuantum = SoftLockAim();
        return _moveCtx;
    }

    /// 屏幕方向 (sx,sz)（W=(0,-1)=前、D=(1,0)=右）→ 世界 (8向Dir, 模拟量Aim)。
    /// world = Right·sx + Forward·(−sz)；heading 约定与 Sim 一致（0=+Z，atan2(x,z)）。
    private (byte Dir, ushort Aim) ToWorldDir(int sx, int sz)
    {
        float fx = Mathf.Sin(_camYaw), fz = Mathf.Cos(_camYaw);   // 视线前方（XZ）
        float rx = -fz, rz = fx;                                  // 视线右方
        float wx = rx * sx + fx * (-sz);
        float wz = rz * sx + fz * (-sz);
        if (Mathf.Abs(wx) < 1e-5f && Mathf.Abs(wz) < 1e-5f) return (0, 0);
        float ang = Mathf.Atan2(wx, wz);
        if (ang < 0) ang += Mathf.Tau;
        ushort aim = (ushort)(Mathf.RoundToInt(ang / Mathf.Tau * 65536f) % 65536);
        byte dir = (byte)(Mathf.RoundToInt(aim / 8192f) % 8);
        return (dir, aim);
    }

    /// 攻击朝向：敌人位于视线前方 ±60° 锥内 → 直接瞄敌人（软锁定，1v1 不搓玻璃）；
    /// 否则瞄相机前方。
    private ushort SoftLockAim()
    {
        if (!_camValid) return QuantumFromAngle(_camYaw);
        float dx = _lastEnemy.X - _lastPlayer.X, dz = _lastEnemy.Z - _lastPlayer.Z;
        float d = Mathf.Sqrt(dx * dx + dz * dz);
        if (d < 0.4f) return QuantumFromAngle(_camYaw);
        float angE = Mathf.Atan2(dx / d, dz / d);
        float diff = angE - _camYaw;
        if (diff > Mathf.Pi) diff -= Mathf.Tau; else if (diff < -Mathf.Pi) diff += Mathf.Tau;
        return Mathf.Abs(diff) <= 1.05f ? QuantumFromAngle(angE) : QuantumFromAngle(_camYaw);
    }

    private static ushort QuantumFromAngle(float a)
    {
        if (a < 0) a += Mathf.Tau;
        return (ushort)(Mathf.RoundToInt(a / Mathf.Tau * 65536f) % 65536);
    }

        /// 鼠标：视角旋转（对战中捕获）+ 滚轮视距
        public override void _Input(InputEvent e)
        {
            if (_autopilot || _state != AppState.Playing) return;
            if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                _camYaw -= mm.Relative.X * 0.0028f;    // 右移鼠标 = 视线向右（yaw 减）
                _camPitch = Mathf.Clamp(_camPitch + mm.Relative.Y * 0.0022f, 0.05f, 1.15f);
                _mouseIdle = 0f;
            }
            else if (e is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp) _camDist = Mathf.Clamp(_camDist - 0.5f, 3.2f, 9f);
                else if (mb.ButtonIndex == MouseButton.WheelDown) _camDist = Mathf.Clamp(_camDist + 0.5f, 3.2f, 9f);
            }
        }


    private const int LocalPlayerId = 0;
    private const int EnemyId = 1;
    private const long MatchLengthTicks = 3600;
    private const float ShakeWorldScale = 8f;   // trauma² 偏移 → 世界米（白盒手感）
    private static readonly Godot.Vector3 FallbackDir = new(0, 0, 1);

    // ---- 菜单流状态 ----
    private enum AppState { Menu, Selecting, Playing, Paused, Result }
    private AppState _state = AppState.Menu;

    private string _playerClass = "BLA";
    private string _enemyClass = "BLA";
    private string _playerWeapon = "";
    private string _enemyWeapon = "";
    private List<ClassDefParser.ClassEntry> _classList = new();
    private List<WeaponCatalogParser.WeaponEntry> _weaponList = new();
    private RuntimeCatalog? _uiCatalog;
    private ClassSelectScreen? _select;
    private WeaponSelectScreen? _weaponSelect;
    private MainMenuScreen? _menu;
    private PauseScreen? _pause;
    private ResultScreen? _result;

    /// 结算统计（只读自 Sim 事件流；StartMatch 清零）
    private int _statDealt, _statTaken, _statHits, _statMaxCombo;

    private bool Selecting => _state is AppState.Menu or AppState.Selecting or AppState.Paused or AppState.Result;

    public override void _Ready()
    {
        _autopilot = Godot.OS.GetEnvironment("ARENA_AUTOPILOT") == "1";
        AddChild(_envRoot);
        AddChild(_matchRoot);

        // 静态环境只建一次（光照/建筑网格——与对局状态无关）
        var builder = new ArenaBuilder();
        builder.BuildEnvironment(_envRoot);

        AddChild(_audio);          // 音效（程序化合成，零素材）
        _hud = new Hud();
        AddChild(_hud);
        _camera = CameraFactory.CreateFollowCamera();
        AddChild(_camera);

        // 非 autopilot（真人）才走选人界面；autopilot 是自动化验证，直接按默认职业开打
        var repo = FindRepoRoot();
        _classList = ClassDefParser.Parse(Path.Combine(repo, "docs/balance-sheet/class-base.csv"));
        _weaponList = WeaponCatalogParser.Parse(Path.Combine(repo, "docs/weapon-spec/weapons.csv"));
        // 界面需要展示职业面板与技能预览 → 编译一次 catalog（仅菜单用；对局仍由 MatchAssembler 自行编译）
        var (_, uiCatalog) = new DataCompiler().CompileWithCatalog(
            Path.Combine(repo, "docs/skill-spec/skills.csv"),
            Path.Combine(repo, "docs/weapon-spec/weapons.csv"),
            Path.Combine(repo, "docs/balance-sheet/class-base.csv"));
        _uiCatalog = uiCatalog;

        // ARENA_P0 / ARENA_P1 指定双方职业并跳过选人界面（渲染验证 / 快速开打用）
        var p0 = Godot.OS.GetEnvironment("ARENA_P0");
        if (p0.Length > 0)
        {
            _playerClass = p0;
            var p1 = Godot.OS.GetEnvironment("ARENA_P1");
            _enemyClass = p1.Length > 0 ? p1 : "BLA";
            _playerWeapon = Godot.OS.GetEnvironment("ARENA_W0");
            _enemyWeapon = Godot.OS.GetEnvironment("ARENA_W1");
            _state = AppState.Playing;      // ← 必须置 Playing，否则 _PhysicsProcess 直接早退（世界不步进）
            StartMatch();
            return;
        }

        if (_autopilot) { _hud.Visible = true; _state = AppState.Playing; StartMatch(); return; }

        _hud.Visible = false;                   // 菜单期间藏起战斗 HUD

        // 渲染取证入口：ARENA_SCREEN=select 直接进选人屏（正常流程要先点「开始对战」）
        if (Godot.OS.GetEnvironment("ARENA_SCREEN") == "select")
        {
            SetWorldVisible(false);
            _state = AppState.Selecting;
            _select = new ClassSelectScreen(_classList, _uiCatalog, OnClassesChosen);
            AddChild(_select);
            return;
        }

        ShowMainMenu();
    }

    // ================= 菜单流（主菜单 → 选职业 → 选武器 → 对战 → 暂停 → 结算） =================

    /// 菜单/选人期间隐藏 3D 场景（没有对局就没什么可看的；避免几何透到界面背后）
    private void SetWorldVisible(bool v)
    {
        _envRoot.Visible = v;
        _matchRoot.Visible = v;
    }

    private void ShowMainMenu()
    {
        _state = AppState.Menu;
        SetWorldVisible(false);
        _menu ??= new MainMenuScreen(OnMenuStart, OnMenuSettings, () => GetTree().Quit(0));
        if (_menu.GetParent() is null) AddChild(_menu);   // 首次需入树（CanvasLayer 不入树不渲染）
        _menu.Visible = true;
        if (_hud is not null) _hud.Visible = false;
    }

    private void OnMenuStart()
    {
        if (_menu is not null) _menu.Visible = false;
        _menu = null;
        _state = AppState.Selecting;
        SetWorldVisible(false);
        _select = new ClassSelectScreen(_classList, _uiCatalog, OnClassesChosen);
        AddChild(_select);
    }

    /// 设置面板尚未实现（登记于 OPEN-ISSUES UI-05）——此处只在控制台留痕，不做假界面
    private void OnMenuSettings() => Console.WriteLine("[UI] 设置面板未实现（OPEN-ISSUES UI-05）");

    private void OnClassesChosen(string player, string enemy)
    {
        _playerClass = player;
        _enemyClass = enemy;
        _select?.QueueFree();
        _select = null;
        _weaponSelect = new WeaponSelectScreen(player, ClassNameOf(player), _weaponList, OnWeaponChosen);
        AddChild(_weaponSelect);
    }

    private void OnWeaponChosen(string weaponId)
    {
        _playerWeapon = weaponId;
        // 对手武器：从对手职业的武器表里随机（未做「对手选武器」界面——登记 OPEN-ISSUES）
        var picks = _weaponList.Where(w => w.ClassId == _enemyClass).ToList();
        _enemyWeapon = picks.Count > 0 ? picks[Random.Shared.Next(picks.Count)].WeaponId : "";
        _weaponSelect?.QueueFree();
        _weaponSelect = null;
        _state = AppState.Playing;
        StartMatch();
    }

    private void OpenPause()
    {
        if (_state != AppState.Playing) return;
        _state = AppState.Paused;
        _pause = new PauseScreen(ClassNameOf(_playerClass), ClassNameOf(_enemyClass),
            onResume: () => { _pause?.QueueFree(); _pause = null; _state = AppState.Playing; },
            onRestart: () => { _pause?.QueueFree(); _pause = null; _state = AppState.Playing; RestartMatch(); },
            onSwitchClass: () => { _pause?.QueueFree(); _pause = null; ReturnToClassSelect(); },
            onMainMenu: () => { _pause?.QueueFree(); _pause = null; ReturnToMainMenu(); });
        AddChild(_pause);
    }

    private void ReturnToClassSelect()
    {
        ClearMatch();
        SetWorldVisible(false);
        _state = AppState.Selecting;
        _select = new ClassSelectScreen(_classList, _uiCatalog, OnClassesChosen);
        AddChild(_select);
    }

    private void ReturnToMainMenu()
    {
        ClearMatch();
        ShowMainMenu();
    }

    /// 拆掉本局动态节点（回菜单/回选人时用——避免残影与残留输入）
    private void ClearMatch()
    {
        _views.Clear();
        foreach (var child in _matchRoot.GetChildren()) child.QueueFree();
        _matchRoot = new Node3D();
        AddChild(_matchRoot);
        _world = null; _setup = null; _bot = null; _botP0 = null; _inputMapper = null;
        _director.Reset();
        if (_hud is not null) _hud.Visible = false;
    }

    private void ShowResultScreen()
    {
        _state = AppState.Result;
        var outcome = _resultText switch
        {
            "DRAW" => MatchOutcome.Draw,
            "BLUE WIN" => MatchOutcome.Win,     // 玩家恒为 0 号位（蓝）
            _ => MatchOutcome.Lose,
        };
        var stats = new MatchStats(_statDealt, _statTaken, _statMaxCombo, _statHits, _tick);
        _result = new ResultScreen(outcome, ClassNameOf(_playerClass), ClassNameOf(_enemyClass), stats,
            onRematch: () => { _result?.QueueFree(); _result = null; _state = AppState.Playing; RestartMatch(); },
            onSwitchClass: () => { _result?.QueueFree(); _result = null; ReturnToClassSelect(); },
            onMainMenu: () => { _result?.QueueFree(); _result = null; ReturnToMainMenu(); });
        AddChild(_result);
        if (_hud is not null) _hud.Visible = false;
    }

    private string _resultText = "";

    /// class_id → 武器类型（weapons.csv 的 type 列；用于选武器家族网格）
    private string WeaponTypeOf(string classId)
    {
        foreach (var w in _weaponList)
            if (w.ClassId == classId) return w.Type;
        return "";
    }

    /// class_id → 系别名（视觉档案的剪影族依据）
    private string BranchOf(string classId)
    {
        foreach (var c in _classList)
            if (c.ClassId == classId) return c.Branch;
        return "";
    }

    /// class_id → 中文名（选人界面与 HUD 显示用）
    private string ClassNameOf(string classId)
    {
        foreach (var c in _classList)
            if (c.ClassId == classId) return c.Name;
        return classId;
    }


    private static string FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static List<ArenaDefParser.ArenaObject> LoadArenaObjects()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/balance-sheet/arena.csv")))
            dir = Path.GetDirectoryName(dir);
        return ArenaDefParser.Parse(Path.Combine(dir!, "docs/balance-sheet/arena.csv"));
    }

    // ---- 每局装配（Restart 复用；动态节点全部挂 _matchRoot——零残留） ----

    private void StartMatch()
    {
        var setup = MatchAssembler.Assemble(0x5EED_0001L, FindRepoRoot(),
            playerClass: _playerClass, enemyClass: _enemyClass,
            playerWeapon: _playerWeapon, enemyWeapon: _enemyWeapon);
        _setup = setup;
        _world = setup.World;
        _catalog = setup.Catalog;
        _tick = 0;
        _matchOver = false;
        CameraFactory.ResetLook(new Godot.Vector3(0, 1.3f, 0));   // 每局开场运镜从干净位姿起
        // 视线初始朝敌人（玩家在 Spawn0、敌人在 Spawn1）
        { var px = (float)(setup.Spawn0.X / 65536.0); var pz = (float)(setup.Spawn0.Z / 65536.0);
          var ex = (float)(setup.Spawn1.X / 65536.0); var ez = (float)(setup.Spawn1.Z / 65536.0);
          _camYaw = Mathf.Atan2(ex - px, ez - pz); }
        _moveCtx = null;
        _resultText = "";
        _statDealt = _statTaken = _statHits = _statMaxCombo = 0;
        SetWorldVisible(true);
        _director.Reset();   // 表现层态机清零（零残留——与 Restart 同语义）

        // 出生点标记
        _spawnMarker(setup.Spawn0.X, setup.Spawn0.Z, Arena.Client.Visuals.FighterVisualProfile.BlueTeam);
        _spawnMarker(setup.Spawn1.X, setup.Spawn1.Z, Arena.Client.Visuals.FighterVisualProfile.OrangeTeam);

        // FighterView
        foreach (var f in _world.Fighters)
        {
            // 按职业取视觉档案（六系剪影 + 系别徽记色）——此前所有职业硬用同一套 Blade 档案
            string cls = f.Id == LocalPlayerId ? _playerClass : _enemyClass;
            var view = new FighterView(f.Id, f.Team,
                Arena.Client.Visuals.FighterVisualProfile.For(cls, BranchOf(cls), WeaponTypeOf(cls)),
                ClassNameOf(cls));
            _views[f.Id] = view;
            _matchRoot.AddChild(view);
        }

        // 输入映射（Press/Held/Release 状态自持——随 Restart 重建归零）
        _inputMapper = InputTranslator.Create(LocalPlayerId, _catalog);

        // AI 对手接入（同权 Command 流——ADR-0007 §1；Bot 实现与 Headless MatchDriver 共用）
        _bot = new AiBot(EnemyId, LocalPlayerId, seed: 0xA1_0001, aggression: 0.55);
        if (_autopilot)
            _botP0 = new AiBot(LocalPlayerId, EnemyId, seed: 0xA2_0002, aggression: 0.7);   // autopilot: 玩家位 AI

        _hud?.Reset(_catalog, _playerClass, ClassNameOf(_playerClass), ClassNameOf(_enemyClass));
        if (_hud is not null) _hud.Visible = true;
    }

    /// R 重开（用户裁定: 重新装配 SimWorld/清理表现节点，零隐式状态残留）
    private void RestartMatch()
    {
        _views.Clear();
        foreach (var child in _matchRoot.GetChildren()) child.QueueFree();
        _matchRoot = new Node3D();
        AddChild(_matchRoot);
        _world = null;
        _setup = null;
        _bot = null;
        _botP0 = null;
        _inputMapper = null;
        // 相机与 HUD 保留实例（视觉参数随新局自动跟随）；StartMatch 重建 world/bot/mapper/views
        StartMatch();
        Console.WriteLine($"[VS] match restarted at frame {Engine.GetPhysicsFrames()}");
    }

    private void _spawnMarker(long x, long z, Color col)
    {
        var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.1f } };
        mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = col };
        mi.Position = new Godot.Vector3((float)((double)x / 65536.0), 0.06f, (float)((double)z / 65536.0));
        _matchRoot.AddChild(mi);
    }

    public override void _PhysicsProcess(double delta)
    {
        // 鼠标捕获: 对战中锁定指针（视角控制），其余状态放出来点菜单
        var wantMouse = (_state == AppState.Playing && !_autopilot)
            ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != wantMouse) Input.MouseMode = wantMouse;

        // 软锁定回正：鼠标静置 0.8s 后，视线以 ≤1.4 rad/s 滑向敌人方位（夹角 >110° 不拉，尊重手动）
        if (_state == AppState.Playing && !_autopilot && _camValid)
        {
            _mouseIdle += (float)delta;
            if (_mouseIdle > 0.8f)
            {
                float bx = _lastEnemy.X - _lastPlayer.X, bz = _lastEnemy.Z - _lastPlayer.Z;
                if (bx * bx + bz * bz > 1f)
                {
                    float bearing = Mathf.Atan2(bx, bz);
                    float diff = bearing - _camYaw;
                    if (diff > Mathf.Pi) diff -= Mathf.Tau; else if (diff < -Mathf.Pi) diff += Mathf.Tau;
                    if (Mathf.Abs(diff) < 1.92f)
                        _camYaw += Mathf.Clamp(diff, -1.4f * (float)delta, 1.4f * (float)delta);
                }
            }
        }

        if (_restartCooldown > 0) _restartCooldown--;

        // 非对战中（菜单/选人/暂停/结算）: 不步进 Sim；只让表现层收尾，并处理全局键
        if (_state is not AppState.Playing)
        {
            if (_camera is not null && _camValid)
            {
                _director.Camera.Tick(delta);
                // 终局特写：结算界面之下做绕胜者环绕推近
                if (_matchOver)
                {
                    _finishT = MathF.Min(1f, _finishT + (float)delta * 0.16f);
                    CameraFactory.Finisher(_camera, _finishFocus, _finishT);
                }
                else CameraFactory.Orbit(_camera, _lastPlayer, _lastEnemy, _camYaw, _camPitch, _camDist);
                ApplyCameraShake();
            }
            return;
        }

        // 对战中: Esc 暂停 / R 快速重开
        if (_restartCooldown == 0 && Input.IsKeyPressed(Key.Escape))
        {
            _restartCooldown = 30;
            OpenPause();
            return;
        }
        if (_restartCooldown == 0 && Input.IsKeyPressed(Key.R))
        {
            _restartCooldown = 30;
            RestartMatch();
            return;
        }
        if (_matchOver || _world is null || _catalog is null) return;   // 装配未就绪/已终局

        _tick++;

        // 指令收集: 玩家（Press/Held/Release 语义）+ AI（同权 Command——共用 SimWorld 权威链路）
        var cmds = new List<Command>(8);
        if (_autopilot)
        {
            cmds.AddRange(_botP0!.Produce(_tick, _world, _catalog!));
            cmds.AddRange(_bot!.Produce(_tick, _world, _catalog!));
        }
        else
        {
            var bindings = InputTranslator.Bindings(_catalog!, _playerClass);
            // 鼠标键复用同一套角色语义（LMB=普攻 / RMB=格挡——有格挡数据才绑）
            var guardUid = bindings.FirstOrDefault(b => b.role == "guard").skillUid;
            bindings.Add((InputMapper.KeyIds.MouseLeft, "basic", 0));
            if (guardUid != 0) bindings.Add((InputMapper.KeyIds.MouseRight, "guard", guardUid));
            cmds.AddRange(_inputMapper!.Collect(bindings, MoveCtxFor()));
            if (_bot is { } bot)
                cmds.AddRange(bot.Produce(_tick, _world, _catalog!));
        }

        // 权威步进（单机同进程；ADR-0009 §2）
        _world.Step(_tick, cmds.ToArray());

        // VS-7 表现调度: 消费 Sim 事件流 → 表现线索（只读——Sim 零触碰）
        foreach (var cue in _director.Advance(_world.Events.All, delta))
            DispatchCue(cue);

        // 视觉/HUD 同步（Hit Stop 冻结期跳过位置/状态同步——表现冻结，Sim 照常步进）
        // 冻结同时下发到各视图：暂停挥摆 tween 并停止尾迹采样（否则解冻瞬间尖端跳变会画出残留直线）
        var frozen = _director.HitStop.ShouldFreezeVisuals;
        foreach (var v in _views.Values) v.SetFrozen(frozen);
        if (!frozen)
        {
            foreach (var f in _world.Fighters)
                if (_views.TryGetValue(f.Id, out var view)) view.Sync(f);
        }
        _hud?.Sync(_world, _tick);
        if (_tick % 60 == 0 && Godot.OS.GetEnvironment("ARENA_DEBUG") == "1" && _camera is not null)
        {
            var fa = _world.Fighters[0]; var fb = _world.Fighters[1];
            Console.WriteLine($"[DBG] t={_tick} f0=({(double)fa.PosX.Raw/65536:0.0},{(double)fa.PosZ.Raw/65536:0.0}) " +
                              $"f1=({(double)fb.PosX.Raw/65536:0.0},{(double)fb.PosZ.Raw/65536:0.0}) " +
                              $"cam=({_camera.Position.X:0.0},{_camera.Position.Y:0.0},{_camera.Position.Z:0.0})");
        }
        if (_world.Fighters.Count >= 2 && _world.Fighters[1].HitstunCount > _statMaxCombo)
            _statMaxCombo = _world.Fighters[1].HitstunCount;

        if (_camera is not null && _world.Fighters.Count >= 2)
        {
            var a = _world.Fighters[0];
            var b = _world.Fighters[1];
            _lastPlayer = new Godot.Vector3((float)(a.PosX.Raw / 65536.0), 0, (float)(a.PosZ.Raw / 65536.0));
            _lastEnemy = new Godot.Vector3((float)(b.PosX.Raw / 65536.0), 0, (float)(b.PosZ.Raw / 65536.0));
            _camValid = true;
            // 开场演出：前 1.5s 用高空拉升机位（§1.7），随后平滑交棒越肩战斗位
            if (_tick < IntroTicks)
                CameraFactory.Intro(_camera, _lastPlayer, _lastEnemy, _tick / (float)IntroTicks);
            else
                CameraFactory.Orbit(_camera, _lastPlayer, _lastEnemy, _camYaw, _camPitch, _camDist);
            ApplyCameraShake();
        }

        // 胜负判定
        if (!_matchOver && _tick > 60)
        {
            var alive0 = _world.Fighters.Any(f => f.Team == 0 && f.State != FighterState.Dead);
            var alive1 = _world.Fighters.Any(f => f.Team == 1 && f.State != FighterState.Dead);
            if (!alive0 || !alive1 || _tick >= MatchLengthTicks)
            {
                _matchOver = true;
                _director.FlagMatchEnd();   // 终局闸断战斗表现（Died 路径由 Bridge 自动置位）
                _resultText = !alive0 && !alive1 ? "DRAW" : alive0 ? "BLUE WIN" : "ORANGE WIN";
                // 终局特写焦点：胜者所在（平局则取两人中点）
                _finishT = 0f;
                _finishFocus = !alive0 && !alive1
                    ? (_lastPlayer + _lastEnemy) * 0.5f
                    : (alive0 ? _lastPlayer : _lastEnemy);
                var result = _resultText;
                _hud?.ShowResult(result, _tick);
                if (!_autopilot) ShowResultScreen();   // 真人局弹结算界面；autopilot 只打印
                if (_autopilot)
                {
                    var hits = _world.Events.All.Count(e => e.Kind == EventKind.Hit);
                    Console.WriteLine($"[VS] match over t={_tick} result={result} hits={hits} " +
                        $"hp0={_world.Fighters[0].Hp} hp1={_world.Fighters[1].Hp}");
                    // 荣耀高光事件统计——确认"系统深度"真的在跑（否则表现层新接的 cue 永远不触发）
                    var kinds = new[] { EventKind.Cancelled, EventKind.BasicStep, EventKind.Launched,
                        EventKind.Relaunched, EventKind.Ukemi, EventKind.WallBounced, EventKind.GuardBroken,
                        EventKind.GetupDone, EventKind.Parry, EventKind.GuardHit, EventKind.Interrupted,
                        EventKind.ForcedDown, EventKind.FloatProtect };
                    var tally = string.Join(" ", kinds.Select(k =>
                        $"{k}={_world.Events.All.Count(e => e.Kind == k)}"));
                    Console.WriteLine($"[VS] events: {tally}");
                    GetTree().Quit(0);
                }
            }
        }

        if (_autopilot && _tick > 4200)
            GetTree().Quit();
    }

    // ================= VS-7 表现线索分发 / 相机偏移（Godot 层唯一职责：cue → 节点操作） =================

    /// 技能的**实际总时长（秒）** = (前摇+判定+后摇)/60 —— 动作按它做速度缩放，
    /// 视觉与判定窗口对齐（帧数据驱动，GDD §28.4）。
    private float SkillSeconds(ushort skillId)
    {
        var def = _world?.GetSkill(skillId);
        if (def is null) return 0.6f;
        int ticks = def.StartupTicks + def.ActiveTicks + def.RecoveryTicks;
        return ticks <= 0 ? 0.6f : ticks / 60f;
    }

    private void ApplyCameraShake()
    {
        if (_camera is null) return;
        var cam = _director.Camera;
        _camera.Position += new Godot.Vector3(
            (float)(cam.ShakeX * ShakeWorldScale),
            (float)(cam.ShakeY * ShakeWorldScale),
            0);
    }

    private void DispatchCue(PresentationCue cue)
    {
        _audio.PlayCue(cue);        // 声音与画面对齐：同一条表现线索驱动
        switch (cue.Kind)
        {
            case PresentationCueKind.WeaponSwing:
                if (_views.TryGetValue(cue.AttackerId, out var atk) && FindFighter(cue.AttackerId) is { } attacker)
                    atk.OnAttackStarted(cue.SkillId, WorldPos(attacker), StrikeDir(attacker, cue), SkillSeconds(cue.SkillId));
                break;
            case PresentationCueKind.HitSpark:
                if (_views.TryGetValue(cue.VictimId, out var vSpark)) vSpark.OnHitSpark(CuePoint(cue));
                break;
            case PresentationCueKind.HitFlash:
                if (_views.TryGetValue(cue.VictimId, out var vFlash)) vFlash.OnHitFlash(CuePoint(cue));
                // 飘伤害数字（第三层命中反馈: 顿帧 → 火花 → 数字）
                DamagePopup.Spawn(_matchRoot, CuePoint(cue), cue.Damage, isCrit: cue.Damage >= 2000);
                if (cue.VictimId == EnemyId) { _statDealt += (int)cue.Damage; _statHits++; }
                else if (cue.VictimId == LocalPlayerId) _statTaken += (int)cue.Damage;
                break;
            case PresentationCueKind.GuardSpark:
                if (_views.TryGetValue(cue.VictimId, out var vGuard)) vGuard.OnGuard(CuePoint(cue));
                break;
            case PresentationCueKind.ParrySpark:
                if (_views.TryGetValue(cue.VictimId, out var vParry)) vParry.OnParry(CuePoint(cue));
                break;
            case PresentationCueKind.Launch:
                if (_views.TryGetValue(cue.VictimId, out var vLaunch)) vLaunch.OnLaunched();
                break;
            case PresentationCueKind.Down:
                if (_views.TryGetValue(cue.VictimId, out var vDown)) vDown.OnDown();
                break;
            case PresentationCueKind.Death:
                if (_views.TryGetValue(cue.VictimId, out var vDead)) vDead.OnDeath();
                break;
            case PresentationCueKind.AttackEnded:
                // 攻击生命周期结束（自然结束/取消）——收束尾迹
                if (_views.TryGetValue(cue.AttackerId, out var vEnd)) vEnd.OnAttackEnded(cue.SkillId);
                break;
            // ---------- 荣耀高光：此前 Sim 有事件、画面什么都没有 ----------
            case PresentationCueKind.SkillCancelled:
                if (_views.TryGetValue(cue.AttackerId, out var vCancel))
                {
                    vCancel.OnCancel();
                    vCancel.OnAttackEnded(cue.SkillId);      // 取消同样要收尾迹
                }
                break;
            case PresentationCueKind.Relaunched:
                // 二段浮空 = 浮空连招节拍：受击者身上爆一次亮火花 + 屏上打"连击"
                if (_views.TryGetValue(cue.VictimId, out var vRel)) vRel.OnRelaunched(CuePoint(cue));
                DamagePopup.SpawnText(_matchRoot, CuePoint(cue), "浮空追击", new Color(1f, 0.78f, 0.35f), 40);
                break;
            case PresentationCueKind.Ukemi:
                if (_views.TryGetValue(cue.VictimId, out var vUkemi))
                {
                    vUkemi.OnUkemi(CuePoint(cue));
                    DamagePopup.SpawnText(_matchRoot, CuePoint(cue), "受身", new Color(0.65f, 0.95f, 1f), 40);
                }
                break;
            case PresentationCueKind.WallBounced:
                if (_views.TryGetValue(cue.VictimId, out var vWall)) vWall.OnWallBounce(CuePoint(cue));
                DamagePopup.SpawnText(_matchRoot, CuePoint(cue), "撞墙", new Color(1f, 0.62f, 0.30f), 40);
                break;
            case PresentationCueKind.GuardBroken:
                if (_views.TryGetValue(cue.VictimId, out var vGb)) vGb.OnGuardBroken(CuePoint(cue));
                DamagePopup.SpawnText(_matchRoot, CuePoint(cue), "破防", new Color(1f, 0.90f, 0.35f), 48);
                break;
            case PresentationCueKind.BasicStep:
                // cue.Value = skills.csv 的 ChainN（第 N 段）——连段在视觉上分段
                if (_views.TryGetValue(cue.AttackerId, out var vStep)) vStep.OnBasicStep((byte)cue.Value);
                break;
            case PresentationCueKind.GetupDone:
                if (_views.TryGetValue(cue.VictimId, out var vGetup)) vGetup.OnGetupDone();
                break;
            case PresentationCueKind.Interrupt:
                // 注: Sim 侧 Interrupted 载荷是 AttackerId=被中断的施法者（字段名与语义不符，
                // 属既有 Sim 契约——不改，只按实际语义消费）——因此这里收的是它的尾迹。
                if (_views.TryGetValue(cue.AttackerId, out var vInt)) vInt.OnAttackEnded(cue.SkillId);
                break;
            case PresentationCueKind.MatchEnd:
                // 终局——所有尾迹立即清空（不留结算画面残影）
                foreach (var v in _views.Values) v.OnMatchEnd();
                break;
            case PresentationCueKind.Knockback:
                // 表现由 Sync 状态色覆盖（无独立节点动作）
                break;
        }
    }

    private FighterStateData? FindFighter(int id)
    {
        if (_world is null) return null;
        foreach (var f in _world.Fighters)
            if (f.Id == id) return f;
        return null;
    }

    private static Godot.Vector3 WorldPos(FighterStateData f) => new(
        (float)(f.PosX.Raw / 65536.0),
        (float)(f.PosY.Raw / 65536.0),
        (float)(f.PosZ.Raw / 65536.0));

    private static Godot.Vector3 CuePoint(PresentationCue cue) => new(cue.PosX, cue.PosY, cue.PosZ);

    /// 打击朝向: 命中点优先（真实接触方向）；技能起手无接触点 → 指向受击者；兜底 +Z。
    private Godot.Vector3 StrikeDir(FighterStateData attacker, PresentationCue cue)
    {
        var from = WorldPos(attacker);
        Godot.Vector3 to;
        if (cue.PosX != 0 || cue.PosZ != 0)
            to = new Godot.Vector3(cue.PosX, 0, cue.PosZ);
        else if (FindFighter(cue.VictimId) is { } victim)
            to = WorldPos(victim);
        else
            return FallbackDir;
        var d = to - from;
        d.Y = 0;
        return d.LengthSquared() < 0.0001f ? FallbackDir : d.Normalized();
    }
}
