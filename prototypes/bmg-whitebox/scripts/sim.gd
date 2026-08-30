# PROTOTYPE - NOT FOR PRODUCTION
# Question: 战斗地基（BMG 白盒）——GDD §2–§8 在 60Hz 确定性仿真中是否自洽
# Date: 2026-08-31
# 纯逻辑仿真核心：无渲染依赖，实机模式（后续 Windows 侧）复用同一套规则代码。
extends RefCounted
const SkillDB = preload("res://scripts/skill_db.gd")

# ---- GDD 常量（行尾注明出处；标「假设」的为白盒拟定，报告中逐条列出）----
const FPS := 60
const GRAVITY := 22.0            # §3.3/§5.3
const MOVE_SPEED := 6.3          # §14.1.2
const JUMP_V := 7.0              # §3.3
const JUMP_STARTUP := 4          # §3.3
const ROLL_FRAMES := 30          # §10.1
const ARENA_R := 12.0            # 假设：圆形竞技场半径
const WALL_HITSTUN := 10         # §5.8
const MAX_HP := 10000.0          # §2.5.3
const DEF_COEF := 0.6            # §2.5.3 对等防
const MP_MAX := 1000.0
const MP_REGEN := 20.0 / 60.0    # §9.1
const MP_HIT := 8.0
const MP_HURT := 4.0
const STAM_MAX := 100.0
const CONTROL_MAX := 100.0       # §7.4
const CONTROL_DECAY := 20.0 / 60.0
const BREAK_FRAMES := 90         # 1.5s
const FLOAT_LIMIT := 3.0         # §5.3 落地保护
const LAUNCH_DECAY := 0.8
const LAUNCH_FLOOR := 3.0
const HITSTUN_DECAY := 0.97      # §8.5②
const HITSTUN_FLOOR := 0.5
const DMG_DECAY := 0.94          # §8.5③
const DMG_DECAY_FROM := 7
const AIR_MOD := 1.05            # §2.5.2
const SWEEP_MOD := 0.7           # §2.5.2
const DOWN_FRAMES := 45          # §5.6
const DOWN_LONG := 80
const UKEMI_WINDOW := 20         # §10.3
const UKEMI_STAM := 15.0
const GETUP_FRAMES := 24
const FC_MP := 60.0              # §10.4
const FC_CD := 240
const BASIC_CANCEL_LAG := 4      # §4.2 生效帧后4f可取消为技能
const MELEE_AIR_REACH := 2.5     # 假设：地面技能对 y≤2.5m 浮空目标可命中
const KBDIST_VEL := 9.0          # 假设：击退距离→初速 =m×9，摩擦×0.85/帧（积分≈全程）
const KB_FRICTION := 0.85
const TIER_RANK := {"BAS": 0, "T1": 1, "T2": 2, "T3": 3, "T4": 4, "U": 5}
const CTRL_ADD := {"stun": 35.0, "freeze": 35.0, "root": 25.0, "sleep": 30.0, "fear": 30.0, "confuse": 30.0, "taunt": 30.0, "silence": 15.0, "blind": 15.0, "slow": 10.0, "weak": 10.0, "weight": 10.0, "seal": 20.0, "禁足": 40.0, "curse": 10.0, "bleed": 0.0, "burn": 0.0, "poison": 0.0}

class Fighter:
	var id := ""
	var pos := Vector3.ZERO       # y = 脚底高度
	var vel := Vector3.ZERO
	var facing := Vector3(0, 0, 1)
	var state := "NORMAL"         # NORMAL/ACT/HITSTUN/LAUNCH/DOWN/GETUP/BREAK/GRABBED/DEAD
	var state_t := 0
	var hp := 10000.0
	var mp := 1000.0
	var stam := 100.0
	var control := 0.0
	var atk := 1120.0
	var act := {}                 # 技能运行时
	var cds := {}                 # skill_id -> 剩余帧
	var fc_cd := 0
	var chain_n := 0              # 普攻链段号
	# 受击侧（连段）状态
	var hitstun_n := 0
	var launch_n := 0
	var air_time := 0.0
	var forced_fall := false
	var dmg_combo := 0.0
	var no_ukemi := false
	var grabbed_by := ""
	var down_t := 0
	var protect_t := 0
	var statuses := {}            # type -> 剩余帧
	var buffs := []               # [{type,left,power}]
	var orbs := []                # [{type,expire}]

	func grounded() -> bool:
		return pos.y <= 0.001

var db
var frame := 0
var events := []
var violations := []
var fighters := []
var world_orbs := []           # 炫纹发射弹
var pending_hits := []         # 动作结束后仍待结算的延迟命中
var long_down := {}            # fighter_id -> bool
var ukemi_policy := "none"     # dummy 受身策略："none" | "f20"
var epoch := 0                 # 连段纪元：木桩恢复行动即 +1（跨纪元的命中 = 新连段）
var peak_dmg := 0.0            # 单连段峰值伤害（dmg_combo 的历史最大）
var peak_air := 0.0            # 单连段峰值累计浮空
var combo_start_f := -1        # 当前连段首击帧
var combo_last_f := -1
var peak_combo_frames := 0     # 单连段峰值时长

func _init(skill_db) -> void:
	db = skill_db

func log_ev(ev: String, data: Dictionary = {}) -> void:
	var e := {"f": frame, "ev": ev}
	for k in data:
		e[k] = data[k]
	events.append(e)

func violation(rule: String, detail: String) -> void:
	violations.append({"f": frame, "rule": rule, "detail": detail})
	log_ev("VIOLATION", {"rule": rule, "detail": detail})

func setup(dummy_z: float = 2.2, ukemi: String = "none") -> void:
	ukemi_policy = ukemi
	frame = 0
	events.clear()
	violations.clear()
	world_orbs.clear()
	long_down.clear()
	fighters.clear()
	var p := Fighter.new(); p.id = "P"
	var d := Fighter.new(); d.id = "DUM"
	d.pos = Vector3(0, 0, dummy_z)
	fighters = [p, d]

func P() -> Fighter: return fighters[0]
func D() -> Fighter: return fighters[1]

# ================= 输入脚本（顺序执行，ASAP 语义 = 玩家连按）=================
var script_cmds := []
var script_idx := 0

func load_seq(cmds: Array) -> void:
	script_cmds = []
	for c in cmds:
		script_cmds.append({"cmd": c, "done": false})
	script_idx = 0

func dist_pd() -> float:
	var a := P().pos; var b := D().pos
	return Vector2(a.x, a.z).distance_to(Vector2(b.x, b.z))

func face_target(f: Fighter, t: Fighter) -> void:
	var dv := Vector3(t.pos.x - f.pos.x, 0, t.pos.z - f.pos.z)
	if dv.length() > 0.0001:
		f.facing = dv.normalized()

func can_cast(f: Fighter, def: Dictionary) -> bool:
	if f.cds.get(def["skill_id"], 0) > 0:
		return false
	if f.mp < SkillDB.fn_float(def["cost_mp"], 0.0):
		return false
	return true

func cancel_legal(cur: Dictionary, nxt: Dictionary) -> bool:
	# §8.2 技能取消→技能：命中确认后，后摇取消窗内可释放档位 ≥ 当前.cancel_min_tier 的技能
	var cmt: String = cur["cancel_min_tier"]
	if cmt == "none" or cmt == "-":
		return false
	if not TIER_RANK.has(nxt["tier"]):
		return false
	var minr: int = TIER_RANK.get(cmt.replace("+", ""), 1)
	return TIER_RANK[nxt["tier"]] >= minr

func try_act(f: Fighter, cmd: Dictionary) -> bool:
	var ty: String = cmd.get("type", "")
	if ty == "skill":
		var def: Dictionary = db.skills.get(cmd["id"], {})
		if def.is_empty():
			return false
		if def["skill_id"] == "BMG_T1_006" and f.orbs.is_empty():
			return false
		if f.state == "NORMAL":
			if not can_cast(f, def):
				return false
			start_skill(f, def)
			return true
		if f.state == "ACT" and f.act["phase"] == "recovery":
			# §4.4 命中后取消（D07 按 GDD 正文口径实现）
			if not f.act.get("hit_confirmed", false):
				return false
			var cur: Dictionary = f.act["def"]
			if not cancel_legal(cur, def):
				return false
			if not can_cast(f, def):
				return false
			var from_id: String = cur["skill_id"]
			start_skill(f, def)
			log_ev("CANCEL", {"from": from_id, "to": def["skill_id"]})
			return true
		return false
	if ty == "basic":
		if f.state == "NORMAL":
			start_basic(f)
			return true
		if f.state == "ACT":
			var cur: Dictionary = f.act["def"]
			if cur.get("type", "") == "basic":
				var su: int = f.act["su"]; var ac: int = f.act["ac"]
				if f.act["t"] > su + ac:  # 生效段结束（§8.2 段间缓冲）
					start_basic(f)
					return true
		return false
	if ty == "fc":
		# §10.4 强制中断：后摇立即结束；前摇/生效不可中断；受击不可用
		if f.state == "ACT" and f.act["phase"] == "recovery" and f.mp >= FC_MP and f.fc_cd <= 0:
			f.mp -= FC_MP
			f.fc_cd = FC_CD
			f.act = {}
			f.state = "NORMAL"
			log_ev("FORCE_CANCEL", {"who": f.id})
			return true
		return false
	if ty == "jump":
		if f.state == "NORMAL" and f.grounded():
			f.vel.y = JUMP_V
			f.pos.y = 0.001
			log_ev("JUMP", {"who": f.id})
			return true
		return false
	return false

func start_basic(f: Fighter) -> void:
	var n := 1
	if f.act.has("def") and f.act["def"].get("type", "") == "basic":
		n = mini(f.chain_n + 1, 5)
	start_skill(f, db.skills["BMG_BAS_00%d" % n], n)

func start_skill(f: Fighter, def: Dictionary, chain: int = 0) -> void:
	var id: String = def["skill_id"]
	f.mp -= SkillDB.fn_float(def["cost_mp"], 0.0)
	f.cds[id] = int(round(SkillDB.fn_float(def["cooldown_s"], 0.0) * 60))
	f.chain_n = chain
	face_target(f, D() if f == P() else P())
	# 攻速 buff 作用于前摇（假设：光炫纹 +4%/档，报告记录）
	var su := SkillDB.fn_int(def["startup_f"], 0)
	var aspd := 0
	for b in f.buffs:
		if b["type"] == "atk_spd":
			aspd += 1
	if aspd > 0:
		su = maxi(1, int(round(su * (1.0 - 0.04 * aspd))))
	var ac := SkillDB.parse_active(def["active_f"])
	var rc := SkillDB.fn_int(def["recovery_f"], 0)
	f.state = "ACT"
	f.act = {"def": def, "t": 0, "su": su, "ac": ac, "rc": rc, "phase": "startup",
		"hit_confirmed": false, "hits_done": {}, "delay_done": false, "id": id}
	# 炫纹发射：消耗 1 枚炫纹 → 自动追踪弹（无法躲闪，命中延迟 = 距离/弹速）
	if id == "BMG_T1_006":
		var orb: Dictionary = f.orbs.pop_front()
		var travel := int(ceil(dist_pd() / 20.0 * 60.0)) + 1
		world_orbs.append({"f_hit": frame + travel, "mult": SkillDB.fn_float(def["damage_mult"], 0.7), "type": orb["type"]})
		f.buffs.append({"type": orb["type"] + "_buff", "left": 1200, "power": 1})
		log_ev("ORB_FIRE", {"type": orb["type"], "travel_f": travel, "orbs_left": f.orbs.size()})
	log_ev("CAST", {"who": f.id, "id": id})

# ================= 每帧推进 =================
func step() -> void:
	frame += 1
	_process_script()
	for f in fighters:
		_tick_fighter(f)
	_tick_orbs()
	for ph in pending_hits.duplicate():
		if frame >= int(ph["due"]):
			pending_hits.erase(ph)
			_try_hit(ph["src"], ph["def"], 99)
	var d := D()
	if d.control > CONTROL_MAX and d.state != "BREAK":
		violation("G4_BREAK_MISSING", "控制值 %.0f >100 未挣脱" % d.control)

func _process_script() -> void:
	while script_idx < script_cmds.size():
		var item: Dictionary = script_cmds[script_idx]
		if item["done"]:
			script_idx += 1
			continue
		var cmd: Dictionary = item["cmd"]
		var p := P()
		# 自动走位：NORMAL 且超出射程 → 本帧用于移动（真实耗时）
		if p.state == "NORMAL" and p.grounded():
			var need := 2.4
			if cmd["type"] == "skill":
				var def: Dictionary = db.skills.get(cmd["id"], {})
				if def.is_empty():
					item["done"] = true
					log_ev("SKIP_UNKNOWN", cmd)
					continue
				need = SkillDB.fn_float(def["range_m"], 2.0) * 0.85
			if dist_pd() > need:
				var dir := D().pos - p.pos; dir.y = 0
				p.facing = dir.normalized()
				p.pos += p.facing * MOVE_SPEED / FPS
				return
		if try_act(p, cmd):
			item["done"] = true
			script_idx += 1
		return  # 不可执行或已执行：下帧再试/推进

func _tick_fighter(f: Fighter) -> void:
	f.mp = minf(MP_MAX, f.mp + MP_REGEN)
	f.stam = minf(STAM_MAX, f.stam + 10.0 / FPS)
	if f.fc_cd > 0:
		f.fc_cd -= 1
	for id in f.cds.keys():
		if f.cds[id] > 0:
			f.cds[id] -= 1
	if f.protect_t > 0:
		f.protect_t -= 1
	for k in f.statuses.keys():
		f.statuses[k] -= 1
		if f.statuses[k] <= 0:
			f.statuses.erase(k)
	match f.state:
		"ACT":
			_tick_act(f)
			_move(f)
		"HITSTUN":
			f.state_t -= 1
			if f.state_t <= 0:
				f.state = "NORMAL"
				_reset_victim(f)
			_move(f)
		"LAUNCH":
			f.air_time += 1.0 / FPS
			if f.air_time >= FLOAT_LIMIT and not f.forced_fall:
				f.forced_fall = true
				f.vel.y = minf(f.vel.y, -12.0)
				log_ev("FLOAT_PROTECT", {"who": f.id, "air": f.air_time})
			f.vel.y -= GRAVITY / FPS
			f.pos.y += f.vel.y / FPS
			_move(f)
			if f.pos.y <= 0.0:
				f.pos.y = 0.0
				f.vel = Vector3.ZERO
				_land(f)
		"DOWN":
			f.down_t += 1
			if ukemi_policy == "f20" and f.down_t == UKEMI_WINDOW and not f.no_ukemi and f.stam >= UKEMI_STAM:
				f.stam -= UKEMI_STAM
				f.state = "GETUP"; f.state_t = GETUP_FRAMES
				log_ev("UKEMI", {"who": f.id})
			elif f.down_t >= (DOWN_LONG if long_down.get(f.id, false) else DOWN_FRAMES):
				f.state = "GETUP"; f.state_t = GETUP_FRAMES
				log_ev("GETUP_START", {"who": f.id})
			_move(f)
		"GETUP":
			f.state_t -= 1
			if f.state_t <= 0:
				f.state = "NORMAL"
				f.protect_t = 60  # 起身保护 1s（§5.7）
				_reset_victim(f)
				log_ev("GETUP_DONE", {"who": f.id})
		"GRABBED":
			f.state_t -= 1
			if f.state_t <= 0:
				long_down[f.id] = false
				f.no_ukemi = false
				f.state = "DOWN"; f.down_t = 0
				log_ev("GRAB_RELEASE", {"who": f.id})
		"BREAK":
			f.state_t -= 1
			if f.state_t <= 0:
				f.state = "NORMAL"
		"DEAD":
			pass
		"NORMAL":
			f.control = maxf(0.0, f.control - CONTROL_DECAY)
			_move(f)

func _land(f: Fighter) -> void:
	var dur := DOWN_LONG if long_down.get(f.id, false) else DOWN_FRAMES
	long_down[f.id] = false
	f.state = "DOWN"; f.down_t = 0; f.state_t = dur
	log_ev("LAND", {"who": f.id, "down": dur, "air": snappedf(f.air_time, 0.01)})

func _reset_victim(f: Fighter) -> void:
	f.hitstun_n = 0; f.launch_n = 0; f.air_time = 0.0
	f.forced_fall = false; f.dmg_combo = 0.0; f.no_ukemi = false
	f.grabbed_by = ""
	if combo_last_f > 0 and combo_start_f > 0:
		peak_combo_frames = maxi(peak_combo_frames, combo_last_f - combo_start_f)
	combo_start_f = -1
	combo_last_f = -1
	epoch += 1

func _move(f: Fighter) -> void:
	if f.vel.length() > 0.01 and f.state != "LAUNCH":
		f.pos += f.vel / FPS
		f.vel *= KB_FRICTION
		if f.vel.length() < 0.05:
			f.vel = Vector3.ZERO
	elif f.state == "LAUNCH" and f.vel.length() > 0.01:
		f.pos.x += f.vel.x / FPS
		f.pos.z += f.vel.z / FPS
		f.vel.x *= KB_FRICTION
		f.vel.z *= KB_FRICTION
	# 场地边界（圆）
	var r2 := Vector2(f.pos.x, f.pos.z)
	if r2.length() > ARENA_R:
		var n := r2.normalized()
		f.pos.x = n.x * ARENA_R
		f.pos.z = n.y * ARENA_R
		var vn: float = f.vel.x * n.x + f.vel.z * n.y
		if vn > 0:
			f.vel.x -= 1.6 * vn * n.x  # 反弹 ×0.6（§5.8）
			f.vel.z -= 1.6 * vn * n.y
			if f.state == "HITSTUN":
				f.state_t += WALL_HITSTUN
			log_ev("WALL", {"who": f.id})

func _tick_act(f: Fighter) -> void:
	var a: Dictionary = f.act
	a["t"] += 1
	var t: int = a["t"]
	var su: int = a["su"]; var ac: int = a["ac"]; var rc: int = a["rc"]
	if t <= su:
		a["phase"] = "startup"
	elif t <= su + ac:
		a["phase"] = "active"
	else:
		a["phase"] = "recovery"
		if t > su + ac + rc:
			# 延迟爆发：动作结束后仍待结算（斗破山河 延迟0.8s）
			if not a.get("delay_done", false) and SkillDB.sp_has(a["def"], "延迟"):
				var sp2: String = a["def"]["special"]
				var di2: int = sp2.find("延迟:")
				var seg2: String = sp2.substr(di2 + 3).split(";")[0]
				var dl := 48
				if seg2.ends_with("s"):
					dl = int(seg2.substr(0, seg2.length() - 1).to_float() * 60)
				pending_hits.append({"due": frame + (su + ac + dl - t), "def": a["def"], "src": f})
			f.act = {}
			f.state = "NORMAL"
			log_ev("ACT_END", {"who": f.id, "id": a["id"], "at": t})
			return
	var def: Dictionary = a["def"]
	# 命中判定（延迟爆发技跳过 active 窗口判定；投射类由弹道系统结算）
	if a["phase"] == "active" and not SkillDB.sp_has(def, "延迟") and not def["hitbox"].begins_with("proj"):
		var sched: Array = SkillDB.hit_schedule(def)
		for k in sched.size():
			if t == su + 1 + int(sched[k]) and not a["hits_done"].has(k):
				a["hits_done"][k] = true
				_try_hit(f, def, k)
	# 延迟命中（斗破山河：延迟0.8s 在后摇内爆发）
	if not a.get("delay_done", false) and SkillDB.sp_has(def, "延迟"):
		var sp: String = def["special"]
		var di: int = sp.find("延迟:")
		var seg: String = sp.substr(di + 3).split(";")[0]
		var delay_f := 48
		if seg.ends_with("s"):
			delay_f = int(seg.substr(0, seg.length() - 1).to_float() * 60)
		if t == su + ac + delay_f:
			a["delay_done"] = true
			_try_hit(f, def, 99)

func _try_hit(f: Fighter, def: Dictionary, k: int) -> void:
	var v := D() if f == P() else P()
	if v.state in ["GETUP", "BREAK", "DEAD"]:
		return
	# §7.2：被抓取者免受其他伤害（抓取者自己的连段除外）
	if v.state == "GRABBED" and v.grabbed_by != def["skill_id"]:
		return
	if v.state == "DOWN" and SkillDB.fn_int(def["sweep"], 0) != 1:
		log_ev("WHIFF_DOWN", {"atk": def["skill_id"]})
		return
	var d := dist_pd()
	var rng: float = SkillDB.fn_float(def["range_m"], 2.0)
	var air_ok: bool = (not v.grounded()) and v.pos.y <= MELEE_AIR_REACH
	if not (v.grounded() or air_ok or v.state == "DOWN"):
		return
	if d > rng:
		log_ev("WHIFF_RANGE", {"atk": def["skill_id"], "d": snappedf(d, 0.1), "rng": rng})
		return
	if def["hitbox"].begins_with("fan"):
		var fa := SkillDB.fn_int(def["angle_deg"], 90)
		if _angle_to(f, v) > fa / 2.0:
			log_ev("WHIFF_ANGLE", {"atk": def["skill_id"]})
			return
	# ---- 命中 ----
	var a: Dictionary = f.act
	if not a.get("hit_confirmed", false):
		a["hit_confirmed"] = true
		log_ev("FIRST_HIT", {"id": def["skill_id"]})
	v.hitstun_n += 1
	var hn: int = v.hitstun_n
	var mult: float = SkillDB.fn_float(def["damage_mult"], 0.0)
	if def["type"] == "grab":
		mult *= 0.5  # 假设：U 抓取两段各取总倍率一半（一段刺杀/二段爆炸）
	var dmg: float = mult * f.atk * DEF_COEF
	if not v.grounded():
		dmg *= AIR_MOD
	if SkillDB.sp_has(def, "对空") and not v.grounded():
		dmg *= 1.15
	if v.state == "DOWN":
		dmg *= SWEEP_MOD
	if v.protect_t > 0:
		dmg *= 0.9
	if hn >= DMG_DECAY_FROM:
		dmg *= pow(DMG_DECAY, hn)
	v.hp -= dmg
	v.dmg_combo += dmg
	f.mp = minf(MP_MAX, f.mp + MP_HIT)
	v.mp = minf(MP_MAX, v.mp + MP_HURT)
	# 炫纹
	var ot := SkillDB.orb_type(def)
	if ot != "" and f.orbs.size() < 7:
		f.orbs.append({"type": ot, "expire": frame + 1800})
		log_ev("ORB", {"type": ot, "n": f.orbs.size()})
	# 异常/控制值
	for st in SkillDB.parse_status(def["status"]):
		var ty2: String = st["type"]
		var dur: String = st["dur"]
		v.statuses[ty2] = int(dur.to_float() * 60) if dur.ends_with("s") else 60
		v.control = minf(v.control + CTRL_ADD.get(ty2, 0.0), 120.0)
	if combo_start_f < 0:
		combo_start_f = frame
	combo_last_f = frame
	peak_dmg = maxf(peak_dmg, v.dmg_combo)
	peak_air = maxf(peak_air, v.air_time)
	log_ev("HIT", {"id": def["skill_id"], "seg": k, "dmg": snappedf(dmg, 0.1), "hn": hn,
		"vst": v.state, "y": snappedf(v.pos.y, 0.01), "ep": epoch})
	if v.hp <= 0:
		v.state = "DEAD"
		log_ev("DEAD", {"who": v.id})
		return
	# 受击结果
	var kb: float = SkillDB.fn_float(def["knockback_m"], 0.0)
	var lv: float = SkillDB.fn_float(def["launch_v"], 0.0)
	var kd: bool = SkillDB.sp_has(def, "强制倒地")
	var grab: bool = def["type"] == "grab" or SkillDB.sp_has(def, "抓取")
	if lv > 0:
		if v.state == "LAUNCH":
			if v.forced_fall:
				log_ev("NO_RELAUNCH", {"id": def["skill_id"]})  # 落地保护：不再击起
			else:
				v.launch_n += 1
				var raw: float = lv * pow(LAUNCH_DECAY, v.launch_n)
				var nv: float = maxf(raw, LAUNCH_FLOOR)
				v.vel.y = nv
				if raw < LAUNCH_FLOOR:
					log_ev("LAUNCH_FLOOR", {"n": v.launch_n, "raw": raw})
				log_ev("RELAUNCH", {"n": v.launch_n, "v": nv})
		else:
			v.launch_n = 0
			v.air_time = 0.0
			v.forced_fall = false
			v.vel.y = lv
			v.vel.x = 0; v.vel.z = 0
			v.state = "LAUNCH"
			log_ev("LAUNCH", {"v": lv})
	elif grab:
		v.state = "GRABBED"
		v.state_t = 60
		v.grabbed_by = def["skill_id"]
		v.vel = Vector3.ZERO
		log_ev("GRAB", {"who": v.id})
	elif kd:
		long_down[v.id] = false
		v.no_ukemi = SkillDB.sp_has(def, "受身无效")
		v.state = "DOWN"; v.down_t = 0
		v.pos.y = 0; v.vel = Vector3.ZERO
		log_ev("FORCED_DOWN", {"id": def["skill_id"], "ukemi_ineffective": v.no_ukemi})
	else:
		var hs: float = SkillDB.fn_float(def["hitstun_f"], 12)
		if kb > 0:
			hs = maxf(hs, 12)  # §5.1 击退=硬直+位移
		hs *= pow(HITSTUN_DECAY, hn)
		hs = maxf(hs, 12.0 * HITSTUN_FLOOR)
		if v.state != "LAUNCH" and v.state != "DOWN":
			v.state = "HITSTUN"
			v.state_t = int(ceil(hs))
		if kb > 0:
			var dir := v.pos - f.pos; dir.y = 0
			if dir.length() > 0.01:
				v.vel += dir.normalized() * kb * KBDIST_VEL
	# 控制值挣脱（§7.4/§8.5④）
	if v.control >= CONTROL_MAX and v.state != "BREAK":
		v.control = 0.0
		v.state = "BREAK"
		v.state_t = BREAK_FRAMES
		v.vel = Vector3.ZERO
		log_ev("BREAK", {"who": v.id})

func _angle_to(a: Fighter, b: Fighter) -> float:
	var fwd := Vector2(a.facing.x, a.facing.z)
	var to := Vector2(b.pos.x - a.pos.x, b.pos.z - a.pos.z)
	if to.length() < 0.001:
		return 0.0
	return absf(rad_to_deg(fwd.angle_to(to)))

# ================= 炫纹发射弹 =================
func _tick_orbs() -> void:
	for o in world_orbs:
		if frame >= int(o["f_hit"]) and not o.get("hit", false):
			o["hit"] = true
			var v := D()
			if v.state != "DEAD":
				var dmg: float = o["mult"] * P().atk * DEF_COEF
				v.hp -= dmg
				v.dmg_combo += dmg
				log_ev("ORB_HIT", {"dmg": snappedf(dmg, 0.1), "type": o["type"]})
	world_orbs = world_orbs.filter(func(o): return not o.get("hit", false))
