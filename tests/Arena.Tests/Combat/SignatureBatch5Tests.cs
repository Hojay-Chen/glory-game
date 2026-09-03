using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Calc;
using Arena.Core.Sim;
using Arena.Core.Sim.Signatures;
using Arena.Infra.Data;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 7 Batch 5: 动作窗/效果生命周期解耦（DDQ-B4-①裁定落地）+ 资源闭环 + 动态施放。
/// SG17 buff 解耦 / SG18 SPF 弹匣闭环 / SG19 BMG 炫纹发射闭环+追踪 / SG20 ROG 动态施放 / SG21 迁移构成报告。
public class SignatureBatch5Tests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private SimWorld CreateWorld(SignatureRegistry? reg, params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0xB5C1_0001L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters)
        {
            switch (cls)
            {
                case "BMG": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Orb, 7); break;
                case "SPF": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Magazine, 20); break;
            }
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        }
        w.SealWorld();
        if (reg is not null) w.InstallSignatures(reg);
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);
    private static Command Basic(int f) => new(f, CmdKind.Basic, 0, 0, 0, 0);

    private static void Run(SimWorld w, int from, int to, Func<int, Command[]>? schedule = null)
    {
        for (int t = from; t <= to; t++) w.Step(t, schedule?.Invoke(t) ?? Array.Empty<Command>());
    }

    public void Dispose() { }

    // ================= SG17: buff 解耦——动作窗与效果生命周期分离 =================

    [Fact]
    public void SG17_BuffDecouple_Action_Free_Effect_Persists()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BER", 0), (1, "BLA", 1));
        var ber = w.Fighters[0];
        Run(w, 1, 1, t => new[] { Skill(0, "BER_T4_003") });   // 嗜血: act=20s（效果持续借位）
        // 动作窗已解耦: 12 su + 2 act + 14 rec → t30 已恢复自由（旧实现锁身 1200T）
        Run(w, 2, 60);
        Assert.Equal(FighterState.Normal, ber.State);
        // 效果生命周期独立承载: ATK+20% 仍在持续（1200T 效果窗，扣 60T 已流逝）
        Assert.Equal(DeterministicMath.DivRoundHalfEven(20 * Fixed.ONE, 100), ber.BuffAtkPctQ);
        Assert.True(ber.BuffAtkPctTicks > 1100 && ber.BuffAtkPctTicks <= 1140,
            $"效果持续独立: ticks={ber.BuffAtkPctTicks}（预期 ~1140）");
    }

    // ================= SG18: SPF 弹匣资源闭环（消耗 → 干火 → 装填回满） =================

    [Fact]
    public void SG18_Spf_Magazine_Consume_Dry_Reload()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "SPF", 0), (1, "BLA", 1));
        var spf = w.Fighters[0];
        w.Fighters[1].PosZ = Fixed.FromInt(3);
        int mSlot = (int)SimWorld.ResourceSlotKind.Magazine;
        Assert.Equal(20, spf.ResourceCaps[mSlot]);   // class-base 弹匣:20（弹药扩充被动常驻有效值）

        // 20 发消耗: 每发 −1（普攻消耗 1/击——SPF_BAS_001 proj 20m 命中敌人）
        var schedule = new Dictionary<int, Command[]>();
        for (int k = 0; k < 20; k++) schedule[1 + k * 12] = new[] { Basic(0) };
        Run(w, 1, 250, t => schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());
        Assert.Equal(0, spf.ResourceCounts[mSlot]);
        int castsBefore = w.Events.All.Count(e => e.Kind == EventKind.SkillCast && e.AttackerId == 0);
        Assert.Equal(20, castsBefore);

        // 第 21 发: 空匣干火失败（无新 SkillCast）
        Run(w, 251, 262, t => t == 253 ? new[] { Basic(0) } : Array.Empty<Command>());
        Assert.Equal(20, w.Events.All.Count(e => e.Kind == EventKind.SkillCast && e.AttackerId == 0));

        // 装填（冰弹装填——施放动作即换弹 GDD §14.5）→ 回满 cap
        Run(w, 263, 320, t => t == 270 ? new[] { Skill(0, "SPF_T1_004") } : Array.Empty<Command>());
        Assert.Equal(20, spf.ResourceCounts[mSlot]);
    }

    // ================= SG19: BMG 炫纹发射资源闭环 + 追踪弹 =================

    [Fact]
    public void SG19_Bmg_Orb_Launch_Consume_Barrage_Buffs_Homing()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "BMG", 0), (1, "BLA", 1));
        var bmg = w.Fighters[0];
        // 预置 3 炫纹: 冰×2 + 火×1（Σ==Orb 槽不变式）
        bmg.ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] = 3;
        bmg.OrbTypeCounts[(int)OrbTagKind.Ice] = 2;
        bmg.OrbTypeCounts[(int)OrbTagKind.Fire] = 1;
        w.Fighters[1].PosX = Fixed.FromInt(5);   // 敌方偏离 +Z 轴——追踪须转向

        Run(w, 1, 10, t => t == 1 ? new[] { Skill(0, "BMG_T1_006") } : Array.Empty<Command>());
        ushort launchUid = Catalog.IdMap["BMG_T1_006"];

        // 全弹幕: 基线 1 + 补 2 = 3 发同刻发射（su4 → cast+4−1=t4）
        var spawns = w.Events.All.Where(e => e.Kind == EventKind.ProjectileSpawned && e.SkillId == launchUid).ToList();
        Assert.Equal(3, spawns.Count);
        Assert.All(spawns, s => Assert.Equal(4, s.Tick));

        // 资源消耗闭环: Orb 槽清零 + 类型分布清零（不变式保持）
        Assert.Equal(0, bmg.ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb]);
        Assert.All(bmg.OrbTypeCounts, n => Assert.Equal(0, n));

        // 按型增益（GDD §14.1.3: 冰=物防+4%/枚、火=ATK+5%/枚，20s）
        Assert.Equal(2 * DeterministicMath.DivRoundHalfEven(4 * Fixed.ONE, 100), bmg.BuffDefPctQ);
        Assert.Equal(DeterministicMath.DivRoundHalfEven(5 * Fixed.ONE, 100), bmg.BuffAtkPctQ);
        Assert.True(bmg.BuffDefPctTicks > 1100, "增益 20s 独立生命周期");

        // 追踪: 弹体朝向自 +Z（0）向目标（+X 方向 ≈ 顺时针 16384）饱和转向
        Run(w, 11, 80);
        var projs = w.Projectiles.Where(p => !p.Expired && p.SkillRuntimeId == launchUid).ToList();
        Assert.NotEmpty(projs);
        Assert.Contains(projs, p => p.HeadingQuantum != 0 && p.HeadingQuantum != 65536);
        var homed = projs.OrderByDescending(p => Math.Abs(((p.HeadingQuantum % 65536) + 65536) % 65536)).First();
        Assert.True(((homed.HeadingQuantum % 65536) + 65536) % 65536 > 4096,
            $"追踪弹已转向（heading={homed.HeadingQuantum}）");
    }

    // ================= SG20: ROG 以牙还牙动态施放 =================

    [Fact]
    public void SG20_Rog_Payback_Dynamic_Cast()
    {
        var w = CreateWorld(DefaultRegistry(), (0, "ROG", 0), (1, "BLA", 1));
        var rog = w.Fighters[0];
        var bla = w.Fighters[1];
        bla.PosZ = Fixed.FromInt(2);
        bla.HeadingQuantum = 0;   // 面朝 +Z（背对 ROG—— ROG 在 −Z 侧……实际 ROG z=0、BLA z=2: BLA 背对）

        // 敌方上挑命中 ROG → 记录（T1 < U 档，非普攻）
        Run(w, 1, 60, t => t == 10 ? new[] { Skill(1, "BLA_T1_001", 32768) } : Array.Empty<Command>());
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 0 && e.SkillId == Catalog.IdMap["BLA_T1_001"]);
        Assert.Contains(Catalog.IdMap["BLA_T1_001"], rog.CopiedSkillUids);

        // 施放以牙还牙 → 动态重定向执行上挑（MP = 原技 30 ×2 = 60；CD 记按键技 30s）
        rog.Mp = 1000;
        // 逐 Tick 重试施法——上挑浮空+倒地期间拒绝（受击不可出招铁则），恢复 Normal 首拍成交（CD 1800T 保证仅一次）
        Run(w, 200, 320, t => new[] { Skill(0, "ROG_T4_001") });
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.SkillCast && e.AttackerId == 0 && e.SkillId == Catalog.IdMap["BLA_T1_001"]);
        Assert.True(rog.Mp < 975 && rog.Mp >= 940, $"MP = 1000 − 60(×2) + 回复: {rog.Mp}");
        Assert.True(rog.Cooldowns.ContainsKey(Catalog.IdMap["ROG_T4_001"]), "CD 记在按键技");
        Assert.False(rog.Cooldowns.ContainsKey(Catalog.IdMap["BLA_T1_001"]), "CD 不记被复制技");
        // 复制技真实执行: 上挑命中敌方（浮空）
        Assert.Contains(w.Events.All, e => e.Kind == EventKind.Hit && e.AttackerId == 0 && e.SkillId == Catalog.IdMap["BLA_T1_001"]);
    }

    // ================= SG21: 迁移构成报告（Data-driven / Primitive-assisted / Signature） =================

    [Fact]
    public void SG21_Migration_Composition_Report()
    {
        // 三类迁移构成（数据驱动启发式 v1——报告性探针，口径记录于 active.md）
        var sigClasses = new HashSet<string> { "BMG", "BER", "ASN", "QIM", "THF", "SBL", "KNI", "ROG" };
        var signatureSelfSkills = new HashSet<string> { "KNI_U_001", "ROG_T4_001", "BMG_T1_006" };
        int dataDriven = 0, primitive = 0, signature = 0;
        foreach (var def in Catalog.Skills)
        {
            bool prim = def.IsSummon || def.DeployKind != DeployKind.None || def.HealAmountQ > 0 ||
                def.FollowHeading || def.IsStealth || def.IsReflect || def.ProjHomingDegPerSec > 0 ||
                def.IsGrab || def.IsCounter || def.Guard is not null || def.ChargeTicks > 0 ||
                def.SelfBuffAtkPctQ != 0 || def.SelfDrainPctQ > 0 || def.LifestealPctQ > 0 || def.IsPureBuff;
            bool sig = sigClasses.Contains(def.ClassId) &&
                (def.Type == "passive" || def.OrbTag != OrbTagKind.None ||
                 def.Name.Contains("波动剑", StringComparison.Ordinal) ||
                 def.SkillId == "BMG_T1_006" || def.SkillId == "KNI_U_001" || def.SkillId == "ROG_T4_001" ||
                 (def.ClassId == "THF" && def.DeployKind == DeployKind.Trap));
            if (sig) signature++;
            else if (prim) primitive++;
            else dataDriven++;
        }
        Assert.Equal(Catalog.Skills.Count, dataDriven + primitive + signature);
        Console.WriteLine($"[composition] Data-driven={dataDriven} Primitive-assisted={primitive} Signature={signature} / total={Catalog.Skills.Count}");
    }
}
