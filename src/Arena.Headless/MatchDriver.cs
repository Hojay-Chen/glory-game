using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Data;

// PRODUCTION - Arena.Headless
// VS-Headless: 完整可玩战斗驱动（ADR-0007 AI 同权 + ADR-0009 权威 Tick 循环）。
// 双 AI 对局 3000T：全机制、KO/限时判定、逐 tick 战报统计。
// 本驱动是「玩家能否完整进行一场真实战斗」的自动化验证载体（表现层在 Arena.Client）。
namespace Arena.Headless;

public static class MatchDriver
{
    public static int Run(string[] args)
    {
        var seed = args.Length > 0 && long.TryParse(args[0], out var s) ? s : 0x5EED_0001L;
        const int TotalTicks = 3000;
        const string PlayerClass = "BLA";
        const string EnemyClass = "BLA";

        var root = FindRoot();
        var compiler = new DataCompiler();
        var (result, catalog) = compiler.CompileWithCatalog(
            Path.Combine(root, "docs/skill-spec/skills.csv"),
            Path.Combine(root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(root, "docs/balance-sheet/class-base.csv"));
        if (catalog is null)
        {
            Console.WriteLine("[VS-FAIL] data compile blockers=" + result.Blockers.Count);
            return 2;
        }

        var world = new SimWorld(seed, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) world.AddSkill(sk);

        // 地形装配（arena.csv——结界墙/平台/掩体）
        var arenaObjects = ArenaDefParser.Parse(Path.Combine(root, "docs/balance-sheet/arena.csv"));
        foreach (var body in ArenaDefParser.BuildTerrain(arenaObjects))
            world.AddTerrain(body);

        // 出场位
        var spawns = arenaObjects.Where(o => o.Kind == "spawn").ToList();
        var (x0, z0, x1, z1) = spawns.Count >= 2
            ? (Q(spawns[0].X), Q(spawns[0].Z), Q(spawns[1].X), Q(spawns[1].Z))
            : (0, -8 << 16, 0, 8 << 16);

        world.AddFighter(0, PlayerClass, Fixed.FromRaw(x0), Fixed.FromRaw(z0), team: 0);
        world.AddFighter(1, EnemyClass, Fixed.FromRaw(x1), Fixed.FromRaw(z1), team: 1);
        world.SealWorld();

        // AI 同权（ADR-0007 §1）: 双 bot 经同一条 Command 流
        var bots = new[]
        {
            new AiBot(0, 1, seed: 0xB07_01),
            new AiBot(1, 0, seed: 0xB07_02),
        };

        var kindCount = new Dictionary<EventKind, int>();
        var dmgByTick = new List<(int tick, long total)>();
        int killTicks = -1;

        for (int t = 1; t <= TotalTicks; t++)
        {
            var cmds = bots.SelectMany(b => b.Produce(t, world, catalog)).ToArray();
            world.Step(t, cmds);

            foreach (var e in world.Events.All)
                kindCount[e.Kind] = kindCount.GetValueOrDefault(e.Kind) + 1;

            var dead = world.Fighters.FirstOrDefault(f => f.State == FighterState.Dead);
            if (dead is not null && killTicks < 0)
            {
                killTicks = t;
                Console.WriteLine($"[VS] KO at t={t}: {dead.ClassId} (team {dead.Team}) down — " +
                    $"survivor HP {world.Fighters.First(f => f.State != FighterState.Dead).Hp}");
            }
        }

        // 战报
        var alive = world.Fighters.Where(f => f.State != FighterState.Dead).ToList();
        Console.WriteLine("=== VS MATCH REPORT ===");
        Console.WriteLine($"seed={seed} ticks={TotalTicks} terrain={world.Collision.Terrain.Count} bodies");
        Console.WriteLine($"result: {(alive.Count == 1 ? $"TEAM {alive[0].Team} WIN ({alive[0].ClassId})" : alive.Count == 2 ? "TIMEOUT DRAW" : "DRAW")}" +
            $" killAt={(killTicks > 0 ? killTicks.ToString() : "n/a")}");
        foreach (var f in world.Fighters)
            Console.WriteLine($"  F{f.Id} {f.ClassId} team{f.Team}: hp={f.Hp}/{f.HpMax} state={f.State}");
        Console.WriteLine("events: " + string.Join(", ", kindCount.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"total damage events: {kindCount.GetValueOrDefault(EventKind.Hit)}");

        // 可玩性判据: 真实战斗发生（命中+施法+浮空/倒地/死亡类事件齐备）
        bool battleHappened = kindCount.GetValueOrDefault(EventKind.Hit) > 10
            && kindCount.GetValueOrDefault(EventKind.SkillCast) > 5
            && (kindCount.GetValueOrDefault(EventKind.Launched) > 0 || killTicks > 0);
        Console.WriteLine(battleHappened ? "[VS-PASS] 真实战斗完整发生" : "[VS-FAIL] 战斗强度不足");
        return battleHappened ? 0 : 1;
    }

    private static long Q(decimal m) => (long)(m * 65536m);

    private static string FindRoot()
    {
        var dir = Environment.CurrentDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
