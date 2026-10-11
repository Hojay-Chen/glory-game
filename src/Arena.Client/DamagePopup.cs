using Godot;

// PRODUCTION - Arena.Client
// 飘字（命中反馈的第三层: 顿帧 → 火花 → 飘字）。
// 纯表现——由表现线索驱动，不读 Sim 状态、不影响任何判定。
namespace Arena.Client;

public static class DamagePopup
{
    /// 伤害数字（暴击加大加色）
    public static void Spawn(Node3D parent, Godot.Vector3 worldPos, long damage, bool isCrit)
    {
        if (damage <= 0) return;
        SpawnText(parent, worldPos, damage.ToString(),
            isCrit ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.95f, 0.9f),
            isCrit ? 64 : 48);
    }

    /// 任意飘字——用于"荣耀高光"（取消 / 受身 / 撞墙 / 破防 / 连段）等非数值提示
    public static void SpawnText(Node3D parent, Godot.Vector3 worldPos, string text, Color col,
        int fontSize = 44, float rise = 0.55f, float life = 0.62f)
    {
        if (string.IsNullOrEmpty(text)) return;

        var label = new Label3D
        {
            Text = text,
            FontSize = fontSize,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Modulate = col,
            OutlineSize = 10,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            Position = worldPos + new Godot.Vector3(0, 0.85f, 0),
        };
        parent.AddChild(label);

        var tween = label.CreateTween().SetParallel();
        tween.TweenProperty(label, "position", label.Position + new Godot.Vector3(0, rise, 0), life);
        tween.TweenProperty(label, "modulate:a", 0f, life);
        tween.Chain().TweenCallback(Callable.From(label.QueueFree));
    }
}
