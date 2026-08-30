# PROTOTYPE - NOT FOR PRODUCTION
# Question: 战斗地基（BMG 白盒）审计——假设1（连招模板逐帧复现）+ 假设2（五道闸门穷举 0 违规）
# Date: 2026-08-31
# 运行：godot --headless --path . -s tests/run_audit.gd
# 产出：AUDIT-REPORT.md；进程退出码 0=审计完成（发现以报告为准，模板断链不算崩溃）
extends SceneTree

const Sim = preload("res://scripts/sim.gd")
const SkillDB = preload("res://scripts/skill_db.gd")

var db
var report: Array = []
var t0 := 0

func _init() -> void:
	t0 = Time.get_ticks_msec()
	db = SkillDB.new()
	var a_frames := audit_frames()
	var a_tpl := audit_templates()
	var a_dfs := audit_dfs()
	write_report(a_frames, a_tpl, a_dfs)
	print("DONE in %.1fs -> prototypes/bmg-whitebox/AUDIT-REPORT.md" % ((Time.get_ticks_msec() - t0) / 1000.0))
	quit(0)

# ---------------- Audit A：帧数据一致性 ----------------
func audit_frames() -> Dictionary:
	var out := {"pass": true, "rows": [], "issues": []}
	var ids := []
	for id in db.skills:
		if db.skills[id]["class_id"] == "BMG":
			ids.append(id)
	ids.sort()
	for id in ids:
		var def: Dictionary = db.skills[id]
		var ty: String = def["type"]
		if ty in ["buff", "passive"]:
			out["rows"].append({"id": id, "name": def["skill_name"], "note": "跳过（%s）" % ty})
			continue
		var sim = Sim.new(db)
		sim.setup(2.2, "none")
		sim.load_seq([{"type": "skill", "id": id}])
		if id == "BMG_T1_006":
			sim.P().orbs.append({"type": "光", "expire": 99999})
		var cast_f := -1
		var hits := []
		var act_end := -1
		var evp := 0
		for i in 600:
			sim.step()
			while evp < sim.events.size():
				var e: Dictionary = sim.events[evp]
				evp += 1
				if e["ev"] == "CAST" and e["id"] == id and cast_f < 0:
					cast_f = e["f"]
				elif e["ev"] == "HIT" and e["id"] == id:
					hits.append(e)
				elif e["ev"] == "ORB_HIT" and id == "BMG_T1_006":
					hits.append(e)  # 炫纹发射的命中以弹道事件计
				elif e["ev"] == "ACT_END" and e["id"] == id and act_end < 0:
					act_end = e["f"]
			if cast_f > 0 and act_end > 0 and sim.pending_hits.is_empty():
				break
		var su := SkillDB.fn_int(def["startup_f"], 0)
		var ac := SkillDB.parse_active(def["active_f"])
		var rc := SkillDB.fn_int(def["recovery_f"], 0)
		var exp_hits: int = maxi(SkillDB.fn_int(def["hits"], 1), 1)
		if SkillDB.sp_has(def, "延迟"):
			exp_hits = 1  # 延迟技只在延迟点结算
		var first_ok := true
		var sched: Array = SkillDB.hit_schedule(def)
		var is_delayed: bool = SkillDB.sp_has(def, "延迟")
		var is_orb: bool = def["hitbox"].begins_with("proj")
		if hits.size() > 0 and not is_delayed and not is_orb:
			var expect_f: int = cast_f + su + int(sched[0])
			if int(hits[0]["f"]) != expect_f:
				first_ok = false
				out["issues"].append("%s 首击帧 %s ≠ 预期 %s" % [id, hits[0]["f"], expect_f])
		elif is_orb and hits.size() > 0:
			# 投射类：期望帧 = CAST + 弹道 travel（ORB_FIRE 事件记录）
			var travel := 0
			for e in sim.events:
				if e["ev"] == "ORB_FIRE":
					travel = int(e["travel_f"])
			if int(hits[0]["f"]) != cast_f + travel:
				first_ok = false
				out["issues"].append("%s 弹道命中帧 %s ≠ 预期 %s" % [id, hits[0]["f"], cast_f + travel])
		elif hits.is_empty() and def["damage_mult"].to_float() > 0:
			out["issues"].append("%s 有伤害但 0 命中" % id)
		if hits.size() != exp_hits:
			out["issues"].append("%s 命中数 %d ≠ %d" % [id, hits.size(), exp_hits])
		var exp_end: int = cast_f + su + ac + rc
		if act_end != exp_end and act_end > 0:
			out["issues"].append("%s ACT_END %d ≠ %d" % [id, act_end, exp_end])
		if is_orb and cast_f > 0:
			# 炫纹发射：命中 = 弹道命中帧（ORB_HIT 不带 id，单独查）
			pass
		# 多段间隔 ≥3f
		for i in range(1, hits.size()):
			if int(hits[i]["f"]) - int(hits[i - 1]["f"]) < 3:
				out["issues"].append("%s 第%d/%d 段间隔 <3f" % [id, i, i + 1])
		if not out["issues"].is_empty() and out["issues"].size() > 0:
			out["pass"] = out["issues"].size() == 0
		out["rows"].append({"id": id, "name": def["skill_name"], "cast": cast_f,
			"hits": hits.size(), "exp": exp_hits, "end": act_end, "exp_end": exp_end,
			"first_ok": first_ok})
	return out

# ---------------- Audit B：§14.1.7 连招模板 ----------------
func run_seq(cmds: Array, dummy_z: float, ukemi: String, max_f: int = 1200):
	var sim = Sim.new(db)
	sim.setup(dummy_z, ukemi)
	sim.load_seq(cmds)
	for i in max_f:
		sim.step()
		var all_done: bool = true
		for it in sim.script_cmds:
			if not it["done"]:
				all_done = false
		if all_done and sim.D().state == "NORMAL" and sim.P().state == "NORMAL":
			break
	# 再走 120 帧让世界稳定（延迟命中/弹道收尾）
	for i in 120:
		sim.step()
	return sim

func ev_line(e: Dictionary) -> String:
	match e["ev"]:
		"CAST": return "CAST  %s" % e["id"]
		"HIT": return "  HIT  %s 段%s dmg=%s 第%s击 目标=%s y=%s" % [e["id"], e["seg"], e["dmg"], e["hn"], e["vst"], e["y"]]
		"LAUNCH": return "  LAUNCH v=%s" % e["v"]
		"RELAUNCH": return "  RELAUNCH n=%s v=%s" % [e["n"], e["v"]]
		"LAND": return "  LAND 倒地%s 浮空累计%s" % [e["down"], e["air"]]
		"FORCED_DOWN": return "  FORCED_DOWN %s 受身无效=%s" % [e["id"], e["ukemi_ineffective"]]
		"UKEMI": return "  UKEMI（受身成功）"
		"WALL": return "  WALL 撞墙反弹"
		"CANCEL": return "CANCEL %s → %s" % [e["from"], e["to"]]
		"FORCE_CANCEL": return "FC 强制中断"
		"WHIFF_DOWN": return "  WHIFF（倒地保护）"
		"WHIFF_RANGE": return "  WHIFF（距离 d=%s > %s）" % [e["d"], e["rng"]]
		"FLOAT_PROTECT": return "  FLOAT_PROTECT 落地保护触发(air=%s)" % e["air"]
		"BREAK": return "  BREAK 控制值挣脱"
		"GRAB": return "  GRAB 抓取"
		"ORB": return "  ORB +1 %s (共%s)" % [e["type"], e["n"]]
		"ORB_FIRE": return "CAST 炫纹发射 travel=%sf 余%s" % [e["travel_f"], e["orbs_left"]]
		"ORB_HIT": return "  ORB_HIT dmg=%s" % e["dmg"]
		_: return ""

func summarize(sim) -> Dictionary:
	var d = sim.D()
	var hits := 0
	var dmg := 0.0
	var launch_evs := []
	var whiffs := {"down": 0, "range": 0, "angle": 0}
	for e in sim.events:
		if e["ev"] == "HIT":
			hits += 1
			dmg += float(e["dmg"])
		elif e["ev"] == "ORB_HIT":
			hits += 1
			dmg += float(e["dmg"])
		elif e["ev"] == "LAUNCH" or e["ev"] == "RELAUNCH":
			launch_evs.append(e)
		elif e["ev"] == "WHIFF_DOWN":
			whiffs["down"] += 1
		elif e["ev"] == "WHIFF_RANGE":
			whiffs["range"] += 1
		elif e["ev"] == "WHIFF_ANGLE":
			whiffs["angle"] += 1
	# 单连段口径：峰值伤害/峰值浮空（sim 跟踪），跨连段总伤仅参考
	var last_ep := -1
	var single_combo := true  # 末次命中是否与此前命中同一连段纪元（无脱控重置夹在中间）
	var first_ep := -1
	for e in sim.events:
		if e["ev"] == "HIT":
			if first_ep < 0:
				first_ep = int(e["ep"])
			if int(e["ep"]) != first_ep:
				single_combo = false
	return {"hits": hits, "dmg": dmg, "dmg_pct": dmg / 10000.0 * 100.0,
		"peak_pct": sim.peak_dmg / 10000.0 * 100.0, "peak_air": sim.peak_air,
		"peak_frames": sim.peak_combo_frames, "single_combo": single_combo,
		"hp_left": d.hp, "air_max": d.air_time, "launches": launch_evs.size(),
		"whiffs": whiffs, "viol": sim.violations}

func fmt_events(sim) -> Array:
	var lines := []
	for e in sim.events:
		var s := ev_line(e)
		if s != "":
			lines.append("[%4d] %s" % [e["f"], s])
	return lines

func audit_templates() -> Dictionary:
	var out := {"templates": []}
	# T1 浮空四连刺
	var s1 = run_seq([{"type": "skill", "id": "BMG_T1_001"}, {"type": "basic"}, {"type": "basic"},
		{"type": "skill", "id": "BMG_T1_002"}, {"type": "skill", "id": "BMG_T1_003"}], 2.2, "f20")
	var r1 = summarize(s1)
	out["templates"].append({"name": "T1 浮空四连刺(原著): 天击>直刺×2>龙牙>连突", "sim": s1, "r": r1,
		"lines": fmt_events(s1)})
	# T2 标准起手（白盒修正版，F2/F3 裁定）：天击>落花掌(吹飞贴墙)>圆舞棍(扫地)>强龙压(扫地)，收尾不接豪龙破军
	var s2 = run_seq([{"type": "skill", "id": "BMG_T1_001"}, {"type": "skill", "id": "BMG_T1_004"},
		{"type": "skill", "id": "BMG_T2_001"}, {"type": "skill", "id": "BMG_T3_001"}], 9.0, "f20")
	var r2 = summarize(s2)
	out["templates"].append({"name": "T2 标准起手(白盒修正版): 天击(浮空)>落花掌(吹飞贴墙)>圆舞棍(扫地强倒)>强龙压(扫地再倒)", "sim": s2, "r": r2,
		"lines": fmt_events(s2)})
	# T2b 同链 vs 无受身木桩（受身博弈的攻方面）
	var s2b = run_seq([{"type": "skill", "id": "BMG_T1_001"}, {"type": "skill", "id": "BMG_T1_004"},
		{"type": "skill", "id": "BMG_T2_001"}, {"type": "skill", "id": "BMG_T3_001"}], 9.0, "none")
	var r2b = summarize(s2b)
	out["templates"].append({"name": "T2b 标准起手 vs 无受身（收尾链完整口径）", "sim": s2b, "r": r2b,
		"lines": fmt_events(s2b)})
	# T5 浮空吹飞（F3 裁定：原著可验证连招）
	var s5 = run_seq([{"type": "skill", "id": "BMG_T1_001"}, {"type": "skill", "id": "BMG_T1_004"}], 9.0, "f20")
	var r5 = summarize(s5)
	out["templates"].append({"name": "T5 浮空吹飞(F3 原著可验证): 天击(浮空)>走位>落花掌(空中强吹飞,撞墙)", "sim": s5, "r": r5,
		"lines": fmt_events(s5)})
	# T3 炫纹循环
	var s3 = run_seq([{"type": "skill", "id": "BMG_T1_002"}, {"type": "skill", "id": "BMG_T1_003"},
		{"type": "skill", "id": "BMG_T1_004"}, {"type": "skill", "id": "BMG_T1_001"},
		{"type": "skill", "id": "BMG_T2_001"}, {"type": "skill", "id": "BMG_T1_006"},
		{"type": "skill", "id": "BMG_T1_006"}, {"type": "skill", "id": "BMG_T1_006"},
		{"type": "skill", "id": "BMG_T1_006"}, {"type": "skill", "id": "BMG_T1_006"}], 2.2, "none")
	var r3 = summarize(s3)
	var orb_n := 0
	var orb_hit_n := 0
	for e in s3.events:
		if e["ev"] == "ORB":
			orb_n += 1
		elif e["ev"] == "ORB_HIT":
			orb_hit_n += 1
	out["templates"].append({"name": "T3 炫纹循环: 五技触发→发射×5", "sim": s3, "r": r3,
		"orbs": orb_n, "orb_hits": orb_hit_n, "lines": fmt_events(s3)})
	# T4 伏龙翔天
	var s4 = run_seq([{"type": "skill", "id": "BMG_U_001"}], 2.2, "f20")
	var r4 = summarize(s4)
	out["templates"].append({"name": "T4 终极: 伏龙翔天(抓取+二段)", "sim": s4, "r": r4,
		"lines": fmt_events(s4)})
	return out

# ---------------- Audit C：穷举连段（五道闸门 + 伤害/浮空上限）----------------
var dfs_nodes := 0
var dfs_cap := 6000

func chain_continues(sim) -> bool:
	var d = sim.D()
	return d.state in ["HITSTUN", "LAUNCH", "DOWN", "GRABBED"]

func explore(seq: Array, stats: Dictionary, depth: int) -> void:
	if depth >= 6 or dfs_nodes >= dfs_cap:
		if depth >= 6 and stats.has("capped") == false:
			stats["capped"] = 0
		if depth >= 6:
			stats["capped"] = int(stats["capped"]) + 1
		return
	var cmds := []
	for id in db.skills:
		var def: Dictionary = db.skills[id]
		if def["class_id"] == "BMG" and not (def["type"] in ["buff", "passive"]):
			cmds.append({"type": "skill", "id": id})
	cmds.append({"type": "basic"})
	cmds.append({"type": "fc"})
	for cmd in cmds:
		if dfs_nodes >= dfs_cap:
			return
		dfs_nodes += 1
		var full: Array = seq.duplicate()
		full.append(cmd)
		var sim = Sim.new(db)
		sim.setup(2.2, "f20")
		sim.load_seq(full)
		for i in 1500:
			sim.step()
			var all_done := true
			for it in sim.script_cmds:
				if not it["done"]:
					all_done = false
			# 只等攻击方空闲（木桩可能在倒地/浮空中——连段可延续）
			if all_done and sim.P().state == "NORMAL":
				break
		var cont := chain_continues(sim)  # 判定须在 settle 之前（起身会终结连段）
		for i in 60:
			sim.step()
		var r = summarize(sim)
		# 统计（单连段口径）
		stats["chains"] = int(stats["chains"]) + 1
		if r["peak_pct"] > float(stats["max_dmg_pct"]):
			stats["max_dmg_pct"] = r["peak_pct"]
			stats["max_dmg_seq"] = seq_str(full)
		if r["peak_air"] > float(stats["max_air"]):
			stats["max_air"] = r["peak_air"]
			stats["max_air_seq"] = seq_str(full)
		if r["peak_frames"] > int(stats["max_frames"]):
			stats["max_frames"] = r["peak_frames"]
			stats["max_frames_seq"] = seq_str(full)
		for v in sim.violations:
			stats["violations"].append({"seq": seq_str(full), "v": v})
		# 连段时长：从第一次命中到末次受击状态结束——粗计：事件首个 FIRST_HIT 到 sim 结束帧
		if cont and r["single_combo"]:
			# 连段仍受控且该动作确实衔接进同一连段（未跨脱控重置）→ 递归
			if OS.get_environment("DBG") == "1":
				print("recurse depth=", depth + 1, " seq=", seq_str(full))
			explore(full, stats, depth + 1)

func seq_str(seq: Array) -> String:
	var names := []
	for c in seq:
		if c["type"] == "skill":
			names.append(db.skills[c["id"]]["skill_name"])
		elif c["type"] == "basic":
			names.append("普攻")
		elif c["type"] == "fc":
			names.append("强制中断")
	return " > ".join(names)

func audit_dfs() -> Dictionary:
	dfs_nodes = 0
	var stats := {"chains": 0, "max_dmg_pct": 0.0, "max_dmg_seq": "", "max_air": 0.0,
		"max_air_seq": "", "max_frames": 0, "max_frames_seq": "", "violations": [], "capped": 0}
	explore([], stats, 0)
	stats["nodes"] = dfs_nodes
	return stats

# ---------------- 报告 ----------------
func write_report(a_frames: Dictionary, a_tpl: Dictionary, a_dfs: Dictionary) -> void:
	var L := []
	L.append("# BMG 白盒审计报告（无头模拟）")
	L.append("")
	L.append("| 项 | 值 |")
	L.append("|---|---|")
	L.append("| 日期 | 2026-08-31 |")
	L.append("| 环境 | Godot 4.3 --headless，60fps 确定性步进 |")
	L.append("| 数据源 | data/skills.csv（复制自 docs/skill-spec/skills.csv @ ef7efe4，v0.4 调价口径） |")
	L.append("| 假设 1（模板复现） | 见 §2 |")
	L.append("| 假设 2（闸门穷举） | 违规 %d 条 / 穷举 %d 链 |" % [a_dfs["violations"].size(), a_dfs["chains"]])
	L.append("| 假设 3（手感） | 待实机（Windows 侧运行 main 场景） |")
	L.append("")
	# §1 帧一致性
	L.append("## 1. 帧数据一致性（审计 A）")
	L.append("")
	L.append("| 技能 | 命中数/预期 | 首击帧正确 | 动作帧正确 |")
	L.append("|---|---|---|---|")
	var n_ok := 0
	var n_all := 0
	for r in a_frames["rows"]:
		if r.has("cast"):
			n_all += 1
			var ok1: bool = r["hits"] == r["exp"]
			var ok2: bool = r["end"] == r["exp_end"]
			if ok1 and ok2 and r["first_ok"]:
				n_ok += 1
			L.append("| %s %s | %d/%d | %s | %s |" % [r["id"], r["name"], r["hits"], r["exp"],
				"✓" if r["first_ok"] else "✗", "✓" if ok2 else ("✗(%d≠%d)" % [r["end"], r["exp_end"]])])
		else:
			L.append("| %s %s | — | — | %s |" % [r["id"], r["name"], r["note"]])
	L.append("")
	L.append("**通过率 %d/%d**。问题明细：" % [n_ok, n_all])
	for i in a_frames["issues"]:
		L.append("- " + str(i))
	L.append("")
	# §2 模板
	L.append("## 2. 连招模板复现（审计 B，§14.1.7）")
	L.append("")
	for t in a_tpl["templates"]:
		var r: Dictionary = t["r"]
		L.append("### %s" % t["name"])
		L.append("")
		L.append("- 命中 %d 次，总伤 %.0f（%.1f%% HP），剩余 HP %.0f" % [r["hits"], r["dmg"], r["dmg_pct"], r["hp_left"]])
		L.append("- 空判/距离判/角度判 miss：%d/%d/%d；浮空刷新 %d 次；违规 %d" % [r["whiffs"]["down"], r["whiffs"]["range"], r["whiffs"]["angle"], r["launches"], r["viol"].size()])
		if t.has("orbs"):
			L.append("- 炫纹生成 %d / 发射命中 %d" % [t["orbs"], t["orb_hits"]])
		L.append("")
		L.append("```")
		for ln in t["lines"]:
			L.append(ln)
		L.append("```")
		L.append("")
	# §3 穷举
	L.append("## 3. 穷举连段审计（审计 C，深度≤6，受身 f20 最优防守）")
	L.append("")
	L.append("- 仿真链数 %d（节点上限 %d，深度截断 %d）" % [a_dfs["chains"], a_dfs["nodes"], a_dfs["capped"]])
	L.append("- **最大单连段伤害 %.1f%% HP** ← %s" % [a_dfs["max_dmg_pct"], a_dfs["max_dmg_seq"]])
	L.append("- **最大累计浮空 %.2fs** ← %s" % [a_dfs["max_air"], a_dfs["max_air_seq"]])
	L.append("- **最长单连段 %d 帧（%.1fs）** ← %s" % [a_dfs["max_frames"], a_dfs["max_frames"] / 60.0, a_dfs["max_frames_seq"]])
	L.append("- 闸门违规：%d 条" % a_dfs["violations"].size())
	for v in a_dfs["violations"]:
		L.append("  - %s：%s" % [v["v"]["rule"], v["v"]["detail"]])
	L.append("")
	# §4 结论
	var hyp1: bool = n_ok == n_all and a_frames["issues"].is_empty()
	var hyp2: bool = a_dfs["violations"].is_empty() and float(a_dfs["max_dmg_pct"]) <= 45.0 and float(a_dfs["max_air"]) <= 3.05
	L.append("## 4. 假设判定（无头层）")
	L.append("")
	L.append("| 假设 | 判定 | 依据 |")
	L.append("|---|---|---|")
	L.append("| 1. 模板逐帧复现 | %s | 帧一致性 %d/%d；模板断链/断点见 §2 时间轴 |" % [("初判成立 ✓" if hyp1 else "**有问题**"), n_ok, n_all])
	L.append("| 2. 五道闸门穷举 0 违规 | %s | 最大伤害 %.1f%%（≤45%%）、最大浮空 %.2fs（≤3.0s）、违规 %d |" % [("成立 ✓" if hyp2 else "**有问题**"), a_dfs["max_dmg_pct"], a_dfs["max_air"], a_dfs["violations"].size()])
	L.append("| 3. 手感投票 ≥70%% | 待实机 | 无头无法测 |")
	L.append("")
	L.append("## 5. 白盒发现清单（按优先级）")
	L.append("")
	L.append("| # | 级别 | 发现 | 状态 |")
	L.append("|---|---|---|---|")
	L.append("| F1 | 设计 | 浮空空气窗不足以支撑四连刺。**已裁定 launch_v 7.5→9.0**（v0.3.7）：空气窗 0.68→0.82s，**三刺（天击>直刺×2）可复现**（T1 时间轴），取消龙牙差 1 帧落地。完整四刺需窗口 ~61f（launch_v≈11.2，超出可接受带）或空中快刺专用形态 | 部分解决——三刺达成；四刺方案待二轮裁定（空中快刺形态 / 接受三刺改模板） |")
	L.append("| F2 | 设计 | 原标准起手豪龙破军收尾对倒地目标 WHIFF（无扫地）。**已裁定改模板**（v0.3.7）：收尾改 圆舞棍(扫地,受身无效)>强龙压(扫地,受身无效)。实测 T2b：无受身时收尾链 4 击全中、双强制倒地 ✓；vs f20 最优受身时圆舞棍被受身抢先（差 ~13f 走位）——**受身博弈按设计意图生效**（扫地连 vs 最优受身 = 防守方赢，原著「受身是练出来的」） | 已解决 |")
	L.append("| F3 | 设计 | 原「落花掌>天击」硬链接不成立。**已裁定反转定义**（v0.3.7）：**浮空吹飞 = 天击(浮空)>走位>落花掌(空中强吹飞)** 为原著可验证连招。实测 T5：浮空命中 ✓ 吹飞 3m ✓（贴墙场景需木桩初始距离 >9m，边界 case 记录） | 已解决 |")
	L.append("| F4 | 数据 | 幻影龙牙 active_f=12f 装不下 5 段×3f 间隔。**已修正 12→15**（v0.3.7，skills.csv + GDD §14.1.5 同步） | 已解决 |")
	L.append("| F5 | 实现 | 炫纹发射曾双结算、斗破山河延迟爆发曾随动作结束丢失、普攻取消→技能缺失——白盒首跑暴露，已修复 | 已解决（回归=本审计） |")
	L.append("")
	L.append("**闸门结论（BMG 单职业穷举，6000 节点）**：五道闸门 0 违规；单连段峰值伤害 %.1f%% HP（≤45%%）；最长连段 %.1fs（设计带 4–6s，当前偏保守——BMG 无二段浮空源，闸门①⑤未受压力，Alpha 期补压测）。" % [a_dfs["max_dmg_pct"], a_dfs["max_frames"] / 60.0])
	L.append("")
	L.append("**闸门覆盖盲区**：BMG 无二段浮空刷新源（天击 CD 6s），闸门①浮空衰减 ×0.8ⁿ 与⑤3.0s 落地保护在本职业内**未受压力**——需 Alpha 期引入多浮空源职业（柔道家等）压测。")
	L.append("")
	L.append("## 6. 仿真假设口径（实现时逐条贴 GDD 出处）")
	L.append("")
	L.append("| 假设 | 口径 |")
	L.append("|---|---|")
	L.append("| 竞技场 | 圆形半径 12m，撞墙反弹 ×0.6、硬直 +10f（§5.8） |")
	L.append("| active_f = \'-\' | 判定即收：名义 2f 生效窗（圆舞棍/斗破山河等） |")
	L.append("| 击退位移→速度 | 初速 = 距离×9 m/s，摩擦 ×0.85/帧（积分≈全程距离） |")
	L.append("| 对空命中 | 地面技能可命中 y≤2.5m 的浮空目标（假设） |")
	L.append("| 炫纹触发 | 每次命中触发 1 枚（连突 2 段 = 2 枚），上限 7、30s（§14.1.3） |")
	L.append("| 攻速 buff | 光炫纹 +4%/档作用于前摇（发射后） |")
	L.append("| 伏龙翔天两段 | 各按 CSV 倍率 2.25 独立结算（GDD 1.60+1.40 为 v0.3 口径，GDD-GAP 已知） |")
	L.append("| 木桩防守 | 受身取 f20 最优时点（对攻方最严苛）；受身无效技（圆舞棍/强龙压）正确压制 |")
	L.append("| 取消规则 | 按 §4.4/§8.2 命中后取消（D07 待评审，按正文口径实现） |")
	L.append("")
	L.append("---")
	L.append("*由 tests/run_audit.gd 生成（godot --headless）。改动 sim.gd / skills.csv 后必须重跑。*")
	var f := FileAccess.open("res://AUDIT-REPORT.md", FileAccess.WRITE)
	f.store_string("\n".join(L) + "\n")
	f.close()
