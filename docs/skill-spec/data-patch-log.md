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
