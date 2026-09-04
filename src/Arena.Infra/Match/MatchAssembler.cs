using System;
using System.IO;
using System.Linq;
using Arena.Core;
using System.Collections.Generic;

using Arena.Core.Collision;
using Arena.Core.Sim;
using Arena.Infra.Data;

// PRODUCTION - Arena.Infra
// Match 装配复用点（ADR-0009）: 数据编译 → SimWorld → 地形 → Fighters → 出场位。
// Godot MatchRoot（表现宿主）与 Headless MatchDriver（权威验证器）共用同一装配——
// 保证「用户运行的客户端」与「自动化验证」打的是同一份战斗。
namespace Arena.Infra.Match;

public sealed record MatchSetup(
    SimWorld World,
    RuntimeCatalog Catalog,
    List<ArenaDefParser.ArenaObject> ArenaObjects,
    (long X, long Z) Spawn0,
    (long X, long Z) Spawn1);

public static class MatchAssembler
{
    public static MatchSetup Assemble(long seed, string repoRoot,
        string playerClass = "BLA", string enemyClass = "BLA",
        int playerTeam = 0, int enemyTeam = 1)
    {
        var compiler = new DataCompiler();
        var (result, catalog) = compiler.CompileWithCatalog(
            Path.Combine(repoRoot, "docs/skill-spec/skills.csv"),
            Path.Combine(repoRoot, "docs/weapon-spec/weapons.csv"),
            Path.Combine(repoRoot, "docs/balance-sheet/class-base.csv"));
        if (catalog is null)
            throw new InvalidOperationException($"data compile failed: {result.Blockers.Count} blockers");

        var world = new SimWorld(seed, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) world.AddSkill(sk);

        var arenaObjects = ArenaDefParser.Parse(Path.Combine(repoRoot, "docs/balance-sheet/arena.csv"));
        foreach (var body in ArenaDefParser.BuildTerrain(arenaObjects))
            world.AddTerrain(body);

        var spawns = arenaObjects.Where(o => o.KindId == ArenaObjectKind.Spawn).ToList();
        var (x0, z0, x1, z1) = spawns.Count >= 2
            ? (Q(spawns[0].X), Q(spawns[0].Z), Q(spawns[1].X), Q(spawns[1].Z))
            : (0, -8 << 16, 0, 8 << 16);

        world.AddFighter(0, playerClass, Fixed.FromRaw(x0), Fixed.FromRaw(z0), team: (byte)playerTeam);
        world.AddFighter(1, enemyClass, Fixed.FromRaw(x1), Fixed.FromRaw(z1), team: (byte)enemyTeam);
        world.SealWorld();

        return new MatchSetup(world, catalog, arenaObjects, (x0, z0), (x1, z1));
    }

    private static long Q(decimal m) => (long)(m * 65536m);
}
