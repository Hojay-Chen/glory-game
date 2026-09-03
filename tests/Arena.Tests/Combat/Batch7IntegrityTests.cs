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

/// Phase 7 Batch 7 上半段: Snapshot / Restore / Replay Integrity Closure。
/// IT01: 3000T 全机制混战 × 6 恢复点链式 restore × 全量指令日志重放——最终逐位一致。
/// IT02: 恢复后事件流非空证明（Replay 确实执行了真实 Command Stream——防假阳性空转）。
/// IT03: 实体关系转换审查（反射/抓取/召唤/复制技的 OwnerId 类状态全快照携带——逐域核对）。
public class Batch7IntegrityTests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private static SimWorld CreateWorld(params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0xB7D0_0001L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters)
        {
            switch (cls)
            {
                case "BMG": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Orb, 7); break;
                case "SBL": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Resonance, 3); break;
                case "SPF": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Magazine, 20); break;
                case "SUM": w.SetClassResource(cls, SimWorld.ResourceSlotKind.Summon, 4); break;
            }
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        }
        w.SealWorld();
        w.InstallSignatures(DefaultRegistry());
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);
    private static Command Basic(int f) => new(f, CmdKind.Basic, 0, 0, 0, 0);

    private static Command[] CommandsFor(int t) => t switch
    {
        15 => new[] { Skill(0, "BMG_T1_001") },              // 天击（浮空+获纹）
        40 => new[] { Skill(0, "BMG_T1_006") },              // 炫纹发射（资源闭环+弹幕+追踪）
        70 => new[] { Skill(1, "BER_T4_003") },              // 嗜血（自增益+自伤脉冲）
        90 => new[] { Skill(1, "BER_T1_001") },              // 倒斩（浮空）
        120 => new[] { Skill(2, "KNI_T3_003") },             // 法术反射（弹体关系转移）
        150 => new[] { Skill(5, "GRP_T1_001") },             // 背摔（抓取实体关系）
        180 => new[] { Skill(3, "BLA_T1_002") },             // 格挡（Guard Resolution）
        200 => new[] { Skill(4, "THF_T1_001") },             // 潜行
        240 => new[] { Skill(4, "THF_T2_001") },             // 陷阱（部署实体）
        300 => new[] { Skill(0, "BMG_T1_003") },             // 连突
        330 => new[] { Skill(1, "BER_T2_001") },             // 冲撞刺击（突进）
        360 => new[] { Skill(2, "KNI_U_001") },              // 骑士精神（CD 重置）
        380 => new[] { Skill(2, "KNI_T1_001") },             // 重置后击退
        420 => new[] { Skill(3, "BLA_T1_001") },             // 上挑
        450 => new[] { Skill(5, "GRP_T1_003") },             // 单手擒
        500 => new[] { Skill(0, "BMG_T1_006") },             // 再发射（跨恢复点弹幕）
        560 => new[] { Skill(1, "BER_T1_002") },             // 重击
        600 => new[] { Skill(3, "BLA_T1_001") },             // 上挑
        640 => new[] { Skill(4, "THF_T1_003") },             // 陷阱解除（破隐）
        700 => new[] { Skill(0, "BMG_T1_004") },             // 落花掌（吹飞）
        750 => new[] { Skill(1, "BER_T3_003") },             // 噬魂血手（范围抓取）
        800 => new[] { Skill(5, "GRP_T2_004") },             // 头上拂
        850 => new[] { Basic(3) },                           // BLA 普攻链
        862 => new[] { Basic(3) },
        874 => new[] { Basic(3) },
        900 => new[] { Skill(0, "BMG_T1_006") },             // 第三次发射
        1000 => new[] { Skill(3, "BLA_T1_001") },            // 上挑
        1100 => new[] { Skill(2, "KNI_T1_001") },            // 击退
        1200 => new[] { Skill(0, "BMG_T1_003") },            // 连突
        1300 => new[] { Skill(1, "BER_T1_001") },            // 倒斩
        1500 => new[] { Skill(0, "BMG_T1_006") },            // 第四次发射
        1600 => new[] { Skill(4, "THF_T2_001") },            // 第二个陷阱（跨恢复点部署实体）
        1700 => new[] { Skill(2, "KNI_U_001") },             // 第二次骑士精神（CD 重置）
        1720 => new[] { Skill(2, "KNI_T1_001") },            // 击退
        1900 => new[] { Skill(1, "BER_T2_001") },            // 冲撞刺击
        2100 => new[] { Skill(0, "BMG_T1_001") },            // 天击
        2300 => new[] { Skill(5, "GRP_T1_001") },            // 背摔
        2500 => new[] { Skill(0, "BMG_T1_006") },            // 第五次发射
        2600 => new[] { Skill(3, "BLA_T1_002") },            // 格挡
        2800 => new[] { Skill(1, "BER_T1_001") },            // 倒斩
        _ => Array.Empty<Command>(),
    };

    public void Dispose() { }

    // ================= IT01: 3000T × 6 恢复点链式 Integrity =================

    [Fact]
    public void IT01_LongMelee_MultiRestore_Chain_Bitwise()
    {
        const int Total = 3000;
        const int Segment = 500;
        var fighters = new (int, string, byte)[]
        {
            (0, "BMG", 0), (1, "BER", 0), (2, "KNI", 0),
            (3, "BLA", 1), (4, "THF", 1), (5, "GRP", 1),
        };

        // 权威全程 + 全量指令日志（打戳）
        var auth = CreateWorld(fighters);
        var authLog = new List<Command>();
        var authSnaps = new Dictionary<int, Arena.Core.Snapshot.SnapshotData>();
        for (int t = 1; t <= Total; t++)
        {
            var cmds = CommandsFor(t);
            auth.Step(t, cmds);
            foreach (var c in cmds) authLog.Add(c with { TargetTick = t });
            if (t % Segment == 0) authSnaps[t] = auth.CaptureSnapshot();
        }

        // 链式: seg K 跑 500T → 快照 → 恢复进新世界 → 重放下一段（共 5 次恢复）
        var log = new List<Command>();
        var current = CreateWorld(fighters);
        for (int seg = 0; seg < Total / Segment; seg++)
        {
            int from = seg * Segment + 1, to = (seg + 1) * Segment;
            var byTick = new Dictionary<int, List<Command>>();
            foreach (var c in authLog)
                if (c.TargetTick >= from && c.TargetTick <= to)
                {
                    if (!byTick.TryGetValue(c.TargetTick, out var l)) byTick[c.TargetTick] = l = new List<Command>();
                    l.Add(c);
                }
            for (int t = from; t <= to; t++)
                current.Step(t, byTick.TryGetValue(t, out var l) ? l.ToArray() : Array.Empty<Command>());

            // 段末与权威同 tick 快照逐位一致
            Assert.True(current.CaptureSnapshot().BitwiseEquals(authSnaps[to]),
                $"恢复链段 {from}-{to} 末态与权威不一致");

            if (to < Total)
            {
                // 恢复进新世界（真实 restore——非延续同一实例）
                var snap = current.CaptureSnapshot();
                current = CreateWorld(fighters);
                current.RestoreSnapshot(snap);
                // 恢复即时自反: 恢复后的世界重捕获 == 原快照
                Assert.True(current.CaptureSnapshot().BitwiseEquals(snap), $"段 {to} 恢复自反不一致");
            }
        }
    }

    // ================= IT02: 重放真实性证明（防假阳性空转） =================

    [Fact]
    public void IT02_Replay_Executes_Real_Command_Stream()
    {
        // 重放段的事件流必须包含真实战斗行为（SkillCast/Hit）——证明非空转 PASS
        var fighters = new (int, string, byte)[] { (0, "BMG", 0), (1, "BER", 0), (2, "BLA", 1) };
        var auth = CreateWorld(fighters);
        var authLog = new List<Command>();
        for (int t = 1; t <= 1000; t++)
        {
            var cmds = CommandsFor(t);
            auth.Step(t, cmds);
            foreach (var c in cmds) authLog.Add(c with { TargetTick = t });
        }

        var client = CreateWorld(fighters);
        for (int t = 1; t <= 500; t++) client.Step(t, CommandsFor(t));
        var snap = client.CaptureSnapshot();
        var restored = CreateWorld(fighters);
        restored.RestoreSnapshot(snap);

        var byTick = new Dictionary<int, List<Command>>();
        foreach (var c in authLog)
            if (c.TargetTick > 500)
            {
                if (!byTick.TryGetValue(c.TargetTick, out var l)) byTick[c.TargetTick] = l = new List<Command>();
                l.Add(c);
            }
        int replayedCommands = 0;
        for (int t = 501; t <= 1000; t++)
        {
            var cmds = byTick.TryGetValue(t, out var l) ? l.ToArray() : Array.Empty<Command>();
            replayedCommands += cmds.Length;
            restored.Step(t, cmds);
        }
        Assert.True(replayedCommands > 0, "重放段必须消费真实指令");
        // 重放段产生了真实战斗事件（SkillCast ≥ 3——指令确实进入权威链路）
        Assert.True(restored.Events.All.Count(e => e.Kind == EventKind.SkillCast) >= 3,
            $"恢复后重放段 SkillCast={restored.Events.All.Count(e => e.Kind == EventKind.SkillCast)}");
        Assert.True(restored.Events.All.Any(e => e.Kind == EventKind.Hit), "重放段产生命中");
    }

    // ================= IT03: 实体关系转换审查（状态域完整性核对） =================

    [Fact]
    public void IT03_EntityRelation_StateDomain_Audit()
    {
        // 类别审查（Batch 6 裁定）: 所有「实体关系发生转移」的机制，其状态必须进入
        // Fighter/实体快照域。核对表（快照 SimWorldSnapshot.cs 逐域比对）:
        //   法术反射        → ReflectTicks ✓ + Projectile.OwnerId/TargetId/HeadingQuantum ✓（Batch 6 修）
        //   抓取            → GrabbedBy/GrabThrowSkill ✓ + GrabReleased 路径 ✓
        //   召唤物/部署实体 → Units 全字段 + _nextUnitUid ✓（Batch 6 修）+ Spec 单源重建 ✓
        //   动态施法（复制技）→ CopiedSkillUids[3]/CopiedSkillNext ✓
        //   可控弹          → Projectile.Disp/TargetId/HeadingQuantum ✓ + 施法者 HeadingQuantum ✓
        //   追踪弹          → TargetId/HeadingQuantum ✓（Batch 6 修）
        //   资源槽          → ResourceCounts/Caps + OrbTypeCounts[6]/LastCastSkillUid/Resonance ✓
        //   输入缓冲        → _inputBuffers ✓（Batch 6 修）
        //   Buff/霸体域     → BuffAtkPct/BuffDefPct/BuffDrain/Lifesteal/BuffArmor ✓
        //   骑士精神 CD 重置 → Cooldowns ✓
        // 本探针以行为验证代表性关系: 抓取-释放 + 反射-回击 在快照恢复后仍正确延续。
        var w = CreateWorld((0, "KNI", 0), (1, "BMG", 1));
        var kni = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(4);

        var schedule = new Dictionary<int, Command[]>
        {
            [20] = new[] { Skill(0, "KNI_T3_003") },             // 反射架势
            [30] = new[] { Skill(1, "BMG_T1_006", 32768) },      // 敌方追踪弹
        };
        for (int t = 1; t <= 45; t++) w.Step(t, schedule.TryGetValue(t, out var c) ? c : Array.Empty<Command>());
        Assert.True(w.Events.All.Any(e => e.Kind == EventKind.Reflected), "反射触发（实体关系转移）");
        var proj = w.Projectiles.First(p => !p.Expired);
        Assert.Equal(0, proj.OwnerId);                        // OwnerId 已转移给反射者
        Assert.Equal(1, proj.TargetId);                       // 追踪目标重锁为原攻击者

        // 快照恢复后: 弹体关系域（OwnerId/TargetId/Heading）原样延续且继续追击
        var snap = w.CaptureSnapshot();
        var restored = CreateWorld((0, "KNI", 0), (1, "BMG", 1));
        restored.RestoreSnapshot(snap);
        var rproj = restored.Projectiles.First(p => !p.Expired);
        Assert.Equal(0, rproj.OwnerId);
        Assert.Equal(1, rproj.TargetId);
        Assert.Equal(proj.HeadingQuantum, rproj.HeadingQuantum);
        for (int t = 46; t <= 120; t++) restored.Step(t, Array.Empty<Command>());
        Assert.True(bmg.Hp > restored.Fighters[1].Hp || restored.Fighters[1].Hp < 10000,
            "恢复后反射弹继续追击原攻击者（实体关系跨恢复延续）");
        Assert.True(restored.Events.All.Any(e => e.Kind == EventKind.Hit && e.VictimId == 1), "恢复后弹体命中原攻击者");
    }
}
