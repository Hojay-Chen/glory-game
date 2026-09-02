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

/// Phase 7 Batch 3 边界复核探针:
/// SG08 多人同职业签名隔离 / SG09 签名状态 Snapshot 恢复 / SG10 QIM buff 域伤害链消费。
public class SignatureBoundaryTests : IDisposable
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SignatureRegistry DefaultRegistry() => Batch3Shared.DefaultRegistry();

    private SimWorld CreateWorld(SignatureRegistry? reg, params (int id, string cls, byte team)[] fighters)
    {
        var w = new SimWorld(0x9EED_1234L, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        foreach (var (id, cls, team) in fighters)
        {
            var cap = cls switch
            {
                "BMG" => (SimWorld.ResourceSlotKind.Orb, 7L),
                _ => (SimWorld.ResourceSlotKind.Orb, 0L),
            };
            w.SetClassResource(cls, cap.Item1, cap.Item2);
            w.AddFighter(id, cls, Fixed.FromInt(0), Fixed.FromInt(0), team);
        }
        w.SealWorld();
        if (reg is not null) w.InstallSignatures(reg);
        return w;
    }

    private static Command Skill(int f, string id, ushort aim = 0) => new(f, CmdKind.Skill, Catalog.IdMap[id], aim, 0, 0);

    private static void Run(SimWorld w, int from, int to)
    {
        for (int t = from; t <= to; t++) w.Step(t, Array.Empty<Command>());
    }

    public void Dispose() { }

    // ================= SG08: 多人同职业签名隔离 =================

    [Fact]
    public void SG08_SameClass_MultiFighter_Signature_Isolation()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg,
            (0, "BMG", 0), (1, "BMG", 0),       // 同职业 × 2（同队）
            (2, "BER", 0), (3, "BER", 0),       // 同职业 × 2（同队）
            (4, "BLA", 1));                      // 共同敌方
        var bl = w.Fighters[4];
        bl.PosZ = Fixed.FromInt(2);

        // BMG#0 天击命中 → 只有 #0 获得炫纹；#1 资源池不受影响
        w.Step(1, new[] { Skill(0, "BMG_T1_001") });
        Run(w, 2, 30);
        Assert.True(w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] >= 1,
            "BMG#0 命中后获得炫纹");
        Assert.Equal(0, w.Fighters[1].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb]);

        // BMG#1 也命中 → 各自资源池独立增长（目标状态重置——避免第一次天击的受击状态干扰）
        long orb0 = w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb];
        w.Fighters[4].State = FighterState.Normal;
        w.Fighters[4].Hp = 10000;
        w.Fighters[4].PosZ = Fixed.FromInt(2);
        w.Fighters[4].Statuses[(int)StatusKind.Burn].Active = false;
        w.Fighters[1].Cooldowns.Clear();
        w.Fighters[1].Mp = 1000;
        w.Fighters[1].PosZ = Fixed.FromInt(3);
        // 目标 BLA#4 在 −Z 方向（z=3→z=2）——aim=32768 朝 −Z（施法 aim 覆盖 heading）
        w.Step(50, new[] { Skill(1, "BMG_T1_001", 32768) });
        Run(w, 51, 80);
        Assert.True(w.Fighters[1].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb] >= 1,
            "BMG#1 命中后获得自己的炫纹");
        Assert.Equal(orb0, w.Fighters[0].ResourceCounts[(int)SimWorld.ResourceSlotKind.Orb]);

        // BER×2: 不同 HP → 各自独立 buff 域
        w.Fighters[2].Hp = 2500;   // 25% → +10%
        w.Fighters[3].Hp = 9000;   // 90% → 无 buff
        Run(w, 100, 110);
        long expected10 = DeterministicMath.DivRoundHalfEven(10 * Fixed.ONE, 100);
        Assert.Equal(expected10, w.Fighters[2].BuffAtkPctQ);
        Assert.Equal(0, w.Fighters[3].BuffAtkPctQ);
        // HP 变化 → 各自 buff 独立翻转
        w.Fighters[3].Hp = 1000;   // 10% → +15%
        Run(w, 111, 115);
        long expected15 = DeterministicMath.DivRoundHalfEven(15 * Fixed.ONE, 100);
        Assert.Equal(expected15, w.Fighters[3].BuffAtkPctQ);
        Assert.Equal(expected10, w.Fighters[2].BuffAtkPctQ);   // #2 不受 #3 状态变化影响
    }

    // ================= SG09: 签名状态 Snapshot 恢复（含同职业多人） =================

    [Fact]
    public void SG09_SignatureState_SnapshotRestore_BitwiseIdentical()
    {
        // 权威: 0..600 完整跑
        var auth = CreateWorld(DefaultRegistry(),
            (0, "BMG", 0), (1, "BMG", 0), (2, "BER", 0), (3, "ASN", 1));
        var authEvents = new List<Command>();
        RunRange(auth, authEvents, 1, 600);

        // 客户端: 0..300 → 快照 → restore → 301..600
        var client = CreateWorld(DefaultRegistry(),
            (0, "BMG", 0), (1, "BMG", 0), (2, "BER", 0), (3, "ASN", 1));
        var clientEvents = new List<Command>();
        RunRange(client, clientEvents, 1, 300);
        var midSnap = client.CaptureSnapshot();
        var restored = CreateWorld(DefaultRegistry(),
            (0, "BMG", 0), (1, "BMG", 0), (2, "BER", 0), (3, "ASN", 1));
        restored.RestoreSnapshot(midSnap);
        // 客户端事件流重放（与 auth 相同 tick-command 流——RunRange 用同序列表）
        ReplayRange(restored, clientEvents, 301, 600);

        Assert.True(auth.CaptureSnapshot().BitwiseEquals(restored.CaptureSnapshot()),
            "签名状态（Orb/Buff/Def 域）快照恢复 + 相同指令 ⇒ 逐位一致");
    }

    private static void RunRange(SimWorld w, List<Command> log, int from, int to)
    {
        for (int t = from; t <= to; t++)
        {
            var cmds = CommandsFor(t);
            w.Step(t, cmds);
            foreach (var c in cmds) log.Add(c);
        }
    }

    private static void ReplayRange(SimWorld w, List<Command> log, int from, int to)
    {
        var byTick = new Dictionary<int, List<Command>>();
        foreach (var c in log)
        {
            if (!byTick.TryGetValue(c.TargetTick, out var list))
                byTick[c.TargetTick] = list = new List<Command>();
            list.Add(c);
        }
        for (int t = from; t <= to; t++)
        {
            var cmds = byTick.TryGetValue(t, out var list) ? list.ToArray() : Array.Empty<Command>();
            w.Step(t, cmds);
        }
    }

    private static Command[] CommandsFor(int t)
    {
        var c = Catalog.IdMap;
        return t switch
        {
            15 => new[] { Skill(0, "BMG_T1_001") },                                  // BMG#0 天击 → 炫纹
            40 => new[] { Skill(1, "BMG_T1_001") },                                  // BMG#1 天击 → 炫纹
            70 => new[] { new Command(2, CmdKind.Skill, c["BMG_T1_001"], 32768, 0, t) }, // ASN 借用天击朝 −Z
            90 => Array.Empty<Command>(),
            120 => new[] { new Command(3, CmdKind.Skill, c["BMG_T1_002"], 0, 0, t) }, // ASN 龙牙朝 +Z
            _ => Array.Empty<Command>(),
        };
    }

    // ================= SG10: QIM BuffDefPct 伤害链消费 =================

    [Fact]
    public void SG10_QIM_DefBuff_Consumed_By_DamageChain()
    {
        var reg = DefaultRegistry();
        var w = CreateWorld(reg, (0, "QIM", 0), (1, "BMG", 1));
        var qim = w.Fighters[0];
        var bmg = w.Fighters[1];
        bmg.PosZ = Fixed.FromInt(2);
        bmg.HeadingQuantum = 0;

        // 高 MP: DEF+15% → 承伤较低
        qim.Mp = 900;
        Run(w, 1, 5);
        Assert.True(qim.BuffDefPctTicks > 0, "MP>70% 反射域激活");
        w.Step(6, new[] { new Command(1, CmdKind.Skill, Catalog.IdMap["BMG_T1_002"], 32768, 0, 6) });   // 龙牙朝 −Z 打 QIM
        Run(w, 7, 40);
        var hitHigh = w.Events.All.Last(e => e.Kind == EventKind.Hit && e.VictimId == 0);

        // 低 MP: DEF−10% → 承伤较高
        bmg.Cooldowns.Clear();
        bmg.Mp = 1000;
        qim.Mp = 200;
        Run(w, 41, 45);
        w.Step(46, new[] { new Command(1, CmdKind.Skill, Catalog.IdMap["BMG_T1_002"], 32768, 0, 46) });
        Run(w, 47, 90);
        var hitLow = w.Events.All.Last(e => e.Kind == EventKind.Hit && e.VictimId == 0);

        Assert.True(hitHigh.DamageRaw < hitLow.DamageRaw,
            $"DEF+15% 承伤 {hitHigh.DamageRaw} < DEF−10% 承伤 {hitLow.DamageRaw}");
        // 基准 Def 不被签名改写（权威状态域保持）
        Assert.Equal(800, qim.Def);
    }
}
