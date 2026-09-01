# Active Session — glory-game 项目进度

- **项目**: 《廿四争锋》(Project ARENA·24) — 3D 竞技场动作对战，复刻荣耀战斗机制，仅战斗+竞技
- **日期**: 2026-08-31
- **引擎**: Godot 4.3 stable / C# (.NET 8+) / GdUnit4Net

## 设计文档状态

- GDD v0.3.7 / Skill-Spec v0.1(+v0.4 补丁) / Weapon-Spec v0.1 / Balance-Sheet v0.1(PASS) / 复刻审计 v1+v2
- Tech-Architecture v1.0（TR 基线 45 条 / 五层架构 / Determinism Contract）
- 一致性审计 v1（487 技 A162/B265/C60/D0）+ Pre-ADR 收敛 v1（B-1 RNG 方案 / 9 项 Core 接口 / 定点数学）

## ADR 状态（必建 10 条全部 Accepted）

| ADR | 文件 | 自审 |
|---|---|---|
| 0001 确定性仿真核心（Q32.16/系数表/RNG/容器纪律/Snapshot/Contract） | adr/ADR-0001-deterministic-simulation.md | 7/7 |
| 0002 数据管线（Source-of-Truth 六层/Compiler 九段/双导出/fail-fast） | adr/ADR-0002-data-pipeline.md | 15/15 |
| 0003 事件协议（两层身份/封闭枚举/派发序/单可靠通道） | adr/ADR-0003-event-protocol.md | 14/14 |
| 0009 Tick 循环与场景架构（累积器/ISimDriver 四模式/Solution 布局） | adr/ADR-0009-tick-loop-and-scene-architecture.md | 15/15 |
| 0004 网络拓扑（Server Authoritative/ENet/四通道/事件环/断线重连） | adr/ADR-0004-network-topology.md | 12/12 |
| 0008 签名插件协议（ISignature/ISimContext 九原语/C 类 60 技 13 插件映射） | adr/ADR-0008-signature-plugin-protocol.md | 12/12 |
| 0006 预测与回溯（预测限定/逐位和解/ring 12Tick 回溯） | adr/ADR-0006-prediction-and-lag-compensation.md | 10/10 |
| 0010 输入系统（映射表/缓冲 Sim 裁决/CommandPacket 16B） | adr/ADR-0010-input-system.md | 10/10 |
| 0005 回放与反作弊（ReplayFile v1/事件不记录/离线证据链） | adr/ADR-0005-replay-and-anti-cheat.md | 10/10 |
| 0007 AI 同权指令接口 | 未创建（依赖 ADR-0008 框架，待补） | — |

测试要求累计 T1–T48（ADR-0001/0003/0009/0004/0006/0010/0005 各自定义）。

## 白盒原型

- prototypes/bmg-whitebox/：无头层 concluded（帧一致性 20/20、闸门 0 违规、峰值 27.9% HP）；实机手感层待 Windows 侧
- v0.3.7 裁定已落地（launch_v 9.0/模板重写/幻影龙牙 15f）；F1 二轮（空中快刺 vs 接受三刺）挂起

## Pre-Implementation Closure 完成（2026-09-01，94f4914）

- audit v1 的 F-1/F-3/F-4/F-6/B-1/B-7/F-7 全部闭合：SPEC-0001~0004（Steer+AimQuantum u16/AI 队列派生式/预测事件生命周期/ArenaDef）+ Errata×4（ADR-0002/0006/0007/0010）+ PATCH-001（skills.csv 19 处纯格式规范化，data-patch-log.md）+ arena.csv（26×13）+ zstd 登记
- **audit v2（implementation-readiness-audit-v2.md）：READY FOR PHASE 0**；L1 fail-fast 残留 3 行待用户裁定（OQ-2/OQ-13），OQ-5 精确化为单行意图待裁

## 碰撞专项设计完成（2026-09-01，6beb5bf）

- **SPEC-0005 确定性碰撞与扫掠**：Intra-Tick 线性运动公理（速度 Tick 内恒定/Hitbox 位姿 Tick 离散）⇒ 全部碰撞对解析可解（二次判别+区间裁剪，无二分万能路径；K=16 误差 1.33 Fixed 量子可翻转 HitRegion 故解析为生产路径）；统一 IntegrateMove 七种运动；Sim.Collision 新模块（Terrain 收窄/HitResolve 消费 CollisionResult）；多碰撞 (toiFixed,layerRank,Id) 总序；ISqrt/FSqrtFixed 入 ADR-0001 白名单（Errata E-3~E-5）
- **SPEC-0006 Hurtbox/命中空间模型**：HurtboxDef（Region 可扩展枚举，v1 启用 Head/Torso，priority Head>Torso）+ hitboxId/hurtboxId 复合键 + CollisionResult 全字段进 Hit 事件（EVENT_PROTOCOL_VERSION bump→2）+ 巴雷特 80m/s 验证矩阵 T54a-j
- **碰撞子系统实现解除阻塞**（Phase 3 按 SPEC-0005/0006 实施）；仍不实现（待 Phase 3）

## Phase 0+1+2 实现完成（2026-09-01，0a07a8d）

- arena.sln 六程序集编译通过，32/32 测试全绿，Math.*/依赖方向门禁验证可捕获违规
- Phase 1: Fixed Q32.16/DeterministicMath/DeterministicTables/SimRng/SnapshotData
- Phase 2: SkillParser/DataCompiler/L1-L4 校验/dataVersionHash；3 行阻塞准确报告
- Deviation: D-P0-1（xUnit 代替 GdUnit4Net，Godot .NET 二进制网络不可得；Phase 3 迁移）

## Phase 3A-3D Combat Runtime Vertical Slice 完成（2026-09-01，ef70a5b）

- Phase 3A: SimTypes(FighterState/Command/SimEvent/FighterStateData) + SkillTimeline + EventLog(SHA-256 诊断)
- Phase 3B: SweepSolver(解析 SweepCircleVsCircle/PointVsCircle/PointInAABB) + HurtboxModel(Head/Torso profile)
- Phase 3C: SimWorld.ResolveHit(伤害公式链→HitRegion 修正→连段递减→浮空/击退/倒地→控制值挣脱→事件发射)
- Phase 3D: CombatVerticalSlice 8 测试（天击/龙牙/落花掌/圆舞棍/巴雷特/连段递减/确定性/回放）——6/8 通过
- Phase 3E: T_VS7 确定性 + T_VS8 回放一致
- Math.Pow 违规被门禁捕获并修正为查表（门禁有效性实证）
- 测试: 38/40 通过（2 VS 失败：T_VS1 数据单位/T_VS5 强制倒地未建模——已知问题待修）
- GdUnit4Net: 未安装（镜像限流），xUnit 持续使用，D-P0-1 维持

## 待处理

1. 【已完成】ADR-0007
2. /create-control-manifest → /test-setup（GdUnit4Net+CI，落地 T1–T48）→ /ux-design → /architecture-review → gate-check pre-production
3. 用户裁定项：**OQ-2 + OQ-13（3 行数据，解除 Compiler 全量）**、OQ-4（加点）、OQ-5（0f 意图）、OQ-6（4 行几率数值）、OQ-7/8/9、OQ-2·11（部署/匹配）、F1 二轮
4. 文档同步项：D-1（architecture.md 事件五元组→两层身份）、D-2（Arena.Infra 拆分纯核心+Godot 适配层）+ 各 README 数字漂移（501→487、94→96、携带 10 格树残留、12→13 列）
