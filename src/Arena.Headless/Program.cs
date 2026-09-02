using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using System.IO;
using Arena.Core.Collision;
using Arena.Core.Sim;
using Arena.Infra.Data;

// PRODUCTION - Arena.Headless
// ADR-0009 §4: 专属服务器控制台（纯 .NET，零 Godot）。Phase 4: 兼作战斗链路诊断器。
namespace Arena.Headless;

public static class Program
{
    public static void Main(string[] args)
    {
        var root = FindRoot();
        var compiler = new DataCompiler();
        var (result, catalog) = compiler.CompileWithCatalog(
            Path.Combine(root, "docs/skill-spec/skills.csv"),
            Path.Combine(root, "docs/weapon-spec/weapons.csv"),
            Path.Combine(root, "docs/balance-sheet/class-base.csv"));
        // GS01 replica
        var wg = new SimWorld(0x5EED, catalog!.DataVersionHash);
        foreach (var sk in catalog.Skills) wg.AddSkill(sk);
        wg.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        wg.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(2), team: 1);
        wg.SealWorld();
        for (int t = 1; t <= 10; t++)
        {
            wg.Step(t, new[] { new Command(0, CmdKind.Move, 0, 0, 0, t) });
            if (t <= 3) Console.WriteLine("[gs01] t" + t + " state=" + wg.Fighters[0].State + " velZ=" + wg.Fighters[0].VelZ.Raw + " z=" + wg.Fighters[0].PosZ.Raw + " grabbedBy=" + wg.Fighters[0].GrabbedBy + " staminamax=" + wg.Fighters[0].Stamina);
        }

Console.WriteLine("[wep] classes=" + string.Join(",", catalog.WeaponsByClass.Keys.OrderBy(x => x).Take(6)) + " bmg=" + catalog.WeaponsByClass["BMG"].Count + " ids=" + string.Join(",", catalog.WeaponsByClass["BMG"].Select(x => x.WeaponId + "/" + x.AtkMod)));
        foreach (var u in catalog.UnroutedStatuses.Where(x => x.Contains("trait"))) Console.WriteLine("  [wt] " + u);

Console.WriteLine("[pilot] coverage compute");
        {
            var routed = new List<string>(); var partial = new List<string>();
            foreach (var d in catalog.Skills)
            {
                var raw = System.IO.File.ReadAllLines(System.IO.Path.Combine(root, "docs/skill-spec/skills.csv")).Skip(1)
                    .Select(l => l.Split(',')).FirstOrDefault(c => c.Length > 0 && c[0] == d.SkillId);
                var sp = raw?[29] ?? ""; var st = raw?[21] ?? ""; var hb = (raw?[12] ?? "").Split(':')[0];
                bool isPartial = false;
                foreach (var kw in new[] { "分身", "假身", "操纵", "附身", "携带", "形态三选", "变弹", "随机", "干扰", "删除", "召唤物", "镜像", "替换", "伪装" })
                    if (sp.Contains(kw)) isPartial = true;
                if (st.Contains("全异常") || st.Contains("冻结值") || st.Contains("震地") || st.Contains("拖拽")
                    || st.Contains("拉拽") || st.Contains("对敌") || st.Contains("截脉") || st.Contains("封印")
                    || st.Contains("嘲讽") || st.Contains("束缚") || st.Contains("藤蔓") || st.Contains("感电")) isPartial = true;
                if (hb is "unit" or "deploy" or "ally" or "wall" or "portal") isPartial = true;
                if (sp.Contains("陷阱") || sp.Contains("炮台") || sp.Contains("部署")) isPartial = true;
                if (isPartial) partial.Add(d.SkillId + "|" + hb + "|" + sp); else routed.Add(d.SkillId);
            }
            Console.WriteLine($"[pilot] routed={routed.Count} partial={partial.Count} total={routed.Count + partial.Count}");
            foreach (var g in partial.Select(x => x.Split('|')[1]).GroupBy(x => x).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  [pilot-partial] {g.Key}: {g.Count()}");
        }

        Console.WriteLine($"catalog={catalog!.Count} blockers={result.Blockers.Count} unroutedStatus={catalog.UnroutedStatuses.Count} unroutedHitbox={catalog.UnroutedHitboxes.Count}");
        foreach (var g in catalog.UnroutedStatuses.GroupBy(x => x.Split(':')[^1].Split(':').LastOrDefault() ?? x).OrderBy(g => -g.Count()).Take(18))
            Console.WriteLine($"  [us] {g.Count()}× {g.First()}");
        foreach (var g in catalog.UnroutedHitboxes.GroupBy(x => x.Split(':')[^1]).OrderBy(g => -g.Count()))
            Console.WriteLine($"  [uh] {g.Count()}× {g.First()}");

        // --- 诊断: 巴雷特 80m/s ---
        var w4 = new SimWorld(0x5EED, catalog.DataVersionHash);
        foreach (var s in catalog.Skills) w4.AddSkill(s);
        w4.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w4.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(2), team: 1);
        w4.AddFighter(2, "SRP", Fixed.FromInt(0), Fixed.FromInt(-15), team: 0);
        w4.SealWorld();
        w4.Fighters[2].HeadingQuantum = 0;
        w4.Step(1, new[] { new Command(2, CmdKind.Skill, catalog.IdMap["SRP_T4_001"], 0, 0, 1) });
        for (int t = 2; t <= 120; t++) w4.Step(t, Array.Empty<Command>());
        foreach (var e in w4.Events.All.Where(e => e.Kind is EventKind.Hit or EventKind.Whiff or EventKind.SkillCast
                 or EventKind.ProjectileSpawned or EventKind.ProjectileDestroyed))
            Console.WriteLine($"  W4 t{e.Tick} {e.Kind} skill={e.SkillId} vic={e.VictimId} dmg={e.DamageRaw} region={e.HitRegion}");

        // --- 诊断: 落花掌撞墙 ---
        var w5 = new SimWorld(0x5EED, catalog.DataVersionHash);
        foreach (var s in catalog.Skills) w5.AddSkill(s);
        w5.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w5.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(41), team: 1);
        w5.SealWorld();
        w5.Fighters[0].PosZ = Fixed.FromInt(38);
        w5.Step(1, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T1_004"], 0, 0, 1) });
        for (int t = 2; t <= 40; t++) w5.Step(t, Array.Empty<Command>());
        foreach (var e in w5.Events.All.Where(e => e.Kind is EventKind.Hit or EventKind.Whiff or EventKind.Knockback
                 or EventKind.WallBounced))
            Console.WriteLine($"  W5 t{e.Tick} {e.Kind} vic={e.VictimId} dmg={e.DamageRaw} nx={e.HitNormalX} nz={e.HitNormalZ}");
        Console.WriteLine($"  W5 victim z={w5.Fighters[1].PosZ.Raw} state={w5.Fighters[1].State}");

        // --- 诊断: GS15 取消拒绝时序 ---
        var w6 = new SimWorld(0x5EED, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) w6.AddSkill(sk);
        w6.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w6.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(5), team: 1);
        w6.SealWorld();
        w6.Step(1, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T1_002"], 0, 0, 1) });
        for (int t = 2; t <= 20; t++) w6.Step(t, Array.Empty<Command>());
        w6.Step(21, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T1_001"], 0, 0, 21) });
        for (int t = 22; t <= 40; t++) w6.Step(t, Array.Empty<Command>());
        foreach (var e in w6.Events.All.Where(e => e.Tick >= 25))
            Console.WriteLine($"  W6 t{e.Tick} seq{e.SeqInTick} {e.Kind} skill={e.SkillId}");

        // --- 诊断: GS07 倒地保护探针 ---
        var w7 = new SimWorld(0x5EED, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) w7.AddSkill(sk);
        w7.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w7.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(2), team: 1);
        w7.SealWorld();
        w7.Step(1, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T2_001"], 0, 0, 1) });
        for (int t = 2; t <= 20; t++) w7.Step(t, Array.Empty<Command>());
        Console.WriteLine($"  W7 t20 victim={w7.Fighters[1].State} caster={w7.Fighters[0].State} casterUid={w7.Fighters[0].ActiveSkillUid}");
        Console.WriteLine($"  W7 ids: T2_001={catalog.IdMap["BMG_T2_001"]} T1_002={catalog.IdMap["BMG_T1_002"]} T1_001={catalog.IdMap["BMG_T1_001"]}");
        var exe = w7.GetExecution(w7.Fighters[0].ActiveSkillUid);
        if (exe is not null) Console.WriteLine($"  W7 exec: offset={exe.CurrentOffset} total={exe.TotalTicks} su={exe.Def!.StartupTicks} ac={exe.Def.ActiveTicks} rc={exe.Def.RecoveryTicks} skill={exe.SkillRuntimeId}");
        w7.Step(40, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T1_002"], 0, 0, 40) });
        Console.WriteLine($"  W7 t40 caster={w7.Fighters[0].State} castOk={w7.Fighters[0].ActiveSkillUid != 0} mp={w7.Fighters[0].Mp}");
        for (int t = 41; t <= 60; t++) w7.Step(t, Array.Empty<Command>());
        foreach (var e in w7.Events.All.Where(e => e.Tick >= 40))
            Console.WriteLine($"  W7 t{e.Tick} {e.Kind} skill={e.SkillId} vic={e.VictimId} reason={e.ReasonByte}");

        // --- 诊断: 伤害链分步（巴雷特对 BLA） ---
        var defB = catalog.Get("SRP_T4_001")!;
        long m1 = DeterministicMath.MulShift(defB.DamageMultQ, 1100);
        long df = DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800);
        long m2 = DeterministicMath.MulShift(m1, df);
        long m3 = DeterministicMath.MulShift(m2, defB.HeadMultQ);
        Console.WriteLine($"  [chain] mult={defB.DamageMultQ} atk→{m1} ×def({df})→{m2} ×head({defB.HeadMultQ})→{m3}");

        // --- 诊断: GS08 圆舞棍二段命中倒地 ---
        var w8 = new SimWorld(0x5EED, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) w8.AddSkill(sk);
        w8.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w8.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(2), team: 1);
        w8.SealWorld();
        w8.Step(1, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T2_001"], 0, 0, 1) });
        for (int t = 2; t <= 20; t++) w8.Step(t, Array.Empty<Command>());
        // 复现 GS07/GS08 连续步进 + 30 处取消重施
        for (int t = 21; t <= 29; t++) w8.Step(t, Array.Empty<Command>());
        w8.Step(30, new[] { new Command(0, CmdKind.Skill, catalog.IdMap["BMG_T2_001"], 0, 0, 30) });
        for (int t = 31; t <= 60; t++) w8.Step(t, Array.Empty<Command>());
        foreach (var e in w8.Events.All.Where(e => e.Kind is EventKind.Hit or EventKind.Whiff or EventKind.Cancelled or EventKind.SkillCast or EventKind.ForcedDown))
            Console.WriteLine($"  W8 t{e.Tick} {e.Kind} skill={e.SkillId} vic={e.VictimId} dmg={e.DamageRaw} sweep={e.SweepFlag}");
        Console.WriteLine($"  W8 victim={w8.Fighters[1].State} downTicks={w8.Fighters[1].DownTicks}");

        // --- 诊断: oracle mismatch case ---
        var sec = ConvexRegion.Sector(0, 0, 30000, 120, 144179);
        bool a = SweepSolver.SweepRegion(sec, -74592, -184175, 89811, 126625, 29491, out var ta, out var tao2, out _, out _);
        bool o = SweepSolver.SweepRegionOracle(sec, -74592, -184175, 89811, 126625, 29491, out var to);
        Console.WriteLine($"  [oracle] analytic={a} [{ta},{tao2}] oracle={o} to={to}");
        Console.WriteLine($"  [oracle] contains(end)= {sec.Contains(15219, -57550)}");
        for (int i = 0; i <= 16; i++)
        {
            long t = (long)((double)i / 16 * Fixed.ONE);
            long px = -74592 + DeterministicMath.MulShift(89811, t) / Fixed.ONE;
            long pz = -184175 + DeterministicMath.MulShift(126625, t) / Fixed.ONE;
            Console.WriteLine($"  [samp] t={t} p=({px},{pz}) in={sec.Contains(px, pz)}");
        }

        // --- 诊断: GS09 巴雷特（含地形 + post-seal fighter） ---
        var w9 = new SimWorld(0x5EED, catalog.DataVersionHash);
        foreach (var sk in catalog.Skills) w9.AddSkill(sk);
        w9.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w9.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(2), team: 1);
        foreach (var tb in ArenaDefParser.BuildTerrain(ArenaDefParser.Parse(System.IO.Path.Combine(root, "docs/balance-sheet/arena.csv")))) w9.AddTerrain(tb);
        w9.SealWorld();
        w9.AddFighter(2, "SRP", Fixed.FromInt(0), Fixed.FromInt(-15), team: 0);
        w9.Fighters[2].HeadingQuantum = 0;
        w9.Step(1, new[] { new Command(2, CmdKind.Skill, catalog.IdMap["SRP_T4_001"], 0, 0, 1) });
        for (int t = 2; t <= 120; t++) w9.Step(t, Array.Empty<Command>());
        foreach (var e in w9.Events.All.Where(e => e.Kind is EventKind.Hit or EventKind.Whiff or EventKind.SkillCast
                 or EventKind.ProjectileSpawned or EventKind.ProjectileDestroyed))
            Console.WriteLine($"  W9 t{e.Tick} {e.Kind} skill={e.SkillId} vic={e.VictimId} dmg={e.DamageRaw} region={e.HitRegion}");
        Console.WriteLine($"  W9 diag: backstab={HitResolve.IsBackstab(w9.Fighters[2], w9.Fighters[1])} vicHead={w9.Fighters[1].HeadingQuantum} atkHead={w9.Fighters[2].HeadingQuantum} vicZ={w9.Fighters[1].PosZ.Raw} atkZ={w9.Fighters[2].PosZ.Raw}");
        var d2 = catalog.Get("SRP_T4_001")!;
        long c1 = DeterministicMath.MulShift(d2.DamageMultQ, 1100);
        long c2 = DeterministicMath.MulShift(c1, DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800));
        long c3 = DeterministicMath.MulShift(c2, d2.HeadMultQ);
        long c4 = DeterministicMath.MulShift(c3, Arena.Core.Calc.DeterministicTables.Modifiers.BackstabX120);
        Console.WriteLine($"  W9 chain: {c1} → {c2} → head {c3} → bs {c4}");

        // --- 诊断: 扫地技选取 ---
        var sweep = catalog.Skills.FirstOrDefault(s => s.Sweep && s.Geo.Kind != GeoKind.None && !s.IsProjectile && s.Type != "grab");
        Console.WriteLine($"  [sweep] {sweep?.SkillId} mult={sweep?.DamageMultQ / 65536.0} head={sweep?.HeadMultQ} cls={sweep?.ClassId} geo={sweep?.Geo.Kind}");
    }

    private static string FindRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "arena.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? ".";
    }
}
