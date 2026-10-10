using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Arena.Infra.Input;
using Arena.Client.Visuals;
using Godot;

// PRODUCTION - Arena.Client
// 对战 HUD（ui-spec §3.4）: 双方血量/蓝/耐力 + 职业名 + 技能栏（键位·技能名·冷却·档位色·MP 校验）
// + 连段计数 + 结算面板。
// 关键设计（评审 §5-3）：
//   · 对手血条叠 **45° 斜条纹**——颜色之外的第二识别通道（色盲安全）
//   · 技能格用**档位色外框**（T1–U 各一色），不看字也能分辨技能等级
//   · 冷却中在格心显示**剩余秒数**；**MP 不足**时外框转红
// 数据全部来自 Sim 公开状态（Cooldowns / Mp / HitstunCount），HUD 不做自己的判定。
namespace Arena.Client;

public sealed partial class Hud : CanvasLayer
{
    private const int SlotSize = 68;
    private const int SlotGap = 10;
    private const int SlotBottom = 700;

    private sealed class Slot
    {
        public ColorRect Box = null!;
        public ColorRect Frame = null!;
        public Label Key = null!;
        public Label Name = null!;
        public Label CdText = null!;
        public ColorRect Cd = null!;
        public ushort RuntimeId;
        public long TotalCdTicks;
        public long MpCost;
        public byte Tier;
    }

    private readonly ColorRect _hp0 = new() { Color = FighterVisualProfile.BlueTeam };
    private readonly ColorRect _mp0 = new() { Color = new Color(0.3f, 0.5f, 1f) };
    private readonly ColorRect _st0 = new() { Color = new Color(0.9f, 0.8f, 0.3f) };
    private readonly ColorRect _hp1 = new() { Color = FighterVisualProfile.OrangeTeam };
    private readonly ColorRect _mp1 = new() { Color = new Color(0.5f, 0.4f, 1f) };
    private readonly ColorRect _hp0Ghost = new() { Color = new Color(0.08f, 0.08f, 0.08f) };
    private readonly ColorRect _hp1Ghost = new() { Color = new Color(0.08f, 0.08f, 0.08f) };
    private readonly TextureRect _hp1Stripes = new();

    private readonly Label _name0 = new();
    private readonly Label _name1 = new();
    private readonly Label _info = new();
    private readonly Label _combo = new();
    private readonly Label _result = new();
    private readonly Label _cdHeader = new();

    private readonly List<Slot> _slots = new();
    private readonly Node2D _slotRoot = new();

    public Hud()
    {
        AddBar(_hp0Ghost, 30, 24, 404, 26);
        AddBar(_hp0, 34, 28, 396, 18);
        AddBar(_mp0, 34, 52, 396, 8);
        AddBar(_st0, 34, 64, 396, 5);
        AddBar(_hp1Ghost, 846, 24, 404, 26);
        AddBar(_hp1, 850, 28, 396, 18);
        AddBar(_mp1, 850, 52, 396, 8);

        // 对手血条的第二识别通道：45° 斜条纹（不靠颜色也能分辨敌我）
        _hp1Stripes.Texture = MakeStripeTexture();
        _hp1Stripes.Position = new Godot.Vector2(850, 28);
        _hp1Stripes.Size = new Godot.Vector2(396, 18);
        _hp1Stripes.StretchMode = TextureRect.StretchModeEnum.Tile;
        AddChild(_hp1Stripes);

        Style(_name0, 30, 2, 400, HorizontalAlignment.Left, 18);
        Style(_name1, 850, 2, 400, HorizontalAlignment.Right, 18);
        Style(_info, 30, 76, 1220, HorizontalAlignment.Left, 14);
        Style(_combo, 0, 100, 1280, HorizontalAlignment.Center, 46);
        _combo.Modulate = new Color(1f, 0.8f, 0.25f);
        _combo.Visible = false;
        Style(_cdHeader, 0, SlotBottom - SlotSize - 22, 1280, HorizontalAlignment.Center, 14);
        _cdHeader.Modulate = new Color(1f, 1f, 1f, 0.55f);
        _cdHeader.Text = "技能";
        Style(_result, 0, 300, 1280, HorizontalAlignment.Center, 64);

        AddChild(_slotRoot);
    }

    /// 45° 斜条纹贴图（程序生成，零素材）
    private static Texture2D MakeStripeTexture()
    {
        const int N = 16;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                bool on = ((x + y) % 8) < 4;
                img.SetPixel(x, y, on ? new Color(0f, 0f, 0f, 0.30f) : new Color(0f, 0f, 0f, 0f));
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// 技能档位色（T1..U）——外框用，让玩家不看字也能分辨技能等级
    private static Color TierColor(byte tier) => tier switch
    {
        1 => new Color(0.55f, 0.72f, 0.86f),
        2 => new Color(0.44f, 0.78f, 0.56f),
        3 => new Color(0.86f, 0.72f, 0.36f),
        4 => new Color(0.86f, 0.50f, 0.36f),
        5 => new Color(0.72f, 0.48f, 0.92f),
        _ => new Color(0.5f, 0.5f, 0.5f),
    };

    private static void Style(Label l, float x, float y, float w, HorizontalAlignment align, int size)
    {
        l.Position = new Godot.Vector2(x, y);
        l.Size = new Godot.Vector2(w, size + 8);
        l.HorizontalAlignment = align;
        l.AddThemeFontSizeOverride("font_size", size);
    }

    private void AddBar(ColorRect bar, float x, float y, float w, float h)
    {
        bar.Position = new Godot.Vector2(x, y);
        bar.Size = new Godot.Vector2(w, h);
        AddChild(bar);
    }

    public void Reset(RuntimeCatalog catalog, string playerClass, string playerName, string enemyName)
    {
        _result.Visible = false;
        _combo.Visible = false;
        _name0.Text = playerName;
        _name1.Text = enemyName;
        _info.Text = "WASD 移动　Space 跳　Shift 翻滚　J 普攻　R 重开　Esc 暂停";

        foreach (var child in _slotRoot.GetChildren()) child.QueueFree();
        _slots.Clear();

        var binds = ClassBindings.For(catalog, playerClass)
            .Where(b => b.role is "skill" or "guard" && b.skillUid != 0)
            .ToList();
        int n = binds.Count;
        float totalW = n * SlotSize + (n - 1) * SlotGap;
        float x0 = (1280 - totalW) / 2f;
        float y = SlotBottom - SlotSize;

        for (int i = 0; i < n; i++)
        {
            var def = catalog.Skills.FirstOrDefault(s => s.RuntimeId == binds[i].skillUid);
            if (def is null) continue;

            var slot = new Slot
            {
                RuntimeId = def.RuntimeId, TotalCdTicks = def.CooldownTicks,
                MpCost = def.MpCost, Tier = def.Tier,
            };
            float x = x0 + i * (SlotSize + SlotGap);

            slot.Frame = new ColorRect { Color = TierColor(def.Tier) };
            slot.Frame.Position = new Godot.Vector2(x - 2, y - 2);
            slot.Frame.Size = new Godot.Vector2(SlotSize + 4, SlotSize + 4);
            _slotRoot.AddChild(slot.Frame);

            slot.Box = new ColorRect { Color = new Color(0.10f, 0.11f, 0.14f, 0.94f) };
            slot.Box.Position = new Godot.Vector2(x, y);
            slot.Box.Size = new Godot.Vector2(SlotSize, SlotSize);
            _slotRoot.AddChild(slot.Box);

            slot.Cd = new ColorRect { Color = new Color(0f, 0f, 0f, 0.62f) };
            slot.Cd.Position = new Godot.Vector2(x, y);
            slot.Cd.Size = new Godot.Vector2(SlotSize, 0);
            _slotRoot.AddChild(slot.Cd);

            var key = new Label { Text = ClassBindings.KeyLabel(binds[i].key) };
            key.Position = new Godot.Vector2(x + 5, y + 1);
            key.AddThemeFontSizeOverride("font_size", 20);
            key.Modulate = TierColor(def.Tier).Lightened(0.25f);
            _slotRoot.AddChild(key);
            slot.Key = key;

            // 冷却剩余秒数（居中显示）
            var cdt = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            cdt.Position = new Godot.Vector2(x, y + SlotSize / 2 - 12);
            cdt.Size = new Godot.Vector2(SlotSize, 24);
            cdt.AddThemeFontSizeOverride("font_size", 20);
            cdt.AddThemeColorOverride("font_color", Colors.White);
            cdt.Visible = false;
            _slotRoot.AddChild(cdt);
            slot.CdText = cdt;

            var nameLbl = new Label { Text = def.Name };
            nameLbl.Position = new Godot.Vector2(x - 16, y + SlotSize + 6);
            nameLbl.Size = new Godot.Vector2(SlotSize + 32, 18);
            nameLbl.HorizontalAlignment = HorizontalAlignment.Center;
            nameLbl.AddThemeFontSizeOverride("font_size", 14);   // §3.4 下限 14px
            _slotRoot.AddChild(nameLbl);
            slot.Name = nameLbl;

            _slots.Add(slot);
        }
    }

    public void Sync(SimWorld world, int tick)
    {
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];

        _hp0.Size = new Godot.Vector2(396f * f0.Hp / f0.HpMax, 18);
        var hp1w = 396f * f1.Hp / f1.HpMax;
        _hp1.Size = new Godot.Vector2(hp1w, 18);
        _hp1Stripes.Size = new Godot.Vector2(hp1w, 18);
        _mp0.Size = new Godot.Vector2(396f * f0.Mp / f0.MpMax, 8);
        _mp1.Size = new Godot.Vector2(396f * f1.Mp / f1.MpMax, 8);
        _st0.Size = new Godot.Vector2(396f * f0.Stamina / RuntimeConstants.STAMINA_MAX, 5);

        foreach (var s in _slots)
        {
            long remain = s.TotalCdTicks > 0 && f0.Cooldowns.TryGetValue(s.RuntimeId, out var v) ? v : 0;
            float frac = s.TotalCdTicks <= 0 ? 0f : (float)remain / s.TotalCdTicks;
            if (frac > 1f) frac = 1f;
            float h = SlotSize * frac;
            s.Cd.Size = new Godot.Vector2(SlotSize, h);
            s.Cd.Position = new Godot.Vector2(s.Cd.Position.X, SlotBottom - h);

            s.CdText.Visible = remain > 0;
            if (remain > 0) s.CdText.Text = (remain / 60f).ToString("0.0");

            // MP 不足 → 外框转红（冷却/蓝耗是两个独立门槛，玩家要能分别看出）
            bool noMp = f0.Mp < s.MpCost;
            s.Frame.Color = noMp ? new Color(0.85f, 0.25f, 0.25f) : TierColor(s.Tier);
        }

        int combo = f1.HitstunCount;
        _combo.Visible = combo >= 2;
        if (combo >= 2) _combo.Text = $"{combo} 连击";
    }

    public void ShowResult(string result, int tick)
    {
        _result.Text = result;
        _result.Visible = true;
        _info.Text = $"tick {tick} — R 重开（同职业）　Esc 换职业";
    }
}
