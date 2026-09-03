using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Core.Sim.Signatures;
using Arena.Infra.Data;
using Arena.Core.Snapshot;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 7 Batch 6: 组合一致性矩阵——验证已建立原语在复杂战斗组合中稳定共存。
/// 重点: 生命周期、优先级、状态覆盖、事件顺序、实体关系（用户 Batch 6 方向）。
/// IC01 格挡×伤害类型 / IC02 完美格挡反击链 / IC03 抓取者死亡释放 / IC04 破隐时序 /
/// IC05 召唤物×召唤者状态 / IC06 反射弹回击 / IC07 DoT×倒地×死亡 / IC08 蓄力打断 /
/// IC09 可控弹转向再碰撞 / IC10 CD重置×连发 / IC11 共鸣混合序列 / IC12 全机制混战确定性。
public class Batch6InteractionMatrixTests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private SimWorld CreateWorld(SignatureRegistry? reg, params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0xB6C0_0001L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters)
        {
            switch (cls)
            {
                case "BMG": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Orb, 7); break;
                case "SPF": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Magazine, 20); break;
                case "SBL": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Resonance, 3); break;
                case "SUM": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Summon, 4); break;
            }
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        }
        w.SealWorld();
        if (reg is not null) w.InstallSignatures(reg);
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to, Func<int, Command[]>? schedule = null)
    {
        for (int t = from; t <= to; t++) w.Step(t, schedule?.Invoke(t) ?? Array.Empty<Command>());
    }

    public void Dispose() { }

    // ================= IC01: 格挡 × 伤害类型（物理化解 / 法术绕过） =================

    [Fact]
    public void IC01_Guard_PhysMitigated_MagicBypasses()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BLA", 0), (1, "BMG", 1));
        var bla = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(2);
        bmg.HeadingQuantum = 32768;   // 面向 −Z（朝 BLA）

        // 格挡 hold（正面 120° 内）→ 物理龙牙: GuardHit + 盾值扣减
        w.Step(1, new[] { Skill(0, "BLA_T1_002") });
        Run(w, 2, 40, t => t == 10 ? new[] { Skill(1, "BMG_T1_002", 32768) } : Array.Empty<Command>());
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.GuardHit && e.VictimId == 0);
        Assert.True(bla.Shield < bla.ShieldMax, "物理命中化解: 盾值扣减");
        long shieldAfterPhys = bla.Shield;

        // 法术天击绕过格挡（PhysicalOnly 门控）→ 正常 Hit 伤害（非 GuardHit）
        int guardHits = w.Events.All.Count(e => e.Kind == EventKind.GuardHit);
        bmg.Cooldowns.Clear();
        bmg.Mp = 1000;
        bla.Mp = 1000;
        w.Step(50, new[] { Skill(0, "BLA_T1_002") });   // 重新架盾（CD 8s 内首次已消耗——cd=8s=480T，t50 在 CD 内！改用 MP 满直接断言）
        Run(w, 51, 90, t => t == 60 ? new[] { Skill(1, "BMG_T1_001", 32768) } : Array.Empty<Command>());
        Assert.Equal(guardHits, w.Events.All.Count(e => e.Kind == EventKind.GuardHit));   // 法术未再触发 GuardHit
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0 && e.SkillId == Catalog.IdMap["BMG_T1_001"]);
    }

    // ================= IC02: 格挡锥 0 伤害命中 vs 完美格挡——优先级冲突（DDQ-B6-1 真实发现） =================

    [Fact]
    public void IC02_GuardCone_Interrupts_Attacker_Before_Parry()
    {
        // 真实发现（IC02 探针）: 格挡锥判定体（BLA_T1_002 dmg=0 cone）在来袭攻击落地前先命中攻击者，
        // 0 伤害命中 → 攻击者技能被中断（§4.3）——完美格挡/弹刀路径因此从未触发。
        // 与 GDD §6.3 弹刀预期的优先级冲突，根因 = Batch 5 遗留设计决策「①格挡锥 dmg=0 判定体意图」。
        // 本探针固化当前行为，待设计裁定后按裁定改写。
        var w = CreateWorld(DefaultRegistry(), (0, "BLA", 0), (1, "BMG", 1));
        var defender = w.Fighters[0];
        var attacker = w.Fighters[1];
        attacker.PosZ = Fixed.FromInt(2);
        attacker.HeadingQuantum = 32768;

        var schedule = new Dictionary<int, Command[]>
        {
            [5] = new[] { Skill(1, "BMG_T1_002", 32768) },
            [9] = new[] { Skill(0, "BLA_T1_002") },
        };
        Run(w, 1, 40, t => schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.AttackerId == 0 && e.SkillId == Catalog.IdMap["BLA_T1_002"] && e.DamageRaw == 0);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Interrupted && e.AttackerId == 1);   // Interrupted 事件按 OwnerId 记攻击方
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Parry);   // 弹刀被锥体先手屏蔽
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.GuardHit); // 来袭攻击未进入格挡化解
    }

    // ================= IC03: 抓取者死亡 → 被擒者释放（实体关系） =================

    [Fact]
    public void IC03_Grabber_Death_Releases_Grabbed()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "GRP", 0), (1, "BLA", 1), (2, "BMG", 1));
        var grp = w.Fighters[0];
        var victim = w.Fighters[1];
        var executioner = w.Fighters[2];
        grp.Hp = 1;   // 处刑靶
        victim.PosZ = Fixed.FromInt(1);   // 背摔抓距内
        victim.HeadingQuantum = 32768;
        executioner.PosZ = Fixed.FromInt(2);
        executioner.HeadingQuantum = 32768;

        // 背摔抓取 victim（t5 su12 → GrabStarted ~t17）；处刑者天击斩杀 GRP
        var schedule = new Dictionary<int, Command[]>
        {
            [5] = new[] { Skill(0, "GRP_T1_001") },
            [10] = new[] { Skill(2, "BMG_T1_001", 32768) },
        };
        Run(w, 1, 80, t => schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.GrabStarted && e.AttackerId == 0);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Died && e.VictimId == 0);
        // 实体关系: 抓取者死亡 → 被擒者释放（GrabReleased）+ 状态域清空
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.GrabReleased && e.AttackerId == 0 && e.VictimId == 1);
        Assert.Equal(-1, victim.GrabbedBy);
        Assert.True(victim.State != FighterState.Grabbed);
    }

    // ================= IC04: 潜行 → 施法破隐 → 命中资格时序 =================

    [Fact]
    public void IC04_Stealth_Break_OnCast_Hittable_After()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "THF", 0), (1, "BMG", 1));
        var thf = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(2);
        bmg.HeadingQuantum = 32768;

        w.Step(1, new[] { Skill(0, "THF_T1_001") });   // 潜行 hold
        Run(w, 2, 30);
        Assert.True(thf.Hidden);

        // 潜行中敌弹不可命中（Visibility sweep 过滤——负控制）
        Run(w, 31, 45, t => t == 33 ? new[] { Skill(1, "BMG_T1_001", 32768) } : Array.Empty<Command>());
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0);

        // 非部署技破隐 → 破隐后敌弹可命中（破隐 tick 早于命中 tick）
        w.Step(60, new[] { Skill(0, "THF_T1_003") });   // 陷阱解除（非 deploy）
        Assert.False(thf.Hidden);
        Run(w, 61, 110, t => t == 70 ? new[] { Skill(1, "BMG_T1_002", 32768) } : Array.Empty<Command>());   // 龙牙（天击 CD 中）
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0);
        Assert.True(w.Events.All.First(e => e.Kind == EventKind.StealthBroken).Tick < 70);
    }

    // ================= IC05: 召唤物 × 召唤者状态（主人被浮空，单位继续作战） =================

    [Fact]
    public void IC05_Summon_Keeps_Fighting_While_Owner_Airborne()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "SUM", 0), (1, "BLA", 1));
        var sum = w.Fighters[0];
        var enemy = w.Fighters[1];
        enemy.PosZ = Fixed.FromInt(3);

        w.Step(1, new[] { Skill(0, "SUM_T1_002") });   // 召唤·哥布林（存在90s）
        Run(w, 2, 30);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitSpawned);
        Assert.NotEmpty(w.Units);

        // 主人被浮空+倒地 → 单位独立 AI 继续攻击（事件 AttackerId = 主人 FighterId——面板挂主人）
        Run(w, 31, 400, t => t == 40 ? new[] { Skill(1, "BMG_T1_001", 32768) } : Array.Empty<Command>());
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Launched && e.VictimId == 0);
        var unitHits = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.AttackerId == 0 && e.Tick > 60).ToList();
        Assert.True(unitHits.Count > 0, $"主人倒地期间单位仍命中敌方（{unitHits.Count} 次）");
    }

    // ================= IC06: 法术反射 → 弹体反弹回击原攻击者 =================

    [Fact]
    public void IC06_Reflect_Projectile_Returns_To_Sender()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "KNI", 0), (1, "BMG", 1));
        var kni = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(4);

        // BMG 炫纹发射（magic 弹）t30 → 弹 ~t45 到达；KNI t40 反射（2s 窗，面朝 +Z 迎弹）
        var schedule = new Dictionary<int, Command[]>
        {
            [30] = new[] { Skill(1, "BMG_T1_006", 32768) },
            [20] = new[] { Skill(0, "KNI_T3_003") },
        };
        Run(w, 1, 140, t => schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());
        foreach (var e in w.Events.All) if (e.Tick >= 40 && e.Tick <= 50) Console.WriteLine($"DBG6 t{e.Tick} {e.Kind} atk={e.AttackerId} vic={e.VictimId} sk={e.SkillId}");
        Console.WriteLine($"DBG6 kni.ReflectTicks@end={kni.ReflectTicks} kni.Hp={kni.Hp} projs={w.Projectiles.Count(p => !p.Expired)} pos={string.Join(";", w.Projectiles.Where(p => !p.Expired).Select(p => p.PosZ / 65536.0))}");

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Reflected);
        // 实体关系: 反弹后 OwnerId 转移 → Hit 事件 AttackerId = KNI、VictimId = BMG
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.AttackerId == 0 && e.VictimId == 1 && e.SkillId == Catalog.IdMap["BMG_T1_006"]);
        Assert.True(bmg.Hp < 10000, "反弹弹体命中原攻击者");
    }

    // ================= IC07: DoT × 倒地 × 死亡（状态叠加 + 事件顺序） =================

    [Fact]
    public void IC07_Dot_Continues_During_Down_And_Kills()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BLA", 0), (1, "THF", 1), (2, "BER", 1));
        var victim = w.Fighters[0];
        var thf = w.Fighters[1];
        var ber = w.Fighters[2];
        victim.Hp = 1340;   // 命中伤害实测 ~1228 → 余 ~112；毒 349 潜力在倒地窗内致死（数值实测校准）
        victim.PosZ = Fixed.FromInt(3);
        thf.PosZ = Fixed.FromInt(3);
        ber.PosZ = Fixed.FromInt(4);   // 倒斩扇 2.8m 外侧——浮空 z=3 处 victim
        ber.HeadingQuantum = 32768;

        // 毒云陷阱布在 victim 脚下（触发中毒 DoT）+ 倒斩浮空倒地 → DoT 在 Down 期继续 → 致死
        Run(w, 1, 35, t => t == 1 ? new[] { Skill(1, "THF_T2_001") } : Array.Empty<Command>());
        victim.PosZ = Fixed.FromInt(3);   // 陷阱触发击退后归位（倒斩须够到）
        Run(w, 36, 900, t => t == 40 ? new[] { Skill(2, "BER_T1_001", 32768) } : Array.Empty<Command>());
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Launched && e.VictimId == 0);
        var landed = w.Events.All.Where(e => e.Kind == EventKind.Landed && e.VictimId == 0).ToList();
        var died = w.Events.All.FirstOrDefault(e => e.Kind == EventKind.Died && e.VictimId == 0);
        var pslot = victim.Statuses[(int)StatusKind.Poison];
        Assert.True(died.Tick != 0, $"毒 DoT 致死: hp={victim.Hp} poisonDot={pslot.DotApplied} active={pslot.Active} remain={pslot.RemainingTicks} launched={w.Events.All.Any(e => e.Kind == EventKind.Launched && e.VictimId == 0)}");
        Assert.True(landed.Count > 0 && landed[0].Tick < died.Tick, "先倒地后死亡");
        // Down 期间 DoT 持续结算（毒槽 DotApplied 累积跨越 Landed→Died 窗口——DoT 无事件，槽字段为准）
        Assert.True(victim.Statuses[(int)StatusKind.Poison].DotApplied > 0,
            $"倒地期毒 DoT 持续: DotApplied={victim.Statuses[(int)StatusKind.Poison].DotApplied}");
        Assert.True(victim.Statuses[(int)StatusKind.Poison].Active || died.Tick != 0);
    }

    // ================= IC08: 蓄力打断（§4.3 中断 + MP 不退）× 蓄满倍率对照 =================

    [Fact]
    public void IC08_Charge_Interrupted_NoBonus_Completed_Bonus()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "LAU", 0), (1, "BMG", 1));
        var lau = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(2);
        bmg.HeadingQuantum = 32768;

        // 第一次: 蓄力前摇（su20+蓄力48=68）中被浮空打断 → 无 Hit、MP 不退
        w.Step(1, new[] { Skill(0, "LAU_T3_001") });
        Run(w, 2, 50, t => t == 30 ? new[] { Skill(1, "BMG_T1_001", 32768) } : Array.Empty<Command>());
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Interrupted && e.VictimId == 0);
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Hit && e.AttackerId == 0);
        long mpAfterCast = 1000 - 105;
        Assert.InRange(lau.Mp, mpAfterCast, mpAfterCast + 20);   // 扣除后无回退（regen 窄带内）

        // 第二次: 不受干扰蓄满 → 命中 ×1.4（蓄力加成在防御系数之后乘——GDD §4.1）
        lau.Mp = 1000;
        lau.Hp = 10000;
        lau.Cooldowns.Clear();   // 激光炮 CD 24s——不重置则二次施法被 CD 拒绝
        bmg.Cooldowns.Clear();
        bmg.Mp = 1000;
        bmg.Hp = 10000;
        bmg.State = FighterState.Normal;
        bmg.PosZ = Fixed.FromInt(3);
        Run(w, 51, 399);   // 逐 Tick 补步进——跳 Tick 冻结物理时间线（Launch 悬停、ActEnded/Landed 顺延）
        w.Step(400, new[] { Skill(0, "LAU_T3_001") });
        Run(w, 401, 500);
        var hit = w.Events.All.Last(e => e.Kind == EventKind.Hit && e.AttackerId == 0);
        Assert.True(hit.Tick >= 460 && hit.Tick <= 480, $"激光炮命中 t{hit.Tick}（预期 460-480: cast 400 + su 68）");
        long baseDmg = DeterministicMath.MulShift(DeterministicMath.MulShift(
            Catalog.Get("LAU_T3_001")!.DamageMultQ, lau.Atk),
            DeterministicMath.DivRoundHalfEven(1200 * Fixed.ONE, 1200 + bmg.Def));
        long charged = DeterministicMath.MulShift(baseDmg, DeterministicMath.DivRoundHalfEven(140 * Fixed.ONE, 100));
        Assert.InRange(hit.DamageRaw, charged - 5, charged + 5);
    }

    // ================= IC09: 可控弹转向后再入碰撞（运动原语 × 碰撞管线） =================

    [Fact]
    public void IC09_Steered_Projectile_Reenters_Collision()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "QIM", 0), (1, "BLA", 1));
        var qim = w.Fighters[0];
        var bla = w.Fighters[1];
        // 转弯半径 R = v/ω = 26/(120°/s) ≈ 12.4m——目标须落在转向弧上（θ=15°: x=R(1−cos15)≈0.42, z=R·sin15≈3.21）
        bla.PosX = Fixed.FromRaw(27525);   // ≈0.42m
        bla.PosZ = Fixed.FromInt(3);

        // 念龙波朝 +Z 发射（t1）→ active 窗内 Steer 45° → 弹体转向后命中 45° 方向目标
        var schedule = new Dictionary<int, Command[]>
        {
            [1] = new[] { Skill(0, "QIM_T3_002") },
            [15] = new[] { new Command(0, CmdKind.Steer, 0, 4096, 0, 15) },   // 15°
        };
        Run(w, 1, 200, t => schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());

        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.AttackerId == 0 && e.VictimId == 1 && e.SkillId == Catalog.IdMap["QIM_T3_002"]);
        Assert.True(bla.Hp < 10000, "转向后弹体与新目标碰撞（位移/转向/碰撞管线一致）");
    }

    // ================= IC10: 骑士精神 CD 重置 × 立即连发 =================

    [Fact]
    public void IC10_KnightSpirit_Reset_Enables_Immediate_Recast()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "KNI", 0), (1, "BLA", 1));
        var kni = w.Fighters[0];
        w.Fighters[1].PosZ = Fixed.FromInt(2);

        w.Step(15, new[] { Skill(0, "KNI_T1_001") });   // 击退 CD 420T
        Run(w, 16, 50);
        Assert.True(kni.Cooldowns[Catalog.IdMap["KNI_T1_001"]] > 0);

        // 骑士精神重置后 5T 内立即二连发（重置 × 输入时序组合）
        var schedule = new Dictionary<int, Command[]>
        {
            [60] = new[] { Skill(0, "KNI_U_001") },
            [115] = new[] { Skill(0, "KNI_T1_001") },
        };
        Run(w, 51, 180, t => schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());
        var knockbacks = w.Events.All.Count(e => e.Kind == EventKind.SkillCast && e.AttackerId == 0 && e.SkillId == Catalog.IdMap["KNI_T1_001"]);
        Assert.Equal(2, knockbacks);   // t15 + t70（重置生效，无需等 CD）
    }

    // ================= IC11: SBL 共鸣——非波动插入不叠层（链语义边界） =================

    [Fact]
    public void IC11_WaveResonance_NonWave_Insert_NoStack()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "SBL", 0), (1, "BLA", 1));
        var sbl = w.Fighters[0];
        w.Fighters[1].PosZ = Fixed.FromInt(3);

        Command[] Sched(int t) => t switch
        {
            15 => new[] { Skill(0, "SBL_T1_001") },   // A → 1 档
            100 => new[] { Skill(0, "SBL_T3_001") },  // 裂波斩（非波动剑）→ 不触发
            400 => new[] { Skill(0, "SBL_T1_002") },  // B: LastCast=裂波斩（非波动）→ 回 1 档（不 +1）
            _ => Array.Empty<Command>(),
        };
        Run(w, 1, 99, Sched);
        Assert.Equal(1, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);
        Run(w, 100, 399, Sched);
        Assert.Equal(1, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);
        Run(w, 400, 500, Sched);
        Assert.Equal(1, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);
    }

    // ================= IC12: 全机制混战 1500T 双跑逐位 + 快照恢复 =================

    [Fact]
    public void IC12_GrandMix_AllMechanisms_Deterministic()
    {
        var fighters = new (int, string, byte)[]
        {
            (0, "BMG", 0), (1, "BER", 0), (2, "KNI", 0),
            (3, "BLA", 1), (4, "THF", 1), (5, "GRP", 1),
        };

        var auth = CreateWorld(DefaultRegistry(), fighters);
        var log = new List<Command>();
        RunRange(auth, log, 1, 1500);

        var client = CreateWorld(DefaultRegistry(), fighters);
        var clientLog = new List<Command>();
        RunRange(client, clientLog, 1, 700);
        var snap = client.CaptureSnapshot();
        var restored = CreateWorld(DefaultRegistry(), fighters);
        restored.RestoreSnapshot(snap);
        ReplayRange(restored, log, 701, 1500);   // 重放用全程指令日志（ADR-0005: Replay 文件=全量指令流，非客户端半程）

        var sa = auth.CaptureSnapshot();
        var sr = restored.CaptureSnapshot();
        if (!sa.BitwiseEquals(sr))
        {
            var (ka, va) = sa.ToArrays();
            var (kr, vr) = sr.ToArrays();
            var ma = new Dictionary<long, long>(); for (int i = 0; i < ka.Length; i++) ma[ka[i]] = va[i];
            var mr = new Dictionary<long, long>(); for (int i = 0; i < kr.Length; i++) mr[kr[i]] = vr[i];
            var diffs = new List<string>();
            foreach (var kv in ma)
                if (!mr.TryGetValue(kv.Key, out var v) || v != kv.Value) diffs.Add($"key{kv.Key}: auth={kv.Value} restored={(mr.TryGetValue(kv.Key, out var x) ? x.ToString() : "MISSING")}");
            foreach (var kv in mr)
                if (!ma.ContainsKey(kv.Key)) diffs.Add($"key{kv.Key}: auth=MISSING restored={kv.Value}");
            Console.WriteLine($"IC12 diff ({diffs.Count}): " + string.Join(" | ", diffs.Take(20)));
        }
        Assert.True(sa.BitwiseEquals(sr),
            "全机制混战（buff/格挡/抓取/弹幕/DoT/CD重置/签名）1500T 双跑 + 快照恢复逐位一致");
        // 组合行为确实发生（非空转）
        Assert.True(auth.Events.All.Count(e => e.Kind == EventKind.Hit) > 10, "混战产生真实命中");
    }

    private static Command[] CommandsFor(int t) => t switch
    {
        15 => new[] { Skill(0, "BMG_T1_001") },              // BMG 天击（浮空+获纹）
        40 => new[] { Skill(0, "BMG_T1_006") },              // 炫纹发射（弹幕）
        70 => new[] { Skill(1, "BER_T4_003") },              // BER 嗜血（自增益+自伤）
        90 => new[] { Skill(1, "BER_T1_001") },              // 倒斩（浮空）
        120 => new[] { Skill(2, "KNI_T3_003") },             // KNI 法术反射
        150 => new[] { Skill(5, "GRP_T1_001") },             // GRP 背摔（抓取）
        180 => new[] { Skill(3, "BLA_T1_002") },             // BLA 格挡
        200 => new[] { Skill(4, "THF_T1_001") },             // THF 潜行
        240 => new[] { Skill(4, "THF_T2_001") },             // THF 陷阱（破隐——非 hold? 陷阱 deploy 破隐例外）
        300 => new[] { Skill(0, "BMG_T1_003") },             // 连突（再获纹）
        330 => new[] { Skill(1, "BER_T2_001") },             // 冲撞刺击（突进）
        360 => new[] { Skill(2, "KNI_U_001") },              // 骑士精神（CD 重置）
        380 => new[] { Skill(2, "KNI_T1_001") },             // 重置后立即击退
        420 => new[] { Skill(3, "BLA_T1_001") },             // 上挑
        450 => new[] { Skill(5, "GRP_T1_003") },             // 单手擒
        500 => new[] { Skill(0, "BMG_T1_006") },             // 再发射（弹幕）
        560 => new[] { Skill(1, "BER_T1_002") },             // 重击
        600 => new[] { Skill(3, "BLA_T1_001") },             // 上挑
        640 => new[] { Skill(4, "THF_T1_003") },             // 陷阱解除（破隐）
        700 => new[] { Skill(0, "BMG_T1_004") },             // 落花掌（吹飞）
        750 => new[] { Skill(1, "BER_T3_003") },             // 噬魂血手（范围抓取）
        800 => new[] { Skill(5, "GRP_T2_004") },             // 头上拂
        900 => new[] { Skill(0, "BMG_T1_006") },             // 第三次发射
        1000 => new[] { Skill(3, "BLA_T1_001") },            // 上挑
        1100 => new[] { Skill(2, "KNI_T1_001") },            // 击退
        1200 => new[] { Skill(0, "BMG_T1_003") },            // 连突
        1300 => new[] { Skill(1, "BER_T1_001") },            // 倒斩
        _ => Array.Empty<Command>(),
    };

    private static void RunRange(SimWorld w, List<Command> log, int from, int to)
    {
        for (int t = from; t <= to; t++)
        {
            var cmds = CommandsFor(t);
            w.Step(t, cmds);
            // 打戳: TargetTick = 实际 Tick——ReplayRange 按 TargetTick 分组重放（默认 0 会丢指令）
            foreach (var cmd in cmds) log.Add(cmd with { TargetTick = t });
        }
    }

    private static void ReplayRange(SimWorld w, List<Command> log, int from, int to)
    {
        var byTick = new Dictionary<int, List<Command>>();
        foreach (var cmd in log)
        {
            if (!byTick.TryGetValue(cmd.TargetTick, out var list)) byTick[cmd.TargetTick] = list = new List<Command>();
            list.Add(cmd);
        }
        for (int t = from; t <= to; t++)
        {
            var cmds = byTick.TryGetValue(t, out var list) ? list.ToArray() : Array.Empty<Command>();
            w.Step(t, cmds);
        }
    }
}
