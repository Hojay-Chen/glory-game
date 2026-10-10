using System;
using System.Collections.Generic;
using Arena.Core.Sim;

// PRODUCTION - Arena.Infra
// VS-7 Presentation Director: 表现层总调度（纯逻辑，零引擎依赖）。
//   输入 = SimWorld 公开事件流（只读）；输出 = 引擎无关的表现线索（PresentationCue）序列，
//   并驱动 HitStop / CameraPresenter 两个表现层态机。
// 架构约束（ADR-0001/0009）: 绝不写回 SimWorld（表现层不预测、不回流）；Restart 时 Reset。
// Godot 层（Arena.Client.MatchRoot）只负责把 cue 翻译成节点操作——不含任何判定逻辑。
namespace Arena.Infra.Presentation;

public enum PresentationCueKind : byte
{
    WeaponSwing = 0,   // 攻击者：武器挥摆 + 尾迹武装（技能/普攻起手）
    HitSpark = 1,      // 受击点：接触火花（判定命中但无伤害）
    HitFlash = 2,      // 受击者：材质闪亮 + 火花（有伤害的命中——Hit Stop 触发源）
    GuardSpark = 3,    // 格挡化解火花
    ParrySpark = 4,    // 完美格挡/弹刀火花
    Interrupt = 5,     // 技能被打断
    Launch = 6,        // 浮空
    Knockback = 7,     // 击退
    Down = 8,          // 倒地
    Death = 9,         // 阵亡
    MatchEnd = 10,     // 终局（仅一次）
    AttackEnded = 11,  // 攻击者：本次攻击生命周期结束——尾迹收束
}

public readonly record struct PresentationCue(
    PresentationCueKind Kind, int Tick,
    int AttackerId, int VictimId, ushort SkillId,
    long Damage, float PosX, float PosY, float PosZ);

public sealed class PresentationDirector
{
    private readonly PresentationEventBridge _bridge = new();
    private readonly List<PresentationCue> _cues = new(64);

    public HitStopController HitStop { get; } = new();
    public CameraPresenter Camera { get; } = new();
    public bool MatchEnded => _bridge.MatchEnded;

    public void Reset()
    {
        _bridge.Reset();
        HitStop.Reset();
        Camera.Reset();
        _cues.Clear();
    }

    /// 终局标记（MatchRoot 超时/胜负判定时调用——Died 路径由 Bridge 自动触发）
    public void FlagMatchEnd() => _bridge.FlagMatchEnd();

    /// 每表现帧调用一次：消费自上次以来的 Sim 事件 → 表现线索（并推进表现层态机）。
    /// 返回的列表为复用缓冲（同帧内有效）。
    public List<PresentationCue> Advance(IReadOnlyList<SimEvent> simEvents, double deltaSeconds)
    {
        _cues.Clear();
        foreach (var pe in _bridge.Consume(simEvents))
            _cues.Add(Map(pe));
        HitStop.Tick();
        Camera.Tick(deltaSeconds);
        return _cues;
    }

    private PresentationCue Map(PresentationEvent pe)
    {
        // 表现线索（纯映射）——桥接层已过滤非表现事件
        var kind = pe.Kind switch
        {
            PresentationEventKind.SkillCast        => PresentationCueKind.WeaponSwing,
            PresentationEventKind.AttackHit        => PresentationCueKind.HitSpark,
            PresentationEventKind.DamageApplied    => PresentationCueKind.HitFlash,
            PresentationEventKind.Guarded          => PresentationCueKind.GuardSpark,
            PresentationEventKind.Parried          => PresentationCueKind.ParrySpark,
            PresentationEventKind.SkillInterrupted => PresentationCueKind.Interrupt,
            PresentationEventKind.Launched         => PresentationCueKind.Launch,
            PresentationEventKind.Knockback        => PresentationCueKind.Knockback,
            PresentationEventKind.FighterDown      => PresentationCueKind.Down,
            PresentationEventKind.FighterDied      => PresentationCueKind.Death,
            PresentationEventKind.MatchEnded       => PresentationCueKind.MatchEnd,
            PresentationEventKind.AttackEnded      => PresentationCueKind.AttackEnded,
            _ => (PresentationCueKind)255,
        };

        // 表现层态机副作用（Hit Stop 时长 / 相机 trauma——按事件量级）
        switch (pe.Kind)
        {
            case PresentationEventKind.DamageApplied:
                HitStop.TriggerDamage(pe.Damage);
                Camera.AddTrauma(0.10 + Math.Clamp(pe.Damage / 20000.0, 0.0, 0.20));
                break;
            case PresentationEventKind.Guarded:
                Camera.AddTrauma(0.08);
                break;
            case PresentationEventKind.Parried:
                HitStop.TriggerParry();
                Camera.AddTrauma(0.35);
                break;
            case PresentationEventKind.Launched:
                HitStop.TriggerLaunch();
                Camera.AddTrauma(0.30);
                break;
            case PresentationEventKind.FighterDown:
                Camera.AddTrauma(0.12);
                break;
            case PresentationEventKind.FighterDied:
                Camera.AddTrauma(0.80);
                break;
            case PresentationEventKind.MatchEnded:
                Camera.SetMatchEnd();
                break;
        }

        return new PresentationCue(
            kind, pe.Tick, pe.AttackerId, pe.VictimId, pe.SkillId,
            pe.Damage, pe.PosX, pe.PosY, pe.PosZ);
    }
}
