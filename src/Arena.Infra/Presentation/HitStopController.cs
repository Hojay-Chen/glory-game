using System;

// PRODUCTION - Arena.Infra
// VS-6.2 Hit Stop Controller（纯逻辑——表现层冻结计时器，不触碰 SimWorld）。
// Trigger 按伤害量级 2~4 表现帧；Tick 逐表现帧递减；Sim Tick 独立不受影响。
namespace Arena.Infra.Presentation;

public sealed class HitStopController
{
    private int _frames;

    public bool ShouldFreezeVisuals => _frames > 0;

    public void TriggerDamage(long damage)
    {
        if (damage <= 0) return;
        _frames = Math.Clamp(2 + (int)(damage / 3000), 2, 4);
    }

    public void TriggerLaunch() => _frames = 4;
    public void TriggerParry() => _frames = 4;

    public void Tick() { if (_frames > 0) _frames--; }

    public void Reset() => _frames = 0;
}
