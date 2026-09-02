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
    public required List<WeaponDef> Weapons { get; init; }
    public required Dictionary<string, List<WeaponDef>> WeaponsByClass { get; init; }
    public required Dictionary<string, ushort> WeaponIds { get; init; }   // weapon_id → 稳定 u16（1..N 列表序）

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
        var hbRawKind = d.HitboxRaw.Split(':')[0];
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

        var guard = ParseGuard(d);
        var charge = ParseCharge(d.Special);

        var def = new SkillRuntimeData
        {
            RuntimeId = runtimeId,
            SkillId = d.SkillId,
            ClassId = d.ClassId,
            Tier = tier,
            Type = d.Type,
            MpCost = d.CostMp,
            CooldownTicks = d.CooldownTicks,
            // 蓄力（GDD §4.1）: 蓄力时长追加至前摇（巴雷特 30+72=102 等）；v1 全额蓄力语义（登记）
            StartupTicks = d.StartupTicks + (charge?.Item1 ?? 0),
            ActiveTicks = d.ActiveTicks,
            RecoveryTicks = d.RecoveryTicks,
            HitSchedule = d.HitSchedule,
            Geo = geo.Geometry,
            DamageType = d.DamageType,
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
            Guard = guard,
            IsGrab = d.Type == "grab",
            IsCounter = d.Type == "counter",
            IsHold = d.ActiveRaw == "hold",
            SteerRateDegPerSec = d.ActiveRaw == "controlled" || d.Special.Contains("可转向") || d.Special.Contains("自由转向")
                ? RuntimeConstants.STEER_DEG_PER_SEC_DEFAULT : 0,
            ChargeTicks = charge?.Item1 ?? 0,
            ChargeBonusQ = charge?.Item2 ?? 0,
            IsSummon = d.Type == "summon" || d.Special.Contains("召唤位") || hbRawKind == "unit",
            SummonHp = ParseSummonHp(d.Special),
            SummonLifetimeTicks = ParseSummonLifetime(d.Special),
            SummonFlying = d.Special.Contains("飞行"),
            SummonTank = d.Special.Contains("坦克"),
            RequireBehindDeg = ParseRequireBehind(d.Special),
            DeployKind = ParseDeployKind(d, hbRawKind),
            DeployHp = ParseDeployHp(d.Special),
            TriggerRadius = hbRawKind == "deploy" && d.HitboxRaw.Contains("触发") ? ParseRadiusArg(d.HitboxRaw, 1) : 0,
            AuraRadius = hbRawKind == "zone" ? ParseRadiusArg(d.HitboxRaw, 1)
                       : hbRawKind == "deploy" ? ParseRadiusArg(d.HitboxRaw, 1) : 0,
            AuraPulseIntervalTicks = (int)RuntimeConstants.TICK_RATE,
            HealAmountQ = ParseHealAmount(d),
            HealIsMana = d.DamageMultRaw.Contains("蓝"),
            HealPulseIntervalTicks = ParseHealPulse(d.Special),
            HealPulseCount = ParseHealPulse(d.Special) > 0
                ? (int)((Dec2(d.AcRaw) * RuntimeConstants.TICK_RATE) / Math.Max(1, ParseHealPulse(d.Special))) : 0,
            IsStealth = d.Special.Contains("完全隐身"),
            StealthSpeedPct = ParseStealthSpeed(d.Special),
            IsReflect = d.Special.Contains("反射") || d.Special.Contains("反弹")
                || d.SkillName.Contains("反射") || d.SkillName.Contains("魔镜"),
            ReflectWindowTicks = ParseReflectWindow(d.Special),
            FollowHeading = (d.ActiveRaw == "controlled" || d.Special.Contains("可转向") || d.Special.Contains("自由转向"))
                && geo.IsProjectile,
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

    // ---- 格挡（GDD §6.2/§6.3；盾值/减伤率来自 special 数据化） ----

    private static GuardDef? ParseGuard(SkillDef d)
    {
        var sp = d.Special;
        var shieldIdx = sp.IndexOf("盾值");
        if (shieldIdx < 0) return null;
        var rest = sp[(shieldIdx + 2)..];
        int end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        if (end == 0 || !long.TryParse(rest[..end], out var shieldMax)) return null;
        // 化解物理 P%（缺省 GDD §6.2 基线 60%）
        long mitNum = 60, mitDen = 100;
        var mitIdx = sp.IndexOf("化解物理");
        if (mitIdx >= 0)
        {
            var mr = sp[(mitIdx + 4)..];
            int pe = 0;
            while (pe < mr.Length && (char.IsDigit(mr[pe]) || mr[pe] == '.')) pe++;
            if (pe > 0 && double.TryParse(mr[..pe], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                mitNum = (long)Math.Round(pct, MidpointRounding.ToEven);
                mitDen = 100;
            }
        }
        return new GuardDef(shieldMax, mitNum, mitDen, PhysicalOnly: true);
    }

    /// 蓄力:Ts[:+P%] → (追加前摇 Tick, 伤害加成 Q32.16)。蓄力突进:8m 类非时长蓄力不匹配。
    private static (int, long)? ParseCharge(string special)
    {
        var idx = special.IndexOf("蓄力:");
        if (idx < 0) return null;
        var rest = special[(idx + 3)..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        // 必须显式 s 后缀: "蓄力:13枚" 是箭矢数量（WRK_T1_001），非 13 秒——无 s 即非时长蓄力
        if (end == 0 || end >= rest.Length || rest[end] != 's'
            || !double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            return null;
        int chargeTicks = (int)Math.Round(sec * RuntimeConstants.TICK_RATE, MidpointRounding.ToEven);
        long bonus = 0;
        var plus = rest.IndexOf('+', end);
        if (plus >= 0)
        {
            var pr = rest[(plus + 1)..];
            int pe = 0;
            while (pe < pr.Length && (char.IsDigit(pr[pe]) || pr[pe] == '.')) pe++;
            if (pe > 0 && double.TryParse(pr[..pe], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                bonus = Quantify(1.0 + pct / 100.0);
        }
        return (chargeTicks, bonus);
    }

    private static decimal Dec2(string s) =>
        decimal.TryParse(s.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// deploy/zone 半径参数（hb 第 k 段 rN）
    private static long ParseRadiusArg(string hitbox, int seg)
    {
        var parts = hitbox.Split(':');
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].StartsWith("r") && parts[i].Length > 1 && char.IsDigit(parts[i][1]))
                return Quantify(decimal.Parse(parts[i][1..].TrimEnd('m'), NumberStyles.Float, CultureInfo.InvariantCulture));
        return 0;
    }

    /// 部署变体（hitbox/special 文本结构决定——无 skillId 分支）
    private static DeployKind ParseDeployKind(SkillDef d, string hbKind)
    {
        if (hbKind == "wall") return DeployKind.Wall;
        if (d.SkillName.Contains("魔镜") || d.Special.Contains("悬浮")) return DeployKind.Mirror;
        if (d.Special.Contains("侦察")) return DeployKind.Scout;
        if (d.Special.Contains("嘲讽")) return DeployKind.Taunt;
        if (hbKind == "deploy" && d.HitboxRaw.Contains("触发")) return DeployKind.Trap;
        if (hbKind == "deploy" || hbKind == "zone" && d.Type == "deploy") return DeployKind.Aura;
        if (hbKind == "zone") return DeployKind.Aura;
        return DeployKind.None;
    }

    /// 部署物 HP（数据: HP300/600HP/HP200/HP150；缺省 300）
    private static long ParseDeployHp(string special)
    {
        foreach (var tok in special.Split(':'))
        {
            var t = tok.Replace("HP", "").Trim();
            if (tok.Contains("HP") && int.TryParse(t, out var v)) return v;
        }
        return 300;
    }

    /// heal 数值（PRI 系: damage_mult 列 = 直接回复量；30%蓝 = 回蓝 30% maxMP）
    private static long ParseHealAmount(SkillDef d)
    {
        if (d.Type != "heal" && d.Type != "active" || !IsHealRow(d)) return 0;
        var raw = d.DamageMultRaw;
        if (raw.Contains("蓝"))
        {
            var pct = raw.Replace("%蓝", "").Replace("%", "");
            return double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                ? Quantify(p / 100.0) : 0;   // Q32.16 比例（×maxMP 由 Runtime 乘）
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : 0;
    }

    private static bool IsHealRow(SkillDef d) =>
        d.Type == "heal" || d.HitboxRaw.StartsWith("ally") || d.SkillName.Contains("回复") || d.SkillName.Contains("治愈");

    /// HoT 脉冲间隔（数据: 每3s回复一次）→ Tick；无 = 0
    private static long ParseHealPulse(string special)
    {
        var idx = special.IndexOf("每");
        if (idx < 0 || idx + 1 >= special.Length) return 0;
        var rest = special[(idx + 1)..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        if (end == 0 || end >= rest.Length || rest[end] != 's'
            || !double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            return 0;
        return (long)Math.Round(sec * RuntimeConstants.TICK_RATE, MidpointRounding.ToEven);
    }

    /// MF-1: 需背身 N°（数据: 需背身120°）
    private static int ParseRequireBehind(string special)
    {
        var idx = special.IndexOf("需背身");
        if (idx < 0) return 0;
        var rest = special[(idx + 3)..];
        int end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return end > 0 && int.TryParse(rest[..end], out var v) ? v : 0;
    }

    /// 召唤单位 HP（数据: HP900/HP1200；缺省 600 基线）
    private static long ParseSummonHp(string special)
    {
        var idx = special.IndexOf("HP");
        if (idx < 0) return 600;
        var rest = special[(idx + 2)..];
        int end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return end > 0 && long.TryParse(rest[..end], out var v) ? v : 600;
    }

    /// 召唤存在期（数据: 存在90s/60s）→ Tick；缺省 60s
    private static int ParseSummonLifetime(string special)
    {
        var idx = special.IndexOf("存在");
        if (idx < 0) return 60 * (int)RuntimeConstants.TICK_RATE;
        var rest = special[(idx + 2)..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        if (end == 0 || !double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            return 60 * (int)RuntimeConstants.TICK_RATE;
        return (int)Math.Round(sec * RuntimeConstants.TICK_RATE, MidpointRounding.ToEven);
    }

    /// 潜行移速（数据: 移速60%）→ 60（百分比整数）
    private static long ParseStealthSpeed(string special)
    {
        var idx = special.IndexOf("移速");
        if (idx < 0) return 60;
        var rest = special[(idx + 2)..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        if (end == 0 || !double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return 60;
        return (long)Math.Round(pct, MidpointRounding.ToEven);
    }

    /// 反射窗（数据: 2s窗口）→ Tick；缺省 2s
    private static int ParseReflectWindow(string special)
    {
        if (!special.Contains("反射")) return 0;
        var idx = special.IndexOf("s窗");
        if (idx < 0) return 2 * (int)RuntimeConstants.TICK_RATE;
        int start = idx;
        while (start > 0 && (char.IsDigit(special[start - 1]) || special[start - 1] == '.')) start--;
        if (!double.TryParse(special[start..idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            return 2 * (int)RuntimeConstants.TICK_RATE;
        return (int)Math.Round(sec * RuntimeConstants.TICK_RATE, MidpointRounding.ToEven);
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
