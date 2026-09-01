using System;
using System.Collections.Generic;
using System.Text;
// PRODUCTION - Arena.Core
// ADR-0003: EventBus——确定性事件收集与派发
// 事件按 (Tick, SeqInTick) 排序，Tick 内产生序 = seq 序


namespace Arena.Core.Sim;

public sealed class EventLog
{
    private readonly List<SimEvent> _events = new();
    private long _tick;
    private ushort _seq;

    public void BeginTick(long tick) { _tick = tick; _seq = 0; }

    public void Emit(SimEvent e)
    {
        _events.Add(e with { Tick = _tick, SeqInTick = _seq++ });
    }

    /// 本 Tick 事件冻结快照（Step 结束时调用）
    public List<SimEvent> FreezeTick() => new(_events.FindAll(e => e.Tick == _tick));

    public IReadOnlyList<SimEvent> All => _events;
    public void Clear() { _events.Clear(); _seq = 0; }

    /// 全事件流 SHA-256（确定性诊断，ADR-0003 §7）
    public string ComputeHash()
    {
        var sb = new StringBuilder();
        foreach (var e in _events)
            sb.Append(e.Tick).Append(':').Append(e.SeqInTick).Append(':')
              .Append((byte)e.Kind).Append(':').Append(e.AttackerId).Append(':')
              .Append(e.VictimId).Append(':').Append(e.DamageRaw).Append(';');
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
