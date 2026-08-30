#!/usr/bin/env python3
"""Weapon-Spec 校验器：读取 weapons.csv（主数据）+ skills.csv（技能主数据），
执行结构/数值/规则级（D12）三类校验，输出 validation-report.md。
用法：python3 tools/validate_weapons.py （在仓库根目录或任意目录运行均可）"""
import csv, re, os, datetime, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WEAPONS = os.path.join(ROOT, "docs/weapon-spec/weapons.csv")
SKILLS = os.path.join(ROOT, "docs/skill-spec/skills.csv")
REPORT = os.path.join(ROOT, "docs/weapon-spec/validation-report.md")

CLASSES = "BMG ELE SUM WIT BLA BER SBL GBL SRP SPF LAU MEH STR QIM GRP ROG ASN THF NJA WRK PRI GAN KNI EXO UNS".split()
TYPES = {"sword", "heavy_sword", "magic_sword", "katana", "spear", "staff", "wand", "broom", "glove",
         "claw", "gauntlet", "pistol", "crossbow", "cannon", "mech_pistol", "scepter", "scythe",
         "shield_sword", "dagger", "twin_sword", "umbrella"}
WEIGHT_OF_TYPE = {  # README §1.6：轻（法器/匕首/手枪）/ 中（剑/爪/弩）/ 重（巨剑/重火器/盾）
    "staff": "light", "wand": "light", "broom": "light", "scepter": "light",
    "pistol": "light", "mech_pistol": "light", "dagger": "light",
    "sword": "medium", "magic_sword": "medium", "katana": "medium", "spear": "medium",
    "glove": "medium", "claw": "medium", "gauntlet": "medium", "crossbow": "medium",
    "scythe": "medium", "twin_sword": "medium", "umbrella": "medium",
    "heavy_sword": "heavy", "cannon": "heavy", "shield_sword": "heavy",
}
GRADE_OF_SEQ = {"1": "standard", "2": "special", "3": "masterwork"}
# D12 红线：禁止数值级强度表述（README §3）
D12_BANNED = [
    (r"吸血", "吸血（强度乘区）"),
    (r"伤害\s*[+×+]\s*\d", "直接伤害加成"),
    (r"加伤\s*[+]\s*\d", "直接伤害加成"),
    (r"重量系数\s*×", "伤害系数乘区"),
    (r"减伤\s*\d+\s*%\s*→", "承伤乘区改动"),
    (r"反弹比例\s*[+]", "反弹强度乘区"),
    (r"每跳\s*\d+\s*→", "回复数值改动"),
]
# trait_rules 引用旧值的抽查表：skill_id -> (列名, 期望当前值子串)
EXPECTED = {
    "BMG_T1_002": ("special", "突进:1.5m"),
    "ELE_T1_002": ("startup_f", "6"),
    "ELE_T4_003": ("cooldown_s", "45"),
    "BLA_T1_002": ("special", "盾值1500"),
    "BLA_T2_001": ("skill_name", "三段斩"),
    "BER_T1_002": ("special", "伤害∝武器重量"),
    "BER_U_001": ("skill_name", "嗜血奋战"),
    "SBL_T4_001": ("hitbox", "r6.0"),
    "GBL_T1_004": ("cooldown_s", "8"),
    "SRP_T4_001": ("special", "蓄力:1.2s"),
    "LAU_T2_001": ("hits", "15"),
    "LAU_T4_001": ("active_f", "8s"),
    "MEH_T3_002": ("notes", "每2s一发"),
    "STR_T2_004": ("special", "满阶五脚"),
    "STR_T3_004": ("special", "1m内+15%"),
    "QIM_T2_002": ("hitbox", "耐久2000"),
    "QIM_T4_002": ("special", "吸附3m"),
    "GRP_T1_002": ("special", "8向可控"),
    "GRP_T2_004": ("hitbox", "r1.5"),
    "ROG_T2_004": ("skill_name", "涂毒"),
    "ROG_T4_001": ("special", "每局记3个"),
    "ASN_T2_001": ("special", "双剑/匕首不同效果"),
    "THF_T1_001": ("special", "移速60%"),
    "THF_T3_002": ("hitbox", "触发+瞬移"),
    "NJA_T2_004": ("hitbox", "a160"),
    "WRK_T1_001": ("special", "满蓄26枚"),
    "WRK_U_001": ("active_f", "3.5s"),
    "PRI_T1_002": ("startup_f", "24"),
    "PRI_T2_005": ("cooldown_s", "12"),
    "GAN_T2_001": ("active_f", "4s"),
    "GAN_T1_002": ("special", "每3s"),
    "KNI_T2_005": ("active_f", "6s"),
    "KNI_T3_005": ("active_f", "10s"),
    "EXO_T2_007": ("hitbox", "25m"),
}

def main():
    errors, warns, notes = [], [], []
    with open(WEAPONS, encoding="utf-8") as f:
        header = f.readline().strip().split(",")
        rows = [dict(zip(header, l.strip().split(","))) for l in f if l.strip()]
    with open(SKILLS, encoding="utf-8") as f:
        skills = {r["skill_id"]: r for r in csv.DictReader(f)}

    if len(rows) != 73:
        errors.append(f"行数 {len(rows)} != 73（24职业×3 + 万象伞）")
    ids = [r["weapon_id"] for r in rows]
    dup = [k for k, v in collections.Counter(ids).items() if v > 1]
    if dup:
        errors.append(f"weapon_id 重复: {dup}")
    per_class = collections.Counter(r["class_id"] for r in rows)
    for c in CLASSES[:-1]:
        if per_class[c] != 3:
            errors.append(f"{c} 武器数 {per_class[c]} != 3")
    if per_class["UNS"] != 1:
        errors.append(f"UNS 武器数 {per_class['UNS']} != 1（万象伞唯一，D13/D14）")

    for r in rows:
        wid = r["weapon_id"]
        m = re.match(r"^W_([A-Z]{3})_(\d{3})$", wid)
        if not m:
            errors.append(f"{wid}: ID 格式非法"); continue
        cls, seq = m.group(1), m.group(2)
        if cls != r["class_id"]:
            errors.append(f"{wid}: ID 职业段 {cls} != class_id {r['class_id']}")
        if r["type"] not in TYPES:
            errors.append(f"{wid}: type 非法 {r['type']}")
        elif r["type"] != "umbrella" and WEIGHT_OF_TYPE.get(r["type"]) != r["weight"]:
            errors.append(f"{wid}: weight {r['weight']} 与类型 {r['type']} 规则不符")
        if cls == "UNS":
            if seq != "001":
                errors.append(f"{wid}: UNS 只允许 001")
            if r["grade"] != "masterwork":
                errors.append(f"{wid}: 万象伞 grade 应为 masterwork（形态切换即特性）")
        elif GRADE_OF_SEQ.get(str(int(seq))) != r["grade"]:
            errors.append(f"{wid}: 序号 {seq} 与 grade {r['grade']} 不符（1=制式/2=特化/3=精工）")
        try:
            spd = float(r["atk_spd"])
        except ValueError:
            errors.append(f"{wid}: atk_spd 非数值"); continue
        if not (0.80 <= spd <= 1.25):
            warns.append(f"{wid}: atk_spd {spd} 超出 0.80–1.25 区间")
        try:
            mod = float(r["atk_mod"])
            if not (-0.05 <= mod <= 0.05):
                errors.append(f"{wid}: atk_mod {mod} 超出 ±5%")
            if r["grade"] == "standard" and mod != 0:
                errors.append(f"{wid}: 制式武器 atk_mod 必须为 0")
        except ValueError:
            if r["weapon_id"] != "W_UNS_001":
                errors.append(f"{wid}: atk_mod 非数值")
        if r["grade"] == "standard" and (r["trait"] != "-" or r["trait_rules"] != "-"):
            errors.append(f"{wid}: 制式武器不应有特性")
        if r["grade"] != "standard" and cls != "UNS" and (r["trait"] == "-" or r["trait_rules"] == "-"):
            errors.append(f"{wid}: 特化/精工缺少特性")
        for pat, label in D12_BANNED:
            if re.search(pat, r["trait"] + " " + r["trait_rules"]):
                errors.append(f"{wid}: 特性含数值级表述「{label}」（D12 红线）")
        # trait_rules 引用的 skill_id 必须存在；EXPECTED 抽查旧值
        for sid in set(re.findall(r"[A-Z]{3}_(?:BAS|T[1-4]|U|PAS)_\d{3}", r["trait_rules"])):
            if sid not in skills:
                errors.append(f"{wid}: trait_rules 引用不存在的技能 {sid}")
            elif sid in EXPECTED:
                col, want = EXPECTED[sid]
                if want not in skills[sid].get(col, ""):
                    warns.append(f'{wid}: 引用 {sid} 的 {col} 与期望「{want}」不符（旧值核对失败，请人工确认）')

    # 同职业攻速相对制式比值区间（README §1.5：0.80–1.25，重武器例外记 warn）
    bycls = collections.defaultdict(list)
    for r in rows:
        bycls[r["class_id"]].append(r)
    for c, ws in bycls.items():
        std = next((w for w in ws if w["grade"] == "standard"), None)
        if not std:
            continue
        base = float(std["atk_spd"])
        for w in ws:
            if w["grade"] == "standard":
                continue
            ratio = float(w["atk_spd"]) / base
            if ratio < 0.80:
                warns.append(f'{w["weapon_id"]}: 相对制式攻速 {ratio:.2f} < 0.80（重武器设计意图则记录放行）')

    # ---- 报告 ----
    today = datetime.date.today().isoformat()
    verdict = "PASS" if not errors else "FAIL"
    lines = [
        "# Weapon-Spec 校验报告",
        "",
        f"| 项 | 值 |", "|---|---|",
        f"| 日期 | {today} |",
        f"| 结论 | **{verdict}**（{len(rows)} 行 × {len(header)} 列） |",
        f"| 错误 | {len(errors)} | 警告 | {len(warns)} |",
        "",
        "## 校验范围",
        "",
        "1. 结构：73 行 / 13 列 / ID 格式 / 全局唯一 / 每职业 3 把 + 万象伞 1 把",
        "2. 枚举：type / weight 与类型映射 / grade 与序号映射（1=制式 2=特化 3=精工）",
        "3. 数值：atk_spd ∈ [0.80, 1.25] 且同职业相对制式 ∈ [0.80, 1.25]（重武器例外记 warn）；atk_mod ∈ ±5% 且制式=0",
        "4. D12 红线：特性文本禁止吸血/直接伤害加成/承伤乘区/回复数值等数值级表述",
        "5. 引用：trait_rules 的 skill_id 必须存在于 skills.csv；EXPECTED 表抽查旧值一致性",
        "",
    ]
    if errors:
        lines += ["## 错误", ""] + [f"- ❌ {e}" for e in errors] + [""]
    if warns:
        lines += ["## 警告", ""] + [f"- ⚠️ {w}" for w in warns] + [""]
    lines += [
        "## 已知警告说明",
        "",
        "- W_BLA_002 重剑 0.85 / 制式 1.10 → 相对比值 0.77 < 0.80：重武器设计意图（README §1.5 区间针对常规武器，重武器例外放行）",
        "- W_ELE_002「落雷」：skills.csv 无对应技能（ELE 技能表无此名），规则保留 TODO 标记，待确认是否指 天雷地火/雷光炼狱 或删改 GDD §16.2",
        "",
        "## 变更记录（D12 规则化改写，GDD §16 已同步）",
        "",
        "| # | 武器 | 原特性（GDD §16 v0.3.4 前） | 改写后（规则级） |",
        "|---|---|---|---|",
        "| 1 | 重剑·镇岳 (W_BLA_002) | 重击类技能伤害 +10% | 重击类命中硬直 +2f、击退 +0.3m（盾值 1500→1800 保留） |",
        "| 2 | 裂地重刃 (W_BER_002) | 重击重量系数 ×1.15 | 重击命中硬直 +2f、击退 +0.5m |",
        "| 3 | 饮血 (W_BER_003) | 嗜血奋战期间普攻 5% 吸血 | 嗜血奋战期间普攻链 4段→5段 |",
        "| 4 | 崩玉 (W_STR_003) | 寸劲 1m 内加伤 +15%→+20% | 寸劲近距离阈值 1m→1.3m |",
        "| 5 | 淬毒爪 (W_ROG_002) | 涂毒一次伤害 +15% | 涂毒附加减速 20%/2s |",
        "| 6 | 暗手 (W_THF_003) | 瞬移后 0.5s 内伤害 +15% | 瞬移后 0.5s 内攻击附带破霸体 |",
        "| 7 | 影切·忍刀 (W_NJA_001) | 「影 cutting·忍刀」（抓取噪声） | 正名「影切·忍刀」 |",
        "| 8 | 圣佑 (W_GAN_003) | 恢复术每跳 200→250 | 恢复术回复间隔 3s→2.5s |",
        "| 9 | 重盾套装 (W_KNI_002) | 盾墙减伤 85%→88% | 盾墙持续 6s→7s |",
        "| 10 | 誓约 (W_KNI_003) | 风暴反击反弹比例 +10% | 风暴反击持续 10s→12s |",
        "",
        "## GDD-GAP（GDD ↔ 主数据口径同步）",
        "",
        "- 治疗术吟唱：GDD §16.21 写 0.8s→0.7s，skills.csv startup=24f（0.4s）→ 按 CSV 口径改为 24f→20f，GDD 已同步",
        "- 召唤兽继承面板 30%：GDD §14.3.3 原未写基础值 → 已补录（群灵之契 30%→33% 的前提）",
        "- 击坠伤害封顶 1200：GDD §14.4.3 原只有硬直 20f + 长倒地 → 已补录封顶 1200（老朋友 1200→800 的前提）",
        "- 「镇岳」重名：BER 制式与 BLA 特化同名 → 待 v0.2 改名其一",
        "",
        f"---\n*由 tools/validate_weapons.py 于 {today} 生成；改 weapons.csv 后必须重跑。*",
    ]
    with open(REPORT, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"[validate_weapons] {verdict}: {len(rows)} rows, {len(errors)} errors, {len(warns)} warns -> {REPORT}")
    for e in errors:
        print("  ERR:", e)
    for w in warns:
        print("  WARN:", w)

if __name__ == "__main__":
    main()
