using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Arena.Client.Visuals;
using Arena.Infra.Input;
using Godot;

// PRODUCTION - Arena.Client
// 对战 HUD（占位级，视觉规格待 docs/ux 落地后替换配色与尺寸）。
// 结构: 双方血量/蓝/耐力 + 职业名 + **技能栏（键位 + 技能名 + 冷却）** + 连段计数 + 结算面板。
// 数据全部来自 Sim 的公开状态（Cooldowns / HitstunCount），HUD 不做任何自己的判定。
namespace Arena.Client;

public sealed partial class Hud : CanvasLayer
{
    private const int SlotSize = 72;
    private const int SlotGap = 10;
    private const int SlotBottom = 700;      // 720 视口底部留 20px

    private sealed class Slot
    {
        public ColorRect Box = null!;
        public Label Key = null!;
        public Label Name = null!;
        public ColorRect Cd = null!;
        public ushort RuntimeId;
        public long TotalCdTicks;
    }

    private readonly ColorRect _hp0 = new() { Color = FighterVisualProfile.BlueTeam };
    private readonly ColorRect _mp0 = new() { Color = new Color(0.3f, 0.5f, 1f) };
    private readonly ColorRect _st0 = new() { Color = new Color(0.9f, 0.8f, 0.3f) };
    private readonly ColorRect _hp1 = new() { Color = FighterVisualProfile.OrangeTeam };
    private readonly ColorRect _mp1 = new() { Color = new Color(0.5f, 0.4f, 1f) };
    private readonly ColorRect _hp0Ghost = new() { Color = new Color(0.08f, 0.08f, 0.08f) };
    private readonly ColorRect _hp1Ghost = new() { Color = new Color(0.08f, 0.08f, 0.08f) };

    private readonly Label _name0 = new();
    private readonly Label _name1 = new();
    private readonly Label _info = new();
    private readonly Label _combo = new();
    private readonly Label _result = new();

    private readonly List<Slot> _slots = new();
    private readonly Node2D _slotRoot = new();

    public Hud()
    {
        AddBar(_hp0Ghost, 30, 24, 404, 26, new Color(0.08f, 0.08f, 0.08f));
        AddBar(_hp0, 34, 28, 396, 18, _hp0.Color);
        AddBar(_mp0, 34, 52, 396, 8, _mp0.Color);
        AddBar(_st0, 34, 64, 396, 5, _st0.Color);
        AddBar(_hp1Ghost, 846, 24, 404, 26, new Color(0.08f, 0.08f, 0.08f));   // 右侧与左侧留边一致
        AddBar(_hp1, 850, 28, 396, 18, _hp1.Color);
        AddBar(_mp1, 850, 52, 396, 8, _mp1.Color);

        Style(_name0, 30, 2, 400, HorizontalAlignment.Left, 18);
        Style(_name1, 850, 2, 400, HorizontalAlignment.Right, 18);
        Style(_info, 30, 76, 1220, HorizontalAlignment.Left, 14);
        Style(_combo, 0, 104, 1280, HorizontalAlignment.Center, 46);
        _combo.Modulate = new Color(1f, 0.8f, 0.25f);
        _combo.Visible = false;

        Style(_result, 0, 300, 1280, HorizontalAlignment.Center, 64);

        AddChild(_slotRoot);
    }

    private static void Style(Label l, float x, float y, float w, HorizontalAlignment align, int size)
    {
        l.Position = new Godot.Vector2(x, y);
        l.Size = new Godot.Vector2(w, size + 8);
        l.HorizontalAlignment = align;
        l.AddThemeFontSizeOverride("font_size", size);
    }

    private void AddBar(ColorRect bar, float x, float y, float w, float h, Color _)
    {
        bar.Position = new Godot.Vector2(x, y);
        bar.Size = new Godot.Vector2(w, h);
        AddChild(bar);
    }

    // ---- 每局重设（换职业 → 重建技能栏） ----

    public void Reset(RuntimeCatalog catalog, string playerClass, string playerName, string enemyName)
    {
        _result.Visible = false;
        _combo.Visible = false;
        _name0.Text = playerName;
        _name1.Text = enemyName;
        _info.Text = "WASD 移动　Space 跳　Shift 翻滚　J 普攻　R 重开　Esc 换职业";

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

            var slot = new Slot { RuntimeId = def.RuntimeId, TotalCdTicks = def.CooldownTicks };
            float x = x0 + i * (SlotSize + SlotGap);

            slot.Box = new ColorRect { Color = new Color(0.10f, 0.11f, 0.14f, 0.92f) };
            slot.Box.Position = new Godot.Vector2(x, y);
            slot.Box.Size = new Godot.Vector2(SlotSize, SlotSize);
            _slotRoot.AddChild(slot.Box);

            slot.Cd = new ColorRect { Color = new Color(0f, 0f, 0f, 0.62f) };
            slot.Cd.Position = new Godot.Vector2(x, y);
            slot.Cd.Size = new Godot.Vector2(SlotSize, 0);
            _slotRoot.AddChild(slot.Cd);

            var key = new Label { Text = ClassBindings.KeyLabel(binds[i].key) };
            key.Position = new Godot.Vector2(x + 5, y + 2);
            key.AddThemeFontSizeOverride("font_size", 20);
            key.Modulate = new Color(1f, 0.85f, 0.3f);
            _slotRoot.AddChild(key);
            slot.Key = key;

            var nameLbl = new Label { Text = def.Name };
            nameLbl.Position = new Godot.Vector2(x - 12, y + SlotSize + 2);
            nameLbl.Size = new Godot.Vector2(SlotSize + 24, 18);
            nameLbl.HorizontalAlignment = HorizontalAlignment.Center;
            nameLbl.AddThemeFontSizeOverride("font_size", 13);
            _slotRoot.AddChild(nameLbl);
            slot.Name = nameLbl;

            _slots.Add(slot);
        }
    }

    // ---- 每帧同步 ----

    public void Sync(SimWorld world, int tick)
    {
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];

        _hp0.Size = new Godot.Vector2(396f * f0.Hp / f0.HpMax, 18);
        _hp1.Size = new Godot.Vector2(396f * f1.Hp / f1.HpMax, 18);
        _mp0.Size = new Godot.Vector2(396f * f0.Mp / f0.MpMax, 8);
        _mp1.Size = new Godot.Vector2(396f * f1.Mp / f1.MpMax, 8);
        _st0.Size = new Godot.Vector2(396f * f0.Stamina / RuntimeConstants.STAMINA_MAX, 5);

        foreach (var s in _slots)
        {
            if (s.TotalCdTicks <= 0) continue;
            long remain = f0.Cooldowns.TryGetValue(s.RuntimeId, out var v) ? v : 0;
            float frac = remain <= 0 ? 0f : (float)remain / s.TotalCdTicks;
            if (frac > 1f) frac = 1f;
            float h = SlotSize * frac;
            s.Cd.Size = new Godot.Vector2(SlotSize, h);
            s.Cd.Position = new Godot.Vector2(s.Cd.Position.X, SlotBottom - h);
        }

        // 连段计数: 直接取 Sim 的「本连段被命中次数」（不做自己的判定）
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
