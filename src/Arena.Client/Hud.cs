using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// HUD: HP/MP 条 ×2 + 双方职业 + **按键对应的真实技能名**（VS-9 起按职业动态生成——
// 此前提示是写死的剑客键位，换职业后完全对不上）。
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
    private string _keyHints = "";
    private string _playerName = "";
    private string _enemyName = "";

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
        _info.Text =
            $"[你] {_playerName}  vs  [对手] {_enemyName}" +
            $"     tick {tick}   stamina {f0.Stamina}\n{_keyHints}";
    }

    /// 每局重设（换职业 → 重算键位提示；提示里的技能名来自 catalog 的真实数据，不是写死的）
    public void Reset(RuntimeCatalog catalog, string playerClass, string playerName, string enemyName)
    {
        _result.Visible = false;
        _playerName = playerName;
        _enemyName = enemyName;
        _keyHints = string.Join("   ", InputTranslator.KeyHints(catalog, playerClass)
            .Select(h => $"{h.Key} {h.Label}"));
        _info.Text = "";
    }

    public void ShowResult(string result, int tick)
    {
        _result.Text = result;
        _result.Visible = true;
        _info.Text = $"tick {tick} — R 重开（同职业）　Esc 换职业";
    }
}
