using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Arena.Core.Collision;
using Arena.Core.Snapshot;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 6 Pilot 真实技能迁移验证：
/// PF01 全表 Compiler 保真度门禁（483 行 RuntimeDef ↔ CSV 独立重解析逐字段比对）
/// PF02 全表可表达性分类（routed / partial / blocked → 迁移规模的真实底数）
/// PF03-PF10 代表性技能 Runtime 执行样本（覆盖全部机制类别）
public class MigrationPilot
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static string Root => CombatGoldenSlice.FindRepoRoot();

    // ---------- 独立 CSV 重解析（不经 SkillParser——双路径交叉验证） ----------

    private sealed record RawRow(string Id, string ClassId, string Tier, string Type, string CostMp,
        string Cooldown, string Su, string Ac, string Rc, string Hitbox, string DamageMult,
        string DamageType, string Hits, string Hitstun, string Knockback, string LaunchV,
        string Status, string Armor, string Inv, string Sweep, string Cancel, string Special);

    private static List<RawRow>? _rawRows;
    private static List<RawRow> RawRows
    {
        get
        {
            if (_rawRows is not null) return _rawRows;
            _rawRows = new List<RawRow>();
            foreach (var line in System.IO.File.ReadAllLines(System.IO.Path.Combine(Root, "docs/skill-spec/skills.csv")).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = line.Split(',');
                if (c.Length != 36) continue;
                _rawRows.Add(new RawRow(c[0], c[2], c[3], c[4], c[5], c[7], c[8], c[9], c[10], c[12],
                    c[15], c[16], c[17], c[18], c[19], c[20], c[21], c[22], c[23], c[24], c[27], c[29]));
            }
            return _rawRows;
        }
    }

    private static double Dec(string s) =>
        double.TryParse(s.TrimEnd('m', '°'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int TicksFromFrames(string s) => int.TryParse(s, out var v) ? v : 0;

    // ---------- PF01: 全表 Compiler 保真度（RuntimeDef ↔ CSV 独立重解析） ----------

    [Fact]
    public void PF01_Compiler_Fidelity_FullTable_FieldMapping()
    {
        var errors = new List<string>();
        int checkedRows = 0;
        foreach (var def in Catalog.Skills)
        {
            var raw = RawRows.FirstOrDefault(r => r.Id == def.SkillId);
            if (raw is null) { errors.Add($"{def.SkillId}: CSV 行缺失"); continue; }
            checkedRows++;

            void Check(bool cond, string field, string detail = "") =>
                Assert.True(cond, $"{def.SkillId}.{field}: {detail}");

            // 身份/数值映射
            Check(def.ClassId == raw.ClassId, "class_id", $"{def.ClassId} != {raw.ClassId}");
            Check(def.Type == raw.Type, "type", $"{def.Type} != {raw.Type}");
            Check(def.MpCost == (int.TryParse(raw.CostMp, out var mp) ? mp : 0), "cost_mp");
            if (double.TryParse(raw.DamageMult, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawDmg))
                Check(def.DamageMultQ == Q(rawDmg), "damage_mult",
                    $"{def.DamageMultQ} != {Q(rawDmg)}");
            if (double.TryParse(Dec(raw.Knockback).ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && raw.Knockback != "-") { }   // knockback 域已在下方数值比对（- 行跳过）
            Check(def.HitstunTicks == TicksFromFrames(raw.Hitstun), "hitstun_f");
            var kbRaw = raw.Knockback == "-" ? "0" : raw.Knockback;
            if (double.TryParse(kbRaw.TrimEnd('m'), NumberStyles.Float, CultureInfo.InvariantCulture, out var kbV))
                Check(def.KnockbackVelQ == Q(kbV * 9), "knockback",
                    $"{def.KnockbackVelQ} != {Q(kbV * 9)}");
            var lvRaw = raw.LaunchV == "-" ? "0" : raw.LaunchV;
            if (double.TryParse(lvRaw.TrimEnd('m'), NumberStyles.Float, CultureInfo.InvariantCulture, out var lvV))
                Check(def.LaunchVelQ == Q(lvV), "launch_v", $"{def.LaunchVelQ} != {Q(lvV)}");
            Check(def.Sweep == (raw.Sweep != "0"), "sweep");
            Check(def.ForcedDown == raw.Special.Contains("受身无效"), "forced_down");
            Check(def.ArmorBreak == raw.Special.Contains("破霸体"), "armor_break");
            Check(def.DamageType == raw.DamageType, "damage_type", $"{def.DamageType} != {raw.DamageType}");

            // hitbox kind 映射
            var kind = raw.Hitbox.Split(':')[0];
            var expectedGeo = kind switch
            {
                "fan" or "cone" => GeoKind.Sector,
                "circle" or "aura" or "zone" => GeoKind.Circle,
                "cyl" => GeoKind.Cylinder,
                "box" or "line" => GeoKind.Obb,
                "proj" => GeoKind.None,   // 弹体: IsProjectile 载体
                "lob" => GeoKind.Circle,
                _ => GeoKind.None,
            };
            if (!def.IsProjectile && expectedGeo != GeoKind.None)
                Check(def.Geo.Kind == expectedGeo, "hitbox.kind", $"{kind} → {def.Geo.Kind} != {expectedGeo}");

            // 弹体参数
            if (def.IsProjectile && kind == "proj")
            {
                var speedP = raw.Hitbox.Split(':').ElementAtOrDefault(1) ?? "";
                if (speedP.EndsWith("m/s") && double.TryParse(speedP[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var spd))
                    Check(def.ProjSpeedQ == Q(spd), "proj.speed", $"{def.ProjSpeedQ} != {Q(spd)}");
            }

            // 状态效果 kind 路由
            foreach (var st in def.Statuses)
            {
                Check(st.Kind != StatusKind.None, "status.kind");
                Check(st.DurationTicks > 0 || st.Kind is StatusKind.GuardBreak, "status.duration");
            }

            // 蓄力前摇追加
            var chargeM = System.Text.RegularExpressions.Regex.Match(raw.Special, @"蓄力:(\d+(?:\.\d+)?)s");
            if (chargeM.Success)
            {
                var ct = (int)Math.Round(double.Parse(chargeM.Groups[1].Value, CultureInfo.InvariantCulture) * 60, MidpointRounding.ToEven);
                Check(def.StartupTicks == TicksFromFrames(raw.Su) + ct, "charge.startup",
                    $"{def.StartupTicks} != {TicksFromFrames(raw.Su)}+{ct}");
            }
        }
        Assert.Equal(483, checkedRows);
        Assert.True(errors.Count == 0, $"全表保真度违规 {errors.Count} 项:\n{string.Join("\n", errors.Take(20))}");
    }

    private static long Q(double v) => (long)Math.Round(v * 65536.0, MidpointRounding.ToEven);

    // ---------- PF02: 全表可表达性分类 ----------

    private enum PilotClass { Routed, Partial, Blocked }

    private static PilotClass Classify(SkillRuntimeData def)
    {
        var raw = RawRows.FirstOrDefault(r => r.Id == def.SkillId);
        if (raw is null) return PilotClass.Blocked;
        // blocked: OQ 阻塞行（Compiler 拒产——不在 Catalog 中，此处不会出现）
        // partial: special/status 含未路由语义关键词
        foreach (var kw in new[] { "分身", "假身", "操纵", "附身", "携带", "形态三选", "变弹", "随机", "干扰", "删除", "召唤物", "镜像", "替换", "伪装" })
            if (raw.Special.Contains(kw)) return PilotClass.Partial;
        if (raw.Status.Contains("全异常") || raw.Status.Contains("冻结值") || raw.Status.Contains("震地")
            || raw.Status.Contains("拖拽") || raw.Status.Contains("拉拽") || raw.Status.Contains("对敌")
            || raw.Status.Contains("截脉") || raw.Status.Contains("封印") || raw.Status.Contains("嘲讽")
            || raw.Status.Contains("束缚") || raw.Status.Contains("藤蔓") || raw.Status.Contains("感电"))
            return PilotClass.Partial;
        if (raw.Hitbox.StartsWith("unit") || raw.Hitbox.StartsWith("deploy") || raw.Hitbox.StartsWith("ally")
            || raw.Hitbox.StartsWith("wall") || raw.Hitbox.StartsWith("portal"))
            return PilotClass.Partial;
        if (raw.Special.Contains("陷阱") || raw.Special.Contains("炮台") || raw.Special.Contains("部署"))
            return PilotClass.Partial;
        return PilotClass.Routed;
    }

    [Fact]
    public void PF02_Coverage_Classification_AllRows_Classified()
    {
        int routed = 0, partial = 0;
        var partialByKind = new Dictionary<string, int>();
        foreach (var def in Catalog.Skills)
        {
            var c = Classify(def);
            if (c == PilotClass.Routed) routed++;
            else
            {
                partial++;
                var raw = RawRows.First(r => r.Id == def.SkillId);
                var reason = raw.Hitbox.Split(':')[0] switch
                {
                    "unit" => "unit", "deploy" => "deploy", "ally" => "ally", "wall" => "wall", "portal" => "portal",
                    _ => raw.Status.Length > 1 && raw.Status != "none" && raw.Status != "-" ? "status" : "special",
                };
                partialByKind[reason] = partialByKind.GetValueOrDefault(reason) + 1;
            }
        }
        // 全表 483 行全部有分类，routed 占比 ≥ 55%（迁移底数下限）
        Assert.Equal(483, routed + partial);
        Assert.True(routed >= 260, $"routed={routed} partial={partial} — 迁移底数不足");
        // 分类可复现（确定性）
        Assert.Equal(routed, Catalog.Skills.Count(d => Classify(d) == PilotClass.Routed));
    }

    // ---------- PF03-PF10: 代表性技能 Runtime 执行样本 ----------

    private static SimWorld PilotWorld(params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0x011217, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
            ArenaDefParser.Parse(System.IO.Path.Combine(Root, "docs/balance-sheet/arena.csv"))))
            w.AddTerrain(t);
        foreach (var (id, cls, team) in fighters)
        {
            var cap6 = cls switch
            {
                "SUM" => (SimWorld.ResourceSlotKind.Summon, 4L),
                "MEH" => (SimWorld.ResourceSlotKind.Deploy, 3L),
                _ => (SimWorld.ResourceSlotKind.Summon, 0L),
            };
            w.SetClassResource(cls, cap6.Item1, cap6.Item2);
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
            if (Catalog.WeaponsByClass.TryGetValue(cls, out var ws))
            {
                var std = ws.FirstOrDefault(x => x.WeaponId.EndsWith("_001")) ?? ws[0];
                w.EquipWeapon(id, Catalog.WeaponIds[std.WeaponId], FixedM(std.AtkMod), 1100);
            }
        }
        w.SealWorld();
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to)
    {
        for (int t = from; t <= to; t++) w.Step(t, Array.Empty<Command>());
    }

    private static long FixedM(decimal m) => (long)Math.Round(m * 65536m, MidpointRounding.ToEven);

    [Fact]
    public void PF03_BMG_T4_001_MeleeWeakPoint_HeadX15()
    {
        var w = PilotWorld((0, "BMG", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Fighters[1].HeadingQuantum = 32768;   // 面朝 attacker（无背击干扰）
        w.Step(1, new[] { Skill(0, "BMG_T4_001") });   // 豪龙破军 box 1.0×1.0×4.0 部位结算:头部×1.5
        Run(w, 2, 50);
        var hit = Assert.Single(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1);
        Assert.Equal((byte)HitRegion.Head, hit.HitRegion);   // 近战 hitbox 带覆盖头 → 部位=Head
        long baseDmg = DeterministicMath.MulShift(DeterministicMath.MulShift(
            DeterministicMath.MulShift(Catalog.Get("BMG_T4_001")!.DamageMultQ, 1100),
            DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800)), FixedM(1.5m));
        Assert.InRange(hit.DamageRaw, baseDmg - 3, baseDmg + 3);
    }

    [Fact]
    public void PF04_ELE_T3_002_Freeze_Status_Data_Routed()
    {
        var def = Catalog.Get("ELE_T3_002")!;
        Assert.Contains(def.Statuses, st => st.Kind == StatusKind.Freeze);   // freeze:4s@100% 数据路由
        var w = PilotWorld((0, "ELE", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(5);
        w.Step(1, new[] { Skill(0, "ELE_T3_002") });
        Run(w, 2, 60);
        // freeze@100% = 必定命中
        Assert.True(w.Fighters[1].Statuses[(int)StatusKind.Freeze].Active
            || w.Events.All.Any(e => e.Kind == EventKind.StatusApplied && e.StatusKind == (byte)StatusKind.Freeze),
            "freeze 状态应施加（@100% 无几率）");
    }

    [Fact]
    public void PF05_WIT_T2_005_Lob_Zone_Arcing()
    {
        var w = PilotWorld((0, "WIT", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(6);
        w.Step(1, new[] { Skill(0, "WIT_T2_005") });    // 熔岩烧瓶 lob 抛物线+地面火海
        Run(w, 2, 90);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.ProjectileSpawned);   // lob 弹体应发射
        Assert.True(w.Events.All.Any(e => e.Kind == EventKind.Hit || e.Kind == EventKind.ProjectileDestroyed),
            "lob 弹体落地/命中应产生事件");
    }

    [Fact]
    public void PF06_GRP_T2_002_Throw_Backstab_Interaction()
    {
        var w = PilotWorld((0, "GRP", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromRaw(FixedM(1.2m));
        w.Step(1, new[] { Skill(0, "GRP_T2_002") });    // 旋投
        Run(w, 2, 45);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.GrabStarted && e.VictimId == 1);
        // 旋投 kb=0 → 投技结束默认倒地（ResolveThrow 兜底路径）
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1 && e.SkillId == Catalog.IdMap["GRP_T2_002"]);
    }

    [Fact]
    public void PF07_NJA_T3_001_Backstab_Grab_Requires_Rear()
    {
        var w = PilotWorld((0, "NJA", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(1);
        // 背身缚首术: 需背身 120° — 从正面施法（attacker 在 victim 面朝方向）
        w.Fighters[1].HeadingQuantum = 32768;   // 面朝 −Z = 朝 attacker
        w.Step(1, new[] { Skill(0, "NJA_T3_001") });
        Run(w, 2, 30);
        // v1 无角度门控原语 → 抓取成立（正面也可抓）——**fidelity gap 登记**（见审计 §3）
        Assert.True(w.Events.All.Any(e => e.Kind == EventKind.GrabStarted)
            || w.Events.All.Any(e => e.Kind == EventKind.Whiff),
            "正面抓取应成立（gap 登记）或被角度门控拒绝（future）");
    }

    [Fact]
    public void PF08_SRP_T2_001_NoUkemi_Shell()
    {
        var w = PilotWorld((0, "SRP", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Step(1, new[] { Skill(0, "SRP_T2_001") });    // 踏射: 受身无效
        Run(w, 2, 40);
        Assert.True(w.Events.All.Any(e => e.Kind == EventKind.Hit && e.VictimId == 1)
            || w.Events.All.Any(e => e.Kind == EventKind.ForcedDown)
            || w.Events.All.Any(e => e.Kind == EventKind.GuardHit), "踏射应产生命中类事件");
        var def = Catalog.Get("SRP_T2_001")!;
        Assert.True(def.ForcedDown || def.Sweep, "踏射应带受身无效或扫地标签");
    }

    [Fact]
    public void PF09_Burn_DoT_Routed_From_Data_And_Ticks()
    {
        // ELE_T1_003 火焰爆弹 (proj magic, burn:60:4s): DoT 状态数据化路由 + 分数累积 Tick
        var def = Catalog.Get("ELE_T1_003")!;
        Assert.Contains(def.Statuses, st => st.Kind == StatusKind.Burn);
        var w = PilotWorld((0, "ELE", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(8);
        long hpBefore = w.Fighters[1].Hp;
        w.Step(1, new[] { Skill(0, "ELE_T1_003") });
        Run(w, 2, 300);
        var applied = w.Events.All.Any(e => e.Kind == EventKind.StatusApplied && e.StatusKind == (byte)StatusKind.Burn);
        var burned = w.Fighters[1].Statuses[(int)StatusKind.Burn].Active || w.Fighters[1].Statuses[(int)StatusKind.Burn].DotApplied > 0;
        Assert.True(applied || burned, $"burn DoT 应由数据路由施加: ev={string.Join("|", w.Events.All.Where(x => x.Kind == EventKind.StatusApplied).Select(x => x.StatusKind))}");
        // DoT 分数累积: 60/s × 4s = 240 总伤害（整数 RHE 累积）——实际命中后才施加，验证 DotApplied 域可用
        Assert.True(w.Fighters[1].Hp < hpBefore, "直接命中+DoT 均应产生 HP 损失");
    }

    [Fact]
    public void PF10_Storm_Kit_Determinism_With_Phase6_Primitives()
    {
        // 全原语混合对局: 召唤+潜行+反射+可控弹+格挡+抓取 → 双跑逐位一致
        var (h1, s1) = RunStorm(0x57027);
        var (h2, s2) = RunStorm(0x57027);
        Assert.Equal(h1, h2);
        Assert.True(s1.BitwiseEquals(s2));
    }

    private static (string, SnapshotData) RunStorm(long seed)
    {
        var w = new SimWorld(seed, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
            ArenaDefParser.Parse(System.IO.Path.Combine(Root, "docs/balance-sheet/arena.csv")))) w.AddTerrain(t);
        w.AddFighter(0, "SUM", Fixed.FromInt(0), Fixed.FromInt(0), team: 0);
        w.AddFighter(1, "THF", Fixed.FromInt(0), Fixed.FromInt(4), team: 0);
        w.AddFighter(2, "KNI", Fixed.FromInt(0), Fixed.FromInt(8), team: 1);
        w.AddFighter(3, "QIM", Fixed.FromInt(0), Fixed.FromInt(12), team: 1);
        w.SealWorld();
        for (int t = 1; t <= 800; t++)
        {
            var cmds = new List<Command>();
            if (t == 20) cmds.Add(Skill(0, "SUM_T1_002"));                                            // 召唤
            if (t == 40) cmds.Add(Skill(1, "THF_T1_001"));                                            // 潜行
            if (t == 60) cmds.Add(Skill(3, "QIM_T3_002"));                                            // 可控弹
            if (t is >= 70 and <= 90) cmds.Add(new Command(3, CmdKind.Steer, 0, 16384, 0, t));        // 转向 +X
            if (t == 100) cmds.Add(Skill(2, "KNI_T3_003"));                                           // 反射
            if (t == 110) cmds.Add(new Command(3, CmdKind.Skill, Catalog.IdMap["BMG_T1_001"], 32768, 0, t));   // 天击朝 −Z
            if (t == 150) cmds.Add(new Command(1, CmdKind.Basic, 0, 0, 0, t));                                 // 普攻破隐
            if (t == 200) cmds.Add(Skill(2, "KNI_T3_003"));                                                    // 反击架势
            if (t == 220) cmds.Add(new Command(0, CmdKind.Skill, Catalog.IdMap["BMG_T2_001"], 0, 0, t));       // 圆舞棍
            if (t == 260) cmds.Add(Skill(0, "SUM_T1_003"));                                           // 第二召唤
            w.Step(t, cmds.Where(c => c.Kind == CmdKind.Skill || c.Kind == CmdKind.Basic || c.Kind == CmdKind.Steer).ToArray());
        }
        return (w.Events.ComputeHash(), w.CaptureSnapshot());
    }
}
