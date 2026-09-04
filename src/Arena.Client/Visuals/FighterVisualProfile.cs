using Godot;

// PRODUCTION - Arena.Client
// VS-6.5 表现档案: 职业视觉参数可替换（未来 BLAVisualProfile/BMGVisualProfile）——Sim 零修改。
namespace Arena.Client.Visuals;

public sealed record FighterVisualProfile(
    Color BodyColor0, Color BodyColor1,
    Color WeaponColor,
    Color SlashColor,
    Color HitSparkColor,
    float BodyRadius, float BodyHeight,
    float WeaponLength, float WeaponWidth)
{
    public static FighterVisualProfile Blade { get; } = new(
        BodyColor0: new Color(0.25f, 0.5f, 1f),
        BodyColor1: new Color(1f, 0.35f, 0.3f),
        WeaponColor: new Color(0.85f, 0.88f, 0.95f),
        SlashColor: new Color(0.7f, 0.9f, 1f),
        HitSparkColor: new Color(1f, 0.8f, 0.3f),
        BodyRadius: 0.4f, BodyHeight: 1.6f,
        WeaponLength: 0.9f, WeaponWidth: 0.08f);
}
