using System;
using System.Collections.Generic;
using System.Linq;
// PRODUCTION - Arena.Infra.Data
// ADR-0002 §3: Compiler 九段管线——Parse → Canonical → Schema → Semantic → Quantize → Sort → RuntimeDef → Hash
using System.Globalization;

namespace Arena.Infra.Data;

public static class SkillParser
{
    public static readonly string[] ExpectedColumns =
    {
        "skill_id","skill_name","class_id","tier","type","cost_mp","cost_hp","cooldown_s",
        "startup_f","active_f","recovery_f","hit_interval_f","hitbox","range_m","angle_deg",
        "damage_mult","damage_type","hits","hitstun_f","knockback_m","launch_v","status",
        "armor","invincible_f","sweep","intercept","channel","cancel_min_tier","jump_cancel",
        "special","notes","learn_level","acq_type","animation","vfx","sound"
    };

    private static readonly HashSet<string> ValidHitboxKinds = new(StringComparer.Ordinal)
    {
        "fan","box","circle","cyl","line","cone","lob","proj","zone","aura",
        "self","unit","deploy","ally","wall","portal","none"
    };

    private static readonly HashSet<string> ValidTiers = new(StringComparer.Ordinal)
    { "BAS", "T1", "T2", "T3", "T4", "U", "PAS" };

    private static readonly HashSet<string> ValidTypes = new(StringComparer.Ordinal)
    { "basic","active","stance","summon","deploy","grab","counter","buff","passive","heal","channel" };

    private static readonly HashSet<string> ActiveCanonical = new(StringComparer.Ordinal)
    { "hold", "controlled", "projectilePhase" };

    public static (SkillDef? def, List<ValidationIssue> issues) ParseRow(string[] cells, string[] header)
    {
        var issues = new List<ValidationIssue>();
        var pending = new List<string>();
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < header.Length && i < cells.Length; i++) d[header[i]] = cells[i].Trim();

        var id = d.GetValueOrDefault("skill_id", "");
        if (!RegexVal(id, @"^[A-Z]{3}_(BAS|T[1-4]|U|PAS)_\d{3}$"))
            issues.Add(new("L1", "ID_FORMAT", $"{id}: skill_id 格式非法"));

        var tier = d.GetValueOrDefault("tier", "");
        if (!ValidTiers.Contains(tier))
            issues.Add(new("L1", "TIER_ENUM", $"{id}: tier={tier} 非法"));

        var type = d.GetValueOrDefault("type", "");
        if (!ValidTypes.Contains(type))
            issues.Add(new("L1", "TYPE_ENUM", $"{id}: type={type} 非法"));

        // active_f: int / Ns / hold / controlled / projectilePhase / -
        var activeRaw = d.GetValueOrDefault("active_f", "-");
        int activeTicks = 0;
        if (activeRaw == "-")
            activeTicks = 2;  // 判定即收：名义 2T
        else if (int.TryParse(activeRaw, out var af))
            activeTicks = af;
        else if (activeRaw.EndsWith("s") && double.TryParse(activeRaw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var asf))
            activeTicks = CheckedRhe(asf * 60);
        else if (activeRaw == "hold" || activeRaw == "controlled" || activeRaw == "projectilePhase")
            activeTicks = 2;  // 简化：非数值形态在 Runtime 用固定 2T nominal + 模式标志
        else
            issues.Add(new("L1", "ACTIVE_FORMAT", $"{id}: active_f={activeRaw} 不在白名单"));

        // cooldown: 整数秒 → Tick (×60 RHE)；公共CD前缀暂按裸秒解析
        var cdRaw = d.GetValueOrDefault("cooldown_s", "-");
        long cdTicks = 0;
        if (cdRaw != "-" && cdRaw != "")
        {
            var cdNum = cdRaw.StartsWith("公共") ? cdRaw[2..] : cdRaw;
            if (double.TryParse(cdNum, NumberStyles.Float, CultureInfo.InvariantCulture, out var cds))
                cdTicks = CheckedRhe(cds * 60);
            else
                issues.Add(new("L1", "CD_FORMAT", $"{id}: cooldown_s={cdRaw} 非法"));
        }

        // startup/recovery/hitstun: 整数帧 → Tick 恒等
        int su = ParseIntOrZero(d.GetValueOrDefault("startup_f", "-"));
        int rc = ParseIntOrZero(d.GetValueOrDefault("recovery_f", "-"));
        int hs = ParseIntOrZero(d.GetValueOrDefault("hitstun_f", "0"));

        // armor: 单位歧义 → fail-fast
        var armor = d.GetValueOrDefault("armor", "-");
        if (armor != "-" && armor != "none" && armor != "")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(armor, @"^(SA|SSA):\d+-\d+f$") &&
                !armor.StartsWith("SA:buff:") && !armor.StartsWith("SSA:buff:"))
            {
                if (armor.Contains('s') && !armor.Contains('f'))
                    issues.Add(new("L1", "ARMOR_UNIT_AMBIGUOUS", $"{id}: armor={armor} 单位歧义(s vs f) → fail-fast (OQ-2)"));
                else
                    pending.Add($"armor:{armor}");
            }
        }

        // invincible: 剥前缀后须为 N-Nf 或 Nf；none = 显式无（119 行）
        var inv = d.GetValueOrDefault("invincible_f", "-");
        if (inv != "-" && inv != "" && inv != "none")
        {
            var inner = inv.StartsWith("invincible:") ? inv["invincible:".Length..] : inv;
            if (inner == "0f")
                pending.Add("invincible:0f_degenerate");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(inner, @"^\d+(-\d+)?f$"))
                issues.Add(new("L1", "INVINCIBLE_FORMAT", $"{id}: invincible_f={inv} 格式非法"));
        }

        // hitbox: kind 白名单
        var hb = d.GetValueOrDefault("hitbox", "none");
        var hbKind = hb.Split(':')[0];
        if (!ValidHitboxKinds.Contains(hbKind))
            issues.Add(new("L1", "HITBOX_KIND", $"{id}: hitbox={hb} 首词 {hbKind} 不在白名单 → fail-fast (OQ-13)"));

        // P-3: 秒→Tick 对齐检查（active_f / cooldown 秒值）
        if (activeRaw.EndsWith("s") && double.TryParse(activeRaw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var aSec))
        {
            if (aSec * 60 != Math.Floor(aSec * 60))
                issues.Add(new("L3", "P3_TICK_ALIGN", $"{id}: active_f={activeRaw} → {aSec * 60} 非整 Tick"));
        }

        // P-2: 多段 hitSchedule
        int hits = ParseIntOrZero(d.GetValueOrDefault("hits", "1"));
        if (hits <= 0) hits = 1;
        int iv = ParseIntOrZero(d.GetValueOrDefault("hit_interval_f", "0"));
        var schedule = new int[hits];
        if (hits > 1 && iv > 0)
        {
            for (int k = 0; k < hits; k++) schedule[k] = Math.Min(k * iv, Math.Max(activeTicks - 1, 0));
            for (int k = 1; k < hits; k++)
            {
                if (schedule[k] - schedule[k - 1] < 3)
                    issues.Add(new("L2", "P2_INTERVAL", $"{id}: 段{k}/{k + 1} 间隔 {schedule[k] - schedule[k - 1]}T < 3T"));
            }
        }
        else if (hits > 1)
        {
            for (int k = 0; k < hits; k++) schedule[k] = hits > 1 ? (int)((long)k * (Math.Max(activeTicks - 1, 0)) / (hits - 1)) : 0;
        }

        double dmgMult = 0;
        double.TryParse(d.GetValueOrDefault("damage_mult", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out dmgMult);
        double.TryParse(d.GetValueOrDefault("knockback_m", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var kb);
        double.TryParse(d.GetValueOrDefault("launch_v", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lv);

        var def = new SkillDef
        {
            SkillId = id,
            SkillName = d.GetValueOrDefault("skill_name", ""),
            ClassId = d.GetValueOrDefault("class_id", ""),
            Tier = tier,
            Type = type,
            CostMp = ParseIntOrZero(d.GetValueOrDefault("cost_mp", "0")),
            CooldownTicks = cdTicks,
            StartupTicks = su,
            ActiveTicks = activeTicks,
            RecoveryTicks = rc,
            HitIntervalTicks = iv,
            HitboxRaw = hb,
            DamageMult = dmgMult,
            DamageType = d.GetValueOrDefault("damage_type", "none"),
            Hits = hits,
            HitstunTicks = hs,
            KnockbackM = kb,
            LaunchV = lv,
            StatusRaw = d.GetValueOrDefault("status", "none"),
            ArmorRaw = armor,
            InvincibleRaw = inv,
            Sweep = ParseIntOrZero(d.GetValueOrDefault("sweep", "0")),
            Intercept = ParseIntOrZero(d.GetValueOrDefault("intercept", "0")),
            Channel = ParseIntOrZero(d.GetValueOrDefault("channel", "0")),
            CancelMinTier = d.GetValueOrDefault("cancel_min_tier", "none"),
            JumpCancel = ParseIntOrZero(d.GetValueOrDefault("jump_cancel", "0")),
            Special = d.GetValueOrDefault("special", "-"),
            HitSchedule = schedule,
            PendingReviewFlags = pending,
        };

        return (def, issues);
    }

    private static int ParseIntOrZero(string s) =>
        int.TryParse(s.Trim(), out var v) ? v : 0;

    private static int CheckedRhe(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    private static bool RegexVal(string s, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, pattern);
}
