using System;
using System.Collections.Generic;
using Godot;

// PRODUCTION - Arena.Client
// 暂停界面（ui-spec §3.6）。半透明遮罩 + 居中面板；显示当前双方职业；
// 四个去向：继续 / 重开本局 / 换职业 / 返回主菜单。
// 冻结对局循环由调用方负责（复用「早退」模式）——本屏只表达意图，不触碰 Sim。
// 颜色/字号/间距/圆角/时长全部取自 UiTheme，无魔数、无 Godot 默认控件外观。
namespace Arena.Client;

public sealed partial class PauseScreen : CanvasLayer
{
    private const float ScrimAlpha = 0.72f;   // ui-spec §1.1 scrim
    private const int PanelW = 560;
    private const int PanelH = 460;
    private const int PanelY = 130;           // (720-460)/2
    private const int BtnW = 400;
    private const int BtnH = 48;
    private const int BtnSep = UiTheme.Sp3;
    private const int FooterY = 658;

    private readonly string _playerClass;
    private readonly string _enemyClass;
    private readonly Action _onResume;
    private readonly Action _onRestart;
    private readonly Action _onSwitchClass;
    private readonly Action _onMainMenu;

    private readonly List<Button> _buttons = new();
    private readonly List<string> _labels = new() { "继续", "重开本局", "换职业", "返回主菜单" };

    private Control _root = null!;
    private PanelContainer _halo = null!;
    private Tween? _focusTween;
    private int _cursor;
    private readonly int _step = BtnH + BtnSep;

    /// <param name="playerClass">我方职业中文名（显示用，本屏不改它）。</param>
    /// <param name="enemyClass">对手职业中文名。</param>
    /// <param name="onResume">「继续」——调用方解冻对局。</param>
    /// <param name="onRestart">「重开本局」——同职业/武器再战。</param>
    /// <param name="onSwitchClass">「换职业」——回到选职业。</param>
    /// <param name="onMainMenu">「返回主菜单」。</param>
    public PauseScreen(string playerClass, string enemyClass,
        Action onResume, Action onRestart, Action onSwitchClass, Action onMainMenu)
    {
        _playerClass = playerClass;
        _enemyClass = enemyClass;
        _onResume = onResume;
        _onRestart = onRestart;
        _onSwitchClass = onSwitchClass;
        _onMainMenu = onMainMenu;
        Layer = 10;
        Build();
    }

    public override void _Ready()
    {
        _root.Modulate = new Color(1f, 1f, 1f, 0f);
        var tw = CreateTween();
        tw.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(_root, "modulate:a", 1.0f, UiTheme.TBase);
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey k || !k.Pressed || k.Echo) return;

        switch (k.Keycode)
        {
            case Key.Up:
            case Key.Left: Move(-1); break;
            case Key.Down:
            case Key.Right: Move(1); break;
            case Key.Enter:
            case Key.KpEnter:
            case Key.Space: Activate(_cursor); break;
            case Key.Escape: _onResume(); break;   // Esc 在暂停中 = 继续
        }
    }

    // ================= 构图 =================

    private void Build()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var scrim = new ColorRect
        {
            Color = new Color(UiTheme.BgSunken.R, UiTheme.BgSunken.G, UiTheme.BgSunken.B, ScrimAlpha),
        };
        scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(scrim);

        BuildPanel();

        Band(UiTheme.Text("↑/↓ 选择　　Enter 确认　　Esc 继续", UiTheme.FsSmall, UiTheme.TextLo,
            HorizontalAlignment.Center, _root), FooterY, 26);

        ApplyFocus(false);
    }

    private void BuildPanel()
    {
        var panel = UiTheme.Panel(_root, UiTheme.BgPanel, UiTheme.Rlg);
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0; panel.AnchorBottom = 0;
        panel.OffsetLeft = -PanelW / 2f; panel.OffsetRight = PanelW / 2f;
        panel.OffsetTop = PanelY; panel.OffsetBottom = PanelY + PanelH;
        panel.CustomMinimumSize = new Vector2(PanelW, PanelH);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", UiTheme.Sp6);
        margin.AddThemeConstantOverride("margin_right", UiTheme.Sp6);
        margin.AddThemeConstantOverride("margin_top", UiTheme.Sp5);
        margin.AddThemeConstantOverride("margin_bottom", UiTheme.Sp5);
        panel.AddChild(margin);

        var col = UiTheme.VBox(margin, UiTheme.Sp4);
        col.Alignment = BoxContainer.AlignmentMode.Center;

        col.AddChild(UiTheme.Title("暂停", UiTheme.FsH1, UiTheme.TextHi));
        ClassLine(col, _playerClass, _enemyClass);
        UiTheme.Divider(col, 0f, 1f);
        BuildMenu(col);
    }

    private void BuildMenu(Node parent)
    {
        int n = _labels.Count;
        int dockH = n * BtnH + (n - 1) * BtnSep;

        var dock = new Control
        {
            CustomMinimumSize = new Vector2(BtnW, dockH),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(dock);

        _halo = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _halo.AddThemeStyleboxOverride("panel",
            UiTheme.Box(WithAlpha(UiTheme.AccentGold, 0.14f), UiTheme.RMd, 2, UiTheme.AccentGold));
        _halo.Size = new Vector2(BtnW + 2 * UiTheme.Sp1, BtnH + 2 * UiTheme.Sp1);
        dock.AddChild(_halo);

        var col = UiTheme.VBox(dock, BtnSep);
        col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;

        for (int i = 0; i < n; i++)
        {
            int idx = i;
            Color accent = idx == n - 1 ? UiTheme.AccentOrange : UiTheme.AccentBlue;
            var b = UiTheme.Btn(_labels[idx], accent, col);
            b.CustomMinimumSize = new Vector2(BtnW, BtnH);
            b.FocusMode = Control.FocusModeEnum.None;
            b.Alignment = HorizontalAlignment.Left;
            b.Pressed += () => Activate(idx);
            b.MouseEntered += () => SetCursor(idx);
            _buttons.Add(b);
        }
    }

    // ================= 交互 =================

    private void SetCursor(int idx)
    {
        if (idx == _cursor) return;
        _cursor = idx;
        ApplyFocus(true);
    }

    private void Move(int delta)
    {
        int n = _buttons.Count;
        _cursor = ((_cursor + delta) % n + n) % n;
        ApplyFocus(true);
    }

    private void Activate(int idx)
    {
        switch (idx)
        {
            case 0: _onResume(); break;
            case 1: _onRestart(); break;
            case 2: _onSwitchClass(); break;
            default: _onMainMenu(); break;
        }
    }

    private void ApplyFocus(bool animate)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            bool on = i == _cursor;
            _buttons[i].Text = (on ? "▶ " : "  ") + _labels[i];
            _buttons[i].AddThemeColorOverride("font_color", on ? UiTheme.AccentGold : UiTheme.TextHi);
        }

        var target = new Vector2(_halo.Position.X, _cursor * _step - UiTheme.Sp1);
        if (!animate) { _halo.Position = target; return; }

        _focusTween?.Kill();
        _focusTween = CreateTween();
        _focusTween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _focusTween.TweenProperty(_halo, "position", target, UiTheme.TBase);
    }

    // ================= 视觉构件 =================

    /// 「你 · 甲　VS　对手 · 乙」——职业身份用阵营色（颜色）+「你/对手」文字（第二通道）。
    private static void ClassLine(Node parent, string playerClass, string enemyClass)
    {
        var h = UiTheme.HBox(null, UiTheme.Sp4);
        h.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        h.Alignment = BoxContainer.AlignmentMode.Center;

        var p = UiTheme.Text($"你 · {playerClass}", UiTheme.FsBody, UiTheme.AccentBlue);
        var mid = UiTheme.Text("VS", UiTheme.FsSmall, UiTheme.TextLo);
        var e = UiTheme.Text($"对手 · {enemyClass}", UiTheme.FsBody, UiTheme.AccentOrange);
        p.VerticalAlignment = VerticalAlignment.Center;
        mid.VerticalAlignment = VerticalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;

        h.AddChild(p);
        h.AddChild(mid);
        h.AddChild(e);
        parent.AddChild(h);
    }

    private static void Band(Control c, int y, int h)
    {
        c.AnchorLeft = 0; c.AnchorRight = 1;
        c.OffsetLeft = UiTheme.Sp6; c.OffsetRight = -UiTheme.Sp6;
        c.OffsetTop = y; c.OffsetBottom = y + h;
        if (c is Label l) l.VerticalAlignment = VerticalAlignment.Center;
    }

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, a);
}
