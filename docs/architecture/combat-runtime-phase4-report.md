# Combat Runtime Phase 4 报告 — SimWorld 从 Stub 到 SPEC 合规重建

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-09-02 |
| 基线 | master `564e013`（Combat Fidelity Review v1） |
| 性质 | Implementation / Combat Fidelity / Determinism 三自审 + 实施记录 |
| 结论 | **权威战斗链路已真实跑通；487 技 Runtime 覆盖率 16% → ~46%；无 READY 宣称——缺口清单见 §6** |

---

## 1. 本阶段做了什么（一句话）

将 combat-fidelity-review-v1 判定为「简化 stub」的 SimWorld 按 SPEC-0005/0006 **重建**为唯一权威战斗链路：
`Compiler(量化 RuntimeDef) → SimWorld 结算总序 → SkillTimeline(hitbox spawn) → CollisionSystem(PA-7 相对扫掠) → ContactList(总序) → HitResolve(零几何) → SimEvent(协议 v2)`——测试与生产消费同一链路，**无第二套简化逻辑**。

## 2. Implementation 自审（代码 vs SPEC 逐条）

### 2.1 碰撞（SPEC-0005）

| SPEC 条款 | 实测 |
|---|---|
| §1 CollisionSystem 唯一空间实现点 | ✅ ConvexRegion 统一约束表示（半平面∩圆盘）+ SweepSolver 区间裁剪引擎；新图元=新构造器，零新求解路径 |
| §2 IntegrateMove 统一路径 | ✅ 走位/击退/突进共用；bounceEnabled 按响应策略区分（§2 表）；垂直运动独立（GroundStop） |
| §3 Intra-Tick 线性公理 | ✅ hitbox 锚定 Tick 离散 + PA-7 相对扫掠（位移 = 双方位移差） |
| §4 ShapeLibrary | ✅ Circle/Aabb/Obb/Sector/HalfSpace + 3D 球特例路径；**扇区精确表示（楔形∩圆盘），优于 SPEC 的 ConvexPoly 弦近似** |
| §5 解析求解（禁二分） | ✅ 半平面线性裁剪 + 圆盘二次求根（Int128 判别式域，E-7）；二分仅 oracle（测试） |
| §5.3 ISqrt/法线/零向量规则 | ✅ 法线=进入约束外向法线；起点重叠→运动反方向（构造确定） |
| §5.4 接触点 | ✅ mover 中心 TOI 位置（半径已并入） |
| PA-1/PA-2/PA-3 | ✅ 区间交；[0,1] 裁剪/起点重叠 TOI=0/相切=接触/t_out<0 无接触；决策全用量化 toiFixed |
| PA-4 排序键 | ✅ (toi, layerRank, attackerId, defenderId, hitboxUid, region, kind)；同键=同一离散时刻 |
| PA-5 L1 迭代 | ✅ 上限 4；每 Tick ≤1 反弹 + 第二约束面 clamp（§6.3） |
| PA-6 BroadPhase | ✅ 8m 网格 swept-AABB 入格；T55 等价性测试（候选集 ⊇ 真实集 + 解析≡oracle 200 case）；无界半空间坐标钳制（仅 BP 分格域） |
| PA-7 相对扫掠 | ✅ hitbox×fighter 与 projectile×fighter 均为相对运动线性扫掠 |

### 2.2 命中空间（SPEC-0006）

| 条款 | 实测 |
|---|---|
| §1.3 Hurtbox 投影（非独立实体） | ✅ 躯干 OBB（0.9×0.6 随朝向旋转）+ 头部球（PA-H1.2 真 3D）；Down 态体带 [0, 0.4] |
| §1.4 hitboxId/SemanticKey | ✅ per (execution, victimId, segmentIndex) 幂等去重 |
| §2 CollisionResult 全字段 | ✅ ContactResult（toi/layer/hitPoint/normal）→ SimEvent 协议 v2（hitRegion/hitPoint/hitNormal 入 Hit） |
| §3 权威链路（HitResolve 零几何） | ✅ **结构性成立**：HitResolve 输入签名只含 CollisionResult + Def + Fighter——无几何重算可能 |
| PA-H1 高度带 | ✅ 双端 inclusive；近战默认带 [0.2,1.9]（PA-H5）；proj aimHeight 1.2/1.6 数据化 |
| PA-H2 区域选取 | ✅ priority Head>Torso；**+ GDD §4.6 门控：非弱点技不使用头部判定**（部位结算 ×1.5/×2 仅弱点技） |
| PA-H3 命中后时序 | ✅ Destroy 停止 / Pierce 扣次 / 每 victim 一次 |
| PA-H4 资格豁免 | ✅ 无敌/倒地保护/同队 在结算前过滤 → Whiff(reason) 事件 |
| §4 T54 矩阵 | ✅ a–j 全绿（head crossing/torso crossing/near miss/边界相切/多目标/墙反弹/墙角/地形摧毁/高度带/确定性 oracle 对照） |

### 2.3 战斗循环（ADR-0001/0003/0009）

- **结算总序**（ADR-0001 §3.2）：① 指令（per-Fighter CmdStream，FighterId 升序，GDD §2.3.2 优先级）② 时间轴+投射物推进 ③ ContactList 总序命中结算 ④ IntegrateMove+L2 推挤 ⑤ 状态/资源 Tick（FighterId 升序）⑥ 死亡 ✅
- **BUG-1（移动清零）BUG-2（指令路由）BUG-3（Sweep 未调用）BUG-4（HitRegion 硬编码）BUG-5（HitPoint=victim 位置）全部修复** ✅
- 命令结构：`Command(FighterId, Kind, SkillId, AimQuantum, DirIndex, TargetTick)`——FighterId 显式入 Core（BUG-2 修正；传输层映射归 Infra/Net 阶段）✅
- Snapshot：ADR-0001 §8.2 完备状态（ fighters/executions/hitboxes/projectiles/RNG 计数器）；事件不入快照 ✅

## 3. 战斗能力清单（本阶段真实可跑）

| 能力 | 实现 | 验证 |
|---|---|---|
| 技能时间轴四段 + hitSchedule 多段 | SkillTimeline.SegmentWindow（段窗=至下一段激活） | GS06 连突 2 段 2 事件、间隔 ≥3T |
| 命中确认取消（GDD §8.2） | 生效帧后取消 / 后摇取消窗 + 档位递进 / 缓冲 12f 恢复即执行 | GS14 成功 / GS15 空技拒绝+吃满后摇 |
| 普攻链段间衔接 | ChainNext Catalog 装配链接 + 缓冲 | D01 soak 普攻链 |
| 浮空引擎（GDD §5.3） | 初速→重力 22m/s²→再命中 ×0.8ⁿ 下限 3.0→3s 强制落地 | GS03 天击浮空（空气窗实测吻合 0.82s）+ 落地倒地 |
| 击退/撞墙反弹（§5.8） | 法向反射 ×0.6 + 切向保留 + 剩余运动重积分 + 硬直延长 10f + 「位移结束+硬直结束」双条件 | GS02 + W5 诊断 |
| 倒地/受身/起身（§5.6/5.7） | 45f/80f 倒地、受身窗 20f（连续倒地 30f）+ 方向 ≤90°、起身 24f 无敌、起身保护 ×0.9 | GS17 受身 / GS07B 受身无效 |
| 强制倒地【受身无效】 | 圆舞棍/背摔/踏射数据标签（special「受身无效」5 行） | GS07 |
| 倒地保护 + 扫地 ×0.7 | 仅扫地可打倒地 | GS08 |
| 部位判定（§4.6 弱点） | 近战弱点 ×1.5 / 巴雷特 ×2 数据化；非弱点技不触发头部 | GS09（×2 端到端）+ GS05 背击对照 |
| 背击 ×1.2（§2.5.2） | 纯 int64 点积比较（2·dot > len，零除法） | GS05 双场景（背/正对照） |
| 霸体（§6.4） | SA/SSA 窗承伤不硬直、SSA 击退无效、破霸体标签、霸体承控值（+10/击，OQ） | GS16 |
| 控制异常（§7.3） | 数据驱动路由 14 类行为位 + 控制值/挣脱/Break 1.5s 免控 + 互斥（冰冻↔灼烧等） | GS11/12/13 |
| DoT（灼烧/出血/毒） | 每秒点数分数累积 RHE（确定性分数伤害） | GS06 数据路由 |
| 投射物（§4.5） | 直线弹（aimHeight）/lob 抛物线/存活 3s/每玩家 8 上限/地形摧毁/穿透标签 | GS09/GS10 |
| 地形（§19 v1） | 结界墙反弹（4 半空间）/掩体立柱阻挡+弹体摧毁/BroadPhase | GS02/GS10 |
| 资源 | MP 20/s 分数累积（ADR-0003 §1 连续量不逐 Tick 发事件）| D01 soak |

## 4. Determinism 自审

| 验证 | 结果 |
|---|---|
| D01 同种子同指令 600 Tick | ✅ 事件 hash + 终态快照逐位一致（×3 复跑） |
| D02 快照 t300 恢复续跑 600 | ✅ 终态逐位一致 + 事件序列一致 |
| D03 同 Tick 指令到达顺序交换 | ✅ 结果不变（FighterId 升序结算） |
| D04 3000 Tick 混合 soak（技能/移动/普攻双人对局） | ✅ 双跑逐位一致 |
| T55 BroadPhase 等价 | ✅ 200 随机 case 解析 ≡ 2^20 采样 oracle |
| Math.*/依赖门禁 | ✅ PASS（Int128 为纯整数软件运算，E-7 登记提案） |
| Core 无 float/无 RNG 旁路 | ✅ 全部几率走 SKILL_CHANCE 流（Rng 隔离测试既有 6 例仍绿） |

**过程中修复的确定性/正确性缺陷**（全部有回归测试锁定）：
1. `DivRoundHalfEven` 负除数语义错误（RHE 符号对称化重写）——本阶段扫掠首次暴露
2. CORDIC 角度约定（SPEC-0001: 0=+Z 顺时针 → 数学域 θ=π/2−h）+ 折叠 ±π 时 (cos,sin) **双双**变号
3. 扇形楔形法线符号（n2 = (−s, +c)）——Sector 单测锁定
4. `MulShift(disp, t) / ONE` 系统性双缩放单位错误（接触点/剩余位移/oracle 采样）——12 处修正
5. 背击点积单位错误（dot/len 已无量纲，误再除）+ 方向语义（命中来源 vs 面朝 >120°）
6. 缓冲排水重复入队（每 Tick 复制缓冲项）→ fromBuffer 不再重复入队
7. 取消非原子（先终止旧技后 CD 预检 → CD 阻断新技且旧技已失）→ CanStartExecution 前置
8. 击退状态零时长（吹飞类 hs=0 即时恢复 → 出界）→ GDD §5.1「位移结束+硬直结束」双条件
9. Snapshot 恢复重建 Fighter 丢失 ClassId（普攻链断裂）→ 复位既有对象
10. AddFighter post-Seal 不注册阵营 → SameTeam 误判友军为敌（巴雷特误伤队友实证）

## 5. 数据链路（ADR-0002）现状

- **Compiler 产出 RuntimeCatalog**：483/487 行量化为 SkillRuntimeData（hitbox 语法→几何、status→效果、armor/invincible→窗、cancel_min_tier→档位、普攻链 ChainNext、弱点头部倍率、aimHeight）
- **L1 阻塞 4 行**（设计裁定项，非实现缺陷）：
  1. `BER_T3_004` SA:12-26s 单位歧义（OQ-2，既有）
  2. `SBL_U_001` hitbox 形态三选（OQ-13，既有）
  3. `SPF_U_001` hitbox 自身全部手雷种类（OQ-13，既有）
  4. **`GBL_T2_001` fan:r3.0:a200 —— 新发现**：SPEC-0005 §4 断言「CSV 现值 a90–a160」不实，该行扇角 200° > 180° 违反凸性前提 → 按 SPEC 裁定 Schema Failure 拒产。**需设计裁定**：改数据 ≤180° 或扩展 SPEC（双扇区拼合等）
- **未路由注册表**（Compiler 显式登记，无静默丢弃）：
  - status 47 条：僵直/水牢/反射法术/forcespin/结界/冻结@P%（中文语法变体 2 行）/weight/amplify/截脉/震地波/拖拽/全异常满值 等——多为签名/特殊机制语义
  - hitbox 48 条：unit 10 / deploy 9 / ally 8 / wall 2 / portal 1（UnitSystem/Deploy 体系，ADR-0008 签名阶段）/ proj 特殊形态 7（追踪/吸附/锁链/贴符/水牢等可控弹）/ self 特殊 6（假身/分身/滞空等）
- **arena.csv** 纳入装配（SPEC-0004）：boundary→4 外向半空间（bounce）、掩体/立柱/木箱/乱石→Stop+弹体摧毁、platform/ramp/spawn/pot v1 穿透（高台体系=TerrainSystem 待实现）

## 6. 仍未成立的能力（诚实清单）

### BLOCKER（下一阶段必须）
| # | 缺口 | 影响 | 去向 |
|---|---|---|---|
| 1 | ISignature/ISimContext 插件框架（ADR-0008） | 60 条 C 类技 + 上表 47+48 条未路由语义的载体 | Phase 5 首项；SimWorld 已按 ISimContext 九原语形态暴露内部服务（SpawnProjectile/ApplyStatus/Roll100/...） |
| 2 | 格挡/完美格挡/盾值（G-04/G-09） | 剑客/骑士核心 | Phase 5（ShieldPool 接口已在 pre-adr §3 定义） |
| 3 | 抓取体系（G-08 关联） | 柔道家全体系 + GRP_T2_001 等 | Phase 5（Grabbed 状态枚举已备） |
| 4 | 反击窗口（counter 类型） | counter 技 2+ 行（cancel_min_tier=counter 未路由） | Phase 5 |
| 5 | 高台/坠落/破坏地形（G-13） | 地形连 | TerrainSystem（arena.csv height/hp 列已就绪） |

### HIGH
- 职业资源槽（炫纹/弹匣/召唤位，G-12）——BMG 炫纹从 special 列已解析未消费
- UnitSystem 召唤兽（G-07，10 行 unit hitbox 已登记）
- 武器 overlay（G-18：weapons.csv 已编译未注入面板/伤害）
- 蓄力（charge）与 hold/controlled active 形态（现按名义 2T 判定窗）
- Steer 指令消费（ADR-0010 已定义，签名可控弹用）

### MEDIUM/登记
- 翻滚位移（Roll 现仅作受身；翻滚 3m/30f+无敌 4–18f 未实现）
- 落地硬直 6f、追踪弹转向（≤120°/s 整数饱和步进——SPEC-0001 §2 公式已备）
- Hitstop（ADR-0009 §3.6 裁定为表现层，与 GDD §2.2.3「服务端同样模拟」冲突——按 ADR 执行，冲突登记）
- 扇区圆盘角点连接圆的过近似（±0.16m@R2.6/r0.45，两角点扇区弧端——方向=判定略宽松；如需精确改 union 分解求解器）
- 软推挤仅作用于击退/浮空位移对（走位重叠合法=GDD §2.1.2 明文；SPEC-0005 L2「成对分离」按此解释执行——**规格歧义，已按 GDD 优先解释，请设计确认**）

## 7. 数字对照（fidelity-review §5 基线 → 现在）

| 类型 | 基线可跑 | 现状可跑 | 依据 |
|---|---:|---:|---|
| A 纯数据 162 | ~60 | **~130** | 伤害+部位+空中/扫地/背击+状态路由（slow/root/stun/freeze/silence/dot/weakness）|
| B 数据+规则 265 | ~20 | **~90** | +取消/普攻链/霸体/强制倒地/受身无效/投射物直弹+lob/地形阻挡/多段 |
| C 签名 60 | 0 | 0 | ISignature 未实现（保持 0 是诚实的） |
| **合计 487** | **~80 (16%)** | **~220 (45%)** | 剩余主要卡在签名/格挡/抓取/资源/Unit 体系 |

## 8. 下一阶段建议（优先序）

1. **ISignature/ISimContext**（ADR-0008 落地 + 1–2 个签名技打样，如 BMG 炫纹/WRK 魔镜）——解锁 C 类 60 技与上表大部分未路由语义
2. 格挡+盾值+完美格挡（剑客/骑士 Golden Slice 补全）
3. 抓取体系 + 反击窗口（柔道家 Golden Slice）
4. GdUnit4Net 迁移评估（D-P0-1 维持 xUnit；测试全绿可先迁 CI）
5. GBL_T2_001 a200 数据裁定 + 冻结@P% 中文语法别名（2 行）——数据补丁轮

**未进入 487 技大规模迁移**——本阶段以 4 职业 Golden Slice 关键技（BMG 7 + SRP 2 + BLA/GRP 数据覆盖）验证表达力，符合「证明成熟度后再迁移」的约束。

## 9. 测试与工程状态

- **78/78 全绿**（原 40 + 新增 38：碰撞 11 / 数学 8 / BroadPhase 2 / Golden Slice 17 / 确定性 4 / 既有 36 全保留适配）
- 旧 CombatVerticalSliceTests（验证 stub 行为）删除，其场景全部由数据驱动 GS 系列继承
- Arena.Headless 兼作战斗链路诊断器（ADR-0009 §4 骨架复用）
- 门禁：依赖方向 PASS / Math.* 禁令 PASS
- GdUnit4Net：未安装（镜像限流），D-P0-1 维持 xUnit
