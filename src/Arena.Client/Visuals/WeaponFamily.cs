using System;

// PRODUCTION - Arena.Client
// weapons.csv 的 type（21 种）→ 8 个武器家族网格（`assets/weapons.glb` 内的节点名）。
// 采用**家族**而非逐把建模：21 种 type 的剪影差异不足以支撑 21 个独立模型，
// 而归族后每个职业仍有明显不同的武器外形（评审 D2：此前全职业共用同一个盒子）。
namespace Arena.Client.Visuals;

public static class WeaponFamily
{
    public const string Fallback = "W_Sword";

    public static string Of(string weaponType) => weaponType switch
    {
        "sword" or "katana" or "twin_sword" => "W_Sword",            // 剑／刀／双剑
        "heavy_sword" or "magic_sword" or "shield_sword" or "scythe" => "W_Greatsword",  // 重剑／魔剑／剑盾／镰
        "spear" or "umbrella" => "W_Spear",                           // 枪／伞（同为长杆）
        "pistol" or "mech_pistol" or "cannon" or "crossbow" => "W_Gun",    // 手枪／机铳／炮／弩
        "claw" or "gauntlet" or "glove" => "W_Claw",                   // 爪／拳套／护手
        "staff" or "scepter" or "wand" => "W_Staff",                   // 法杖／权杖／魔杖
        "dagger" => "W_Dagger",
        "broom" => "W_Broom",
        _ => Fallback,
    };
}
