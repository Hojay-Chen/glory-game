using System;
using System.Collections.Generic;
using Godot;

// PRODUCTION - Arena.Client
// 结算界面（ui-spec §3.5）。胜/负/平大字 + 双方职业 + 5 项基础数据 + 三个去向。
// 结果不只靠颜色区分：大字文案 + 形状符号（★/▼/＝）双通道冗余（§5.3 可访问性）。
// 5 项数据由调用方从 Sim 事件流聚合后传入（ui-spec D-05：聚合器在 Infra 层，本屏不自己判战斗）。
// 颜色/字号/间距/圆角/时长全部取自 UiTheme，无魔数。
namespace Arena.Client;

/// <summary>对局结果——决定结算大字文案、形状符号与配色。</summary>
public enum MatchOutcome { Win, Lose, Draw }

/// <summary>结算 5 项基础数据（ui-spec §3.5）。Ticks 以 60Hz Sim tick 计。</summary>
public readonly record struct MatchStats(
    int DamageDealt, int DamageTaken, int MaxCombo, int HitCount, int Ticks);

public sealed partial class ResultScreen : CanvasLayer
{
    private const float ScrimAlpha = 0.72f;   // ui-spec §1.1 scrim
    private const int BannerY = 104;
    private const int BannerH = 96;
    private const int SubtitleY = 210;
    private const int SubtitleH = 32;
    private const int PanelW = 560;
    private const int PanelH = 272;
    private const int PanelY = 252;
    private const int RowH = 32;
    private const int BtnW = 200;
    private const int BtnH = 48;
    private const int BtnSep = UiTheme.Sp5;
    private const int MenuTop = 548;
    private const int FooterY = 652;

    private readonly MatchOutcome _outcome;
    private readonly string _playerClass;
    private readonly string _enemyClass;
    private readonly MatchStats _stats;
    private readonly Action _onRematch;
    private readonly Action _onSwitchClass;
    private readonly Action _onMainMenu;

    private readonly List<Button> _buttons = new();
    private readonly List<string> _labels = new() { "再来一局", "换职业", "返回主菜单" };

    private Control _root = null!;
    private PanelContainer _halo = null!;
    private Tween? _focusTween;
    private int _cursor;
    private readonly int _step = BtnW + BtnSep;

    /// <param name="outcome">胜/负/平。</param>
    /// <param name="playerClass">我方职业中文名。</param>
    /// <param name="enemyClass">对手职业中文名。</param>
    /// <param name="stats">5 项基础数据（调用方聚合）。</param>
    /// <param name="onRematch">「再来一局」——同构筑再战。</param>
    /// <param name="onSwitchClass">「换职业」——Esc 同义。</param>
    /// <param name="onMainMenu">「返回主菜单」。</param>
    public ResultScreen(MatchOutcome outcome, string playerClass, string enemyClass, MatchStats stats,
        Action onRematch, Action onSwitchClass, Action onMainMenu)
    {
        _outcome = outcome;
        _playerClass = playerClass;
        _enemyClass = enemyClass;
        _stats = stats;
        _onRematch = onRematch;
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
            case Key.Escape: _onSwitchClass(); break;   // ui-spec §3.5：结算 Esc = 换职业
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

        // 结果大字：文案 + 形状符号双通道 + 结果色。
        var banner = UiTheme.Title($"{Symbol()}　{OutcomeText()}", UiTheme.FsDisplay, OutcomeColor(), _root);
        Band(banner, BannerY, BannerH);

        // 双方职业（居中容器，内容宽度自适应）。
        var holder = new CenterContainer();
        holder.AnchorLeft = 0; holder.AnchorRight = 1;
        holder.OffsetLeft = UiTheme.Sp6; holder.OffsetRight = -UiTheme.Sp6;
        holder.OffsetTop = SubtitleY; holder.OffsetBottom = SubtitleY + SubtitleH;
        _root.AddChild(holder);
        ClassLine(holder, _playerClass, _enemyClass);

        BuildStats();
        BuildMenu();

        Band(UiTheme.Text("↑/↓ 选择　　Enter 确认　　Esc 换职业", UiTheme.FsSmall, UiTheme.TextLo,
            HorizontalAlignment.Center, _root), FooterY, 26);

        ApplyFocus(false);
    }

    private void BuildStats()
    {
        var panel = UiTheme.Panel(_root, UiTheme.BgPanel, UiTheme.Rlg);
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0; panel.AnchorBottom = 0;
        panel.OffsetLeft = -PanelW / 2f; panel.OffsetRight = PanelW / 2f;
        panel.OffsetTop = PanelY; panel.OffsetBottom = PanelY + PanelH;
        panel.CustomMinimumSize = new Vector2(PanelW, PanelH);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", UiTheme.Sp4);
        margin.AddThemeConstantOverride("margin_right", UiTheme.Sp4);
        margin.AddThemeConstantOverride("margin_top", UiTheme.Sp4);
        margin.AddThemeConstantOverride("margin_bottom", UiTheme.Sp4);
        panel.AddChild(margin);

        var col = UiTheme.VBox(margin, UiTheme.Sp2);
        col.AddChild(UiTheme.Text("对局数据", UiTheme.FsSub, UiTheme.TextMid, HorizontalAlignment.Center));
        UiTheme.Divider(col, 0f, 1f);

        Row(col, "输出伤害", _stats.DamageDealt.ToString());
        Row(col, "承受伤害", _stats.DamageTaken.ToString());
        Row(col, "最大连段", $"{_stats.MaxCombo} 连");
        Row(col, "命中次数", $"{_stats.HitCount} 次");
        Row(col, "对局用时", FormatTime(_stats.Ticks));
    }

    private static void Row(Node parent, string label, string value)
    {
        var row = UiTheme.HBox(parent, UiTheme.Sp4);
        row.CustomMinimumSize = new Vector2(0, RowH);

        var k = UiTheme.Text(label, UiTheme.FsBody, UiTheme.TextMid);
        k.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        k.VerticalAlignment = VerticalAlignment.Center;

        var v = UiTheme.Text(value, UiTheme.FsBody, UiTheme.TextHi, HorizontalAlignment.Right);
        v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        v.VerticalAlignment = VerticalAlignment.Center;

        row.AddChild(k);
        row.AddChild(v);
    }

    private void BuildMenu()
    {
        int n = _labels.Count;
        int dockW = n * BtnW + (n - 1) * BtnSep;

        var dock = new Control { CustomMinimumSize = new Vector2(dockW, BtnH) };
        dock.AnchorLeft = 0.5f; dock.AnchorRight = 0.5f;
        dock.OffsetLeft = -dockW / 2f; dock.OffsetRight = dockW / 2f;
        dock.OffsetTop = MenuTop; dock.OffsetBottom = MenuTop + BtnH;
        _root.AddChild(dock);

        _halo = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _halo.AddThemeStyleboxOverride("panel",
            UiTheme.Box(WithAlpha(UiTheme.AccentGold, 0.14f), UiTheme.RMd, 2, UiTheme.AccentGold));
        _halo.Size = new Vector2(BtnW + 2 * UiTheme.Sp1, BtnH + 2 * UiTheme.Sp1);
        dock.AddChild(_halo);

        var row = UiTheme.HBox(dock, BtnSep);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.MouseFilter = Control.MouseFilterEnum.Ignore;

        for (int i = 0; i < n; i++)
        {
            int idx = i;
            Color accent = idx == n - 1 ? UiTheme.AccentOrange : UiTheme.AccentBlue;
            var b = UiTheme.Btn(_labels[idx], accent, row);
            b.CustomMinimumSize = new Vector2(BtnW, BtnH);
            b.FocusMode = Control.FocusModeEnum.None;
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
            case 0: _onRematch(); break;
            case 1: _onSwitchClass(); break;
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

        var target = new Vector2(_cursor * _step - UiTheme.Sp1, _halo.Position.Y);   // 横向滑动
        if (!animate) { _halo.Position = target; return; }

        _focusTween?.Kill();
        _focusTween = CreateTween();
        _focusTween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _focusTween.TweenProperty(_halo, "position", target, UiTheme.TBase);
    }

    // ================= 文案 / 视觉构件 =================

    private string OutcomeText() => _outcome switch
    {
        MatchOutcome.Win => "胜利",
        MatchOutcome.Lose => "失败",
        _ => "平局",
    };

    /// 形状通道：不依赖颜色的第二种区分（★ 胜 / ▼ 负 / ＝ 平）。
    private string Symbol() => _outcome switch
    {
        MatchOutcome.Win => "★",
        MatchOutcome.Lose => "▼",
        _ => "＝",
    };

    private Color OutcomeColor() => _outcome switch
    {
        MatchOutcome.Win => UiTheme.AccentGold,   // win 色（ui-spec §1.1）
        MatchOutcome.Lose => UiTheme.TextMid,     // lose 用中性灰，读作「黯」
        _ => UiTheme.TextHi,                      // draw
    };

    private static string FormatTime(int ticks)
    {
        int total = ticks / 60;   // 60 Sim tick/s
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// 「你 · 甲　VS　对手 · 乙」——职业身份用阵营色 + 文字标签双通道。
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
