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

/// Phase 7 Batch 4: 自增益通道（通用 B 类）+ 小签名插件批次探针。
/// SG11 BER 嗜血（自 buff+自伤脉冲）/ SG12 嗜血奋战（霸体+正嗜血）/
/// SG13 THF 陷阱精通（潜行设陷阱不解除）/ SG14 SBL 杀意波动（共鸣层+伤害+前摇）/
/// SG15 KNI 骑士精神（CD 重置）/ SG16 快照恢复逐位一致。
/// 纪律: 全部逐 Tick 步进（跳 Tick 会使 exec.CurrentOffset 与墙钟 Tick 错位——armor/前摇窗口判定失真）。
public class SignatureBatch4Tests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private SimWorld CreateWorld(SignatureRegistry? reg, params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0xB4A7_0001L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters)
        {
            switch (cls)
            {
                case "BMG": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Orb, 7); break;
                case "SBL": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Resonance, 3); break;
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

    // ================= SG11: BER 嗜血——通用自增益通道（ATK+20% + 自伤 1.5%/s 脉冲） =================

    [Fact]
    public void SG11_BerXixue_SelfBuff_DrainPulses()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BER", 0), (1, "BLA", 1));
        var ber = w.Fighters[0];
        Run(w, 1, 5, t => t == 1 ? new[] { Skill(0, "BER_T4_003") } : Array.Empty<Command>());
        // ATK+20% 自增益（数据: ATK+20%，时限 = active 20s = 1200T）；
        // Ticks=1195: 施法 Tick + t2..t5 共 5 次递减（全步进确定口径）
        Assert.Equal(DeterministicMath.DivRoundHalfEven(20 * Fixed.ONE, 100), ber.BuffAtkPctQ);
        Assert.Equal(1195, ber.BuffAtkPctTicks);
        Assert.True(ber.BuffDrainHpPctTicks > 0, "自伤通道激活");

        // 血气唤醒不踩长周期槽: HP 30% 以下时唤醒欲写 +10%，被嗜血 1200T 槽占据 → 不覆盖
        ber.Hp = 3000;
        Run(w, 6, 10);
        Assert.Equal(DeterministicMath.DivRoundHalfEven(20 * Fixed.ONE, 100), ber.BuffAtkPctQ);

        // 自伤脉冲: 20 脉冲 × 150（10000 × 1.5%）= 3000；钳 1 HP 不触发死亡
        ber.Hp = 10000;   // 复位（守卫检查用 3000——脉冲从当前 HP 起扣）
        Run(w, 11, 1300);
        var pulses = w.Events.All.Where(e => e.Kind == EventKind.DrainPulse && e.VictimId == 0).ToList();
        Assert.Equal(20, pulses.Count);
        Assert.Equal(3000, pulses.Sum(e => e.DamageRaw));
        Assert.Equal(7000, ber.Hp);
        Assert.True(ber.State != FighterState.Dead);
        Assert.Equal(0, ber.BuffDrainHpPctTicks);   // 到期清零
    }

    [Fact]
    public void SG11b_BerXixue_DrainFloorsAtOneHp()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BER", 0), (1, "BLA", 1));
        var ber = w.Fighters[0];
        Run(w, 1, 1, t => new[] { Skill(0, "BER_T4_003") });
        ber.Hp = 100;   // 第一脉冲 150 → 钳至 HP=1
        Run(w, 2, 700);
        var pulses = w.Events.All.Where(e => e.Kind == EventKind.DrainPulse && e.VictimId == 0).ToList();
        Assert.Single(pulses);                  // 后续脉冲 real=0 不发事件
        Assert.Equal(99, pulses[0].DamageRaw);
        Assert.Equal(1, ber.Hp);
        Assert.True(ber.State != FighterState.Dead, "自伤不可致死（DDQ-B4-5）");
    }

    // ================= SG12: BER 嗜血奋战——霸体（SSA 数据）+ 正嗜血 10% =================

    [Fact]
    public void SG12_BerXixueFenzhan_Armor_Lifesteal()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BER", 0), (1, "BMG", 1));
        var ber = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(2);
        bmg.HeadingQuantum = 32768;

        Run(w, 1, 5, t => t == 1 ? new[] { Skill(0, "BER_U_001") } : Array.Empty<Command>());   // ATK+8%+SSA+自伤/正嗜血
        Assert.Equal(DeterministicMath.DivRoundHalfEven(8 * Fixed.ONE, 100), ber.BuffAtkPctQ);
        Assert.True(ber.LifestealTicks > 0, "正嗜血通道激活");

        // 霸体: 敌天击 t30 施法（su14）→ 命中 ≈t45，落在 armor 窗 [24,244) → 不硬直不中断
        Run(w, 6, 60, t => t == 30 ? new[] { Skill(1, "BMG_T1_001", 32768) } : Array.Empty<Command>());
        Assert.Equal(FighterState.Act, ber.State);   // 全程霸体: 无硬直/浮空
        Assert.NotEqual(0, ber.ActiveSkillUid);      // GDD §4.3: 霸体不被打断——技能继续
        Assert.True(ber.Hp < 10000, "霸体不减伤——伤害照常结算");
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.Launched && e.VictimId == 0);

        // 正嗜血: BER 命中 → 回复造成伤害的 10%。
        // v1 施法锁死: buff 技 act=持续时长（DDQ-B4-6）→ 执行体 t1251 才结束、lifesteal t1201 到期，
        // 窗口内无法出招——此处执行体结束后注入剩余域，单独验证 HitResolve 消耗路径。
        Run(w, 61, 1300);
        // 域对（PctQ+Ticks）同注: ticks 到期路径会清 PctQ——注入剩余窗口需两者一致
        ber.LifestealPctQ = DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100);
        ber.LifestealTicks = 600;
        Run(w, 1301, 1360, t => t == 1320 ? new[] { Skill(0, "BER_T1_001") } : Array.Empty<Command>());   // 倒斩 fan r2.8
        var hit = w.Events.All.Last(e => e.Kind == EventKind.Hit && e.AttackerId == 0);
        var heal = w.Events.All.Last(e => e.Kind == EventKind.Healed && e.VictimId == 0);
        Assert.Equal(DeterministicMath.MulShift(hit.DamageRaw, DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100)), heal.DamageRaw);
        Assert.True(hit.Tick >= 1330 && hit.Tick <= 1350, $"倒斩命中 t{hit.Tick}（预期 1330-1350）");
    }

    // ================= SG13: THF 陷阱精通——潜行设陷阱不解除 =================

    [Fact]
    public void SG13_ThfTrapMastery_StealthTrapKeepsHidden()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "THF", 0), (1, "BLA", 1));
        var thf = w.Fighters[0];
        w.Fighters[1].PosZ = Fixed.FromInt(3);   // 敌方离开陷阱位（避免落点即触发）
        Run(w, 1, 30, t => t == 1 ? new[] { Skill(0, "THF_T1_001") } : Array.Empty<Command>());   // 潜行 hold
        Assert.True(thf.Hidden);

        // 陷阱（deploy 技）替换潜行 hold → 隐身不解除
        Run(w, 31, 80, t => t == 50 ? new[] { Skill(0, "THF_T1_002") } : Array.Empty<Command>());   // 陷阱扣 deploy:r1.5:触发
        Assert.True(thf.Hidden, "陷阱精通: 设陷阱不解除潜行（GDD §14.17）");
        Assert.DoesNotContain(w.Events.All, e => e.Kind == EventKind.StealthBroken);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.UnitSpawned && e.Tick >= 50 && e.Tick <= 80);

        // 非部署技照常破隐
        Run(w, 81, 110, t => t == 100 ? new[] { Skill(0, "THF_T1_003") } : Array.Empty<Command>());   // 陷阱解除 circle（非 deploy）
        Assert.False(thf.Hidden);
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.StealthBroken && e.Tick >= 100);
    }

    [Fact]
    public void SG13b_NoSignature_AnyCastBreaksStealth()
    {
        // 负对照: 无签名注册 → 施法一律破隐（默认门控 true）
        var w = CreateWorld(null, (0, "THF", 0), (1, "BLA", 1));
        var thf = w.Fighters[0];
        Run(w, 1, 30, t => t == 1 ? new[] { Skill(0, "THF_T1_001") } : Array.Empty<Command>());
        Assert.True(thf.Hidden);
        Run(w, 31, 55, t => t == 50 ? new[] { Skill(0, "THF_T1_002") } : Array.Empty<Command>());
        Assert.False(thf.Hidden, "无陷阱精通: 设陷阱也解除潜行");
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.StealthBroken);
    }

    // ================= SG14: SBL 杀意波动——共鸣层（伤害 +4%/档 + 前摇 −1f/层） =================

    [Fact]
    public void SG14_SblWaveResonance_Stacks_Damage_Startup()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "SBL", 0), (1, "BLA", 1));
        var sbl = w.Fighters[0];
        w.Fighters[1].PosZ = Fixed.FromInt(3);
        ushort uidA = Catalog.IdMap["SBL_T1_001"];

        // 施法序列: A(t15)→1 档 / B(t100)→2 档 / A(t400)→3 档 / A(t700)→同把链断回 1 档。
        // v1 语义: 施法读到的层数=上一手累计（本次 +1 归下一手生效）——发射 Tick 与之对应。
        Command[] Sched(int t) => t switch
        {
            15 => new[] { Skill(0, "SBL_T1_001") },
            100 => new[] { Skill(0, "SBL_T1_002") },
            400 => new[] { Skill(0, "SBL_T1_001") },
            700 => new[] { Skill(0, "SBL_T1_001") },
            _ => Array.Empty<Command>(),
        };
        Run(w, 1, 99, Sched);
        Assert.Equal(1, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);
        Run(w, 100, 399, Sched);
        Assert.Equal(2, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);
        Run(w, 400, 699, Sched);
        Assert.Equal(3, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);
        Run(w, 700, 760, Sched);
        Assert.Equal(1, sbl.ResourceCounts[(int)SimWorld.ResourceSlotKind.Resonance]);

        // 前摇 −1f/层（施法读上一手累计层——本次 +1 归后效）→ 发射 Tick（= cast+effective−1，
        // cast 当 Tick offset 已递增）: A#1(0 档 su12)=26 / B(1 档 su7)=106 /
        // A#3(2 档 su10)=409 / A#4(3 档 su9)=708
        var spawns = w.Events.All.Where(e => e.Kind == EventKind.ProjectileSpawned).Select(e => e.Tick).ToList();
        Assert.Equal(new List<long> { 26, 106, 409, 708 }, spawns);

        // 伤害乘区: 3 档命中伤害 = 1 档 × ~1.0769（1.12/1.04，容差覆盖整数舍入）
        var hitsA = w.Events.All.Where(e => e.Kind == EventKind.Hit && e.AttackerId == 0).ToList();
        Assert.True(hitsA.Count >= 3, $"A/B/A/A 四次施放应 ≥3 次命中，实际 {hitsA.Count}");
        double ratio = (double)hitsA[2].DamageRaw / hitsA[0].DamageRaw;   // A#3（3 档）/ A#1（1 档）
        Assert.True(ratio > 1.05 && ratio < 1.10, $"3 档/1 档伤害比 {ratio:F4}（期望 ≈1.0769）");
    }

    // ================= SG15: KNI 骑士精神——重置除本技外全部 CD =================

    [Fact]
    public void SG15_KniKnightSpirit_ResetsAllCooldowns()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "KNI", 0), (1, "BLA", 1));
        var kni = w.Fighters[0];
        w.Fighters[1].PosZ = Fixed.FromInt(2);
        Run(w, 1, 20, t => t == 15 ? new[] { Skill(0, "KNI_T1_001") } : Array.Empty<Command>());   // 击退 cd=420
        Assert.True(kni.Cooldowns.ContainsKey(Catalog.IdMap["KNI_T1_001"]));

        Run(w, 21, 55, t => t == 50 ? new[] { Skill(0, "KNI_U_001") } : Array.Empty<Command>());   // 骑士精神
        Assert.Single(kni.Cooldowns);
        Assert.True(kni.Cooldowns.ContainsKey(Catalog.IdMap["KNI_U_001"]), "除本技外全部重置——本技 CD 保留");
        Assert.True(kni.State == FighterState.Act, "骑士精神生效中");
    }

    // ================= SG16: 新状态域快照恢复 + 指令流重放逐位一致 =================

    [Fact]
    public void SG16_Batch4_SnapshotRestore_BitwiseIdentical()
    {
        var fighters = new (int, string, byte)[] { (0, "SBL", 0), (1, "BER", 0), (2, "KNI", 0), (3, "THF", 0), (4, "BLA", 1) };

        var auth = CreateWorld(DefaultRegistry(), fighters);
        var log = new List<Command>();
        RunRange(auth, log, 1, 700);

        var client = CreateWorld(DefaultRegistry(), fighters);
        var clientLog = new List<Command>();
        RunRange(client, clientLog, 1, 350);
        var snap = client.CaptureSnapshot();
        var restored = CreateWorld(DefaultRegistry(), fighters);
        restored.RestoreSnapshot(snap);
        ReplayRange(restored, clientLog, 351, 700);

        Assert.True(auth.CaptureSnapshot().BitwiseEquals(restored.CaptureSnapshot()),
            "Batch 4 新状态域（Drain/Lifesteal/Resonance/LastCast）快照恢复 + 相同指令 ⇒ 逐位一致");
    }

    private static Command[] CommandsFor(int t)
    {
        return t switch
        {
            15 => new[] { Skill(0, "SBL_T1_001") },
            40 => new[] { Skill(0, "SBL_T1_002") },
            60 => new[] { Skill(1, "BER_T4_003") },
            90 => new[] { Skill(2, "KNI_T1_001") },
            120 => new[] { Skill(2, "KNI_U_001") },
            150 => new[] { Skill(3, "THF_T1_001") },
            180 => new[] { Skill(3, "THF_T1_002") },
            200 => new[] { Skill(3, "THF_T1_003") },
            240 => new[] { Skill(0, "SBL_T1_001") },
            300 => new[] { Skill(1, "BER_T1_001") },
            _ => Array.Empty<Command>(),
        };
    }

    private static void RunRange(SimWorld w, List<Command> log, int from, int to)
    {
        for (int t = from; t <= to; t++)
        {
            var cmds = CommandsFor(t);
            w.Step(t, cmds);
            foreach (var cmd in cmds) log.Add(cmd);
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
