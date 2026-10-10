using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Arena.Infra.Data;

// PRODUCTION - Arena.Client
// 选武器（UI-02，ui-spec §3.3）: 每职业 3 把赛前选 1（散人固定万象伞）。
// 左列表 + 右详情；数值与特性直接来自 weapons.csv（**这里只展示，不做任何数值解释**）。
// 选中结果经构造传入的 Action 回交调用方——本屏不触碰战斗状态。
namespace Arena.Client;

public sealed partial class WeaponSelectScreen : CanvasLayer
{
    private const int PanelX = 120, PanelY = 150, PanelW = 1040, PanelH = 420;
    private const int ListW = 420;
    private const int RowH = 76;

    private readonly string _classId;
    private readonly string _className;
    private readonly List<WeaponCatalogParser.WeaponEntry> _weapons;
    private readonly Action<string> _onChosen;

    private readonly Label _title = new();
    private readonly Label _hint = new();
    private readonly List<PanelContainer> _rows = new();
    private readonly VBoxContainer _detail = new();

    private int _cursor;

    public WeaponSelectScreen(string classId, string className,
        List<WeaponCatalogParser.WeaponEntry> all, Action<string> onChosen)
    {
        _classId = classId;
        _className = className;
        _weapons = all.Where(w => w.ClassId == classId).ToList();
        _onChosen = onChosen;
        Layer = 10;
        Build();
    }

    private void Build()
    {
        UiTheme.Backdrop(this);

        var frame = new PanelContainer();
        frame.Position = new Godot.Vector2(PanelX, PanelY);
        frame.Size = new Godot.Vector2(PanelW, PanelH);
        frame.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BgPanel, UiTheme.Rlg, 1));
        AddChild(frame);

        _title.Text = $"选择武器　·　{_className}";
        _title.Position = new Godot.Vector2(0, 92);
        _title.Size = new Godot.Vector2(UiTheme.DesignW, 44);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.AddThemeFontSizeOverride("font_size", UiTheme.FsTitle);
        _title.AddThemeColorOverride("font_color", UiTheme.TextHi);
        AddChild(_title);

        var body = new HBoxContainer();
        body.Position = new Godot.Vector2(PanelX + UiTheme.Sp5, PanelY + UiTheme.Sp5);
        body.Size = new Godot.Vector2(PanelW - UiTheme.Sp5 * 2, PanelH - UiTheme.Sp5 * 2);
        body.AddThemeConstantOverride("separation", UiTheme.Sp5);
        AddChild(body);

        // 左：武器列表
        var left = new VBoxContainer { CustomMinimumSize = new Godot.Vector2(ListW, 0) };
        left.AddThemeConstantOverride("separation", UiTheme.Sp2);
        body.AddChild(left);
        for (int i = 0; i < _weapons.Count; i++)
        {
            var w = _weapons[i];
            var row = new PanelContainer { CustomMinimumSize = new Godot.Vector2(ListW, RowH) };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 2);
            var nm = UiTheme.Text(w.Name, UiTheme.FsSub, UiTheme.TextHi);
            var meta = UiTheme.Text($"{GradeLabel(w.Grade)}　攻击修正 {(w.AtkMod >= 0 ? "+" : "")}{w.AtkMod:P0}　攻速 ×{w.AtkSpd:0.00}", UiTheme.FsSmall, UiTheme.TextMid);
            box.AddChild(nm); box.AddChild(meta);
            row.AddChild(box);
            left.AddChild(row);
            _rows.Add(row);
        }

        // 右：详情
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", UiTheme.Sp3);
        body.AddChild(right);
        right.AddChild(_detail);

        _hint.Text = "↑/↓ 选择　Enter 确认　（数值来自 weapons.csv，只做面板与规则级修正）";
        _hint.Position = new Godot.Vector2(0, PanelY + PanelH + 24);
        _hint.Size = new Godot.Vector2(UiTheme.DesignW, 24);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.AddThemeFontSizeOverride("font_size", UiTheme.FsSmall);
        _hint.AddThemeColorOverride("font_color", UiTheme.TextLo);
        AddChild(_hint);

        Redraw();
    }

    private static string GradeLabel(string grade) => grade switch
    {
        "standard" => "制式",
        "special" => "特化",
        "masterwork" => "精工",
        _ => grade,
    };

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (_weapons.Count == 0) return;

        switch (k.Keycode)
        {
            case Key.Up: _cursor = (_cursor - 1 + _weapons.Count) % _weapons.Count; Redraw(); break;
            case Key.Down: _cursor = (_cursor + 1) % _weapons.Count; Redraw(); break;
            case Key.Enter:
            case Key.KpEnter:
                Visible = false;
                _onChosen(_weapons[_cursor].WeaponId);
                break;
        }
    }

    private void Redraw()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            bool sel = i == _cursor;
            _rows[i].AddThemeStyleboxOverride("panel",
                UiTheme.Box(sel ? UiTheme.BgRaised.Lightened(0.06f) : UiTheme.BgPanel,
                            UiTheme.RMd, sel ? 2 : 1, sel ? UiTheme.AccentGold : UiTheme.BorderSoft));
        }

        foreach (var c in _detail.GetChildren()) c.QueueFree();
        if (_weapons.Count == 0)
        {
            _detail.AddChild(UiTheme.Text("该职业无武器数据", UiTheme.FsBody, UiTheme.TextLo));
            return;
        }

        var w = _weapons[_cursor];
        _detail.AddChild(UiTheme.Text(w.WeaponId, UiTheme.FsSmall, UiTheme.TextLo));
        _detail.AddChild(UiTheme.Text(w.Name, UiTheme.FsTitle, UiTheme.TextHi));
        _detail.AddChild(UiTheme.Divider(_detail, 520));
        _detail.AddChild(UiTheme.Text($"类型 {w.Type}　·　档级 {GradeLabel(w.Grade)}　·　射程 {w.RangeM:0.0} m", UiTheme.FsBody, UiTheme.TextMid));
        _detail.AddChild(UiTheme.Text($"攻击修正 {(w.AtkMod >= 0 ? "+" : "")}{w.AtkMod:P0}　·　攻速 ×{w.AtkSpd:0.00}", UiTheme.FsBody, UiTheme.TextMid));
        _detail.AddChild(UiTheme.Text("特性", UiTheme.FsSmall, UiTheme.AccentGold));
        _detail.AddChild(UiTheme.Text(w.Trait.Length > 0 ? w.Trait : "无", UiTheme.FsBody, UiTheme.TextHi));
        if (w.Notes.Length > 0)
            _detail.AddChild(UiTheme.Text(w.Notes, UiTheme.FsSmall, UiTheme.TextLo));
    }
}
