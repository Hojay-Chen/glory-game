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
    AttackEnded = 12,       // ActEnded（自然结束——武器尾迹收束锚点）
    // ---- 荣耀手感的"高光时刻"——此前全被丢掉或与其它事件混同，导致这些深度在画面上完全看不见 ----
    SkillCancelled = 13,    // Cancelled（**取消**：技能取消普攻后摇/强制取消/跳跃取消）——原先与 AttackEnded 混同
    Relaunched = 14,        // Relaunched（**二段浮空**：空中被再次打起——浮空连招的核心节拍）
    Ukemi = 15,             // Ukemi（**受身**：被击飞时受身）
    WallBounced = 16,       // WallBounced（**撞墙**：击飞撞结界墙反弹）
    BasicStep = 17,         // BasicStep（**普攻连段第 N 段**）
    GuardBroken = 18,       // GuardBroken（**破防**：格挡值被打光）
    GetupDone = 19,         // GetupDone（起身完成）
}

public readonly record struct PresentationEvent(
    PresentationEventKind Kind,
    int Tick,
    int AttackerId,
    int VictimId,
    ushort SkillId,
    long Damage,
    float PosX, float PosY, float PosZ,
    long Value = 0)   // SimEvent.ValueRaw 透传（普攻连段序号等）
{
    public static PresentationEvent From(SimEvent e) => new(
        Map(e), (int)e.Tick, e.AttackerId, e.VictimId, e.SkillId, e.DamageRaw,
        e.HitPointX / 65536f, e.HitPointY / 65536f, e.HitPointZ / 65536f, e.ValueRaw);

    // Hit 按伤害分流: 有伤害 → DamageApplied（Hit Stop/闪白触发源）；无伤害 → AttackHit（纯接触火花）。
    // 仍保持「1 个 SimEvent → 1 个 PresentationEvent」，PE01/PE04 的 1:1 不变式不受影响。
    private static PresentationEventKind Map(SimEvent e) => e.Kind switch
    {
        EventKind.SkillCast => PresentationEventKind.SkillCast,
        EventKind.Hit => e.DamageRaw > 0 ? PresentationEventKind.DamageApplied : PresentationEventKind.AttackHit,
        EventKind.GuardHit => PresentationEventKind.Guarded,
        EventKind.Parry => PresentationEventKind.Parried,
        EventKind.Interrupted => PresentationEventKind.SkillInterrupted,
        EventKind.Launched => PresentationEventKind.Launched,
        EventKind.Knockback => PresentationEventKind.Knockback,
        EventKind.Landed or EventKind.ForcedDown or EventKind.FallLanded => PresentationEventKind.FighterDown,
        EventKind.Died => PresentationEventKind.FighterDied,
        EventKind.ActEnded => PresentationEventKind.AttackEnded,
        // 取消与自然结束**分开**：取消链是荣耀的核心节奏，画面必须看得见（客户端二者都收尾迹）
        EventKind.Cancelled => PresentationEventKind.SkillCancelled,
        EventKind.Relaunched => PresentationEventKind.Relaunched,
        EventKind.Ukemi => PresentationEventKind.Ukemi,
        EventKind.WallBounced => PresentationEventKind.WallBounced,
        EventKind.BasicStep => PresentationEventKind.BasicStep,
        EventKind.GuardBroken => PresentationEventKind.GuardBroken,
        EventKind.GetupDone => PresentationEventKind.GetupDone,
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
