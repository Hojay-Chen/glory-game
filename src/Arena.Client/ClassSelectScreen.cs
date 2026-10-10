using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Arena.Infra.Data;

// PRODUCTION - Arena.Client
// 选职业（UI-01，ui-spec §3.2 改造版）: 左列表 + 右详情（职业面板 + 技能预览）。
// 玩家在这里要能一眼判断"这个职业是什么、面板如何、有哪些招"——只列名字是不够的。
// 全部走 UiTheme（设计系统唯一真源），本屏不触碰任何战斗状态。
namespace Arena.Client;

public sealed partial class ClassSelectScreen : CanvasLayer
{
    private const int Window = 9;              // 列表可视条目数（须能完整落在面板内）
    private const int RowH = 34;
    private const int PanelX = 96, PanelY = 132, PanelW = 1088, PanelH = 452;

    private readonly List<ClassDefParser.ClassEntry> _classes;
    private readonly RuntimeCatalog? _catalog;
    private readonly Action<string, string> _onChosen;

    private readonly Label _title = new();
    private readonly Label _hint = new();
    private readonly VBoxContainer _list = new();
    private readonly VBoxContainer _detail = new();

    private int _cursor;                        // 0.._classes.Count（最后一项 = 随机）
    private int _step;                          // 0 = 选我方；1 = 选敌方
    private string _playerClass = "";

    public ClassSelectScreen(List<ClassDefParser.ClassEntry> classes, RuntimeCatalog? catalog,
        Action<string, string> onChosen)
    {
        _classes = classes;
        _catalog = catalog;
        _onChosen = onChosen;
        Layer = 10;
        Build();
    }

    private void Build()
    {
        UiTheme.Backdrop(this);

        _title.Position = new Godot.Vector2(0, 52);
        _title.Size = new Godot.Vector2(UiTheme.DesignW, 44);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.AddThemeFontSizeOverride("font_size", UiTheme.FsTitle);
        _title.AddThemeColorOverride("font_color", UiTheme.TextHi);
        AddChild(_title);

        var frame = new PanelContainer();
        frame.Position = new Godot.Vector2(PanelX, PanelY);
        frame.Size = new Godot.Vector2(PanelW, PanelH);
        frame.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BgPanel, UiTheme.Rlg, 1));
        AddChild(frame);

        var body = new HBoxContainer();
        body.Position = new Godot.Vector2(PanelX + UiTheme.Sp5, PanelY + UiTheme.Sp5);
        body.Size = new Godot.Vector2(PanelW - UiTheme.Sp5 * 2, PanelH - UiTheme.Sp5 * 2);
        body.AddThemeConstantOverride("separation", UiTheme.Sp5);
        AddChild(body);

        var left = new VBoxContainer { CustomMinimumSize = new Godot.Vector2(360, 0) };
        left.AddThemeConstantOverride("separation", 2);
        left.AddChild(_list);
        body.AddChild(left);

        var right = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BgSunken, UiTheme.RMd, 1, UiTheme.BorderSoft));
        var rightPad = new VBoxContainer();
        rightPad.AddThemeConstantOverride("separation", UiTheme.Sp2);
        rightPad.AddChild(_detail);
        right.AddChild(rightPad);
        body.AddChild(right);

        _hint.Position = new Godot.Vector2(0, PanelY + PanelH + 20);
        _hint.Size = new Godot.Vector2(UiTheme.DesignW, 24);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.AddThemeFontSizeOverride("font_size", UiTheme.FsSmall);
        _hint.AddThemeColorOverride("font_color", UiTheme.TextLo);
        AddChild(_hint);

        Redraw();
    }

    private int OptionCount => _classes.Count + 1;   // 末项 = 随机

    private string NameOf(string classId) =>
        _classes.FirstOrDefault(c => c.ClassId == classId)?.Name ?? classId;

    private void Confirm()
    {
        string picked = _cursor == _classes.Count
            ? _classes[Random.Shared.Next(_classes.Count)].ClassId
            : _classes[_cursor].ClassId;

        if (_step == 0)
        {
            _playerClass = picked;
            _step = 1;
            Redraw();
            return;
        }
        Visible = false;
        _onChosen(_playerClass, picked);
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey k || !k.Pressed || k.Echo) return;
        switch (k.Keycode)
        {
            case Key.Up:
                _cursor = (_cursor - 1 + OptionCount) % OptionCount; Redraw(); break;
            case Key.Down:
                _cursor = (_cursor + 1) % OptionCount; Redraw(); break;
            case Key.Enter:
            case Key.KpEnter: Confirm(); break;
            case Key.Backspace:
                if (_step == 1) { _step = 0; Redraw(); } break;
        }
    }

    private void Redraw()
    {
        _title.Text = _step == 0
            ? "选择你的职业"
            : $"你的职业　{NameOf(_playerClass)}　—　选择对手职业";

        // 左：列表窗口
        foreach (var c in _list.GetChildren()) c.QueueFree();
        int half = Window / 2;
        int start = Math.Max(0, Math.Min(_cursor - half, OptionCount - Window));
        int end = Math.Min(OptionCount, start + Window);
        for (int i = start; i < end; i++)
        {
            bool sel = i == _cursor;
            bool rnd = i == _classes.Count;
            var row = new PanelContainer { CustomMinimumSize = new Godot.Vector2(360, RowH) };
            row.AddThemeStyleboxOverride("panel", UiTheme.BoxTight(
                sel ? UiTheme.BgRaised.Lightened(0.06f) : new Color(0, 0, 0, 0),
                UiTheme.RSm, sel ? 2 : 0, sel ? UiTheme.AccentGold : UiTheme.BorderSoft));
            var line = UiTheme.Text(
                rnd ? "【随机】" : $"{_classes[i].ClassId,-4}  {_classes[i].Name}",
                UiTheme.FsBody, sel ? UiTheme.TextHi : UiTheme.TextMid);
            row.AddChild(line);
            _list.AddChild(row);
        }

        // 右：详情
        foreach (var c in _detail.GetChildren()) c.QueueFree();
        if (_cursor == _classes.Count)
        {
            _detail.AddChild(UiTheme.Text("随机", UiTheme.FsTitle, UiTheme.TextHi));
            _detail.AddChild(UiTheme.Text("由系统从 25 职业中随机挑选", UiTheme.FsBody, UiTheme.TextMid));
            _hint.Text = HintText();
            return;
        }

        var cls = _classes[_cursor];
        _detail.AddChild(UiTheme.Text($"{cls.Name}", UiTheme.FsTitle, UiTheme.TextHi));
        _detail.AddChild(UiTheme.Text($"{cls.ClassId}　·　{cls.Branch}", UiTheme.FsSmall, UiTheme.AccentGold));
        _detail.AddChild(UiTheme.Text(
            $"攻击 {cls.Atk}　防御 {cls.Def}　移速 {cls.MoveSpeedMps:0.0} m/s　负重 {cls.BodyWeight}",
            UiTheme.FsBody, UiTheme.TextMid));
        _detail.AddChild(UiTheme.Divider(_detail, 560));
        _detail.AddChild(UiTheme.Text("技能", UiTheme.FsSmall, UiTheme.TextLo));

        if (_catalog is not null)
        {
            var skills = _catalog.Skills.Where(s => s.ClassId == cls.ClassId && s.Type != "basic").ToList();
            var sb = new StringBuilder();
            foreach (var tier in new[] { "T1", "T2", "T3", "T4", "U" })
            {
                var row = skills.Where(s => TierName(s.Tier) == tier).Select(s => s.Name).ToList();
                if (row.Count == 0) continue;
                sb.Append(tier).Append("　").Append(string.Join("　", row)).Append('\n');
            }
            _detail.AddChild(UiTheme.Text(sb.ToString().TrimEnd('\n'), UiTheme.FsSmall, UiTheme.TextMid));
        }

        _hint.Text = HintText();
    }

    private string HintText() =>
        "↑/↓ 选择　Enter 确认" + (_step == 1 ? "　Backspace 返回" : "") + $"　（共 {_classes.Count} 职业）";

    private static string TierName(byte tier) => tier switch
    {
        1 => "T1", 2 => "T2", 3 => "T3", 4 => "T4", 5 => "U", _ => "",
    };
}
