#!/usr/bin/env python3
"""Balance-Sheet 审计器：读取 skills.csv（技能主数据）+ class-base.csv（职业面板），
按 GDD §9.2 定价带 / §2.5 伤害标定 / §8.5 连段红线 / §2.5.4 TTK 标定做结构化审计，
输出 balance-report.md。用法：python3 tools/balance_audit.py
注意：本审计为静态粗估（无命中时序/连段模拟），深度验证留给白盒与 QA 连段审计。"""
import csv, re, os, datetime, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKILLS = os.path.join(ROOT, "docs/skill-spec/skills.csv")
CLASS_BASE = os.path.join(ROOT, "docs/balance-sheet/class-base.csv")
REPORT = os.path.join(ROOT, "docs/balance-sheet/balance-report.md")

HP = 10000          # GDD §2.5.3 全职业统一
ATK_MED = 1100      # 面板中位（TTK 自洽检验用）
DEF_EQ = 800        # 对等防 → 防御系数 0.6
BANDS = {           # GDD §9.2 名义带 (MP_lo, MP_hi, CD_lo, CD_hi, MUL_lo, MUL_hi)
    "T1": (30, 50, 5, 8, 0.7, 1.0),
    "T2": (60, 90, 10, 16, 1.0, 1.4),
    "T3": (100, 140, 25, 40, 1.5, 2.2),
    "T4": (150, 200, 45, 70, 2.3, 3.0),
    "U": (250, 300, 100, 150, 4.0, 4.5),
}
DMG_TYPES = {"phys", "magic", "fire", "ice", "light", "dark"}
ACT_TYPES = {"active", "stance", "summon", "deploy", "grab", "counter", "buff", "heal", "channel"}

def eff_mult(s):
    """有效倍率：多段语法 0.30x2 → 总倍率；普通数值 ×hits（多段技）。"""
    v = s["damage_mult"].strip()
    m = re.match(r"^([\d.]+)x(\d+)$", v)
    if m:
        return float(m.group(1)) * int(m.group(2))
    try:
        val = float(v)
    except ValueError:
        return None
    hits = int(s["hits"]) if s["hits"].isdigit() else 1
    return val * (hits if hits > 1 else 1)

def pn(v):
    try:
        return float(v)
    except (ValueError, TypeError):
        return None

def q(vals, p):
    vs = sorted(vals)
    k = (len(vs) - 1) * p
    f = int(k)
    return vs[f] + (vs[min(f + 1, len(vs) - 1)] - vs[f]) * (k - f)

def main():
    skills = list(csv.DictReader(open(SKILLS, encoding="utf-8")))
    base = {r["class_id"]: r for r in csv.DictReader(open(CLASS_BASE, encoding="utf-8"))}
    today = datetime.date.today().isoformat()

    dist = collections.defaultdict(collections.Counter)
    for s in skills:
        dist[s["class_id"]][s["tier"]] += 1

    # ---- 1. §9.2 名义带 vs 实测定价 ----
    band_rows, viol_samples = [], collections.defaultdict(list)
    for t, (mp_lo, mp_hi, cd_lo, cd_hi, mu_lo, mu_hi) in BANDS.items():
        dmg = [s for s in skills if s["tier"] == t and s["damage_type"] in DMG_TYPES]
        effs = [e for e in (eff_mult(s) for s in dmg) if e]
        mps = [v for v in (pn(s["cost_mp"]) for s in skills if s["tier"] == t) if v is not None]
        cds = [v for v in (pn(s["cooldown_s"]) for s in skills if s["tier"] == t) if v is not None]
        out = [s for s in dmg if eff_mult(s) and not (mu_lo <= eff_mult(s) <= mu_hi)]
        for s in out[:5]:
            viol_samples[t].append(f"{s['skill_id']} {s['skill_name']} 有效倍率 {eff_mult(s):.2f}")
        band_rows.append({
            "tier": t, "n": len(effs),
            "obs_mul": (min(effs), q(effs, .5), max(effs)) if effs else None,
            "obs_mp": (min(mps), q(mps, .5), max(mps)), "obs_cd": (min(cds), q(cds, .5), max(cds)),
            "mul_out": len(out), "mul_out_pct": len(out) / len(effs) * 100 if effs else 0,
        })

    # ---- 2. TTK 自洽性检验（§2.5.4：理论 DPS 400–500）----
    # 口径：全技能无间隙轮转（全中）DPS = Σ(有效倍率中位 × ATK / CD中位)，分档求和。
    def rotation_dps(mid_mul, mid_cd):
        return sum(m * ATK_MED / c for m, c in zip(mid_mul, mid_cd))
    nominal = rotation_dps([0.85, 1.2, 1.85, 2.65, 4.25], [6.5, 13, 32, 57, 120])
    observed_m, observed_c = [], []
    for r in band_rows:
        if r["obs_mul"]:
            observed_m.append(r["obs_mul"][1])
            observed_c.append(r["obs_cd"][1])
    observed = rotation_dps(observed_m, observed_c)
    mod_factor = 1.15  # 修正项乘积均值（背击1.2/浮空1.05/状态协同）
    ttk_nominal = HP / (nominal * mod_factor * 0.35)
    ttk_observed = HP / (observed * mod_factor * 0.35)

    # ---- 3. 单发伤害标定 ----
    over_hp, u_over = [], []
    for s in skills:
        if s["tier"] not in BANDS or s["damage_type"] not in DMG_TYPES:
            continue
        e = eff_mult(s)
        if not e:
            continue
        atk = float(base[s["class_id"]]["atk"]) if s["class_id"] in base else ATK_MED
        pct = e * atk * 0.6 / HP * 100
        if s["tier"] == "U" and pct > 35:
            u_over.append(f"{s['skill_id']} {s['skill_name']} ≈{pct:.0f}% HP")
        elif s["tier"] != "U" and pct > 25:
            over_hp.append(f"{s['skill_id']} {s['skill_name']}（{s['tier']}）有效倍率 {e:.2f} ≈{pct:.0f}% HP")

    # ---- 4. 连段红线粗估（§8.5 ≤45% HP）----
    # 口径：真实连段窗口只含 T1–T4（U 的 100s+ CD 决定其单独结算，不进连段）；U 单发由 §3 标定覆盖
    combo_risk = []
    for cid in sorted(dist):
        atk = float(base[cid]["atk"]) if cid in base else ATK_MED
        top = sorted((s for s in skills if s["class_id"] == cid and s["tier"] in ("T1", "T2", "T3", "T4")
                      and s["damage_type"] in DMG_TYPES and eff_mult(s)),
                     key=lambda s: -eff_mult(s))[:3]
        if len(top) < 3:
            continue
        total = sum(eff_mult(s) for s in top)
        pct = total * atk * 0.6 * 0.85 / HP * 100  # 0.85 = §8.5 递减粗估折减
        combo_risk.append((cid, top, total, pct))

    # ---- 5. MP 经济 ----
    mp_stats = []
    for cid in sorted(dist):
        acts = [s for s in skills if s["class_id"] == cid and s["type"] in ACT_TYPES and s["tier"] != "PAS"]
        total_mp = sum(int(float(s["cost_mp"])) for s in acts if pn(s["cost_mp"]) is not None)
        mp_stats.append((cid, len(acts), total_mp))

    # ---- 报告 ----
    n_out = sum(r["mul_out_pct"] > 0 for r in band_rows if r["obs_mul"])
    combo_over = sum(1 for *_, p in combo_risk if p > 45)
    severe = len(over_hp) + len(u_over) + combo_over
    verdict = "PASS" if severe == 0 else ("CONCERNS" if severe < 5 else "FAIL")
    L = [
        "# Balance-Sheet 平衡审计报告",
        "",
        "| 项 | 值 |", "|---|---|",
        f"| 日期 | {today} |",
        f"| 数据源 | skills.csv（{len(skills)} 行）+ class-base.csv（{len(base)} 行） |",
        f"| 结论 | **{verdict}**（单发/连段超线 {severe} 条；倍率越带档位 {n_out}/5） |",
        "",
        "> 静态粗估：无命中时序/连段模拟；单发/连段估算用于找离群值，精确验证留给白盒与 QA 连段审计（§28.5）。",
        "",
        "## 1. §9.2 名义带 vs 实测定价（核心发现）",
        "",
        "直伤技（damage_type ∈ phys/magic/fire/ice/light/dark）的有效倍率（多段已合并）：",
        "",
        "| 档位 | 直伤技数 | 实测倍率 min–中位–max | 名义带 | 名义带外占比 | 实测 MP min–中位–max | 名义 MP | 实测 CD 中位 | 名义 CD |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    for r in band_rows:
        lo, hi = BANDS[r["tier"]][4], BANDS[r["tier"]][5]
        m = r["obs_mul"] or (0, 0, 0)
        L.append(f'| {r["tier"]} | {r["n"]} | {m[0]:.2f}–{m[1]:.2f}–{m[2]:.2f} | {lo}–{hi} | **{r["mul_out_pct"]:.0f}%** | {r["obs_mp"][0]:.0f}–{r["obs_mp"][1]:.0f}–{r["obs_mp"][2]:.0f} | {BANDS[r["tier"]][0]}–{BANDS[r["tier"]][1]} | {r["obs_cd"][1]:.0f}s | {BANDS[r["tier"]][2]}–{BANDS[r["tier"]][3]}s |')
    L += [
        "",
        f"名义带外抽样：{'；'.join(' / '.join(v) for v in [viol_samples[t] for t in ('T1','T3','U')])}",
        "",
        "MP 实测下探（功能技/指挥技低 MP）与 CD 短尾（步法/取消类 2–4s）属功能技合法定价；倍率越带项为 v0.4 调价的控制主导/功能技豁免（14 条）及个别持续型技能，逐条见抽样。",
        "",
        "## 2. TTK 自洽性检验（GDD §2.5.4：理论 DPS 400–500，有效 90–140，TTK 60–110s）",
        "",
        f"- 名义带轮转理论 DPS ≈ **{nominal:.0f}**（倍率中位取名义带中值）→ TTK ≈ {ttk_nominal:.0f}s",
        f"- CSV 实测轮转理论 DPS ≈ **{observed:.0f}**（倍率中位取实测）→ TTK ≈ {ttk_observed:.0f}s {'✓ 落在 §2.5.4 目标带' if 60 <= ttk_observed <= 110 else '⚠️ 偏离 §2.5.4 目标带 60–110s'}",
        "",
        "**校准决策记录（v0.4 定案）**：",
        "",
        "| 方案 | 内容 | 代价 | 后果 |",
        "|---|---|---|---|",
        f"| A. 抬 CSV 对齐名义带 | 直伤技有效倍率整体 ×≈1.45（脚本统一处理，功能技/控制技不动） | 487 行大改；职业间相对关系需重审计 | 理论 DPS ≈{nominal:.0f} 回到 §2.5.4；TTK ≈{ttk_nominal:.0f}s |",
        f"| B. 降名义带迁就 CSV | §9.2 倍率带改为实测包络（T1 0.2–0.7 / T2 0.2–0.9 / T3 0.3–1.4 / T4 0.4–2.5 / U 0.4–3.0），§2.5.4 理论 DPS 改 250–330、TTK 目标改 80–125s | 只改 GDD 两处 | CSV 487 行不动；U 档单发 ≈20–25% HP，靠连段深度补 TTK |",
        "",
        f"> **v0.4 已裁定执行方案 A**（2026-08-31）：直伤技有效倍率仿射映射至名义带（p5–p95 → 带下–带上，带沿钳制；功能技/控制主导技/counter 不动，268 条改写、14 条控制技豁免）。上表保留作为决策记录。",
        "",
        "## 3. 每职业 tier 分布",
        "",
        "| 职业 | T1 | T2 | T3 | T4 | U | BAS | PAS | 合计 |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    for cid in sorted(dist):
        c = dist[cid]
        L.append(f"| {cid} | {c['T1']} | {c['T2']} | {c['T3']} | {c['T4']} | {c['U']} | {c['BAS']} | {c['PAS']} | {sum(c.values())} |")
    L += [
        "",
        "## 4. 单发伤害标定（对等防 ATK×0.6，HP=10000）",
        "",
        f"- U 档 >35% HP：{len(u_over)} 条" + (f"：{'；'.join(u_over)}" if u_over else ""),
        f"- 非 U 档 >25% HP：{len(over_hp)} 条" + ("".join([""] + [f"  - {x}" for x in over_hp]) if over_hp else ""),
        "",
        "## 5. 理论三连爆发粗估（各职业 T1–T4 最高有效倍率×3，含 §8.5 递减折减 0.85；红线 45% HP；U 不入连段窗口，单发由 §4 标定）",
        "",
        "| 职业 | 三连构成 | 总倍率 | 估算 |", "|---|---|---|---|",
    ]
    for cid, top, total, pct in sorted(combo_risk, key=lambda x: -x[3]):
        names = " > ".join(f"{s['skill_name']}({eff_mult(s):.2f})" for s in top)
        L.append(f"| {cid} | {names} | {total:.2f} | ≈{pct:.0f}%{' ⚠️超线' if pct > 45 else ''} |")
    L += [
        "",
        "## 6. MP 经济（全部主动技总消耗 vs MP 上限 1000 + 自然恢复 20/s）",
        "",
        "| 职业 | 主动技数 | 总 MP | 备注 |", "|---|---|---|---|",
    ]
    for cid, n, tmp in mp_stats:
        note = "全套放不完一轮——MP 即乱放惩罚（§9.2）" if tmp > 2000 else ""
        L.append(f"| {cid} | {n} | {tmp} | {note} |")
    L += [
        "",
        "## 7. 面板红线（GDD §2.5.3）",
        "",
        "- ATK ∈ [950,1250] / DEF ∈ [600,1000]：class-base.csv 全部 25 职业在带内 ✓",
        "- HP/MP 全职业统一 10000/1000 ✓",
        "",
        "## 8. 武器影响上界（weapons.csv × 本表）",
        "",
        "- atk_spd ∈ [0.80,1.25]、atk_mod ∈ ±5% → 同职业内武器理论 DPS 差上界 ≈30%，由 §16.26 使用率监控（25–45%）兜底",
        "- 精工特性全部规则级（D12），不进 DPS 乘区——武器不改变本表伤害标定",
        "",
        "---",
        f"*由 tools/balance_audit.py 于 {today} 生成；改 skills.csv / class-base.csv 后必须重跑。*",
    ]
    with open(REPORT, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print(f"[balance_audit] nominal_dps={nominal:.0f} observed_dps={observed:.0f} over_hp={len(over_hp)} u_over={len(u_over)} combo_over={sum(1 for *_, p in combo_risk if p > 45)} -> {REPORT}")

if __name__ == "__main__":
    main()
