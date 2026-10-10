using System;
using System.Collections.Generic;
using Godot;

// PRODUCTION - Arena.Client
// 主菜单（UI-01，ui-spec §3.1）。三段构图：标题区 / 主操作区 / 底部信息行；
// 背景分两层（底色 + 由 token 派生的纵向渐变）营造氛围，而非干巴巴的按钮堆。
// 所有颜色/字号/间距/圆角/时长取自 UiTheme（设计系统唯一真源），界面代码无魔数。
// 本屏不触碰任何战斗状态——只把「玩家意图」经构造传入的 Action 回交调用方。
namespace Arena.Client;

public sealed partial class MainMenuScreen : CanvasLayer
{
    // —— 布局常量（1280×720 基准，坐标取自 ui-spec §3.1）——
    private const int BtnW = 320;
    private const int BtnH = 48;
    private const int BtnSep = UiTheme.Sp3;
    private const int TitleY = 118;
    private const int TitleH = 88;
    private const int SubtitleY = 210;
    private const int SubtitleH = 30;
    private const int AccentY = 256;
    private const int AccentW = 220;
    private const int MenuTop = 312;
    private const int FooterRuleY = 642;
    private const int FooterY = 658;

    private readonly Action _onStart;
    private readonly Action _onSettings;
    private readonly Action _onQuit;

    private readonly List<Button> _buttons = new();
    private readonly List<string> _labels = new() { "开始对战", "设置", "退出" };

    private Control _root = null!;
    private PanelContainer _halo = null!;
    private Tween? _focusTween;
    private int _cursor;
    private readonly int _step = BtnH + BtnSep;

    /// <param name="onStart">「开始对战」——调用方负责进入选职业/装配对局。</param>
    /// <param name="onSettings">「设置」——调用方负责弹出设置面板。</param>
    /// <param name="onQuit">「退出」——调用方负责退出（本屏不直接调用 GetTree().Quit）。</param>
    public MainMenuScreen(Action onStart, Action onSettings, Action onQuit)
    {
        _onStart = onStart;
        _onSettings = onSettings;
        _onQuit = onQuit;
        Layer = 10;
        Build();
    }

    public override void _Ready()
    {
        // 克制入场：整体淡入（≤ TBase），无过场炫技。
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
        }
    }

    // ================= 构图 =================

    private void Build()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // 背景层次 1：底色。背景层次 2：纵向渐变（颜色由 token 派生）。
        UiTheme.Backdrop(_root);
        _root.AddChild(BackgroundGradient());

        // —— 标题区 ——
        Band(UiTheme.Title("廿四争锋", UiTheme.FsDisplay, UiTheme.TextHi, _root), TitleY, TitleH);
        Band(UiTheme.Text("竞技场对战", UiTheme.FsSub, UiTheme.TextMid, HorizontalAlignment.Center, _root),
            SubtitleY, SubtitleH);
        CenteredRule(_root, AccentY, 2, UiTheme.AccentGold, AccentW);

        // —— 主操作区 ——
        BuildMenu();

        // —— 底部信息行 ——
        Rule(_root, FooterRuleY, 1, UiTheme.BorderSoft, UiTheme.Sp6);
        Band(UiTheme.Text("↑/↓ 选择　　Enter 确认", UiTheme.FsSmall, UiTheme.TextLo,
            HorizontalAlignment.Center, _root), FooterY, 26);

        ApplyFocus(false);
    }

    private void BuildMenu()
    {
        int n = _labels.Count;
        int dockH = n * BtnH + (n - 1) * BtnSep;

        var dock = new Control { CustomMinimumSize = new Vector2(BtnW, dockH), MouseFilter = Control.MouseFilterEnum.Ignore };
        dock.AnchorLeft = 0.5f; dock.AnchorRight = 0.5f;
        dock.OffsetLeft = -BtnW / 2f; dock.OffsetRight = BtnW / 2f;
        dock.OffsetTop = MenuTop; dock.OffsetBottom = MenuTop + dockH;
        _root.AddChild(dock);

        // 焦点高亮：滑动的金色描边光晕（置于按钮之下→形成外发光轮廓）。
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
            int idx = i;   // 闭包捕获：for 循环变量共享，必须复制到局部
            Color accent = idx == n - 1 ? UiTheme.AccentOrange : UiTheme.AccentBlue;
            var b = UiTheme.Btn(_labels[idx], accent, col);
            b.CustomMinimumSize = new Vector2(BtnW, BtnH);
            b.FocusMode = Control.FocusModeEnum.None;   // 键盘由本屏手动导航，避免与 ui_accept 双触发
            b.Alignment = HorizontalAlignment.Left;
            b.Pressed += () => Activate(idx);
            b.MouseEntered += () => SetCursor(idx);      // 鼠标悬停即聚焦
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
        _cursor = ((_cursor + delta) % n + n) % n;   // 含环绕
        ApplyFocus(true);
    }

    private void Activate(int idx)
    {
        switch (idx)
        {
            case 0: _onStart(); break;
            case 1: _onSettings(); break;
            default: _onQuit(); break;
        }
    }

    private void ApplyFocus(bool animate)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            bool on = i == _cursor;
            // 双通道焦点：左侧 ▶ 光标（形状）+ 金色文字（颜色）。
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

    private static TextureRect BackgroundGradient()
    {
        var g = new Gradient();
        g.SetOffset(0, 0f);
        g.SetOffset(1, 1f);
        g.SetColor(0, UiTheme.BgBase);                              // 顶：底色
        g.SetColor(1, UiTheme.BgBase.Lerp(UiTheme.AccentBlue, 0.18f)); // 底：向阵营蓝偏移，营造纵深

        var tex = new GradientTexture2D
        {
            Gradient = g,
            Width = 8,
            Height = 256,
            FillFrom = new Vector2(0.5f, 0f),
            FillTo = new Vector2(0.5f, 1f),
        };
        var tr = new TextureRect { Texture = tex, StretchMode = TextureRect.StretchModeEnum.Scale };
        tr.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return tr;
    }

    private static void Band(Control c, int y, int h)
    {
        c.AnchorLeft = 0; c.AnchorRight = 1;
        c.OffsetLeft = UiTheme.Sp6; c.OffsetRight = -UiTheme.Sp6;
        c.OffsetTop = y; c.OffsetBottom = y + h;
        if (c is Label l) l.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void Rule(Control parent, int y, float h, Color color, float inset)
    {
        var r = new ColorRect { Color = color };
        r.AnchorLeft = 0; r.AnchorRight = 1;
        r.OffsetLeft = inset; r.OffsetRight = -inset;
        r.OffsetTop = y; r.OffsetBottom = y + h;
        parent.AddChild(r);
    }

    private static void CenteredRule(Control parent, int y, float h, Color color, int w)
    {
        var r = new ColorRect { Color = color };
        r.AnchorLeft = 0.5f; r.AnchorRight = 0.5f;
        r.OffsetLeft = -w / 2f; r.OffsetRight = w / 2f;
        r.OffsetTop = y; r.OffsetBottom = y + h;
        parent.AddChild(r);
    }

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, a);
}
