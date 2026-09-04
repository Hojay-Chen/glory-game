using System;
using System.Collections.Generic;
using Arena.Core.Sim;

// PRODUCTION - Arena.Infra
// VS-6.1 Presentation Event Bridge: SimWorld → EventStream → Bridge → Godot Presentation。
// 单向只读: Bridge 消费 SimEvent（公开事件流），产出表现层语义事件——绝不写回 Sim。
// 纯逻辑零引擎依赖（可测）；确定性无关紧要（表现层不回流），但实现本身确定。
namespace Arena.Infra.Presentation;

public enum PresentationEventKind : byte
{
    AttackStarted = 0,      // SkillCast（技能/普攻起手）
    AttackHit = 1,          // Hit（判定接触）
    DamageApplied = 2,      // Hit 且 DamageRaw>0（伤害结算——Hit Stop/特效触发源）
    Guarded = 3,            // GuardHit（格挡化解）
    Parried = 4,            // Parry（完美格挡——弹刀）
    SkillCast = 5,          // SkillCast（技能释放——表现 1:1 锚点）
    SkillInterrupted = 6,   // Interrupted
    Launched = 7,           // Launched（浮空——Camera Feedback）
    Knockback = 8,          // Knockback
    FighterDown = 9,        // Landed/ForcedDown（倒地）
    FighterDied = 10,       // Died
    MatchEnded = 11,        // 终局（首个 Died 或外部 Flag——仅一次）
}

public readonly record struct PresentationEvent(
    PresentationEventKind Kind,
    int Tick,
    int AttackerId,
    int VictimId,
    ushort SkillId,
    long Damage,
    float PosX, float PosY, float PosZ)
{
    public static PresentationEvent From(SimEvent e) => new(
        Map(e.Kind), (int)e.Tick, e.AttackerId, e.VictimId, e.SkillId, e.DamageRaw,
        e.HitPointX / 65536f, e.HitPointY / 65536f, e.HitPointZ / 65536f);

    private static PresentationEventKind Map(EventKind k) => k switch
    {
        EventKind.SkillCast => PresentationEventKind.SkillCast,
        EventKind.Hit => PresentationEventKind.AttackHit,
        EventKind.GuardHit => PresentationEventKind.Guarded,
        EventKind.Parry => PresentationEventKind.Parried,
        EventKind.Interrupted => PresentationEventKind.SkillInterrupted,
        EventKind.Launched => PresentationEventKind.Launched,
        EventKind.Knockback => PresentationEventKind.Knockback,
        EventKind.Landed or EventKind.ForcedDown or EventKind.FallLanded => PresentationEventKind.FighterDown,
        EventKind.Died => PresentationEventKind.FighterDied,
        _ => (PresentationEventKind)255,   // 非表现事件（Whiff/StatusExpired 等）——桥接层过滤
    };

    public bool IsCombat => Kind is not (PresentationEventKind.MatchEnded or (PresentationEventKind)255);
}

/// 游标式消费器（表现侧状态——不进 Sim/Snapshot；Restart 时 Reset）
public sealed class PresentationEventBridge
{
    private int _consumed;
    private bool _matchEnded;

    public bool MatchEnded => _matchEnded;

    public void Reset()
    {
        _consumed = 0;
        _matchEnded = false;
    }

    /// 终局标记（MatchRoot 超时判定时调用——Died 路径自动触发）
    public void FlagMatchEnd() => _matchEnded = true;

    /// 消费自上次调用以来的新 Sim 事件 → 表现事件（战斗事件在 MatchEnded 后被闸断）
    public List<PresentationEvent> Consume(IReadOnlyList<SimEvent> all)
    {
        var list = new List<PresentationEvent>();
        for (int i = _consumed; i < all.Count; i++)
        {
            var pe = PresentationEvent.From(all[i]);
            if (pe.Kind == (PresentationEventKind)255) continue;
            if (pe.Kind == PresentationEventKind.FighterDied && !_matchEnded)
            {
                list.Add(pe);
                list.Add(new PresentationEvent(PresentationEventKind.MatchEnded, pe.Tick, pe.AttackerId, pe.VictimId, 0, 0, pe.PosX, pe.PosY, pe.PosZ));
                _matchEnded = true;
                continue;
            }
            // 终局后闸断战斗表现（用户裁定: MatchEnd 后不继续产生战斗表现）
            if (_matchEnded && pe.IsCombat) continue;
            list.Add(pe);
        }
        _consumed = all.Count;
        return list;
    }
}
