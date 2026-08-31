# SPEC-0004 — ArenaDef Canonical 事实源规范（F-7 闭合）

> 本文件是 **ADR-0002 的 Implementation Specification Appendix**（implementation-readiness-audit-v1 C-5/F-7）。只建立事实源与 schema；**不实现 TerrainSystem**（归 Phase 3）。

## 1. 事实源

- **Canonical 文件**：`docs/balance-sheet/arena.csv`（百炼竞技场 ARENA001，唯一地图，D17）
- **语义上游**：GDD §19.1–19.7（布局/尺寸/HP/交互规则）；坐标缺失处标 `TODO-GAP`
- **纳入 dataVersionHash**（ADR-0002 §5.2 输入清单增补第 4 个 CSV——见 ADR-0002 Errata）
- **Compiler 管线**：与其他三 CSV 同走 Parse→Canonical→Validate→Quantize→RuntimeDef→JSON/.res

## 2. Schema（13 列）

| 列 | 类型 | 说明 |
|---|---|---|
| arena_id | string | 当前恒 `ARENA001` |
| object_id | string | `A_<kind><序号>`，全局唯一 |
| kind | enum | `boundary` 结界墙 / `platform` 高台·擂台 / `ramp` 坡道 / `cover_wall` 掩体墙 / `pillar` 立柱 / `prop_wood` 木箱 / `prop_pot` 陶罐 / `prop_rock` 石块 / `spawn` 出生点 |
| shape | enum | `rect`（x,z 为中心，后两列为半宽/半深）/ `circle`（r 列）/ `point` |
| x_m / z_m | Fixed(m) | 中心坐标（原点=中央擂台圆心；北=+Z）；`TODO-GAP` 待美术白盒定精确位 |
| r_m | Fixed(m) | 圆形半径（shape=circle） |
| half_w_m / half_d_m | Fixed(m) | 矩形半宽/半深（shape=rect） |
| height_m | Fixed(m) | 顶面高度（0=地面） |
| hp | int | 可破坏物耐久；0=不可破坏 |
| interaction | enum | `bounce`（击飞反弹 §5.8）/ `block_los_proj`（挡视线/弹道）/ `fall_edge`（台缘坠落 §3.5）/ `restore_mp` / `none` |
| notes | string | 出处/TODO-GAP 标记 |

## 3. 当前数据（v1，源自 GDD §19.2–19.5；坐标推定值标 `TODO-GAP`）

| object_id | kind | shape | x | z | r/半宽 | 半深 | 高 | HP | interaction |
|---|---|---|---|---|---|---|---|---|---|
| A_boundary | boundary | rect | 0 | 0 | 30 | 42 | 0 | 0 | bounce |
| A_platform_center | platform | circle | 0 | 0 | 8 | — | 1.2 | 0 | fall_edge |
| A_platform_north / A_platform_south | platform | rect | 0 / 0 | ±28 | 6 | 4 | 3 | 0 | fall_edge |
| A_ramp_north / A_ramp_south | ramp | rect | 0 / 0 | ±14.5 | 2 | 4 | — | 0 | none（30° 坡道通行） |
| A_cover_wall_east/west ×2 | cover_wall | rect | ±22 | 0 | 1.2 | 4 | 2.4 | **800** | block_los_proj |
| A_pillar_ne/nw/se/sw ×4 | pillar | circle | ±18 | ±18 | 0.8 | — | 4 | 0 | block_los_proj（特殊技能可碎：每局一次，§19.5） |
| A_wood_ne/nw/se/sw ×4 | prop_wood | rect | ±26 | ±34 | 0.5 | 0.5 | 1.5 | **200** | block_los_proj（碎后挡视线 4s） |
| A_pot_ne/nw/se/sw ×4 | prop_pot | point | ±27 | ±36 | — | — | 0 | **1** | restore_mp（5% MP，r2m 拾取） |
| A_rock ×2 | prop_rock | rect | ±26 | ±22 | 0.8 | 0.8 | 1.2 | 0 | block_los_proj |
| A_spawn_1v1_n/s | spawn | point | 0 / 0 | ±4 / ±4 | — | — | 1.2 | — | none（中央擂台对角对称） |
| A_spawn_team_n/s | spawn | point | TODO-GAP | ±38 | — | — | 0 | — | none（5v5 布局待 §20.4 细化） |

（实际 CSV 一行一对象；上表为可读投影。）

## 4. 校验规则（并入 Compiler L2）

- boundary 唯一且 interaction=bounce
- spawn 成对对称（x/z 取反集合相等——无死角公平 §19.1-2）
- 可破坏物 hp>0 必有 interaction；platform/ramp 高度 >0 时必须存在可达 ramp（连通性，v1 人工审核）
- 全部 Fixed 量化走 ADR-0001 §1.2（米→Q32.16）
