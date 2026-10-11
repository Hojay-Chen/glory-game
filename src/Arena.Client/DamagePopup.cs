using Godot;

// PRODUCTION - Arena.Client
// 飘伤害数字（命中反馈的第三层: 顿帧 → 火花 → 数字）。
// 纯表现——由命中表现线索的伤害值驱动，不读 Sim 状态、不影响任何判定。
namespace Arena.Client;

public static class DamagePopup
{
    /// 在世界坐标处弹出一个上浮渐隐的伤害数字并自销毁
    public static void Spawn(Node3D parent, Godot.Vector3 worldPos, long damage, bool isCrit)
    {
        if (damage <= 0) return;

        var label = new Label3D
        {
            Text = damage.ToString(),
            FontSize = isCrit ? 64 : 48,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,                       // 数字永远可见（不被角色挡住）
            Modulate = isCrit ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.95f, 0.9f),
            OutlineSize = 10,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            Position = worldPos + new Godot.Vector3(0, 0.85f, 0),
        };
        parent.AddChild(label);

        // 上浮幅度收窄（原 1.2m 会一路飘到名牌/HUD 上，和「职业名+血条」重叠——评审 R-6）
        var tween = label.CreateTween().SetParallel();
        tween.TweenProperty(label, "position", label.Position + new Godot.Vector3(0, 0.55f, 0), 0.62f);
        tween.TweenProperty(label, "modulate:a", 0f, 0.62f);
        tween.Chain().TweenCallback(Callable.From(label.QueueFree));
    }
}
