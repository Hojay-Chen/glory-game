using Godot;

// PRODUCTION - Arena.Client
// 视觉档案: 阵营色 + 系别色 + 系别剪影族。按 `docs/art/art-bible-v1.md` 取值。
// 与 Sim 完全解耦——判定体是纯几何（胶囊/盒子），外形怎么改都不影响战斗。
namespace Arena.Client.Visuals;

public sealed record FighterVisualProfile(
    string ClassId,
    string Branch,
    Color TeamA,        // 阵营 ①（本地玩家）
    Color TeamB,        // 阵营 ②（对手）
    Color Accent,       // 系别徽记色
    BranchShape Shape,  // 剪影族（决定肩垫/背饰/腿长等变体件）
    string WeaponMesh)  // 武器家族网格名（assets/weapons.glb 内的节点）
{
    /// 阵营色取自美术规格的**色盲安全对**（蓝 / 橙，非红绿），并配形状冗余
    public static readonly Color BlueTeam = new(0.18f, 0.49f, 1.00f);    // #2E7DFF
    public static readonly Color OrangeTeam = new(1.00f, 0.54f, 0.17f);  // #FF8A2B
    private static readonly Color Dark = new(0.165f, 0.180f, 0.212f);    // #2A2E36 靴/手/中性件

    public Color BodyColor0 => BlueTeam;
    public Color BodyColor1 => OrangeTeam;

    // 特效与武器色（原档案字段，保持 FighterView 接口不变）
    public Color WeaponColor => new(0.85f, 0.88f, 0.95f);
    public Color SlashColor => new(0.70f, 0.90f, 1.00f);
    public Color HitSparkColor => new(1.00f, 0.80f, 0.30f);
    public Color GuardSparkColor => new(0.55f, 0.80f, 1.00f);
    public Color ParrySparkColor => new(1.00f, 1.00f, 0.85f);

    // 判定体参考尺寸（视觉已改为人形装配，这几个值仅供特效/伤害数字定位参考）
    public float BodyRadius => 0.40f;
    public float BodyHeight => 1.60f;
    public float WeaponLength => 0.90f;
    public float WeaponWidth => 0.08f;

    /// 系别 → 剪影族 + 徽记色（class-base.csv 的 branch 字段为中文系别名）
    public static FighterVisualProfile For(string classId, string branch, string weaponType = "") => new(
        classId, branch, BlueTeam, OrangeTeam, AccentOf(branch), ShapeOf(branch),
        WeaponFamily.Of(weaponType));

    private static Color AccentOf(string branch) => branch switch
    {
        "魔法师系" => new Color(0.545f, 0.361f, 0.965f),   // #8B5CF6 奥术紫
        "剑士系" => new Color(0.208f, 0.655f, 0.769f),     // #35A7C4 锋钢青
        "枪手系" => new Color(0.949f, 0.710f, 0.204f),     // #F2B534 炽弹黄
        "格斗系" => new Color(0.824f, 0.231f, 0.231f),     // #D23B3B 煞气赤
        "暗夜系" => new Color(0.247f, 0.710f, 0.494f),     // #3FB57E 幽冥绿
        "圣职系" => new Color(0.961f, 0.914f, 0.659f),     // #F5E9A8 圣辉金
        _ => new Color(0.910f, 0.925f, 0.949f),            // #E8ECF2 散人 中性白
    };

    private static BranchShape ShapeOf(string branch) => branch switch
    {
        "魔法师系" => BranchShape.Mage,
        "剑士系" => BranchShape.Blade,
        "枪手系" => BranchShape.Gunner,
        "格斗系" => BranchShape.Brawler,
        "暗夜系" => BranchShape.Night,
        "圣职系" => BranchShape.Cleric,
        _ => BranchShape.Free,
    };

    public static Color DarkPart => Dark;
}

/// 六系 + 散人的剪影族（决定变体件：肩垫形状 / 背饰 / 体型微调）
public enum BranchShape : byte { Mage, Blade, Gunner, Brawler, Night, Cleric, Free }
