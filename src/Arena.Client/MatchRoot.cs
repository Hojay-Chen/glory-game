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
using Godot;

// PRODUCTION - Arena.Client
// Vertical Slice 主入口（ADR-0009 §5 场景树落地）:
//   StartMatch 装配（数据编译→SimWorld→地形→Fighters→AI）→ 60Hz 权威步进 → 视觉/HUD/相机同步
//   → 胜负判定 → R 重开（RestartMatch: 完整重装配，零状态残留）。
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

    private const int LocalPlayerId = 0;
    private const int EnemyId = 1;
    private const long MatchLengthTicks = 3600;

    public override void _Ready()
    {
        _autopilot = Godot.OS.GetEnvironment("ARENA_AUTOPILOT") == "1";
        AddChild(_envRoot);
        AddChild(_matchRoot);

        // 静态环境只建一次（光照/建筑网格——与对局状态无关）
        var builder = new ArenaBuilder();
        builder.BuildEnvironment(_envRoot);

        _hud = new Hud();
        AddChild(_hud);
        _camera = CameraFactory.CreateFollowCamera();
        AddChild(_camera);

        StartMatch();
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
        var setup = MatchAssembler.Assemble(0x5EED_0001L, FindRepoRoot());
        _setup = setup;
        _world = setup.World;
        _catalog = setup.Catalog;
        _tick = 0;
        _matchOver = false;

        // 出生点标记
        _spawnMarker(setup.Spawn0.X, setup.Spawn0.Z, new Color(0.2f, 0.4f, 1f));
        _spawnMarker(setup.Spawn1.X, setup.Spawn1.Z, new Color(1f, 0.3f, 0.3f));

        // FighterView
        foreach (var f in _world.Fighters)
        {
            var view = new FighterView(f.Id, f.Team);
            _views[f.Id] = view;
            _matchRoot.AddChild(view);
        }

        // 输入映射（Press/Held/Release 状态自持——随 Restart 重建归零）
        _inputMapper = InputTranslator.Create(LocalPlayerId, _catalog);

        // AI 对手接入（同权 Command 流——ADR-0007 §1；Bot 实现与 Headless MatchDriver 共用）
        _bot = new AiBot(EnemyId, LocalPlayerId, seed: 0xA1_0001, aggression: 0.55);
        if (_autopilot)
            _botP0 = new AiBot(LocalPlayerId, EnemyId, seed: 0xA2_0002, aggression: 0.7);   // autopilot: 玩家位 AI

        _hud?.Reset();
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
        if (_restartCooldown > 0) _restartCooldown--;
        if (_world is null || _matchOver)
        {
            // 胜负已分: R 重开（Press——冷却 60T 防按住连触发）
            if (_restartCooldown > 0) { return; }
            if (Input.IsKeyPressed(Key.R))
            {
                _restartCooldown = 60;
                RestartMatch();
            }
            return;
        }
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
            cmds.AddRange(_inputMapper!.Collect(InputTranslator.Bindings(_catalog!)));
            if (_bot is { } bot)
                cmds.AddRange(bot.Produce(_tick, _world, _catalog!));
        }

        // 权威步进（单机同进程；ADR-0009 §2）
        _world.Step(_tick, cmds.ToArray());

        // 视觉/HUD 同步
        foreach (var f in _world.Fighters)
            if (_views.TryGetValue(f.Id, out var view)) view.Sync(f);
        _hud?.Sync(_world, _tick);

        if (_camera is not null && _world.Fighters.Count >= 2)
        {
            var a = _world.Fighters[0];
            var b = _world.Fighters[1];
            var mid = new Godot.Vector3(
                (float)((a.PosX.Raw + b.PosX.Raw) / 131072.0),
                0,
                (float)((a.PosZ.Raw + b.PosZ.Raw) / 131072.0));
            var spread = (float)(Math.Abs(a.PosX.Raw - b.PosX.Raw) + Math.Abs(a.PosZ.Raw - b.PosZ.Raw)) / 65536f;
            CameraFactory.Follow(_camera, mid, spread);
        }

        // 胜负判定
        if (!_matchOver && _tick > 60)
        {
            var alive0 = _world.Fighters.Any(f => f.Team == 0 && f.State != FighterState.Dead);
            var alive1 = _world.Fighters.Any(f => f.Team == 1 && f.State != FighterState.Dead);
            if (!alive0 || !alive1 || _tick >= MatchLengthTicks)
            {
                _matchOver = true;
                var result = !alive0 && !alive1 ? "DRAW" : alive0 ? "BLUE WIN" : "RED WIN";
                _hud?.ShowResult(result, _tick);
                if (_autopilot)
                {
                    var hits = _world.Events.All.Count(e => e.Kind == EventKind.Hit);
                    Console.WriteLine($"[VS] match over t={_tick} result={result} hits={hits} " +
                        $"hp0={_world.Fighters[0].Hp} hp1={_world.Fighters[1].Hp}");
                    GetTree().Quit(0);
                }
            }
        }

        if (_autopilot && _tick > 4200)
            GetTree().Quit();
    }
}
