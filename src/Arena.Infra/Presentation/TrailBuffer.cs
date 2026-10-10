using System;
using System.Collections.Generic;

// PRODUCTION - Arena.Infra
// VS-8 Weapon Trail 的纯逻辑状态机（零引擎依赖、可测）。
// 分工: 本类只管「采样点集合的生命周期」（武装/解除/老化/上限/去重）；
// Godot 侧 WeaponTrail 节点只负责把点集合铺成网格。表现层——绝不写回 Sim。
namespace Arena.Infra.Presentation;

/// 单个尾迹采样点（世界坐标 + 存活时长）
public readonly record struct TrailPoint(float X, float Y, float Z, double Age);

public sealed class TrailBuffer
{
    /// 采样点上限（超过即丢最旧——定长上界，杜绝无界增长）
    public const int MaxPoints = 24;
    /// 单点存活时长（秒）——尾迹自然淡出
    public const double LifetimeSeconds = 0.20;
    /// 追加阈值（米）——武器静止时不刷重复点，避免退化网格
    public const float MinStepMeters = 0.04f;

    private const float MinStepSq = MinStepMeters * MinStepMeters;

    private readonly List<TrailPoint> _points = new(MaxPoints);
    private int _armedSkill = -1;   // -1 = 未武装

    /// 是否正随某次攻击采样
    public bool Armed => _armedSkill >= 0;
    /// 当前武装的技能（Sim 侧 SkillRuntimeId）；未武装时 -1
    public int ArmedSkill => _armedSkill;
    /// 当前点数（含解除后仍在淡出的余点）
    public int Count => _points.Count;

    public TrailPoint this[int index] => _points[index];

    /// 起手：武装并清掉上一次攻击的余点（连续攻击不跨技连线）
    public void Arm(int skillId)
    {
        _points.Clear();
        _armedSkill = skillId;
    }

    /// 结束：仅当结束的正是当前武装的技能才解除。
    /// 取消链里「旧技 Cancelled」可能排在新技 SkillCast 之后——靠技能号匹配避免误解除新技尾迹。
    public void Disarm(int skillId)
    {
        if (_armedSkill == skillId) _armedSkill = -1;
    }

    /// 硬清空（阵亡 / 终局 / 重开——立即无残留，不做淡出）
    public void Clear()
    {
        _points.Clear();
        _armedSkill = -1;
    }

    /// 每表现帧推进：先老化（到期的丢弃），武装中且位移够大才追加新点。
    public void Advance(float x, float y, float z, double deltaSeconds)
    {
        for (int i = _points.Count - 1; i >= 0; i--)
        {
            double age = _points[i].Age + deltaSeconds;
            if (age >= LifetimeSeconds) _points.RemoveAt(i);
            else _points[i] = _points[i] with { Age = age };
        }

        if (!Armed) return;

        if (_points.Count > 0)
        {
            var last = _points[_points.Count - 1];
            float dx = x - last.X, dy = y - last.Y, dz = z - last.Z;
            if (dx * dx + dy * dy + dz * dz < MinStepSq) return;
        }

        _points.Add(new TrailPoint(x, y, z, 0));
        if (_points.Count > MaxPoints) _points.RemoveAt(0);
    }

    /// 该点的剩余生命（1=刚采样，0=即将消失）——供网格做 alpha/宽度渐隐
    public float FadeAt(int index)
    {
        float f = 1f - (float)(_points[index].Age / LifetimeSeconds);
        return f < 0f ? 0f : f > 1f ? 1f : f;
    }
}
