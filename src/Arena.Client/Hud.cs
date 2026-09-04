using Arena.Core;
using Arena.Core.Sim;
using Godot;

// PRODUCTION - Arena.Client
// HUD（Vertical Slice 占位）: HP/MP/耐力条 ×2 + tick/结果标签。
namespace Arena.Client;

public sealed partial class Hud : CanvasLayer
{
    private readonly ColorRect _hp0 = new() { Color = new Color(0.2f, 0.8f, 0.3f) };
    private readonly ColorRect _mp0 = new() { Color = new Color(0.3f, 0.5f, 1f) };
    private readonly ColorRect _hp1 = new() { Color = new Color(0.9f, 0.3f, 0.25f) };
    private readonly ColorRect _mp1 = new() { Color = new Color(0.5f, 0.4f, 1f) };
    private readonly ColorRect _hp0Ghost = new() { Color = new Color(0.1f, 0.1f, 0.1f) };
    private readonly ColorRect _hp1Ghost = new() { Color = new Color(0.1f, 0.1f, 0.1f) };
    private readonly Label _info = new();
    private readonly Label _result = new();

    public Hud()
    {
        AddBar(_hp0Ghost, 30, 24, 404, 26, new Color(0.08f, 0.08f, 0.08f));
        AddBar(_hp0, 30, 24, 400, 22, _hp0.Color);
        AddBar(_mp0, 30, 50, 400, 10, _mp0.Color);
        AddBar(_hp1Ghost, 774, 24, 404, 26, new Color(0.08f, 0.08f, 0.08f));
        AddBar(_hp1, 778, 24, 400, 22, _hp1.Color);
        AddBar(_mp1, 778, 50, 400, 10, _mp1.Color);
        _info.Position = new Godot.Vector2(30, 68);
        _info.AddThemeFontSizeOverride("font_size", 16);
        AddChild(_info);
        _result.Position = new Godot.Vector2(330, 300);
        _result.AddThemeFontSizeOverride("font_size", 64);
        _result.Visible = false;
        AddChild(_result);
        foreach (var bar in new[] { _hp0Ghost, _hp0, _mp0, _hp1Ghost, _hp1, _mp1 }) AddChild(bar);
    }

    private static void AddBar(ColorRect bar, float x, float y, float w, float h, Color _) { bar.Position = new Godot.Vector2(x, y); bar.Size = new Godot.Vector2(w, h); }

    public void Sync(SimWorld world, int tick)
    {
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];
        _hp0.Size = new Godot.Vector2(400f * f0.Hp / f0.HpMax, 22);
        _hp1.Size = new Godot.Vector2(400f * f1.Hp / f1.HpMax, 22);
        _mp0.Size = new Godot.Vector2(400f * f0.Mp / f0.MpMax, 10);
        _mp1.Size = new Godot.Vector2(400f * f1.Mp / f1.MpMax, 10);
        _info.Text = $"tick {tick}  stamina {f0.Stamina}  [WASD move | Space jump | Shift roll | J basic | K 上挑 | L 三段斩 | U 格挡 | I 仙人指路 | O 拔刀斩]";
    }

    public void Reset()
    {
        _result.Visible = false;
        _info.Text = "";
    }

    public void ShowResult(string result, int tick)
    {
        _result.Text = result;
        _result.Visible = true;
        _info.Text = $"tick {tick} — 按 R 重开";
    }
}
