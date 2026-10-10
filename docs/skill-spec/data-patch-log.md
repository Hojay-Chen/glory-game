# Skill-Spec 数据补丁日志（provenance 记录）

> 本日志记录 skills.csv 的每一次纯格式规范化。语义类修改（数值/机制）走平衡补丁流程，不在本日志。
> provenance：完整历史由 git 追踪（本文件与 skills.csv 同提交）；dataVersionHash 变化由 ADR-0002 §5.2 机制自然生效。

## PATCH-001（2026-09-01）：格式规范化（Pre-Implementation Closure，语义零变更）

| # | skill_id | 技能 | 列 | 旧值 | 新值 | 理由 |
|---|---|---|---|---|---|---|
| 1 | BLA_T1_002 | 格挡 | active_f | `维持` | `hold` | 纯格式：形态词正名 维持→hold（语义 1:1，ADR-0002 §4.1） |
| 2 | BLA_T3_002 | 逆风刺 | active_f | `可控` | `controlled` | 纯格式：形态词正名 可控→controlled（语义 1:1，ADR-0002 §4.1） |
| 3 | SBL_U_001 | 星云波动剑 | active_f | `可控` | `controlled` | 纯格式：形态词正名 可控→controlled（语义 1:1，ADR-0002 §4.1） |
| 4 | STR_T3_001 | 空手入白刃 | invincible_f | `invincible:8-16f` | `8-16f` | 纯格式：剥离冗余前缀 invincible: |
| 5 | QIM_T3_002 | 念龙波 | active_f | `可控` | `controlled` | 纯格式：形态词正名 可控→controlled（语义 1:1，ADR-0002 §4.1） |
| 6 | GRP_T2_001 | 接投 | invincible_f | `invincible:10-20f` | `10-20f` | 纯格式：剥离冗余前缀 invincible: |
| 7 | ROG_T4_001 | 以牙还牙 | invincible_f | `invincible:0f` | `0f` | 纯格式：剥离冗余前缀 invincible: |
| 8 | ASN_T3_001 | 瞬身刺 | invincible_f | `invincible:8-14f` | `8-14f` | 纯格式：剥离冗余前缀 invincible: |
| 9 | ASN_T3_002 | 闪烁突刺 | invincible_f | `invincible:6-12f` | `6-12f` | 纯格式：剥离冗余前缀 invincible: |
| 10 | THF_T1_001 | 潜行 | active_f | `持续` | `hold` | 纯格式：形态词正名 持续→hold（语义 1:1，ADR-0002 §4.1） |
| 11 | THF_T2_003 | 脱逃 | active_f | `持续` | `hold` | 纯格式：形态词正名 持续→hold（语义 1:1，ADR-0002 §4.1） |
| 12 | NJA_T3_002 | 地心斩首术 | invincible_f | `invincible:地底16-146f` | `16-146f` | 纯格式：剥离前缀与状态词（地底语义见 special「地底潜伏」） |
| 13 | NJA_T3_005 | 忍法·替身术 | invincible_f | `invincible:6-12f` | `6-12f` | 纯格式：剥离冗余前缀 invincible: |
| 14 | NJA_T3_006 | 忍法·乱身冲 | invincible_f | `invincible:8-20f` | `8-20f` | 纯格式：剥离冗余前缀 invincible: |
| 15 | WRK_T3_004 | 操纵术 | active_f | `持续` | `hold` | 纯格式：形态词正名 持续→hold（语义 1:1，ADR-0002 §4.1） |
| 16 | PRI_T2_005 | 净化 | invincible_f | `invincible:0-6f` | `0-6f` | 纯格式：剥离冗余前缀 invincible: |
| 17 | EXO_T1_003 | 加速符 | hitbox | `self/-` | `self` | 纯格式：剥离 - 尾缀（加速符=自身增益，self 语义唯一） |
| 18 | EXO_T2_007 | 魂御 | active_f | `飞行` | `projectilePhase` | 纯格式：形态词正名 飞行→projectilePhase（语义 1:1，ADR-0002 §4.1） |
| 19 | UNS_T1_001 | 万象切换 | startup_f | `15(0.25s)` | `15` | 纯格式：剥离括号注释（15T≡0.25s 自明） |

未修改项（待用户裁定，见 implementation-readiness-audit-v2 §B-1）：
- `SA:12-26s`（BER_T3_004 狂暴）——单位歧义 fail-fast，OQ-2
- `invincible:0f`——零宽窗口意图待裁定，OQ-5
- hitbox 自由文本 2 行（星云波动剑「形态三选」/乱雷「自身全部手雷种类」）——运行时决定判定体语义待裁定，OQ-13
- 裸「几率」4 行（寒冰粉/巴雷特/毒针/黑暗之爪）——数值待补录，OQ-6

| 20 | SPF (class-base) | resource 列 | `弹匣+手雷库存` | `弹匣:20` | 格式归一: prose→结构化（BMG `炫纹:7` 同律）；有效 cap 20 = GDD §14.5.2 弹匣 15 发 + 弹药扩充转职被动（常驻）→20；基线 15 保留于 skills.csv SPF_BAS_001 special。手雷库存独立资源 v1 不接入（乱雷语义后续批次）。PATCH-002 裁定源: 用户 Batch 5 资源闭环方向 2026-09-03 |
| 21 | BER_U_001 | 嗜血奋战 | armor | `SSA:24-244f` | `SSA:24-1224f` | 数据修正:D13 裁定书附录——GDD 两处均写「**全程霸体**」(§14 觉醒行「比狂暴强 25%+全程霸体」/ 技能详述「全程霸体」),而该技 `active = 20s`(1200T);原 `24-244f`(≈4.07s)与之相差 5 倍,判定为 CSV 笔误。修正后霸体窗 = 24–1224f(≈20s)覆盖全程。用户 2026-10-11 裁定「按推荐执行」;此项影响 41 个 buff 技的同类判定口径,故单列。 |
| 22 | WRK_T3_006 | 鬼影缠身 | status | `附身DoT` | `poison:45:6s` | 数值补录:D5 裁定——原 status 只有机制名、**无每秒伤害**。按 §7.3 DoT 带取 T3 中位 **45/s**（全表 28 条 DoT 值域 20–60、中位 45；T3 簇 40–60）；时长取该技 `active = 6s`（原著「时间内附身目标制造伤害」）。元素归属取 **Poison**（术士暗系无对应元素；引擎内 Burn/Bleed/Poison 三者同律，仅 Burn 额外与冰冻互斥，故选不参与互斥的 Poison）。格式归一: prose→结构化（PATCH-002 同律）。**未落地**: 原著「结束后 33% 伤害补充为术士生命」——属独立机制，已登记待办。 |
| 23 | WRK_T4_003 | 幽魂缠绕 | status | `slow:强;禁闪现` | `slow:50%:8s;禁闪现` | 数值补录:D7 裁定——`强` 非数值，`ParsePercent` 解析失败 → Potency=0 → **该技实际完全不减速**（与「虚弱」同类静默缺陷）。按 §7.3「减速 −30%/−50%（两档）」取**强档 −50%**（原著「极强烈的减速效果」）；时长取该 zone 存在期 8s。`禁闪现` 保留为无操作（本作无闪现系统，D7 已改判为「区域内打断位移类技能」，待落地）。 |
