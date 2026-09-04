using System;

// PRODUCTION - Arena.Infra
// VS-6.4 相机表现参数（纯逻辑——Godot CameraRig 消费）：shake trauma 衰减+偏移量计算。
namespace Arena.Infra.Presentation;

public sealed class CameraPresenter
{
    private double _trauma;
    private readonly Random _rng = new(0xC4);
    private double _shakeX, _shakeY;

    public double ShakeX => _shakeX;
    public double ShakeY => _shakeY;
    public bool MatchEnd { get; private set; }

    public void AddTrauma(double amount) => _trauma = Math.Min(1.0, _trauma + amount);

    public void Tick(double delta)
    {
        _trauma = Math.Max(0, _trauma - delta * 1.8);
        var mag = _trauma * _trauma * 0.15;
        _shakeX = (_rng.NextDouble() * 2 - 1) * mag;
        _shakeY = (_rng.NextDouble() * 2 - 1) * mag;
    }

    public void SetMatchEnd() => MatchEnd = true;
    public void Reset() { _trauma = 0; _shakeX = _shakeY = 0; MatchEnd = false; }
}
