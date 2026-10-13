using System;
using Godot;

// PRODUCTION - Arena.Client
// 火盆灯火：暖光 + 体积光锥 + 噪声闪烁——夜场竞技场的生命感来源。
// 三路正弦叠加近似火光抖动（无随机数——每帧确定，帧率无关）。
namespace Arena.Client.Visuals;

public partial class FlickerLight : Node3D
{
    private OmniLight3D _light = null!;
    private float _t;
    private float _base;
    private float _phase;

    public override void _Ready()
    {
        _light = new OmniLight3D
        {
            LightColor = new Color(1.0f, 0.62f, 0.28f),        // 火橙
            OmniRange = 9.5f,
            OmniAttenuation = 1.6f,
            LightEnergy = _base = 6.5f,
            LightVolumetricFogEnergy = 2.4f,                   // 体积光锥（火盆的核心价值）
            ShadowEnabled = true,
            ShadowBlur = 1.2f,
        };
        AddChild(_light);
        _phase = Position.X * 0.7f + Position.Z * 1.3f;        // 每盆错相（位置哈希，确定）
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        float f = 0.82f
                + 0.10f * MathF.Sin(_t * 11.3f + _phase)
                + 0.06f * MathF.Sin(_t * 27.7f + _phase * 2.1f)
                + 0.04f * MathF.Sin(_t * 5.1f - _phase);
        _light.LightEnergy = _base * f;
    }
}
