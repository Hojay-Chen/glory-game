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

public enum DamageModStage : byte { BackstabBonus = 0 }   // 修正乘区（v1: 背击追加）

/// 签名插件（每职业至多一个；装配期按 ClassId 注册）
public interface ISignature
{
    string ClassId { get; }
    /// 每 Tick 事件派发（本 Tick 全部事件按 (Tick, Seq) 序投递——签名内部过滤自身语义）
    void OnEvent(ISimContext ctx, in SimEvent e);
    /// 每 Tick 结算钩子（资源回复/持续时间等）
    void OnTick(ISimContext ctx) { }
    /// 伤害修正乘区（v1: BackstabBonus——返回 Q32.16 乘数，ONE = 无修正）
    long ModifyDamage(DamageModStage stage, ISimContext ctx, int attackerId, int victimId) => Fixed2.ONE;
}
internal static class Fixed2 { public const long ONE = 65536; }

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
    SkillRuntimeData? GetSkillDef(ushort runtimeId);
    long GetResource(SimWorld.ResourceSlotKind kind);
    long GetResourceCap(SimWorld.ResourceSlotKind kind);
    void AddResource(SimWorld.ResourceSlotKind kind, long n);
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
    public SkillRuntimeData? GetSkillDef(ushort runtimeId) => _world.GetSkill(runtimeId);
    public long GetResource(SimWorld.ResourceSlotKind kind)
    {
        var f = _world.GetFighter(FighterId);
        return f is null ? 0 : f.ResourceCounts[(int)kind];
    }
    public long GetResourceCap(SimWorld.ResourceSlotKind kind)
    {
        var f = _world.GetFighter(FighterId);
        return f is null ? 0 : f.ResourceCaps[(int)kind];
    }
    public void AddResource(SimWorld.ResourceSlotKind kind, long n)
    {
        var f = _world.GetFighter(FighterId);
        if (f is null) return;
        var cap = f.ResourceCaps[(int)kind];
        f.ResourceCounts[(int)kind] = Math.Min(cap, f.ResourceCounts[(int)kind] + n);
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

    /// 伤害修正查询（HitResolve 在对应乘区调用——绑定到实际攻击者 FighterId）
    internal long GetDamageModifier(DamageModStage stage, FighterStateData attacker, FighterStateData victim)
    {
        if (_signatures is null) return Fixed2.ONE;
        foreach (var sig in _signatures.All)
        {
            if (sig.ClassId != attacker.ClassId) continue;
            if (!_ctxCache.TryGetValue(attacker.Id, out var ctx))
                _ctxCache[attacker.Id] = ctx = new SimContext(this, attacker.Id);
            return sig.ModifyDamage(stage, ctx, attacker.Id, victim.Id);
        }
        return Fixed2.ONE;
    }

    /// 派发（TickFighters 之后、死亡判定之前——ADR-0001 §3.2 ⑤；Step 调用）。
    /// 多人同职业: 每个 Fighter 独立绑定 ctx 独立派发——签名行为按 Fighter 完全隔离。
    private void DispatchSignatures()
    {
        if (_signatures is null) return;
        List<SimEvent>? tickEvents = null;   // 本 Tick 事件物化一次（签名发射归下一 Tick——ADR-0003 §2.2）
        foreach (var sig in _signatures.All)   // ClassId 升序（注册序）
        {
            for (int i = 0; i < Fighters.Count; i++)
            {
                var f = Fighters[i];
                if (f.ClassId != sig.ClassId) continue;   // 该职业未参赛——钩子不执行
                if (!_ctxCache.TryGetValue(f.Id, out var ctx))
                    _ctxCache[f.Id] = ctx = new SimContext(this, f.Id);
                sig.OnTick(ctx);
                tickEvents ??= MaterializeTickEvents();
                for (int k = 0; k < tickEvents.Count; k++) sig.OnEvent(ctx, tickEvents[k]);
            }
        }
    }

    private List<SimEvent> MaterializeTickEvents()
    {
        var list = new List<SimEvent>();
        foreach (var e in Events.All)
        {
            if (e.Tick != Tick) continue;
            list.Add(e);
        }
        return list;
    }
}
