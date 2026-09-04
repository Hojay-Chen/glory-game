using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Core;
using Arena.Core.Collision;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// Vertical Slice 主入口（ADR-0009 §5 场景树落地）:
//   装配（数据编译→SimWorld→地形→Fighters→AI）→ 60Hz 权威步进 → 视觉/HUD 同步。
// 单机同进程权威（ADR-0004 网络后续接入）；AI 同权产 Command（ADR-0007 §1）。
// 环境变量 ARENA_AUTOPILOT=1 → 双 AI 自动对局（headless 冒烟/回归用）。
namespace Arena.Client;

public partial class MatchRoot : Node
{
    private SimWorld? _world;
    private RuntimeCatalog? _catalog;
    private int _tick;
    private bool _matchOver;
    private string _matchResult = "";
    private bool _autopilot;

    private readonly Dictionary<int, FighterView> _views = new();
    private Camera3D? _camera;
    private Hud? _hud;
    private uint _localPlayerId;

    private const int LocalPlayerId = 0;

    public override void _Ready()
    {
        _autopilot = Godot.OS.GetEnvironment("ARENA_AUTOPILOT") == "1";
        var root = FindRepoRoot();

        // 数据编译（ADR-0002 九段管线）
        var compiler = new DataCompiler();
        var (result, catalog) = compiler.CompileWithCatalog(
            Path.Combine(root, "docs/skill-spec/skills.csv"),
            Path.Combine(root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(root, "docs/balance-sheet/class-base.csv"));
        if (catalog is null)
            throw new InvalidOperationException($"data compile failed: {result.Blockers.Count} blockers");
        _catalog = catalog;

        // SimWorld 装配
        var world = new SimWorld(0x5EED_0001L, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) world.AddSkill(sk);

        // 地形（arena.csv → TerrainBody + 视觉网格）
        var arenaObjects = ArenaDefParser.Parse(Path.Combine(root, "docs/balance-sheet/arena.csv"));
        var terrain = ArenaDefParser.BuildTerrain(arenaObjects);
        foreach (var body in terrain) world.AddTerrain(body);
        var builder = new ArenaBuilder();
        builder.Build(this, arenaObjects);

        // 出场位（spawn 点从 arena.csv 取前两个；缺省对角）
        var spawns = arenaObjects.Where(o => o.Kind == "spawn").ToList();
        var (x0, z0, x1, z1) = PickSpawns(spawns);
        _spawnMeshRoot(x0, z0, x1, z1);

        world.AddFighter(LocalPlayerId, "BLA", Fixed.FromRaw(x0), Fixed.FromRaw(z0), team: 0);
        world.AddFighter(1, "BLA", Fixed.FromRaw(x1), Fixed.FromRaw(z1), team: 1);
        world.SealWorld();
        _world = world;

        // 视觉
        _camera = CameraFactory.CreateFollowCamera();
        AddChild(_camera);
        foreach (var f in world.Fighters)
        {
            var view = new FighterView(f.Id, f.Team);
            _views[f.Id] = view;
            AddChild(view);
        }

        // HUD
        _hud = new Hud();
        AddChild(_hud);

    }

    private static (long, long, long, long) PickSpawns(List<ArenaDefParser.ArenaObject> spawns)
    {
        if (spawns.Count >= 2)
            return (Q(spawns[0].X), Q(spawns[0].Z), Q(spawns[1].X), Q(spawns[1].Z));
        return (0, -8 << 16, 0, 8 << 16);   // 缺省对角（Q32.16）
    }

    private static long Q(decimal m) => (long)(m * 65536m);

    private void _spawnMeshRoot(long x0, long z0, long x1, long z1)
    {
        // 出生点标记（视觉——magenta 柱）
        foreach (var (x, z, col) in new[] { (x0, z0, new Color(0.2f, 0.4f, 1f)), (x1, z1, new Color(1f, 0.3f, 0.3f)) })
        {
            var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.1f } };
            var mat = new StandardMaterial3D { AlbedoColor = col };
            mi.MaterialOverride = mat;
            mi.Position = new Godot.Vector3((float)((double)x / 65536.0), 0.06f, (float)((double)z / 65536.0));
            AddChild(mi);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_world is null || _matchOver) return;
        _tick++;

        // 指令收集：本地玩家真键位（AI 对手经 Headless MatchDriver 验证——ADR-0007 同权）
        var cmds = new List<Command>(8);
        cmds.AddRange(InputTranslator.Collect(LocalPlayerId, _world, _catalog!));

        // 权威步进（单机同进程；ADR-0009 §2 Tick 循环）
        _world.Step(_tick, cmds.ToArray());

        // 视觉/HUD 同步
        foreach (var f in _world.Fighters)
            if (_views.TryGetValue(f.Id, out var view)) view.Sync(f);
        _hud?.Sync(_world, _tick);

        // 相机跟随（两战斗员中点）
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
            if (!alive0 || !alive1 || _tick >= 3600)
            {
                _matchOver = true;
                _matchResult = !alive0 && !alive1 ? "DRAW" : alive0 ? "BLUE WIN" : "RED WIN";
                _hud?.ShowResult(_matchResult, _tick);
                if (_autopilot)
                {
                    var hits = _world.Events.All.Count(e => e.Kind == EventKind.Hit);
                    Console.WriteLine($"[VS] match over t={_tick} result={_matchResult} hits={hits} " +
                        $"hp0={_world.Fighters[0].Hp} hp1={_world.Fighters[1].Hp}");
                    GetTree().Quit(0);
                }
            }
        }

        // autopilot 无渲染时长保险
        if (_autopilot && _tick > 4000)
            GetTree().Quit();
    }

    private static string FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
