# Skill-Spec 技能规格书 — 规范与字段字典

| 项 | 值 |
|---|---|
| 版本 | v0.1（对应 GDD v0.3） |
| 日期 | 2026-08-29 |
| 组成 | 本规范 + `skills.csv`（主数据，唯一事实源）+ `实现注记.md`（特殊规则语义）+ `validation-report.md`（结构校验） |
| 上游 | GDD `docs/GDD-Gameplay-v0.1.md`（§14 职业设计、§17 数据规范、§28 命名规范） |
| 考据 | `docs/reference/荣耀职业介绍-ycyoc-wiki.md`（技能名与机制） |

---

## 1. 文档体系与使用方式

```
GDD §14（设计意图：为什么是这个技能、连招定位、克制关系）
        ↓ 拆解
skills.csv（实现参数：程序直接消费的唯一数值源）   ← 本目录
实现注记.md（特殊规则的实现语义：判定时机/边界/交互）  ← 本目录
        ↓ 导入
引擎数据表（禁止硬编码，GDD §28.1）
```

- **改数值 → 只改 CSV**，走平衡补丁流程（GDD §25.4）
- **改规则/加技能 → 先改 GDD/实现注记，再同步 CSV**
- 程序/QA 以 CSV 为准；GDD 数值与 CSV 冲突时，**CSV 优先**并在 validation-report 记录差异

## 2. ID 规则（GDD §28.2）

```
<职业缩写>_<档位>_<序号>     例：BMG_T1_001
普攻段：  <职业缩写>_BAS_00<段号>
被动：    <职业缩写>_PAS_001
散人引用：<职业缩写>_REF_0<序号>（special 列写 ref:<原技能ID>:<降档系数>）
```

职业缩写：BMG 战斗法师 / ELE 元素法师 / SUM 召唤师 / WIT 魔道学者 / BLA 剑客 / BER 狂剑士 / SBL 魔剑士 / GBL 鬼剑士 / SRP 神枪手 / SPF 弹药师 / LAU 枪炮师 / MEH 机械师 / STR 拳法家 / QIM 气功师 / GRP 柔道家 / ROG 流氓 / ASN 刺客 / THF 盗贼 / NJA 忍者 / WRK 术士 / PRI 牧师 / GAN 守护天使 / KNI 骑士 / EXO 驱魔师 / UNS 散人

序号规则：同职业内按 档位（BAS→T1→T2→T3→T4→U→PAS）再按 GDD §14 表内出现顺序编 001、002…

## 3. 字段字典（38 列）

### 3.1 标识

| 列 | 类型 | 说明 |
|---|---|---|
| skill_id | string | 唯一 ID，见 §2 |
| skill_name | string | 中文名（原著考据名优先） |
| class_id | enum | 职业缩写（§2） |
| tier | enum | BAS / T1 / T2 / T3 / T4 / U / PAS / REF |
| type | enum | basic(普攻段) / active / stance(形态·附着切换) / summon(召唤单位) / deploy(部署物) / grab(抓取) / counter(反击架势) / buff / passive / ref(散人引用) |

### 3.2 消耗与冷却

| 列 | 类型 | 说明 |
|---|---|---|
| cost_mp | int | MP 消耗；0 表示无 |
| cost_hp | int | HP 消耗（血价/舍命类）；百分比用 `10%` 写法 |
| cooldown_s | float | CD 秒；0=无 CD；`公共1.5` 写法表示切换公共 CD |

### 3.3 帧数据（60fps 整数帧，GDD §2.2）

| 列 | 类型 | 说明 |
|---|---|---|
| startup_f | int | 前摇帧；Channel/部署物为「展开帧」；被动留空 |
| active_f | int | 生效帧；多段用 `3x8`（每段3帧×8段）；持续型用秒 `2.5s` |
| recovery_f | int | 后摇帧 |
| hit_interval_f | int | 多段技段间隔帧（单段留空） |

### 3.4 判定体

| 列 | 类型 | 说明 |
|---|---|---|
| hitbox | string | 形状紧凑记法：`fan:r2.6:a100`（扇形 半径/角度）、`box:1.5x1.2x2.4`、`circle:r3`、`cyl:r2.5:h5`、`line:12`（直线长 m）、`proj:30m/s:15m`（弹道 速度/射程）、`self`（自身）、`unit`（召唤单位）、`zone:r4:6s`（区域 持续）、`aura:r5`（光环）、`none`（增益类） |
| range_m | float | 有效攻击距离（m） |
| angle_deg | int | 扇形角度（非扇形留空） |

### 3.5 伤害与控制

| 列 | 类型 | 说明 |
|---|---|---|
| damage_mult | float | 相对 ATK 倍率；多段 `0.30x2`；固定治疗/护盾值直接写数值并注明 |
| damage_type | enum | phys / magic / fire / ice / light / dark / heal / shield / none |
| hits | int | 判定段数（含普攻段=1） |
| hitstun_f | int | 命中硬直帧 |
| knockback_m | float | 击退距离 |
| launch_v | float | 浮空初速 m/s（6.5–8.5 区间，天击 7.5 为标准） |
| status | string | 附加异常，紧凑记法：`burn:60:4s` `bleed:60:4s` `freeze:1.0s` `slow:30%:3s` `stun:1.2s` `root:1.8s` `blind:2.5s` `sleep:2.5s` `confuse:3s` `taunt:3s` `silence:2s` `seal:3s` `curse` `weight:x2` `none` |
| armor | enum | none / SA:<帧区间>（霸体）/ SSA（超级霸体）/ superSA:buff（状态型） |
| invincible_f | string | 无敌帧区间如 `4-14f`；无留空 |
| sweep | 0/1 | 【扫地】可命中倒地 |
| intercept | 0/1 | 投射物【可拦截】 |
| channel | 0/1 | 持续施放（硬直可打断） |

### 3.6 取消与特殊

| 列 | 类型 | 说明 |
|---|---|---|
| cancel_min_tier | enum | 命中后可被更高档取消的最低档：`T1+`/`T2+`/`T3+`/`T4+`/`none`/`any`（随时可用） |
| jump_cancel | 0/1 | 【跳取消】 |
| special | string | 特殊规则关键词（分号分隔，语义见实现注记.md）：`炫纹:光` `抓取` `受身无效` `破霸体` `无视霸体` `穿透` `追踪:90°/s` `蓄力:0.8s:+40%` `延迟:0.5s` `部署:12s` `结界` `幻影:2-5` `要害:200%` `ref:BMG_T1_001:0.9` 等 |
| notes | string | 备注（含设计出处/待定项标记 `TODO:`） |

### 3.7 资源引用（派生列，脚本生成）

| 列 | 说明 |
|---|---|
| animation | 按 §28.2 规则：`AM_<职业>_<技能拼音或英文缩写>` |
| vfx | `VX_<职业>_<缩写>[_HIT]` |
| sound | `SD_<职业>_<缩写>[_HIT]` |

> v0.1 阶段三列由脚本按 ID 派生占位，正式动作命名在白盒期（GDD §28.3）确定后回填。

## 4. 填写规范

1. **数字一律用半角**；区间用 `min-max`；倍率多段用 `x`（`0.30x2`）
2. 空值写 `-`（不用空字符串，便于校验）
3. 所有与 GDD v0.3 不同的数值（拆解时发现的设计缺口补全）必须在 notes 标 `GDD-GAP` 并进 validation-report
4. 所有凭平衡直觉拟定、GDD 未定的数值标 `TODO:balance`
5. 抓取技（type=grab）固定规则：无视普通霸体、不无视无敌、对倒地不可抓（GDD §14.15.3），special 写 `抓取` 即继承
6. 反击技（type=counter）固定规则：架势帧=前摇区间，被命中转专属反击；空放惩罚见各技能 notes

## 5. QA 结构校验（validate_skills.py 自动执行）

1. skill_id 全局唯一、格式合法
2. tier ∈ 枚举；type ∈ 枚举；职业缩写合法
3. 帧数据为非负整数（active 允许 `NxM`/秒写法）
4. launch_v ∈ [5.5, 9.5]；天击=7.5 标准参照（GDD §14.1）
5. 每职业 U 档 ≤1（散人 0）；被动 ≥1
6. 主动技 cost_mp 与 cooldown_s 必填
7. 【受身无效】仅允许：圆舞棍/背摔/踏射/空中灌篮/强龙压（GDD §5.6 白名单）
8. 红线检查（GDD §28.5）：硬控 >1.5s 白名单（绝对零度/六星光牢/催眠/螺旋念气杀逆时针）；理论连段伤害由 QA 连段审计另行覆盖
9. 每职业技能池计数 → 携带上限 10（散人 12）的构筑压力报告

## 6. 版本与变更

- CSV 变更 = 平衡补丁，需在 validation-report 追加变更记录（日期/范围/原因）
- v0.1 范围：GDD v0.3 全部技能的结构化拆解 + 缺口补全标记；v0.2 计划：动作资源回填、连段审计用例、白盒手感动效验证
