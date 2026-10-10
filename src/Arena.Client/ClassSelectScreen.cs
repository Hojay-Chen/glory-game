using System;
using System.Collections.Generic;
using Godot;
using Arena.Infra.Data;
using Arena.Infra.Input;

// PRODUCTION - Arena.Client
// VS-9 选人界面: 开局前选双方职业（↑/↓ 选择、Enter 确认、Backspace 返回）。
// 只决定「用哪个 class_id 装配」——数值/技能/按键全部由 catalog 与 InputTranslator 派生，
// 界面本身不含任何战斗规则（架构约束: Godot 层只产 Input + Presentation）。
namespace Arena.Client;

public sealed partial class ClassSelectScreen : CanvasLayer
{
    private const int Window = 9;          // 光标上下各显示多少条

    private readonly List<ClassDefParser.ClassEntry> _classes;
    private readonly Action<string, string> _onChosen;

    private readonly Label _title = new();
    private readonly Label _hint = new();
    private readonly Label _body = new();

    private int _cursor;                    // 0.._classes.Count（最后一项 = 随机）
    private int _step;                      // 0 = 选我方；1 = 选敌方
    private string _playerClass = "";

    public ClassSelectScreen(List<ClassDefParser.ClassEntry> classes, Action<string, string> onChosen)
    {
        _classes = classes;
        _onChosen = onChosen;
        Layer = 10;

        _title.Position = new Godot.Vector2(0, 40);
        _title.Size = new Godot.Vector2(1280, 40);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.AddThemeFontSizeOverride("font_size", 30);

        _body.Position = new Godot.Vector2(0, 110);
        _body.Size = new Godot.Vector2(1280, 460);
        _body.HorizontalAlignment = HorizontalAlignment.Center;
        _body.AddThemeFontSizeOverride("font_size", 20);

        _hint.Position = new Godot.Vector2(0, 620);
        _hint.Size = new Godot.Vector2(1280, 40);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.AddThemeFontSizeOverride("font_size", 16);

        AddChild(_title);
        AddChild(_body);
        AddChild(_hint);
        Redraw();
    }

    /// 随机对手用同一套职业表——随机项排在列表最后
    private int OptionCount => _classes.Count + 1;

    private void MoveCursor(int delta)
    {
        _cursor = (_cursor + delta + OptionCount) % OptionCount;
        Redraw();
    }

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

    /// Enter 跳过整块界面（自选我方 + 随机对手）——想快速开打时不必走两步
    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey k || !k.Pressed || k.Echo) return;

        switch (k.Keycode)
        {
            case Key.Up: MoveCursor(-1); break;
            case Key.Down: MoveCursor(1); break;
            case Key.Enter:
            case Key.KpEnter: Confirm(); break;
            case Key.Backspace:
                if (_step == 1) { _step = 0; Redraw(); }
                break;
        }
    }

    private void Redraw()
    {
        _title.Text = _step == 0 ? "选择你的职业" : $"你的职业：{NameOf(_playerClass)}　—　选择对手职业";

        int half = Window / 2;
        int start = Math.Max(0, Math.Min(_cursor - half, OptionCount - Window));
        int end = Math.Min(OptionCount, start + Window);

        var sb = new System.Text.StringBuilder();
        for (int i = start; i < end; i++)
        {
            string label = i == _classes.Count
                ? "【随机】"
                : $"{_classes[i].ClassId,-4} {_classes[i].Name,-8} {_classes[i].Branch}";
            sb.Append(i == _cursor ? "▶  " : "    ").Append(label).Append('\n');
        }
        _body.Text = sb.ToString();
        _hint.Text = "↑/↓ 选择　Enter 确认" + (_step == 1 ? "　Backspace 返回" : "") + "　（共 " + _classes.Count + " 职业）";
    }

    private string NameOf(string classId)
    {
        foreach (var c in _classes)
            if (c.ClassId == classId) return c.Name;
        return classId;
    }
}
