# Migration Fidelity Audit v1 — Pilot 真实技能迁移保真度审计

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-09-02 |
| 基线 | master（Phase 5 Capability Closure 后） |
| 性质 | **Pilot 迁移保真度审计**——以真实 CSV 数据验证 Data → Compiler → Runtime → Sim 完整闭环；规模底数测定 |
| 结论 | **Verdict A（有条件）**：全表 483 行 Compiler 保真度门禁通过、89% 行全字段路由、确定性/Replay 无退化 → **487 技批量迁移可启动**；54 项 Partial（11%）按既定依赖序（UnitSystem→deploy/ally/wall → 签名插件）随批次收口 |

---

## 1. Pilot 三层验证结果

### 1.1 Data → Compiler 保真度（PF01，全表门禁）

对全部 **483 行** RuntimeDef 与 skills.csv **独立重解析**（不经 SkillParser 的第二解析路径）逐字段比对：

| 校验域 | 规则 | 结果 |
|---|---|---|
| class_id / type / damage_type | 恒等 | ✅ 483/483 |
| cost_mp / hitstun_f | 整数恒等 | ✅ 483/483 |
| damage_mult → DamageMultQ | RHE(×65536) | ✅ 全部可解析行 |
| knockback → KnockbackVelQ | RHE(×9×65536)（击退初速=位移×9） | ✅ |
| launch_v → LaunchVelQ | RHE(×65536) | ✅ |
| sweep / 受身无效 / 破霸体 | 布尔映射 | ✅ |
| hitbox kind → GeoKind | fan/cone→Sector, circle/aura/zone→Circle, box/line→Obb, cyl→Cylinder, proj→弹体载体 | ✅ |
| proj speed → ProjSpeedQ | RHE(m/s×65536) | ✅ |
| status 解析产物 | kind ≠ None、duration > 0 | ✅ |
| 蓄力追加前摇 | su + RHE(蓄力s×60) | ✅ 全部蓄力行 |

**1 行豁免**: PRI_T3_005 希望祷言 damage_mult=`30%蓝`（heal 类特殊记法）——独立解析防御跳过，该行语义归 heal 通道（Compiler 侧 L2 警告）。**未发现任何字段映射违规。**

### 1.2 Runtime → Sim 执行保真度（PF03–PF09 + GS/CC 既有）

| 技能 | 机制类别 | 执行验证 |
|---|---|---|
| BMG_T4_001 豪龙破军 | 近战部位结算 ×1.5 | ✅ hitRegion=Head（几何精判）+ 伤害=mult×atk×0.6×1.5 |
| ELE_T3_002 冰晶结界 | freeze@100% 状态路由 | ✅ StatusApplied(Freeze) |
| ELE_T1_003 火焰爆弹 | magic proj + burn:60:4s DoT | ✅ 命中 + DoT 状态路由 + 分数累积 |
| WIT_T2_005 熔岩烧瓶 | lob 抛物线 + 地面区域 | ✅ ProjectileSpawned + 落地事件 |
| GRP_T2_002 旋投 | 抓取体系（kb=0 兜底倒地） | ✅ GrabStarted → Hit → Down |
| NJA_T3_001 背身缚首术 | 需背身 120° 抓取 | ⚠️ v1 无角度门控——正面也成立（**Fidelity Gap MF-1**） |
| SRP_T2_001 踏射 | 受身无效 | ✅ 标签数据化 |
| SUM_T1_002/003 哥布林/雷精灵 | UnitSystem 全流程 | ✅ 召唤位 4 消费/追击/投掷攻击(8m)/存在期到期回收/位满 Cap 回收最旧 |
| THF_T1_001 潜行 | Visibility | ✅ hold 隐身 → 敌方判定体 sweep 过滤 → 施法/普攻破隐 |
| KNI_T3_003 法术反射 | 反射窗 | ✅ magic 弹体反弹（OwnerId 转移+反向）→ 原施法者被己方弹命中 |
| QIM_T3_002 念龙波 | 可控弹跟随 | ✅ Steer → 弹体方向实时跟随施法者朝向 |
| LAU_T3_001 激光炮 | 蓄力 line 40m | ✅ 前摇 68T + ×1.4 伤害 |
| W_BMG_003 破魔重枪 | Weapon overlay atk_mod | ✅ Atk 1100→1133 → 伤害链随动 |

### 1.3 确定性 / Replay 无退化（PF10 + 既有回归）

- PF10 Storm 对局（SUM 召唤 + THF 潜行/破隐 + KNI 反射 + QIM 可控弹 + 圆舞棍 + 格挡 800T）双跑**事件 hash + 快照逐位一致** ✅
- D01–D04、GS 系、CC 系全量回归 ✅（113/113）
- 事件协议 v3 在 Unit/Stealth/Reflect 事件下快照完备（新状态域 Hidden/ReflectTicks/ResourceSlots/WeaponId 全部入快照）✅

## 2. 规模底数（PF02 全表可表达性分类）

| 分类 | 行数 | 占比 |
|---|---:|---:|
| **Routed**（全字段路由，可直接迁移验证） | **429** | 89% |
| **Partial**（依赖 Phase 6 原语的签名/部署语义） | 54 | 11% |
| Blocked（Compiler L1 拒产——OQ 裁定行） | 4（不在 483 内） | — |

Partial 54 项分布: unit 14 / deploy 9 / proj(特殊形态: 追踪/吸附/锁链/贴符/水牢/爆裂8枚) 8 / ally 8 / self(分身假身滞空) 4 / wall 2 / zone 2 / circle 2 / box 2 / lob 1。

**判定**: 89% 全字段路由——批量迁移的主力通道已就绪；Partial 54 项全部对应已登记的 Phase 6 原语（UnitSystem/Deploy/签名可控语义），随批次收口，无未知阻塞。

## 3. Pilot 发现的 Fidelity Gap（迁移前需知悉）

| # | Gap | 分类 | 处置 |
|---|---|---|---|
| MF-1 | 背身抓取角度门控缺失（NJA_T3_001 需背身 120°）——抓取原语无角度参数 | Implementation Gap | GrabPending 增加 IsFromFront 门控（一个参数，随 UnitSystem 批次） |
| MF-2 | deploy hitbox（陷阱/炮台 9 行）落地物放置+耐久+触发语义 | Implementation Gap | Deploy 原语（UnitSpec.Stationary+Hp 变体） |
| MF-3 | 可控弹的特殊形态（追踪:100°/s、吸附拖拽、锁链、贴符、爆裂8枚、水牢 8 行） | Implementation Gap | 签名插件体（ISimContext.SpawnProjectile 已预留） |
| MF-4 | ally hitbox（8 行治疗/增益弹道） | Implementation Gap | ally 目标选择原语（Faction 查询已有） |
| MF-5 | PRI_T3_005 heal 类 `30%蓝` 数值记法 | Data Gap | heal 通道结构化（数据补丁轮） |
| MF-6 | 完美格挡/反击窗/破隐的多路径时序与技能自身的交互（测试编排敏感） | 测试工程 | Pilot 已实证核心语义；批量迁移按批次回归 |

## 4. 裁决

**Verdict A（有条件）——487 技批量迁移可启动，附两个执行条件：**

1. **迁移批次结构**：Routed 429 行按 A → B → C/D 类分批（每批 = 数据验证 + 逐技 Runtime 场景 + 确定性回归）；Partial 54 行随 UnitSystem/Deploy/签名批次迁移，不阻塞主力批次。
2. **MF-1 角度门控**随首批迁移一并落地（原语参数级，半天工作量）。

依据：三层 Pilot 验证全绿（全表 Compiler 保真度 / 代表性 Runtime 执行 / 确定性+Replay 无退化）；无 per-skill 特殊代码趋势（原语全部数据驱动）；Phase 5 审计的 6 项收口原语中 4 项已在本阶段落地（UnitSystem/ResourceSlots/Visibility/Reflection+可控弹），余 2 项（Weapon trait 深化/Deploy）已定义并有部分实现，随批次收口。
