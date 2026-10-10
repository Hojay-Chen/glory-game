using Godot;

// PRODUCTION - Arena.Client
// 界面设计系统（唯一真源）。所有屏幕/控件的颜色、字号、间距、圆角、动效时长都从这里取，
// **禁止在界面代码里写魔数**——这是"看起来像正式产品而不是小 demo"的地基。
// 纯代码构样式（StyleBoxFlat/主题覆盖），不依赖任何图片资源。
namespace Arena.Client;

public static class UiTheme
{
    // ---- 调色板（暗色电竞基调；取自 art-bible 的阵营色作强调色）----
    public static readonly Color BgBase = new(0.043f, 0.055f, 0.078f);   // #0B0E14 底层
    public static readonly Color BgPanel = new(0.086f, 0.102f, 0.137f);  // #161A23 面板
    public static readonly Color BgRaised = new(0.118f, 0.137f, 0.180f); // #1E232E 抬起层
    public static readonly Color BgSunken = new(0.027f, 0.035f, 0.051f); // #07090D 凹陷
    public static readonly Color Border = new(0.180f, 0.208f, 0.271f);   // #2E3545 描边
    public static readonly Color BorderSoft = new(0.129f, 0.149f, 0.196f);

    public static readonly Color TextHi = new(0.902f, 0.925f, 0.965f);   // #E6ECF6 主文本
    public static readonly Color TextMid = new(0.639f, 0.678f, 0.749f);  // #A3ADBF 次文本
    public static readonly Color TextLo = new(0.435f, 0.475f, 0.545f);   // #6F798B 弱文本

    public static readonly Color AccentBlue = new(0.180f, 0.490f, 1.000f);   // #2E7DFF 我方
    public static readonly Color AccentOrange = new(1.000f, 0.541f, 0.169f); // #FF8A2B 敌方
    public static readonly Color AccentGold = new(0.949f, 0.788f, 0.259f);   // #F2C942 强调/选中
    public static readonly Color Success = new(0.208f, 0.769f, 0.420f);
    public static readonly Color Danger = new(1.000f, 0.349f, 0.302f);

    // ---- 缩放档（8pt 网格）----
    public const int Sp1 = 4, Sp2 = 8, Sp3 = 12, Sp4 = 16, Sp5 = 24, Sp6 = 32, Sp7 = 48, Sp8 = 64;

    // ---- 字号档 ----
    public const int FsMicro = 12, FsSmall = 14, FsBody = 16, FsSub = 20,
                     FsTitle = 28, FsH1 = 40, FsDisplay = 64;

    // ---- 圆角 ----
    public const int RSm = 4, RMd = 8, Rlg = 12, RPill = 999;

    // ---- 动效时长（秒）----
    public const float TFast = 0.08f, TBase = 0.15f, TSlow = 0.25f;

    public const int DesignW = 1280, DesignH = 720;   // 设计基准分辨率

    // ================= 控件工厂 =================

    public static StyleBoxFlat Box(Color fill, int radius = RMd, int borderPx = 0, Color? border = null)
    {
        var sb = new StyleBoxFlat { BgColor = fill };
        sb.SetCornerRadiusAll(radius);
        if (borderPx > 0)
        {
            sb.SetBorderWidthAll(borderPx);
            sb.BorderColor = border ?? Border;
        }
        sb.ContentMarginLeft = Sp4; sb.ContentMarginRight = Sp4;
        sb.ContentMarginTop = Sp3; sb.ContentMarginBottom = Sp3;
        return sb;
    }

    /// 面板（带描边，抬起层次）
    public static PanelContainer Panel(Node? parent = null, Color? fill = null, int radius = Rlg)
    {
        var p = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        p.AddThemeStyleboxOverride("panel", Box(fill ?? BgPanel, radius, 1));
        parent?.AddChild(p);
        return p;
    }

    /// 文本（统一字色与字号）
    public static Label Text(string s, int size = FsBody, Color? color = null,
        HorizontalAlignment align = HorizontalAlignment.Left, Node? parent = null)
    {
        var l = new Label { Text = s, HorizontalAlignment = align };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? TextHi);
        parent?.AddChild(l);
        return l;
    }

    /// 标题（带字距感：用大字重与留白而非加粗模拟）
    public static Label Title(string s, int size = FsTitle, Color? color = null, Node? parent = null)
    {
        var l = Text(s, size, color ?? TextHi, HorizontalAlignment.Center, parent);
        l.AddThemeConstantOverride("line_spacing", Sp2);
        return l;
    }

    /// 按钮（常态/悬停/按下/禁用 四态齐全——这是"正式产品"与"占位控件"的分水岭）
    public static Button Btn(string text, Color? accent = null, Node? parent = null)
    {
        var a = accent ?? AccentBlue;
        var b = new Button { Text = text, CustomMinimumSize = new Godot.Vector2(0, 40) };
        b.AddThemeFontSizeOverride("font_size", FsBody);
        b.AddThemeColorOverride("font_color", TextHi);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_pressed_color", Colors.White);
        b.AddThemeColorOverride("font_disabled_color", TextLo);

        b.AddThemeStyleboxOverride("normal", Box(BgRaised, RMd, 1, BorderSoft));
        b.AddThemeStyleboxOverride("hover", Box(BgRaised.Lightened(0.08f), RMd, 1, a));
        b.AddThemeStyleboxOverride("pressed", Box(a.Darkened(0.25f), RMd, 1, a));
        b.AddThemeStyleboxOverride("disabled", Box(BgSunken, RMd, 1, BorderSoft));
        b.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), RMd, 1, a));

        parent?.AddChild(b);
        return b;
    }

    /// 细分隔线
    public static ColorRect Divider(Node parent, float w, float h = 1)
    {
        var r = new ColorRect { Color = BorderSoft, CustomMinimumSize = new Godot.Vector2(w, h) };
        parent.AddChild(r);
        return r;
    }

    /// 布局助手：纵向盒（统一间距）
    public static VBoxContainer VBox(Node? parent = null, int sep = Sp3)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        parent?.AddChild(v);
        return v;
    }

    /// 布局助手：横向盒
    public static HBoxContainer HBox(Node? parent = null, int sep = Sp3)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        parent?.AddChild(h);
        return h;
    }

    /// 全屏根（统一底色）
    public static ColorRect Backdrop(Node? parent = null)
    {
        var r = new ColorRect { Color = BgBase };
        parent?.AddChild(r);
        // 必须在入树后设置：Godot 的 SetAnchorsPreset 只设锚点不设尺寸，新控件尺寸为 0 → 不可见
        r.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        r.Size = new Godot.Vector2(DesignW, DesignH);
        return r;
    }
}
