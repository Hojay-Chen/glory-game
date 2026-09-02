using System;
using System.Collections.Generic;
using Arena.Core.Rng;
// PRODUCTION - Arena.Core
// ADR-0008: Signature Plugin 协议——ISignature/ISimContext 最小框架。
// 边界裁定: 签名=职业机制组合器，仅经 ISimContext 消费 Core 原语；无字段状态（ADR-0008
// 「插件无字段状态」——可回溯/可快照的结构性保证，全部副作用经 Sim 状态域）。
// 注册序 = ClassId 升序（装配期冻结，ADR-0001 §3.4）。
// 派发点: ADR-0001 §3.2 ⑤ 签名钩子（状态/闸门/资源 Tick 之后、死亡判定之前）。
// ISimContext 九原语（ADR-0008 §4.6）v1 实装子集: Roll100/IsAlly/ApplyStatus/SetHeading/
// ResetCooldown/RouteDamage/GetFighter；SpawnUnit/SpawnDecoy 归 UnitSystem 阶段（登记）。
namespace Arena.Core.Sim;

/// 签名插件（每职业至多一个；装配期按 ClassId 注册）
public interface ISignature
{
    string ClassId { get; }
    /// 每 Tick 事件派发（本 Tick 全部事件按 (Tick, Seq) 序投递——签名内部过滤自身语义）
    void OnEvent(ISimContext ctx, in SimEvent e);
    /// 每 Tick 结算钩子（资源回复/持续时间等）
    void OnTick(ISimContext ctx) { }
}

/// 签名上下文（身份绑定: FighterId = 签名所属职业的 Fighter；RNG 流键强制绑定）
public interface ISimContext
{
    long Tick { get; }
    int FighterId { get; }
    /// 几率 roll（流键 = (SKILL_CHANCE, FighterId, skillId)——签名无法伪造他人流键）
    int Roll100(ushort skillId);
    bool IsAlly(int a, int b);
    FighterStateData? GetFighter(int fighterId);
    void ApplyStatus(int targetId, in StatusEffectDef eff, ushort skillId);
    void SetHeading(long quantum);
    void ResetCooldown(ushort skillId);
    void RouteDamage(int targetId, long delta, EventKind reason);
}

/// 注册表（装配期冻结；ClassId 升序 = ADR-0001 §3.4 注册序）
public sealed class SignatureRegistry
{
    private readonly List<ISignature> _signatures = new();
    private readonly Dictionary<string, ISignature> _byClass = new(StringComparer.Ordinal);

    public void Register(ISignature signature)
    {
        if (_byClass.ContainsKey(signature.ClassId))
            throw new InvalidOperationException($"duplicate signature for class {signature.ClassId}");
        _signatures.Add(signature);
        _byClass[signature.ClassId] = signature;
    }

    /// 装配完成（ClassId 升序冻结）
    public void Seal() => _signatures.Sort((a, b) => string.CompareOrdinal(a.ClassId, b.ClassId));

    public IReadOnlyList<ISignature> All => _signatures;
}

/// SimWorld 内部实现（per-signature 身份绑定包装——每次派发复用，无跨 Tick 状态）
internal sealed class SimContext : ISimContext
{
    private readonly SimWorld _world;
    public int FighterId { get; }

    public SimContext(SimWorld world, int fighterId) { _world = world; FighterId = fighterId; }

    public long Tick => _world.Tick;
    public int Roll100(ushort skillId) =>
        _world.Rng.Roll100(new RollScope(StreamClass.SKILL_CHANCE, FighterId, skillId));
    public bool IsAlly(int a, int b) => _world.SameTeam(a, b);
    public FighterStateData? GetFighter(int fighterId) => _world.GetFighter(fighterId);
    public void ApplyStatus(int targetId, in StatusEffectDef eff, ushort skillId)
    {
        var f = _world.GetFighter(targetId);
        if (f is not null) _world.ApplyStatus(f, eff, FighterId, skillId);
    }
    public void SetHeading(long quantum) { var f = _world.GetFighter(FighterId); if (f is not null) f.HeadingQuantum = quantum; }
    public void ResetCooldown(ushort skillId) { var f = _world.GetFighter(FighterId); f?.Cooldowns.Remove(skillId); }
    public void RouteDamage(int targetId, long delta, EventKind reason)
    {
        var f = _world.GetFighter(targetId);
        if (f is null) return;
        f.Hp -= delta;
        _world.Events.Emit(new SimEvent { Kind = reason, AttackerId = FighterId, VictimId = targetId, DamageRaw = delta });
    }
}

/// SimWorld 签名派发（⑤ 钩子之后；每个签名绑定其职业的首个 Fighter——多人同职业=同一机制域）
public sealed partial class SimWorld
{
    private SignatureRegistry? _signatures;
    private readonly Dictionary<int, SimContext> _ctxCache = new();

    public void InstallSignatures(SignatureRegistry registry)
    {
        if (_signatures is not null) throw new InvalidOperationException("signatures already installed");
        registry.Seal();
        _signatures = registry;
    }

    /// 派发（TickFighters 之后、死亡判定之前——ADR-0001 §3.2 ⑤；Step 调用）
    private void DispatchSignatures()
    {
        if (_signatures is null) return;
        foreach (var sig in _signatures.All)   // ClassId 升序（注册序）
        {
            int binderId = -1;
            foreach (var f in Fighters)
            {
                if (f.ClassId == sig.ClassId) { binderId = f.Id; break; }
            }
            if (binderId < 0) continue;   // 该职业未参赛——钩子不执行
            if (!_ctxCache.TryGetValue(binderId, out var ctx))
                _ctxCache[binderId] = ctx = new SimContext(this, binderId);
            sig.OnTick(ctx);
            // 本 Tick 事件物化快照——签名经 ISimContext 发射的新事件归下一 Tick 派发（ADR-0003 §2.2 不可变语义）
            var tickEvents = new List<SimEvent>();
            foreach (var e in Events.All)
            {
                if (e.Tick != Tick) continue;
                tickEvents.Add(e);
            }
            foreach (var e in tickEvents) sig.OnEvent(ctx, e);
        }
    }
}
