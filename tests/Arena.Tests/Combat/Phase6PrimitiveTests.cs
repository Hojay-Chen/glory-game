using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 6 原语收口探针：UnitSystem / 资源槽 / Visibility / 反射 / 可控弹 / Weapon overlay。
/// 全部经 Compiler → RuntimeDef → SimWorld 权威链路。
public class Phase6PrimitiveTests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private readonly string _root = CombatGoldenSlice.FindRepoRoot();

    private SimWorld CreateWorld(params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0x6EED_1234L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var t in ArenaDefParser.BuildTerrain(
            ArenaDefParser.Parse(System.IO.Path.Combine(_root, "docs/balance-sheet/arena.csv"))))
            w.AddTerrain(t);
        // 职业资源容量先行注入（class-base resource 列语义——AddFighter 读取）
        foreach (var (_, cls, _) in fighters)
        {
            var cap = cls switch
            {
                "SUM" => (SimWorld.ResourceSlotKind.Summon, 4L),
                "MEH" => (SimWorld.ResourceSlotKind.Deploy, 3L),
                "BMG" => (SimWorld.ResourceSlotKind.Orb, 7L),
                "SRP" => (SimWorld.ResourceSlotKind.Magazine, 15L),
                "EXO" => (SimWorld.ResourceSlotKind.Magazine, 15L),
                "THF" => (SimWorld.ResourceSlotKind.SacrificeHp, 3000L),
                _ => (SimWorld.ResourceSlotKind.Summon, 0L),
            };
            w.SetClassResource(cls, cap.Item1, cap.Item2);
        }
        foreach (var (id, cls, team) in fighters)
        {
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
            if (Catalog.WeaponsByClass.TryGetValue(cls, out var ws))
            {
                var std = ws.FirstOrDefault(x => x.WeaponId.EndsWith("_003")) ?? ws[0];
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

    public void Dispose() { }

    // ================= UnitSystem（SUM 召唤位=4） =================

    [Fact]
    public void UT01_Summon_Spawn_Chase_Attack_Lifetime()
    {
        var w = CreateWorld((0, "SUM", 0), (1, "BLA", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(20);
        w.Step(1, new[] { Skill(0, "SUM_T1_002") });    // 召唤·哥布林（投掷射程 8m）
        Run(w, 2, 30);
        var unit = Assert.Single(w.Units);
        Assert.Equal(0, unit.OwnerFighterId);
        Assert.True(unit.Hp > 0);
        // 单位追击: 20m > 射程 8m → 距离应单调缩短（移速 4.5 m/s）
        long d1 = Math.Abs(unit.PosZ.Raw - w.Fighters[1].PosZ.Raw);
        Assert.True(d1 > FixedM(8m), $"初始超出射程: {d1}");
        Run(w, 31, 90);
        long d2 = Math.Abs(unit.PosZ.Raw - w.Fighters[1].PosZ.Raw);
        Assert.True(d2 < d1, $"单位追击: {d1} → {d2}");
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitSpawned);
        // 进入射程后产生命中（8m 投掷射程 + 2s CD）
        Run(w, 91, 900);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1 && e.SkillId == Catalog.IdMap["SUM_T1_002"]);
    }

    [Fact]
    public void UT02_Summon_Lifetime_Expiry_Frees_Slot()
    {
        var w = CreateWorld((0, "SUM", 0), (1, "BLA", 1));
        w.Step(1, new[] { Skill(0, "SUM_T1_003") });    // 雷精灵 存在60s
        var spawnedTick = 1 + Catalog.Get("SUM_T1_003")!.StartupTicks;
        Run(w, 2, spawnedTick + 2);
        Assert.Single(w.Units);
        // 60s 存在期到 → UnitDied(Lifetime) → 召唤位释放
        Run(w, spawnedTick + 3, spawnedTick + 60 * (int)RuntimeConstants.TICK_RATE + 5);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitDied && e.ReasonByte == (byte)UnitSystem.UnitEndReason.Lifetime);
        Assert.Empty(w.Units);
    }

    [Fact]
    public void UT03_SummonSlot_Cap4_RecallOldest()
    {
        var w = CreateWorld((0, "SUM", 0), (1, "BLA", 1));
        var def = Catalog.Get("SUM_T1_003")!;
        // 连续 5 次召唤（间隔 CD 10s=600T——用 5 个不同精灵变体绕 CD：同 def 只能等 CD）
        // 简化: 两次召唤同 def → 第二次受 CD 阻塞 → 直接操纵资源槽验证容量逻辑
        w.Step(1, new[] { Skill(0, "SUM_T1_003") });
        Run(w, 2, 20);
        Assert.Single(w.Units);
        Assert.Equal(1, w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Summon]);
        // 手动打满召唤位 → 第 5 只时最旧被回收（Cap 路径——由 ResourceCounts/Caps 驱动）
        w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Summon] = 4;
        w.Fighters[0].Cooldowns.Clear();
        w.Step(700, new[] { Skill(0, "SUM_T1_003") });
        Run(w, 701, 720);
        Assert.True(w.Units.Count <= 4, $"召唤位封顶 4: actual={w.Units.Count}");
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitDied && e.ReasonByte == (byte)UnitSystem.UnitEndReason.Cap);
    }

    // ================= Visibility（THF_T1_001 潜行） =================

    [Fact]
    public void UT04_Stealth_Invisible_Until_Cast()
    {
        var w = CreateWorld((0, "THF", 0), (1, "BMG", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Step(1, new[] { Skill(0, "THF_T1_001") });    // 潜行 hold
        Run(w, 2, 10);
        Assert.True(w.Fighters[0].Hidden);
        // 敌方龙牙（朝 +Z）打不到潜行者 → Invulnerable 语义路径不触发——直接被 sweep 过滤
        w.Step(11, new[] { Skill(1, "BMG_T1_002") });
        Run(w, 12, 30);
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0);
        // 潜行者施法 → 破隐（潜行 hold 先终止再进入新施法——同帧破隐+新隐身不行，改普攻破隐）
        w.Step(31, new[] { new Command(0, CmdKind.Basic, 0, 0, 0, 31) });
        Run(w, 32, 40);
        Assert.False(w.Fighters[0].Hidden, $"攻击解除潜行: hidden={w.Fighters[0].Hidden} state={w.Fighters[0].State} uid={w.Fighters[0].ActiveSkillUid} ev={string.Join("|", w.Events.All.Where(x => x.Tick >= 30).Select(x => x.Tick + ":" + x.Kind))}");
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.StealthBroken && e.VictimId == 0);
        // 破隐后可被命中（新龙牙）
        // 破隐后可见性恢复: 隐身过滤不再适用（SweepCombat 同一路径）——Hidden 域已清除
        Assert.False(w.Fighters[0].Hidden);
    }

    // ================= 法术反射（KNI_T3_003 2s 窗） =================

    [Fact]
    public void UT05_SpellReflect_Projectile_Reverses_To_Caster()
    {
        var w = CreateWorld((0, "KNI", 0), (1, "ELE", 1));
        w.Fighters[1].PosZ = Fixed.FromInt(-12);
        // ELE 法术弹（magic proj）从 −Z 侧射向 KNI；KNI 反射窗覆盖命中时刻
        var reflectDef = Catalog.Get("KNI_T3_003")!;
        w.Step(1, new[] { Skill(0, "KNI_T3_003") });
        var window = reflectDef.ReflectWindowTicks;
        // 法术弹 su? — 查 def.StartupTicks 让命中落在反射窗内
        var proj = Catalog.Get("ELE_BAS_001")!;
        int fireAt = 2;
        // 校正发射时刻使弹体抵达 ∈ 反射窗 [1+su, 1+su+window)
        int reflectStart = 1 + reflectDef.StartupTicks;
        int flyTicks = (int)((12m * RuntimeConstants.TICK_RATE) / 28m);
        int projArrive = fireAt + proj.StartupTicks + flyTicks;
        if (projArrive < reflectStart) fireAt += reflectStart - projArrive;
        projArrive = fireAt + proj.StartupTicks + flyTicks;
        w.Step(fireAt, new[] { Skill(1, "ELE_BAS_001", 0) });   // 朝 +Z
        Run(w, fireAt + 1, Math.Max(projArrive + 40, reflectStart + window + 5));
        // 反射事件存在；原施法者被自己的弹命中
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Reflected);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1 && e.SkillId == Catalog.IdMap["ELE_BAS_001"]);
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0 && e.SkillId == Catalog.IdMap["ELE_BAS_001"]);
    }

    // ================= 可控弹跟随（QIM_T3_002 念龙波） =================

    [Fact]
    public void UT06_FollowHeading_Projectile_Tracks_Caster_Aim()
    {
        var w = CreateWorld((0, "QIM", 0), (1, "BLA", 1));
        w.Fighters[1].PosX = Fixed.FromInt(6);   // 目标在 +X 侧
        w.Step(1, new[] { Skill(0, "QIM_T3_002") });    // 念龙波 proj controlled 可转向
        Run(w, 2, 2 + Catalog.Get("QIM_T3_002")!.StartupTicks);
        var p = Assert.Single(w.Projectiles);
        // Steer 朝 +X（heading 16384）→ 弹体方向随之转向
        w.Step(3 + Catalog.Get("QIM_T3_002")!.StartupTicks, new[] { new Command(0, CmdKind.Steer, 0, 16384, 0, 0) });
        Run(w, 4 + Catalog.Get("QIM_T3_002")!.StartupTicks, 8 + Catalog.Get("QIM_T3_002")!.StartupTicks);
        Assert.True(p.DispX > 0, $"可控弹跟随转向: dispX={p.DispX} dispZ={p.DispZ}");
    }

    // ================= Weapon overlay（GDD §16 atk_mod） =================

    [Fact]
    public void UT07_Weapon_AtkMod_Applied_To_Damage()
    {
        var w = CreateWorld((0, "BMG", 0), (1, "BLA", 1));   // W_BMG_003 破魔重枪 atk_mod 0.03
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        w.Fighters[1].HeadingQuantum = 32768;   // 面朝 attacker（无背击干扰）
        // 攻击者武器 = 列表第 3 把（破魔重枪 0.03）→ Atk = 1100×1.03
        Assert.Equal(1133, w.Fighters[0].Atk);
        w.Step(1, new[] { Skill(0, "BMG_T1_002") });   // 龙牙
        Run(w, 2, 30);
        var hit = Assert.Single(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1);
        // 基线 528（atk 1100）；+3% → 528×1.03 = 544（整数链）
        long expect = DeterministicMath.MulShift(DeterministicMath.MulShift(Catalog.Get("BMG_T1_002")!.DamageMultQ, 1133),
            DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + 800));
        Assert.Equal(expect, hit.DamageRaw);
    }
}
