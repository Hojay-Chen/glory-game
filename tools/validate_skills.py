#!/usr/bin/env python3
"""Skill-Spec 结构校验：合并分片 → 生成派生列 → 输出 skills.csv 与 validation-report.md"""
import csv, re, sys, collections, datetime

PARTS = [f"/tmp/skillspec/p{i}.csv" for i in range(1, 8)]
OUT = "/home/ubuntu/claude-workspace/glory-game/docs/skill-spec/skills.csv"
REPORT = "/home/ubuntu/claude-workspace/glory-game/docs/skill-spec/validation-report.md"
CLASSES = "BMG ELE SUM WIT BLA BER SBL GBL SRP SPF LAU MEH STR QIM GRP ROG ASN THF NJA WRK PRI GAN KNI EXO UNS".split()
TIERS = {"BAS","T1","T2","T3","T4","U","PAS","REF"}
TYPES = {"basic","active","stance","summon","deploy","grab","counter","buff","passive","ref","heal","channel"}
BASIC_PASSIVE_OK = {"buff","passive","stance","deploy","ref","heal","summon"}
SWEEP_WHITELIST = {"圆舞棍","背摔","踏射","空中灌篮","强龙压","肘落","地雷震","大跳劈","银光落刃"}

rows = []
errors, warns = [], []
with open(PARTS[0], encoding="utf-8") as f:
    header = f.readline().strip().split(",")
for pi, p in enumerate(PARTS):
    with open(p, encoding="utf-8") as f:
        first = True
        for ln, line in enumerate(f, 1):
            if first and pi == 0:
                first = False
                continue
            first = False
            line = line.strip()
            line = line.strip()
            if not line:
                continue
            cells = line.split(",")
            if len(cells) != len(header):
                errors.append(f"{p}:{ln}: 列数 {len(cells)} != {len(header)}")
                continue
            rows.append(dict(zip(header, cells)))

# 唯一性
ids = [r["skill_id"] for r in rows]
dup = [k for k, v in collections.Counter(ids).items() if v > 1]
for d in dup:
    errors.append(f"ID 重复: {d}")

FRAME_OK = re.compile(r"^(-|\d+|\d+x\d+|\d+(\.\d+)?s|维持|可控|飞行|持续|\d+\(0\.25s\))$")
# 派生列 + 逐行校验
out_rows = []
for r in rows:
    sid = r["skill_id"]
    m = re.match(r"^([A-Z]{3})_(BAS|T1|T2|T3|T4|U|PAS|REF)_(\d{3})$", sid)
    if not m:
        errors.append(f"{sid}: ID 格式非法")
    elif m.group(1) not in CLASSES:
        errors.append(f"{sid}: 职业缩写非法")
    if r["tier"] not in TIERS: errors.append(f"{sid}: tier 非法 {r['tier']}")
    if r["type"] not in TYPES: errors.append(f"{sid}: type 非法 {r['type']}")
    for col in ("startup_f","active_f","recovery_f"):
        if not FRAME_OK.match(r[col]):
            errors.append(f"{sid}: {col} 帧格式非法: {r[col]}")
    lv = r["launch_v"]
    if lv not in ("-","0") and lv.replace(".","",1).isdigit():
        v = float(lv)
        if not (5.0 <= v <= 9.5):
            errors.append(f"{sid}: 浮空初速越界 {lv}")
    if r["tier"] == "U" and r["type"] not in ("active","grab","buff","heal"):
        warns.append(f"{sid}: U 档非主动技")
    if r["type"] in ("active","grab","channel") :
        if r["cost_mp"] in ("-", "") and r["cost_hp"] in ("-", ""):
            errors.append(f"{sid}: 主动技缺消耗")
        if r["cooldown_s"] in ("-", ""):
            errors.append(f"{sid}: 主动技缺 CD")
    if "受身无效" in r["special"] and r["skill_name"] not in SWEEP_WHITELIST:
        errors.append(f"{sid}: 受身无效不在白名单: {r['skill_name']}")
    # 派生资源列
    hit = r["damage_mult"] not in ("0","-") or r["type"] in ("grab",)
    r["animation"] = f"AM_{sid}"
    r["vfx"] = f"VX_{sid}" + ("_HIT" if hit else "")
    r["sound"] = f"SD_{sid}" + ("_HIT" if hit else "")
    out_rows.append(r)

# 每职业统计
by_class = collections.OrderedDict((c, collections.Counter()) for c in CLASSES)
for r in out_rows:
    by_class[r["class_id"]][r["tier"]] += 1
for c, cnt in by_class.items():
    if cnt["U"] > 1:
        if c == "SUM":
            warns.append("SUM: 2 个 U 档（兽王四元素阵/精灵献祭）——按流派二选一携带处理，需在实现注记明确")
        else:
            errors.append(f"{c}: U 档 {cnt['U']} > 1")
    if cnt["PAS"] == 0:
        errors.append(f"{c}: 缺被动")
    if cnt["BAS"] == 0:
        errors.append(f"{c}: 缺普攻行")

# 写出 CSV（含派生列）
all_cols = header + ["animation","vfx","sound"]
with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(f, fieldnames=all_cols)
    w.writeheader()
    for r in out_rows:
        w.writerow(r)

# 报告
today = datetime.date.today().isoformat()
n_active = sum(1 for r in out_rows if r["type"] in ("active","grab","channel","stance"))
n_basic = sum(1 for r in out_rows if r["type"]=="basic")
n_summon = sum(1 for r in out_rows if r["type"]=="summon")
n_deploy = sum(1 for r in out_rows if r["type"]=="deploy")
n_passive = sum(1 for r in out_rows if r["type"]=="passive")
n_ref = sum(1 for r in out_rows if r["type"]=="ref")
lines = [
    "# Skill-Spec 结构校验报告", "",
    f"- 生成：{today}　脚本：`tools/validate_skills.py`　数据：`skills.csv`",
    f"- 总行数：**{len(out_rows)}**（主动 {n_active} / 普攻段 {n_basic} / 召唤 {n_summon} / 部署 {n_deploy} / 被动 {n_passive} / 散人引用 {n_ref}）",
    f"- 校验结果：**{'✅ 全部通过' if not errors else '❌ ' + str(len(errors)) + ' 个错误'}**；警告 {len(warns)} 条", "",
    "## 各职业技能池统计（构筑压力：池大小 vs 携带 10 格）", "",
    "| 职业 | BAS | T1 | T2 | T3 | T4 | U | PAS | REF | 可携带池* |", "|---|---|---|---|---|---|---|---|---|---|",
]
for c, cnt in by_class.items():
    pool = cnt["T1"]+cnt["T2"]+cnt["T3"]+cnt["T4"]+cnt["U"]
    cap = 12 if c == "UNS" else 10
    lines.append(f"| {c} | {cnt['BAS']} | {cnt['T1']} | {cnt['T2']} | {cnt['T3']} | {cnt['T4']} | {cnt['U']} | {cnt['PAS']} | {cnt['REF']} | {pool}/{cap} |")
lines += ["", f"*可携带池 = T1–U 总数（不含普攻/被动/引用）。", "", "## 错误", ""] + ([f"- ❌ {e}" for e in errors] or ["- 无"]) 
lines += ["", "## 警告", ""] + ([f"- ⚠️ {w}" for w in warns] or ["- 无"])
lines += ["", "## 已知取舍与待定项", "",
"- SUM 双 U 档按「流派二选一携带」处理（兽王阵=兽系流 / 精灵献祭=精灵流），见实现注记",
"GDD-GAP 清单（Skill-Spec 拆解时发现并已补全，待回补 GDD）：",
"1. WIT 缺 扫把冲刺/空袭俯冲/药剂护盾 三行（v0.2 值回补，CSV 已含）",
"2. THF/NJA/GAN/EXO/PRI/KNI 普攻链缺失 → 本表已按武器模板补全（notes 标 GDD-GAP）",
"3. BMG 豪龙破军 后摇 22f/8f 冲突 → 取 8f（收招极快是设计意图）",
"4. STR 被动无原著出处 → 占位「霸体体术」（命名待定）",
"5. GDD §1.5「技能池 12–14」与实际 12–23 不符 → 以本表为准，GDD 待更新",
""]
with open(REPORT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
print(f"rows={len(out_rows)} errors={len(errors)} warns={len(warns)}")
for e in errors: print("ERR:", e)
for w in warns: print("WARN:", w)
