using System;
using System.Collections.Generic;
using System.Globalization;
using Arena.Core.Sim;
// PRODUCTION - Arena.Infra.Data
// ADR-0002 §3 段6→7: RuntimeDef 量化——SkillDef（解析域）→ SkillRuntimeData（Core 消费域）。
// 全部实数量化为 Q32.16（RHE）；时间统一 Tick；几何语法 → HitboxGeometry。
// 本层是 CSV 原始语法在工程中的唯一解析点——Core 禁止再见 CSV 字符串语义。
namespace Arena.Infra.Data;

public sealed class RuntimeCatalog
{
    public required List<SkillRuntimeData> Skills { get; init; }       // RuntimeId 升序
    public required Dictionary<string, ushort> IdMap { get; init; }    // skill_id → RuntimeId
    public required List<ValidationIssue> Blockers { get; init; }      // L1 拒产行（OQ 登记）
    public required List<ValidationIssue> Warnings { get; init; }      // L2/L3 标记
    public required List<string> UnroutedStatuses { get; init; }       // 状态语法未路由清单（报告用）
    public required List<string> UnroutedHitboxes { get; init; }       // hitbox 语法未路由清单（报告用）
    public required string DataVersionHash { get; init; }

    public SkillRuntimeData? Get(string skillId) =>
        IdMap.TryGetValue(skillId, out var id) ? Skills[id - 1] : null;

    public int Count => Skills.Count;
}

public static class RuntimeSkillFactory
{
    // Tier 映射（GDD §8.2 档位递进）
    private static readonly Dictionary<string, byte> TierMap = new(StringComparer.Ordinal)
    {
        ["BAS"] = 0, ["T1"] = 1, ["T2"] = 2, ["T3"] = 3, ["T4"] = 4, ["U"] = 5, ["PAS"] = 0,
    };

    public static (SkillRuntimeData? def, List<string> unroutedStatus, List<string> unroutedHitbox) Build(
        SkillDef d, ushort runtimeId)
    {
        var unroutedStatus = new List<string>();
        var unroutedHitbox = new List<string>();

        if (!TierMap.TryGetValue(d.Tier, out var tier)) tier = 0;

        var geo = ParseHitbox(d, unroutedHitbox);
        var statuses = ParseStatuses(d, unroutedStatus);
        var armor = ParseArmor(d);
        var invuln = ParseInvuln(d);
        byte cancelTier = ParseCancelTier(d.CancelMinTier);
        bool forcedDown = d.Special.Contains("受身无效");
        bool armorBreak = d.Special.Contains("破霸体");
        bool isProj = geo.IsProjectile;
        long headMultQ = ParseHeadMult(d.Special);
        long aimHeightQ = (isProj && d.Special.Contains("头部"))
            ? RuntimeConstants.PROJ_AIM_HEIGHT_HEAD : RuntimeConstants.PROJ_AIM_HEIGHT_DEFAULT;

        // 普攻链: skill_id 尾号 = 段号（BMG_BAS_001 → ChainN=1）；ChainNext 由 Catalog 装配阶段链接
        byte chainN = 0;
        var idx = d.SkillId.LastIndexOf('_');
        if (d.Type == "basic" && idx >= 0 && int.TryParse(d.SkillId[(idx + 1)..], out var seg)) chainN = (byte)seg;

        var def = new SkillRuntimeData
        {
            RuntimeId = runtimeId,
            SkillId = d.SkillId,
            ClassId = d.ClassId,
            Tier = tier,
            Type = d.Type,
            MpCost = d.CostMp,
            CooldownTicks = d.CooldownTicks,
            StartupTicks = d.StartupTicks,
            ActiveTicks = d.ActiveTicks,
            RecoveryTicks = d.RecoveryTicks,
            HitSchedule = d.HitSchedule,
            Geo = geo.Geometry,
            DamageMultQ = Quantify(d.DamageMult),
            HeadMultQ = headMultQ,
            HitstunTicks = d.HitstunTicks,
            KnockbackVelQ = Quantify(d.KnockbackM * 9),     // 击退初速 = 位移 ×9 m/s（摩擦 0.85/tick 收敛）
            LaunchVelQ = Quantify(d.LaunchV),
            Statuses = statuses,
            Armor = armor,
            Invuln = invuln,
            Sweep = d.Sweep != 0,
            ArmorBreak = armorBreak,
            IsProjectile = isProj,
            IsLob = geo.IsLob,
            ProjSpeedQ = Quantify(geo.ProjSpeed),
            ProjRadius = Quantify(0.3m),                    // GDD §4.5 体积 0.2–0.5m——逐技半径数据缺失（Schema-15 邻接 GAP，登记）
            ProjRangeTicks = geo.ProjRangeTicks,
            AimHeightQ = aimHeightQ,
            CancelMinTier = cancelTier,
            JumpCancel = d.JumpCancel != 0,
            ChainN = chainN,
            ForcedDown = forcedDown,
            Special = d.Special,
        };
        return (def, unroutedStatus, unroutedHitbox);
    }

    private static long Quantify(double v) => (long)Math.Round(v * 65536.0, MidpointRounding.ToEven);
    private static long Quantify(decimal v) => (long)Math.Round(v * 65536m, MidpointRounding.ToEven);

    // ---- hitbox 语法解析（SPEC-0005 §4 映射表） ----

    private readonly record struct GeoResult(HitboxGeometry Geometry, bool IsProjectile, bool IsLob,
        double ProjSpeed, int ProjRangeTicks);

    private static GeoResult ParseHitbox(SkillDef d, List<string> unrouted)
    {
        var raw = d.HitboxRaw;
        if (raw is "-" or "" or "none" or "self" or "unit" or "ally" or "portal")
        {
            if (raw is "unit" or "ally" or "portal") unrouted.Add($"{d.SkillId}:hitbox:{raw}");
            return new GeoResult(HitboxGeometry.None, false, false, 0, 0);
        }

        var parts = raw.Split(':');
        var kind = parts[0];
        long bandLow = RuntimeConstants.MELEE_BAND_LOW, bandHigh = RuntimeConstants.MELEE_BAND_HIGH;

        switch (kind)
        {
            case "fan":
            case "cone":
            {
                var r = ParseM(GetArg(parts, "r"));
                var a = ParseInt(GetArg(parts, "a"));
                if (a > 180)
                {
                    // SPEC-0005 §4: 扇角 >180° = Schema Failure（凸性前提）
                    throw new FormatException($"{d.SkillId}: hitbox {raw} 扇角 {a}° > 180°（SPEC-0005 §4 凸性 Schema Failure）");
                }
                return new GeoResult(new HitboxGeometry(GeoKind.Sector, r, a, 0, 0, bandLow, bandHigh), false, false, 0, 0);
            }
            case "circle":
            case "aura":
                return new GeoResult(new HitboxGeometry(GeoKind.Circle, ParseM(GetArg(parts, "r")), 0, 0, 0, bandLow, bandHigh), false, false, 0, 0);
            case "cyl":
            {
                var r = ParseM(GetArg(parts, "r"));
                var h = ParseM(GetArg(parts, "h"));
                return new GeoResult(new HitboxGeometry(GeoKind.Cylinder, r, 0, 0, 0, 0, h), false, false, 0, 0);
            }
            case "box":
            {
                // box:WxDxL——W 横向 × D 纵向厚 × L 前向长
                var dims = parts[1].Split('x');
                var w = ParseDec(dims[0]);
                var l = dims.Length > 2 ? ParseDec(dims[2].Split('-')[0]) : 1m;
                return new GeoResult(new HitboxGeometry(GeoKind.Obb, 0, 0, Quantify(l / 2), Quantify(w / 2), bandLow, bandHigh), false, false, 0, 0);
            }
            case "line":
            {
                var len = ParseDec(parts[1].TrimEnd('m'));
                var w = parts.Length > 2 && parts[2].StartsWith("宽") ? ParseDec(parts[2][1..].TrimEnd('m')) : 1.0m;
                return new GeoResult(new HitboxGeometry(GeoKind.Obb, 0, 0, Quantify(len / 2), Quantify(w / 2), bandLow, bandHigh), false, false, 0, 0);
            }
            case "zone":
                return new GeoResult(new HitboxGeometry(GeoKind.Circle, ParseM(GetArg(parts, "r")), 0, 0, 0, bandLow, bandHigh), false, false, 0, 0);
            case "proj":
                return ParseProj(d, parts, unrouted);
            case "lob":
                return ParseLob(d, parts, unrouted);
            case "deploy":
            case "wall":
                unrouted.Add($"{d.SkillId}:hitbox:{kind}");
                return new GeoResult(HitboxGeometry.None, false, false, 0, 0);
            default:
                unrouted.Add($"{d.SkillId}:hitbox:{raw}");
                return new GeoResult(HitboxGeometry.None, false, false, 0, 0);
        }
    }

    private static GeoResult ParseProj(SkillDef d, string[] parts, List<string> unrouted)
    {
        // proj:VS:NM[:flags]——标准形态；特殊形态（吸附拖拽/水牢/锁链…）→ 未路由（签名阶段）
        double speed = 0; int rangeTicks = 0;
        var p1 = parts.Length > 1 ? parts[1] : "";
        var p2 = parts.Length > 2 ? parts[2] : "";
        if (p1.EndsWith("m/s") && double.TryParse(p1[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            speed = s;
            var rangeM = p2.TrimEnd('m');
            if (double.TryParse(rangeM, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                rangeTicks = (int)Math.Round(r / s * RuntimeConstants.TICK_RATE, MidpointRounding.ToEven) + 1;
            else
            {
                unrouted.Add($"{d.SkillId}:proj:range:{p2}");   // proj:20m/s:追踪 等
                rangeTicks = (int)(3 * RuntimeConstants.TICK_RATE);
            }
            return new GeoResult(HitboxGeometry.None, true, false, speed, rangeTicks);
        }
        unrouted.Add($"{d.SkillId}:hitbox:proj:{raw(parts)}");
        return new GeoResult(HitboxGeometry.None, false, false, 0, 0);
    }

    private static GeoResult ParseLob(SkillDef d, string[] parts, List<string> unrouted)
    {
        // lob:rR[:距离]——落点区域半径 R；水平距离取 range_m 列；飞行时间固定 0.8s（实现约定 OQ 登记）
        var r = ParseM(GetArg(parts, "r"));
        double rangeM = (double)ParseDec(d.RangeM);
        const double flightSec = 0.8;
        var speed = rangeM / flightSec;
        var vy = 22.0 * flightSec / 2;   // 抛物线: v_y0 = g·T/2（落地 y=0）
        _ = vy;
        // lob 弹体: ProjSpeed = 水平速度；LaunchVelQ 复用为垂直初速（ProjectileSystem.DispY）
        var geo = new HitboxGeometry(GeoKind.Circle, r, 0, 0, 0, 0, RuntimeConstants.MELEE_BAND_HIGH);
        return new GeoResult(geo, true, true, speed, (int)(flightSec * RuntimeConstants.TICK_RATE) + 1);
    }

    private static string raw(string[] parts) => string.Join(":", parts);

    private static string GetArg(string[] parts, string prefix)
    {
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].StartsWith(prefix) && parts[i].Length > prefix.Length &&
                (char.IsDigit(parts[i][prefix.Length])))
                return parts[i][prefix.Length..];
        return "0";
    }

    private static long ParseM(string s) => Quantify(ParseDec(s));
    private static decimal ParseDec(string s) =>
        decimal.TryParse(s.TrimEnd('m'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    private static int ParseInt(string s) =>
        int.TryParse(s.TrimEnd('°', 'm'), out var v) ? v : 0;

    // ---- status 语法解析（GDD §7.3 路由集；未路由语法显式登记） ----

    private static StatusEffectDef[] ParseStatuses(SkillDef d, List<string> unrouted)
    {
        var raw = d.StatusRaw;
        if (raw is "none" or "-" or "") return Array.Empty<StatusEffectDef>();
        var tokens = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<StatusEffectDef>();
        foreach (var tok in tokens)
        {
            var seg = tok.Split(':');
            var kindStr = seg[0];
            var chance = ParseChance(tok);
            switch (kindStr)
            {
                case "slow":
                {
                    var potency = ParsePercent(seg.Length > 1 ? seg[1] : "30%");
                    var dur = ParseDuration(seg, 2, defaultSec: 3);
                    list.Add(new StatusEffectDef(StatusKind.Slow, potency, dur, chance));
                    break;
                }
                case "root":
                    list.Add(new StatusEffectDef(StatusKind.Root, 0, ParseDuration(seg, 1, defaultSec: 2), chance));
                    break;
                case "stun":
                    list.Add(new StatusEffectDef(StatusKind.Stun, 0, ParseDuration(seg, 1, defaultSec: 1), chance));
                    break;
                case "paralysis":
                {
                    var dur = ParseDuration(seg, 1, defaultSec: 1);
                    list.Add(new StatusEffectDef(StatusKind.Paralysis, 0, dur, chance));
                    break;
                }
                case "freeze":
                    list.Add(new StatusEffectDef(StatusKind.Freeze, 0, ParseDuration(seg, 1, defaultSec: 1), chance));
                    break;
                case "silence":
                    list.Add(new StatusEffectDef(StatusKind.Silence, 0, ParseDuration(seg, 1, defaultSec: 2), chance));
                    break;
                case "blind":
                    list.Add(new StatusEffectDef(StatusKind.Blind, 0, ParseDuration(seg, 1, defaultSec: 2), chance));
                    break;
                case "burn":
                case "bleed":
                case "poison":
                {
                    var kind = kindStr == "burn" ? StatusKind.Burn : kindStr == "bleed" ? StatusKind.Bleed : StatusKind.Poison;
                    var dps = seg.Length > 1 ? ParseDec(seg[1]) : 0;
                    var dur = ParseDuration(seg, 2, defaultSec: 4);
                    list.Add(new StatusEffectDef(kind, Quantify(dps), dur, chance));
                    break;
                }
                case "虚弱":
                    list.Add(new StatusEffectDef(StatusKind.Weakness, 0, ParseDuration(seg, 1, defaultSec: 6), chance));
                    break;
                case "破防":
                    list.Add(new StatusEffectDef(StatusKind.GuardBreak, 0, 5 * (int)RuntimeConstants.TICK_RATE, chance));
                    break;
                case "taunt":
                    list.Add(new StatusEffectDef(StatusKind.Taunt, 0, ParseDuration(seg, 1, defaultSec: 3), chance));
                    break;
                case "confuse":
                    list.Add(new StatusEffectDef(StatusKind.Confuse, 0, ParseDuration(seg, 1, defaultSec: 3), chance));
                    break;
                case "curse":
                    list.Add(new StatusEffectDef(StatusKind.Curse, 0, ParseDuration(seg, 1, defaultSec: 4), chance));
                    break;
                case "shock":
                    list.Add(new StatusEffectDef(StatusKind.Shock, 0, ParseDuration(seg, 1, defaultSec: 2), chance));
                    break;
                default:
                    unrouted.Add($"{d.SkillId}:status:{tok}");
                    break;
            }
        }
        return list.ToArray();
    }

    private static int ParseChance(string tok)
    {
        var at = tok.IndexOf('@');
        if (at < 0) return 0;
        var pct = tok[(at + 1)..].TrimEnd('%');
        return int.TryParse(pct, out var v) ? v : 0;
    }

    private static long ParsePercent(string s)
    {
        s = s.TrimEnd('%');
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Quantify(v / 100m) : 0;
    }

    /// 时长解析: "4s"/"0.5s" → Tick；"持续" → 600T（10s 上限，逐条 OQ）；缺省用 defaultSec
    private static int ParseDuration(string[] seg, int idx, int defaultSec)
    {
        if (seg.Length <= idx) return defaultSec * (int)RuntimeConstants.TICK_RATE;
        var s = seg[idx];
        if (s.Contains("持续")) return 10 * (int)RuntimeConstants.TICK_RATE;
        if (s.EndsWith("s") && decimal.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (int)Math.Round(v * RuntimeConstants.TICK_RATE, MidpointRounding.ToEven);
        return defaultSec * (int)RuntimeConstants.TICK_RATE;
    }

    // ---- armor / invincible / cancel ----

    private static ArmorWindowDef? ParseArmor(SkillDef d)
    {
        var raw = d.ArmorRaw;
        if (raw is "-" or "none" or "") return null;
        if (raw.StartsWith("SA:") && raw.EndsWith("f") && !raw.Contains('s'))
        {
            var inner = raw[3..][..^1];
            var dash = inner.IndexOf('-');
            if (dash > 0 && int.TryParse(inner[..dash], out var a) && int.TryParse(inner[(dash + 1)..], out var b))
                return new ArmorWindowDef(false, a, b);
        }
        if (raw.StartsWith("SSA:") && raw.EndsWith("f") && !raw.Contains('s'))
        {
            var inner = raw[4..][..^1];
            var dash = inner.IndexOf('-');
            if (dash > 0 && int.TryParse(inner[..dash], out var a) && int.TryParse(inner[(dash + 1)..], out var b))
                return new ArmorWindowDef(true, a, b);
        }
        return null;   // SA:buff:… / SA:12-26s(OQ-2)——parser 层已标记
    }

    private static InvulnWindowDef? ParseInvuln(SkillDef d)
    {
        var raw = d.InvincibleRaw;
        if (raw is "-" or "" or "none") return null;
        var inner = raw.StartsWith("invincible:") ? raw["invincible:".Length..] : raw;
        if (!inner.EndsWith("f")) return null;
        var core = inner[..^1];
        var dash = core.IndexOf('-');
        if (dash > 0 && int.TryParse(core[..dash], out var a) && int.TryParse(core[(dash + 1)..], out var b))
            return new InvulnWindowDef(a, b);
        if (int.TryParse(core, out var single))
            return new InvulnWindowDef(0, single);
        return null;
    }

    private static byte ParseCancelTier(string raw) => raw switch
    {
        "any" => 0,
        "BAS" => 0,
        "T1+" => 1,
        "T2+" => 2,
        "T3+" => 3,
        "T4+" => 4,
        "U+" => 5,
        _ => 255,   // none / - / counter(登记) = 无数据取消
    };

    /// 部位结算倍率（special 含「头部×N」）：豪龙破军 ×1.5 / 巴雷特 ×2（SPEC-0006 §1.4）
    private static long ParseHeadMult(string special)
    {
        var idx = special.IndexOf("头部×");
        if (idx < 0) return 0;
        var rest = special[(idx + 3)..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        if (end == 0) return 0;
        if (double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return Quantify(v);
        return 0;
    }
}
