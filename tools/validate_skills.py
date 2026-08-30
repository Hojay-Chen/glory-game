#!/usr/bin/env python3
"""Skill-Spec 构建管线：合并分片 → 删除散人引用行 → 生成习得等级(learn_level) → 派生资源列 → 结构校验 → 输出 skills.csv 与 validation-report.md"""
import csv, re, collections, datetime

PARTS = [f"/tmp/skillspec/p{i}.csv" for i in range(1, 8)]
OUT = "/home/ubuntu/claude-workspace/glory-game/docs/skill-spec/skills.csv"
REPORT = "/home/ubuntu/claude-workspace/glory-game/docs/skill-spec/validation-report.md"
CLASSES = "BMG ELE SUM WIT BLA BER SBL GBL SRP SPF LAU MEH STR QIM GRP ROG ASN THF NJA WRK PRI GAN KNI EXO UNS".split()
TIERS = {"BAS", "T1", "T2", "T3", "T4", "U", "PAS"}
TYPES = {"basic", "active", "stance", "summon", "deploy", "grab", "counter", "buff", "passive", "ref", "heal", "channel"}
SWEEP_WHITELIST = {"圆舞棍", "背摔", "踏射", "空中灌篮", "强龙压", "肘落", "地雷震", "银光落刃"}

# ---- 习得等级：来自 tools/learn_levels.py（用户提供的荣耀技能全表，2026-08-29）----
from learn_levels import LEARN, JC, AW, N  # LEARN 值取 [0]=等级；acq 由上方规则派生

# ---- 读取 ----
rows, errors, warns = [], [], []
with open(PARTS[0], encoding="utf-8") as f:
    header = f.readline().strip().split(",")
for pi, p in enumerate(PARTS):
    with open(p, encoding="utf-8") as f:
        for ln, line in enumerate(f, 1):
            if pi == 0 and ln == 1:
                continue
            line = line.strip()
            if not line:
                continue
            cells = line.split(",")
            if len(cells) != len(header):
                errors.append(f"{p}:{ln}: 列数 {len(cells)} != {len(header)}")
                continue
            d = dict(zip(header, cells))
            if d["tier"] == "REF":        # 散人引用行：原著口径下散人学的就是技能本身，独立记录废除
                continue
            rows.append(d)

# ---- learn_level + acq_type ----
# acq 派生规则（用户 2026-08-29 裁定）：learn_level < 20 = 常规（转职前可学）；
# ≥ 20 = 转职后习得（job_change）；50 级且属觉醒技白名单 = 觉醒（awakening）。
DEFAULT_LV = {"BAS": 1, "T1": 15, "T2": 25, "T3": 50, "T4": 70, "U": 70, "PAS": 20}
AW_SET = {"BMG_PAS_001", "BLA_U_001", "BER_U_001", "KNI_U_001", "ASN_T4_001", "LAU_T4_002"}  # 斗者意志/剑定天下/嗜血奋战/骑士精神/要害攻击/蓄能火炮
for r in rows:
    sid = r["skill_id"]
    lv = LEARN.get(sid, (DEFAULT_LV.get(r["tier"], 20), N))[0]
    r["learn_level"] = lv
    if sid in AW_SET:
        r["acq_type"] = AW
    elif lv >= 20:
        r["acq_type"] = JC
    else:
        r["acq_type"] = N

# ---- 派生列 + 校验 ----
out_rows = []
ids = []
for r in rows:
    sid = r["skill_id"]
    ids.append(sid)
    m = re.match(r"^([A-Z]{3})_(BAS|T1|T2|T3|T4|U|PAS)_(\d{3})$", sid)
    if not m:
        errors.append(f"{sid}: ID 格式非法")
    elif m.group(1) not in CLASSES:
        errors.append(f"{sid}: 职业缩写非法")
    if r["tier"] not in TIERS: errors.append(f"{sid}: tier 非法 {r['tier']}")
    if r["type"] not in TYPES: errors.append(f"{sid}: type 非法 {r['type']}")
    FRAME_OK = re.compile(r"^(-|\d+|\d+x\d+|\d+(\.\d+)?s|维持|可控|飞行|持续|\d+\(0\.25s\)|15\(0\.25s\))$")
    for col in ("startup_f", "active_f", "recovery_f"):
        if not FRAME_OK.match(r[col]):
            errors.append(f"{sid}: {col} 帧格式非法: {r[col]}")
    lv = r["launch_v"]
    if lv not in ("-", "0") and lv.replace(".", "", 1).isdigit():
        if not (5.0 <= float(lv) <= 9.5):
            errors.append(f"{sid}: 浮空初速越界 {lv}")
    if r["tier"] == "U" and r["type"] not in ("active", "grab", "buff", "heal"):
        warns.append(f"{sid}: U 档非主动技")
    if r["type"] in ("active", "grab", "channel"):
        if r["cost_mp"] in ("-", "") and r["cost_hp"] in ("-", ""):
            errors.append(f"{sid}: 主动技缺消耗")
        if r["cooldown_s"] in ("-", ""):
            errors.append(f"{sid}: 主动技缺 CD")
    if "受身无效" in r["special"] and r["skill_name"] not in SWEEP_WHITELIST:
        errors.append(f"{sid}: 受身无效不在白名单: {r['skill_name']}")
    hit = r["damage_mult"] not in ("0", "-") or r["type"] in ("grab",)
    r["animation"] = f"AM_{sid}"
    r["vfx"] = f"VX_{sid}" + ("_HIT" if hit else "")
    r["sound"] = f"SD_{sid}" + ("_HIT" if hit else "")
    out_rows.append(r)

dup = [k for k, v in collections.Counter(ids).items() if v > 1]
for d in dup:
    errors.append(f"ID 重复: {d}")

# ---- 每职业统计 + 散人池 ----
by_class = collections.OrderedDict((c, collections.Counter()) for c in CLASSES)
uns_pool = collections.Counter()
for r in out_rows:
    by_class[r["class_id"]][r["tier"]] += 1
    if r["class_id"] != "UNS" and int(r["learn_level"]) < 20 and r["acq_type"] == N and r["type"] not in ("basic", "passive"):
        uns_pool[r["class_id"]] += 1
for c, cnt in by_class.items():
    if cnt["U"] > 1 and c != "SUM":
        errors.append(f"{c}: U 档 {cnt['U']} > 1")
    if cnt["BAS"] == 0:
        errors.append(f"{c}: 缺普攻行")
for c, n in uns_pool.items():
    if c != "UNS" and not (2 <= n <= 8):
        errors.append(f"{c}: 19级以下技能池 {n} 个，超出合理区间(2-8)")
pool_total_n = sum(uns_pool.values())
if pool_total_n > 100:
    errors.append(f"散人池 {pool_total_n} > 100（快捷键上限）")

# ---- 写出 ----
all_cols = header + ["learn_level", "acq_type", "animation", "vfx", "sound"]
with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(f, fieldnames=all_cols)
    w.writeheader()
    for r in out_rows:
        w.writerow(r)

# ---- 报告 ----
today = datetime.date.today().isoformat()
n_type = collections.Counter(r["type"] for r in out_rows)
pool_total = sum(uns_pool.values())
lines = [
    "# Skill-Spec 结构校验报告", "",
    f"- 生成：{today}　脚本：`tools/validate_skills.py`　数据：`skills.csv`",
    f"- 总行数：**{len(out_rows)}**（" + " / ".join(f"{k} {v}" for k, v in n_type.most_common()) + "）",
    f"- 校验结果：**{'✅ 全部通过' if not errors else '❌ ' + str(len(errors)) + ' 个错误'}**；警告 {len(warns)} 条", "",
    "## 习得模型（learn_level + acq_type 为原著主轴；tier 为竞技档位，两者解耦）", "",
    "| acq_type | 含义 | learn_level |", "|---|---|---|",
    "| normal | 常规习得 | 原著明确等级照录；无记载的由本作拟定 |",
    "| job_change 转职 | 20 级转职后习得（散人不可学） | 20 |",
    "| awakening 觉醒 | 50 级觉醒（散人不可学） | 50 |",
    "", "tier（BAS/T1–T4/U）= 本作竞技档位，仅用于取消链与 MP/CD 定价，**不代表习得等级**；",
    "BAS=职业普攻；T1/T2/T3/T4=按竞技定位划分的档位；U=职业终极技（多为觉醒技或 75 级大招，逐职业择一）；PAS=被动。", "",
    f"## 散人低阶技能池（上限 100：快捷键限制；20 级及以上=转职后习得，散人不可学）", "",
    "散人可学 = `learn_level < 20` 且 `acq_type=normal` 的非普攻非被动主动技，**即技能本身，无削弱系数**：", "",
    "| 职业 | 池数 | | 职业 | 池数 |", "|---|---|---|---|---|",
]
cl = [c for c in CLASSES if c != "UNS"]
for i in range(0, len(cl), 2):
    a, b = cl[i], cl[i + 1] if i + 1 < len(cl) else ("", "")
    lines.append(f"| {a} | {uns_pool.get(a, 0)} | | {b} | {uns_pool.get(b, 0)} |")
lines += ["", f"**散人池合计：{pool_total} 个**（上限 100；原著「平均每职业 5 个以下、共约 120」为完整网游口径，竞技场取 <20 级子集）", "",
    "## 各职业档位统计", "",
    "| 职业 | BAS | T1 | T2 | T3 | T4 | U | PAS | 技能池* |", "|---|---|---|---|---|---|---|---|---|",
]
for c, cnt in by_class.items():
    if c == "UNS":
        continue
    pool = cnt["T1"] + cnt["T2"] + cnt["T3"] + cnt["T4"] + cnt["U"]
    lines.append(f"| {c} | {cnt['BAS']} | {cnt['T1']} | {cnt['T2']} | {cnt['T3']} | {cnt['T4']} | {cnt['U']} | {cnt['PAS']} | {pool}（全部可用） |")
lines += ["", "## 错误", ""] + ([f"- ❌ {e}" for e in errors] or ["- 无"])
lines += ["", "## 警告", ""] + ([f"- ⚠️ {w}" for w in warns] or ["- 无"])
lines += ["", "## 已知取舍与待定项", "",
    "- **无携带上限**（用户裁定）：每职业全部已学技能在战斗中可用，快捷键布局为实现层问题",
    "- SUM 双 U 档按「流派二选一使用」处理（兽王阵=四兽流 / 精灵献祭=精灵流）",
    "- 散人池：REF 引用行已废除（原著口径：散人学的就是技能本身）；池主体为 wiki 有据低阶技，**原创技能一律不进池**（圣光惩击上调 25 级，波动斩/盾牌横扫已删除）；wiki 未标等级技能的等级为拟定，待原著核对",
    "- 习得等级：wiki 明示的照录；未标注的按档位拟定（TODO: 待原著逐条核对）",
    "- 转职等级按 20 级拟定（与「20 级以下为散人池」自洽）；觉醒等级按 50 级拟定——均待原著核对",
]
with open(REPORT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
print(f"rows={len(out_rows)} errors={len(errors)} warns={len(warns)} uns_pool_total={pool_total}")
for e in errors:
    print("ERR:", e)
for w in warns:
    print("WARN:", w)
