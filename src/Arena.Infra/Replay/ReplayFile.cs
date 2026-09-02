using System;
using System.Collections.Generic;
using Arena.Core.Sim;
using Arena.Core.Snapshot;
// PRODUCTION - Arena.Infra.Replay
// ADR-0005: ReplayFile v1 最小闭环——种子 + dataVersionHash + 指令流（事件不记录，重演重算）。
// 确定性契约: 同 Replay（种子+数据版本+指令流）⇒ 同事件流（事件 hash 校验=违约定位手段）。
namespace Arena.Infra.Replay;

public sealed record ReplayCommand(int Tick, int FighterId, byte Kind, ushort SkillId, ushort AimQuantum, byte DirIndex, int TargetTick);

public sealed class ReplayFile
{
    public long MatchSeed { get; init; }
    public string DataVersionHash { get; init; } = "";
    public string EventHash { get; set; } = "";        // 权威事件流 hash（违约定位）
    private readonly List<ReplayCommand> _commands = new();
    public IReadOnlyList<ReplayCommand> Commands => _commands;

    public void Add(int tick, in Command c) =>
        _commands.Add(new ReplayCommand(tick, c.FighterId, (byte)c.Kind, c.SkillId, c.AimQuantum, c.DirIndex, c.TargetTick));

    public IEnumerable<(int tick, List<Command> cmds)> EnumerateByTick()
    {
        int last = -1;
        var cur = new List<Command>();
        foreach (var rc in _commands)   // 已按录制序（Tick 升序）
        {
            if (rc.Tick != last)
            {
                if (last >= 0) yield return (last, cur);
                cur = new List<Command>();
                last = rc.Tick;
            }
            cur.Add(new Command(rc.FighterId, (CmdKind)rc.Kind, rc.SkillId, rc.AimQuantum, rc.DirIndex, rc.TargetTick));
        }
        if (last >= 0) yield return (last, cur);
    }
}

public static class ReplayRecorder
{
    /// 录制一场对局（Step 全过程指令捕获）
    public static ReplayFile Record(long seed, string dataVersionHash, int totalTicks, Action<int, ReplayFile> step)
    {
        var file = new ReplayFile { MatchSeed = seed, DataVersionHash = dataVersionHash };
        for (int t = 1; t <= totalTicks; t++) step(t, file);
        return file;
    }
}

public static class ReplayPlayer
{
    /// 重演: 种子+数据版本校验（不匹配显式拒绝——ADR-0005）+ 指令流重放
    public static (string EventHash, SnapshotData FinalState) Replay(
        ReplayFile file, string currentDataVersionHash,
        Func<long, string, SimWorld> worldFactory, int totalTicks)
    {
        if (file.DataVersionHash != currentDataVersionHash)
            throw new InvalidOperationException($"replay data version mismatch: {file.DataVersionHash} != {currentDataVersionHash}（ADR-0005 显式拒绝）");
        var world = worldFactory(file.MatchSeed, file.DataVersionHash);
        var byTick = new Dictionary<int, List<Command>>();
        foreach (var (tick, cmds) in file.EnumerateByTick()) byTick[tick] = cmds;
        for (int t = 1; t <= totalTicks; t++)
        {
            var cmds = byTick.TryGetValue(t, out var list) ? list.ToArray() : Array.Empty<Command>();
            world.Step(t, cmds);
        }
        return (world.Events.ComputeHash(), world.CaptureSnapshot());
    }
}
