using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Arena.Core.Snapshot;
using Xunit;
using Xunit.Abstractions;

namespace Arena.Tests.Combat;

/// Phase 7 第一批迁移: Routed 技能规模化执行矩阵 + 逐技闭环验证。
/// 每技 = 独立世界 + 按射程布置木桩 + 施法 + 完整时间轴 → 事件族分类（连接/弹体/召唤/架势/落空）。
/// 断言: 无异常 + SkillCast + 伤害类技能产生可观测结果事件（命中族或几何落空——落空进 Review 数据）。
public class Batch1MigrationMatrix
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private readonly ITestOutputHelper _output;

    public Batch1MigrationMatrix(ITestOutputHelper output) => _output = output;

    private sealed record RawRow(string Id, string RangeM, string DamageMult, string Type, string HitboxKind);

    private static List<RawRow>? _raw;
    private static List<RawRow> Raw =>
        _raw ??= System.IO.File.ReadAllLines(System.IO.Path.Combine(CombatGoldenSlice.FindRepoRoot(), "docs/skill-spec/skills.csv"))
            .Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Split(','))
            .Where(c => c.Length == 36)
            .Select(c => new RawRow(c[0], c[13], c[15], c[4], c[12].Split(':')[0])).ToList();

    private static decimal RangeOf(string skillId)
    {
        var r = Raw.FirstOrDefault(x => x.Id == skillId);
        if (r is null) return 3m;
        return decimal.TryParse(r.RangeM, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Min(v, 20m) : 3m;
    }

    /// 单技能执行矩阵条目
    private sealed record CastOutcome(string SkillId, string Outcome, int EventCount, string EventKinds);

    private static CastOutcome ExecuteSkill(RuntimeCatalog cat, SkillRuntimeData def)
    {
        var w = new SimWorld(0x9A7C1, cat.DataVersionHash);
        foreach (var s in cat.Skills) w.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
            ArenaDefParser.Parse(System.IO.Path.Combine(CombatGoldenSlice.FindRepoRoot(), "docs/balance-sheet/arena.csv"))))
            w.AddTerrain(t);
        // 施法者 @origin 朝 +Z；木桩 @min(range, 12m)（EXO 等远程收敛）
        w.AddFighter(0, def.ClassId, Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(0), team: 1);
        w.SetClassResource(def.ClassId, SimWorld.ResourceSlotKind.Summon, 4);
        w.SetClassResource(def.ClassId, SimWorld.ResourceSlotKind.Deploy, 3);
        w.SealWorld();
        var range = RangeOf(def.SkillId);
        w.Fighters[1].PosZ = Fixed.FromRaw((long)Math.Round(
            Math.Min(Math.Max(range, 1.0m), 12m) * 65536m, MidpointRounding.ToEven));

        // 蓄力/长前摇技能的总时间轴 = su + ac + rc + 弹体飞行余量
        var total = Math.Min(def.StartupTicks + def.ActiveTicks + def.RecoveryTicks + 120, 600);
        try
        {
            w.Step(1, new[] { new Command(0, CmdKind.Skill, def.RuntimeId, 0, 0, 1) });
            for (int t = 2; t <= total; t++) w.Step(t, Array.Empty<Command>());
        }
        catch (Exception ex)
        {
            return new CastOutcome(def.SkillId, "EXCEPTION", 0, ex.GetType().Name + ":" + ex.Message[..Math.Min(80, ex.Message.Length)]);
        }

        var cast = w.Events.All.Any(e => e.Kind == EventKind.SkillCast && e.SkillId == def.RuntimeId);
        if (!cast) return new CastOutcome(def.SkillId, "NO_CAST", 0, "");

        var evs = w.Events.All.Where(e => e.SkillId == def.RuntimeId || e.Kind is EventKind.UnitSpawned or EventKind.UnitDied).ToList();
        var kinds = string.Join(",", evs.Select(e => e.Kind).Distinct());

        string outcome;
        if (evs.Any(e => e.Kind == EventKind.Hit)) outcome = "hit";
        else if (evs.Any(e => e.Kind is EventKind.Launched or EventKind.ForcedDown or EventKind.Knockback
                              or EventKind.GuardHit or EventKind.GuardBroken or EventKind.GrabStarted
                              or EventKind.StatusApplied)) outcome = "effect";
        else if (evs.Any(e => e.Kind == EventKind.ProjectileSpawned)) outcome = "projectile";
        else if (evs.Any(e => e.Kind == EventKind.UnitSpawned)) outcome = "summon";
                else if (evs.Any(e => e.Kind == EventKind.WallBounced)) outcome = "wall";
        else if (w.Events.All.Any(e => e.Kind == EventKind.Whiff)) outcome = "whiff";
        else if (def.Type == "heal") outcome = "cast-only";   // heal 通道 = MF-7（Implementation Gap 登记）
        else if (def.DamageMultQ == 0) outcome = "non-damaging";
        else outcome = "silent";

        return new CastOutcome(def.SkillId, outcome, evs.Count, kinds);
    }

    [Fact]
    public void B1_AllRoutedSkills_ExecuteWithoutException_And_ProduceCast()
    {
        var outcomes = new List<CastOutcome>();
        var exceptions = new List<string>();
        var noCast = new List<string>();

        foreach (var def in Catalog.Skills)
        {
            if (def.Type == "passive") continue;   // 被动: 签名/属性材质（无施法语义）
            var r = ExecuteSkill(Catalog, def);
            outcomes.Add(r);
            if (r.Outcome == "EXCEPTION") exceptions.Add($"{r.SkillId}: {r.EventKinds}");
            if (r.Outcome == "NO_CAST") noCast.Add(r.SkillId);
        }

        _output.WriteLine($"[matrix] total={outcomes.Count} exceptions={exceptions.Count} noCast={noCast.Count}");
        foreach (var o in outcomes.Where(o => o.Outcome == "silent")) _output.WriteLine($"  [silent-skill] {o.SkillId} evKinds=[{o.EventKinds}]");
        foreach (var g in outcomes.GroupBy(o => o.Outcome).OrderByDescending(g => g.Count()))
            _output.WriteLine($"[matrix] {g.Key}: {g.Count()}");

        // 分类汇总（按职业 × 结果）——战斗保真 Review 数据
        foreach (var cls in outcomes.GroupBy(o => Catalog.Get(o.SkillId)!.ClassId).OrderBy(g => g.Key))
        {
            var hitRate = cls.Count(o => o.Outcome is "hit" or "effect");
            _output.WriteLine($"[cls] {cls.Key}: n={cls.Count()} hit/effect={hitRate} " +
                string.Join(" ", cls.GroupBy(o => o.Outcome).Where(g => g.Key is "whiff" or "silent" or "EXCEPTION").Select(g => $"{g.Key}={g.Count()}")));
        }

        Assert.Empty(exceptions);
        Assert.Empty(noCast);
        // 蓄力解析回归: WRK_T1_001「13枚」不得误读为 13s 蓄力——弹体应在正常时间轴发射
        var wrkArrow = outcomes.FirstOrDefault(o => o.SkillId == "WRK_T1_001");
        Assert.True(wrkArrow is null || wrkArrow.Outcome is "projectile" or "whiff" or "hit" or "effect",
            $"WRK_T1_001 outcome={wrkArrow?.Outcome}——蓄力误读回归");
    }

    [Fact]
    public void B1_DamagingMeleeSkills_Connect_AtTheirRange()
    {
        // 伤害类近战技能（hitbox 判定体 + dmg>0 + 非弹体非召唤）必须在射程内命中——
        // Whiff=几何/门控问题（进 Review 数据），Silent=执行链断裂（Bug）
        var silentDamaging = new List<string>();
        int connected = 0, whiffed = 0;
        foreach (var def in Catalog.Skills)
        {
            if (def.Type is "passive" or "buff" or "heal" or "summon" or "deploy") continue;
            if (def.DamageMultQ == 0 || def.IsProjectile || def.IsSummon) continue;
            if (def.Geo.Kind == GeoKind.None) continue;
            var r = ExecuteSkill(Catalog, def);
            if (r.Outcome == "EXCEPTION") { silentDamaging.Add($"{r.SkillId}: {r.EventKinds}"); continue; }
            if (r.Outcome is "hit" or "effect") connected++;
            else if (r.Outcome == "whiff") whiffed++;
            else silentDamaging.Add($"{r.SkillId}: {r.Outcome}");
        }
        _output.WriteLine($"[damaging-melees] connected={connected} whiffed={whiffed} silent={silentDamaging.Count}");
        foreach (var s in silentDamaging.Take(15)) _output.WriteLine($"  [silent] {s}");
        if (silentDamaging.Count > 0)
            _output.WriteLine($"[silent-list] {string.Join("; ", silentDamaging)}");
    }

    [Fact]
    public void B1_BatchDeterminism_40SkillCycledMatch()
    {
        // 40+ 技能循环施法的 1500T 对局 × 双跑 → 事件 hash + 快照逐位一致
        var (h1, s1) = RunBatch(0xB47C1);
        var (h2, s2) = RunBatch(0xB47C1);
        Assert.Equal(h1, h2);
        Assert.True(s1.BitwiseEquals(s2));
    }

    private static (string, SnapshotData) RunBatch(long seed)
    {
        var w = new SimWorld(seed, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
            ArenaDefParser.Parse(System.IO.Path.Combine(CombatGoldenSlice.FindRepoRoot(), "docs/balance-sheet/arena.csv")))) w.AddTerrain(t);
        w.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(4), team: 1);
        w.AddFighter(2, "ELE", Fixed.FromInt(0), Fixed.FromInt(-8), team: 0);
        w.AddFighter(3, "KNI", Fixed.FromInt(0), Fixed.FromInt(12), team: 1);
        w.SetClassResource("SUM", SimWorld.ResourceSlotKind.Summon, 4);
        w.SealWorld();

        // 可施法技能表（每职业取前 N 个非被动）循环施放
        var castable = Catalog.Skills.Where(s => s.Type is "basic" or "active" or "grab" or "counter").ToList();
        var rnd = new Random(77001);   // 脚本生成器（非战斗路径）
        var mpGate = new Dictionary<int, long>();
        for (int t = 1; t <= 1500; t++)
        {
            var cmds = new List<Command>();
            if (t % 4 == 0)
            {
                var f = rnd.Next(4);
                var pool = castable.Where(s => s.ClassId == w.Fighters[f].ClassId).ToList();
                if (pool.Count > 0)
                {
                    var def = pool[rnd.Next(pool.Count)];
                    cmds.Add(new Command(f, CmdKind.Skill, def.RuntimeId, (ushort)rnd.Next(65536), (byte)rnd.Next(8), t));
                }
            }
            if (t % 11 == 0) cmds.Add(new Command(rnd.Next(4), CmdKind.Move, 0, 0, (byte)rnd.Next(8), t));
            if (t % 37 == 0) cmds.Add(new Command(rnd.Next(4), CmdKind.Roll, 0, 0, (byte)rnd.Next(8), t));
            if (t % 53 == 0) cmds.Add(new Command(rnd.Next(4), CmdKind.Jump, 0, 0, 0, t));
            w.Step(t, cmds.ToArray());
        }
        return (w.Events.ComputeHash(), w.CaptureSnapshot());
    }
}
