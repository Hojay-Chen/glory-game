using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Infra.Data;

// PRODUCTION - Arena.Infra
// VS-9 职业通用键位：把「某职业的哪几个技绑到哪几个键」这件事放在 Infra（纯逻辑、可测），
// 而不是写死在 Godot 客户端里——此前 InputTranslator 直接把剑客的 4 个 skill_id 硬编码，
// 导致换职业后按键全部失效、HUD 提示也对不上。
// 只做「键位 → 技能号」的搬运；合法性/CD/MP 仍由 Sim 裁决。
namespace Arena.Infra.Input;

public static class ClassBindings
{
    /// 档位 → 键位（每档取该职业首个可用代表技）
    private static readonly (int Key, byte Tier, string Label)[] TierSlots =
    {
        (InputMapper.KeyIds.K, 1, "T1"),
        (InputMapper.KeyIds.L, 2, "T2"),
        (InputMapper.KeyIds.I, 3, "T3"),
        (InputMapper.KeyIds.O, 4, "T4"),
        (InputMapper.KeyIds.P, 5, "U"),
    };

    /// 该职业的键位表：Space 跳 / Shift 翻滚 / J 普攻 / K..P 各档技 / U 格挡（hold 姿态技）。
    /// 某档没有可用技时该键不绑定（客户端按下无效果——Sim 侧本就会拒绝非法技能）。
    public static List<(int key, string role, ushort skillUid)> For(RuntimeCatalog catalog, string classId)
    {
        var list = new List<(int, string, ushort)>
        {
            (InputMapper.KeyIds.Space, "jump", 0),
            (InputMapper.KeyIds.Shift, "roll", 0),
            (InputMapper.KeyIds.J, "basic", 0),
        };

        foreach (var (key, tier, _) in TierSlots)
        {
            var def = catalog.Skills
                .Where(s => s.ClassId == classId && s.Tier == tier && !s.IsHold && s.Type != "passive")
                .OrderBy(s => s.SkillId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (def is not null) list.Add((key, "skill", def.RuntimeId));
        }

        // 格挡 = 有 Guard(盾值)判定的技；退而求其次取 hold 姿态技。
        // 此前 U 绑的是 role=guard 且 skillUid=0，映射层又无 guard 分支 → 按下毫无反应，
        // 而 HUD 却写着「U 格挡」。
        // ⚠️ 现状: 全表只有 BLA 有盾值技（24/25 职业**无任何格挡数据**，而 GDD §6.2 视格挡为核心
        //    通用机制）——属内容缺口，已登记待裁定，此处按实际数据绑、不代填。
        var guard = catalog.Skills
            .Where(s => s.ClassId == classId && (s.Guard is not null || s.IsHold))
            .OrderByDescending(s => s.Guard is not null)
            .ThenBy(s => s.SkillId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (guard is not null) list.Add((InputMapper.KeyIds.U, "guard", guard.RuntimeId));

        return list;
    }

    /// HUD 提示：键位 + **真实技能名**（玩家才知道自己在放哪一招）
    public static List<(string Key, string Label)> Hints(RuntimeCatalog catalog, string classId)
    {
        var hints = new List<(string, string)>
        {
            ("WASD", "移动"), ("Space", "跳"), ("Shift", "翻滚"), ("J", "普攻"),
        };
        foreach (var (key, role, uid) in For(catalog, classId))
        {
            if (uid == 0) continue;
            string name = role == "guard"
                ? "格挡"
                : catalog.Skills.FirstOrDefault(s => s.RuntimeId == uid)?.Name ?? "";
            if (name.Length > 0) hints.Add((KeyLabel(key), name));
        }
        return hints;
    }

    public static string KeyLabel(int key) => key switch
    {
        InputMapper.KeyIds.K => "K", InputMapper.KeyIds.L => "L", InputMapper.KeyIds.I => "I",
        InputMapper.KeyIds.O => "O", InputMapper.KeyIds.P => "P", InputMapper.KeyIds.U => "U",
        _ => "?",
    };
}
