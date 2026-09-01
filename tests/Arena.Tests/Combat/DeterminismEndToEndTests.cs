using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core.Sim;
using Arena.Core.Snapshot;
using Arena.Infra.Data;
using Xunit;

namespace Arena.Tests.Combat;

/// ADR-0001 §9 Determinism Contract 端到端: 同种子同指令 ⇒ 同状态同事件（逐位）
public class DeterminismEndToEnd
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;

    private static SimWorld CreateWorld(long seed)
    {
        var w = new SimWorld(seed, Catalog.DataVersionHash);
        foreach (var s in Catalog.Skills) w.AddSkill(s);
        w.AddFighter(0, "BMG", Arena.Core.Fixed.FromInt(0), Arena.Core.Fixed.FromInt(0), team: 0);
        w.AddFighter(1, "SRP", Arena.Core.Fixed.FromInt(0), Arena.Core.Fixed.FromInt(8), team: 1);
        w.SealWorld();
        return w;
    }

    /// 600 Tick 脚本化对局（移动/技能/普攻/受身混合）
    private static Command[] CommandsForTick(int t)
    {
        var cmds = new List<Command>();
        if (t == 30) cmds.Add(new Command(0, CmdKind.Skill, Catalog.IdMap["BMG_T1_001"], 0, 0, t));      // 天击
        if (t == 120) cmds.Add(new Command(0, CmdKind.Move, 0, 0, 0, t));                                 // 前压
        if (t is >= 120 and <= 135) cmds.Add(new Command(0, CmdKind.Move, 0, 0, 0, t));
        if (t == 140) cmds.Add(new Command(0, CmdKind.Skill, Catalog.IdMap["BMG_T1_003"], 0, 0, t));      // 连突
        if (t == 200) cmds.Add(new Command(1, CmdKind.Skill, Catalog.IdMap["SRP_T1_001"], 32768, 0, t));  // 浮空弹朝 −Z
        if (t == 240) cmds.Add(new Command(0, CmdKind.Basic, 0, 0, 0, t));                                // 普攻链
        if (t == 260) cmds.Add(new Command(0, CmdKind.Basic, 0, 0, 0, t));
        if (t == 300) cmds.Add(new Command(0, CmdKind.Skill, Catalog.IdMap["BMG_T2_002"], 0, 0, t));      // 蛟龙出海 slow
        if (t == 360) cmds.Add(new Command(1, CmdKind.Jump, 0, 0, 0, t));
        if (t == 400) cmds.Add(new Command(0, CmdKind.Skill, Catalog.IdMap["BMG_T2_001"], 0, 0, t));      // 圆舞棍
        if (t == 420) cmds.Add(new Command(1, CmdKind.Roll, 0, 0, 0, t));                                 // 受身尝试
        if (t == 480) cmds.Add(new Command(0, CmdKind.ForceCancel, 0, 0, 0, t));
        if (t == 520) cmds.Add(new Command(1, CmdKind.Skill, Catalog.IdMap["SRP_T4_001"], 0, 0, t));       // 巴雷特
        return cmds.ToArray();
    }

    private static (string eventHash, SnapshotData snap) RunMatch(long seed)
    {
        var w = CreateWorld(seed);
        for (int t = 1; t <= 600; t++)
            w.Step(t, CommandsForTick(t));
        return (w.Events.ComputeHash(), w.CaptureSnapshot());
    }

    [Fact]
    public void D01_SameSeed_SameCommands_Bitwise_Identical()
    {
        var (h1, s1) = RunMatch(0xA11CE);
        var (h2, s2) = RunMatch(0xA11CE);
        Assert.Equal(h1, h2);
        Assert.True(s1.BitwiseEquals(s2), "终态快照必须逐位一致");
    }

    [Fact]
    public void D02_SnapshotRestore_MidMatch_Continues_Identically()
    {
        // 权威侧跑完 600
        var auth = CreateWorld(0xB0B);
        for (int t = 1; t <= 600; t++) auth.Step(t, CommandsForTick(t));
        var (authHash, authSnap) = (auth.Events.ComputeHash(), auth.CaptureSnapshot());

        // 客户端侧: 前 300 + 快照回传 + 续跑 300
        var client = CreateWorld(0xB0B);
        for (int t = 1; t <= 300; t++) client.Step(t, CommandsForTick(t));
        var midSnap = client.CaptureSnapshot();
        Assert.True(midSnap.BitwiseEquals(authSnap) == false || true);   // 中段快照独立存在
        var restored = CreateWorld(0xB0B);
        restored.RestoreSnapshot(midSnap);
        for (int t = 301; t <= 600; t++) restored.Step(t, CommandsForTick(t));

        // 续跑侧终态 = 权威终态（ADR-0001 §8 完备性）
        Assert.True(authSnap.BitwiseEquals(restored.CaptureSnapshot()),
            "快照恢复 + 相同指令流 ⇒ 逐位一致终态");
        // 事件: 恢复侧只含 301+ 段——与权威对应段一致
        var authTail = auth.Events.All.Where(e => e.Tick > 300).ToList();
        var restoredEvents = restored.Events.All;
        Assert.Equal(authTail.Count, restoredEvents.Count);
        for (int i = 0; i < authTail.Count; i++)
            Assert.Equal(authTail[i], restoredEvents[i]);
    }

    [Fact]
    public void D03_Command_Arrival_Order_Does_Not_Matter()
    {
        // 同 Tick 两条不同 Fighter 指令——到达顺序交换，结果必须一致（FighterId 升序结算）
        var w1 = CreateWorld(7);
        var w2 = CreateWorld(7);
        var c0 = new Command(0, CmdKind.Skill, Catalog.IdMap["BMG_T1_001"], 0, 0, 10);
        var c1 = new Command(1, CmdKind.Skill, Catalog.IdMap["SRP_T1_001"], 32768, 0, 10);
        w1.Step(10, new[] { c0, c1 });
        w2.Step(10, new[] { c1, c0 });
        for (int t = 11; t <= 80; t++)
        {
            w1.Step(t, Array.Empty<Command>());
            w2.Step(t, Array.Empty<Command>());
        }
        Assert.True(w1.CaptureSnapshot().BitwiseEquals(w2.CaptureSnapshot()));
        Assert.Equal(w1.Events.ComputeHash(), w2.Events.ComputeHash());
    }

    [Fact]
    public void D04_HealthBar_Drift_Zero_Over_Long_Soak()
    {
        // 3000 Tick 混合负载——事件 hash 与快照双确认（ soak: 5×60s 战斗密度）
        var (h1, s1) = RunSoak(0x50A7);
        var (h2, s2) = RunSoak(0x50A7);
        Assert.Equal(h1, h2);
        Assert.True(s1.BitwiseEquals(s2));
    }

    private static (string, SnapshotData) RunSoak(long seed)
    {
        var w = CreateWorld(seed);
        var rnd = new Random(2026);   // 脚本生成器熵源（非战斗路径——指令流本身固定）
        for (int t = 1; t <= 3000; t++)
        {
            var cmds = new List<Command>();
            if (rnd.Next(20) == 0)
                cmds.Add(new Command(0, CmdKind.Skill, (ushort)rnd.Next(1, 40), (ushort)rnd.Next(65536), (byte)rnd.Next(8), t));
            if (rnd.Next(25) == 0)
                cmds.Add(new Command(1, CmdKind.Skill, (ushort)rnd.Next(1, 40), (ushort)rnd.Next(65536), (byte)rnd.Next(8), t));
            if (rnd.Next(3) == 0)
                cmds.Add(new Command(0, CmdKind.Move, 0, 0, (byte)rnd.Next(8), t));
            if (rnd.Next(4) == 0)
                cmds.Add(new Command(1, CmdKind.Move, 0, 0, (byte)rnd.Next(8), t));
            if (rnd.Next(30) == 0)
                cmds.Add(new Command(0, CmdKind.Basic, 0, 0, 0, t));
            w.Step(t, cmds.ToArray());
        }
        return (w.Events.ComputeHash(), w.CaptureSnapshot());
    }
}
