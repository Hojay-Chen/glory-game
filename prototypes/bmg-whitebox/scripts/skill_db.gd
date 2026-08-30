# PROTOTYPE - NOT FOR PRODUCTION
# Question: 战斗地基（BMG 白盒）——GDD §2–§8 规则在 60Hz 确定性仿真中是否自洽
# Date: 2026-08-31
# 技能主数据加载器：解析 skills.csv（36 列，复制自 docs/skill-spec/skills.csv @ ef7efe4）
extends RefCounted

const COLS := ["skill_id","skill_name","class_id","tier","type","cost_mp","cost_hp","cooldown_s","startup_f","active_f","recovery_f","hit_interval_f","hitbox","range_m","angle_deg","damage_mult","damage_type","hits","hitstun_f","knockback_m","launch_v","status","armor","invincible_f","sweep","intercept","channel","cancel_min_tier","jump_cancel","special","notes","learn_level","acq_type","animation","vfx","sound"]

var skills := {}  # skill_id -> Dictionary

func _init(path: String = "res://data/skills.csv") -> void:
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		push_error("cannot open " + path)
		return
	var first := true
	while not f.eof_reached():
		var line: String = f.get_line().strip_edges()
		if line.is_empty():
			continue
		if first:
			first = false
			continue  # header
		var cells := line.split(",")
		if cells.size() != COLS.size():
			push_error("bad col count: " + line.substr(0, 40))
			continue
		var d := {}
		for i in COLS.size():
			d[COLS[i]] = cells[i]
		skills[d["skill_id"]] = d

func by_class(cls: String) -> Array:
	var out := []
	for id in skills:
		if skills[id]["class_id"] == cls:
			out.append(skills[id])
	return out

# ---- 解析辅助（全部静态，供 sim 复用）----

static func fn_int(s: String, def: int = -1) -> int:
	s = s.strip_edges()
	if s == "-" or s.is_empty():
		return def
	return int(s)

static func fn_float(s: String, def: float = -1.0) -> float:
	s = s.strip_edges()
	if s == "-" or s.is_empty():
		return def
	return s.to_float()

# active_f: "3" | "3x2"(旧语法) | "6" | "2s" | "-" → 总帧数
static func parse_active(s: String) -> int:
	s = s.strip_edges()
	if s == "-" or s.is_empty():
		return 2  # 判定即收：名义 2f（实现注记口径，日志记录该假设）
	if s.ends_with("s"):
		return int(s.substr(0, s.length() - 1).to_float() * 60.0)
	return fn_int(s, 2)

# 命中时刻表：返回 active 窗口内每击的相对帧偏移
static func hit_schedule(def: Dictionary) -> Array:
	var n := fn_int(def["hits"], 1)
	if n <= 0:
		n = 1
	var W := parse_active(def["active_f"])
	var iv := fn_int(def["hit_interval_f"], 0)
	var out := []
	if iv > 0:
		for k in n:
			out.append(mini(k * iv, W - 1))
	else:
		if n == 1:
			out.append(0)
		else:
			for k in n:
				out.append(int(round(k * (W - 1) / float(n - 1))))
	return out

# special 列关键词
static func sp_has(def: Dictionary, kw: String) -> bool:
	return def["special"].find(kw) >= 0

static func orb_type(def: Dictionary) -> String:
	var s: String = def["special"]
	if s.find("炫纹:") < 0:
		return ""
	var rest: String = s.substr(s.find("炫纹:") + 3)
	var endi: int = rest.find(";")
	if endi < 0:
		endi = rest.length()
	return rest.substr(0, endi)

# 解析 status 列：如 "slow:30%:3s" / "bleed:60:4s@50%" / "none"
static func parse_status(s: String) -> Array:
	var out := []
	if s == "none" or s == "-" or s.is_empty():
		return out
	for part in s.split(";"):
		var p: String = part.split("@")[0]  # 去几率修饰（@50% → 几率判定不做，记录假设）
		var seg := p.split(":")
		if seg.size() >= 2:
			out.append({"type": seg[0], "p1": seg[1], "dur": seg[2] if seg.size() >= 3 else "0s"})
	return out
