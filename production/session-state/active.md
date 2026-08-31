---
name: glory-game-project
description: glory-game 项目：《廿四争锋》竞技场动作对战游戏，复刻全职高手荣耀战斗机制，仅做战斗
metadata: 
  node_type: memory
  type: project
  originSessionId: 567a7249-77dc-4b81-ac68-85ec046b1c6c
  modified: 2026-08-30T16:52:32.024Z
---

用户在开发 `glory-game/`（《廿四争锋》Project ARENA·24）：3D 竞技场动作对战游戏，**完全复刻《全职高手》「荣耀」的战斗机制（24 职业/技能取消/浮空连招/武器系统），其余网游玩法全部不做**（无养成/副本/经济），地图只有一张竞技场。

- 玩法总案：`glory-game/docs/GDD-Gameplay-v0.1.md`（内部版本 **v0.3.5**，2026-08-31）
- **考据基准**：`docs/reference/荣耀职业介绍-ycyoc-wiki.md`。**易错名：拳法家（非拳法师）、流氓（非街霸）、弹药师（非弹药专家）**；鬼剑士=布甲阵法系、魔剑士=板甲波动剑系；通用技能=疾跑/翻滚/受身/弹跃
- **Skill-Spec v0.1 已完成**：`docs/skill-spec/`（skills.csv 487 行=唯一数值源 + README 字段字典 36 列 + 实现注记 + validation-report）。散人池=<20 级常规技共 96 个（≤100 上限）；无携带上限（D37）；tier 与 learn_level 解耦（§3.7b/GDD §17.0）；校验 `tools/validate_skills.py`
- **Weapon-Spec v0.1 已完成（2026-08-31）**：`docs/weapon-spec/`（README 规范+trait_rules 语法 + weapons.csv 73 行=24 职业×3+万象伞 + tools/validate_weapons.py 含 D12 红线校验，PASS）。武器三原则：每职业 3 把赛前选 1/特性只作用于规则层/武器不改技能数值。**D12 口径已收紧：数值级禁止（伤害%/吸血/减伤%），规则级允许（距离/CD/时长/段数/半径/角度/耐久/解锁）**——GDD §16 十条数值级特性已规则化改写（validation-report 变更记录 #1–#10）；「影 cutting·忍刀」正名「影切·忍刀」；剑客剑五类只落 3 把（大剑/光剑 v0.2）
- **Balance-Sheet v0.1 已完成，v0.4 平衡补丁后 PASS（2026-08-31）**：`docs/balance-sheet/`（README 规范 + class-base.csv 25 职业面板 + tools/balance_audit.py → balance-report.md）。**用户已裁定「方案 A：抬 CSV 对齐名义带」**：直伤技有效倍率逐档仿射映射至 §9.2 名义带（268 条改写；功能技/控制主导技 14 条豁免）；离群技已压线（十字军审判 9.6→3.0、伏龙翔天 6.0→4.5、诅咒之箭 3.9→1.0、流星式→3.0、剑刃风暴 0.22→0.20/段）。结果：理论 DPS 241→396（§2.5.4 带内）、TTK 63s、单发/三连超线清零。**遗留：GDD §14 技能表倍率为调价前口径（CSV 优先），回填列入 v0.2**
- 用户裁决：团体赛 5v5 封顶先做竞技；道具/装备先不做
- IP 合规原则：复刻机制不抄 IP——不用原作角色名/战队名/专有道具名（千机伞 → 万象伞）
- 待评审项：D07（命中后才能取消）、D20（不做搓招）、散人护甲（无精通，暂定布甲 DEF 800 待评审）
- **BMG 白盒原型（GDD §29 Prototype）无头层完成（2026-08-31）**：`prototypes/bmg-whitebox/`（Godot 4.3 纯逻辑 60Hz 确定性仿真 + tests/run_audit.gd 无头审计，~33s）。帧一致性 20/20；五道闸门 BMG 穷举 0 违规（峰值 27.9% HP、最长连段 1.6s）。**v0.3.7 用户裁定已落地**：F1 launch_v 7.5→9.0（浮空带 6.5–9.0；三刺可复现，完整四刺差 9f 待二轮裁定：空中快刺形态 or 接受三刺）；F2 改模板收尾=圆舞棍>强龙压 双扫地（T2b 验证通过；vs 最优受身被防住=受身博弈生效）；F3 用户重定义「浮空吹飞=天击>落花掌」为原著可验证连招（废除落花掌>天击硬链接）；F4 幻影龙牙 active 12→15。Sim 补齐 §4.2 普攻取消→技能。重力 g=22 为 GDD 手感设计值（跳跃滞空 0.64s 节奏依据）。闸门①⑤盲区：BMG 无二段浮空源。实机手感层待 Windows 侧（main 场景未建）。引擎=Godot 4（ADR 随 Tech-Architecture）
- **Tech-Architecture v1.0 完成（2026-08-31）**：`docs/architecture/architecture.md`——TR 基线 45 条、五层架构（**Core=纯 C# 引擎无关仿真（Arena.Core）/专属服务器=纯 .NET 控制台（Arena.Headless），均零 Godot 依赖**）、程序集 Arena.Core/Infra/Client/Headless、五数据流（用户修正：Hitstop 仅表现层不暂停 Sim Tick；网络补偿=Rollback/Prediction 职责与 Hitstop 无关；Rollback 只读历史快照+事件唯一键幂等）、架构原则 P1 确定性至上/P2 数据驱动/P3 表现仿真分离/P4 AI 同权/P5 白盒先行、TD 自审 APPROVED WITH CONDITIONS。**引擎已钉死：Godot 4.3 + C# (.NET 8+) + GdUnit4Net**（用户裁定 C#，白盒 GDScript 原型不迁移），glory-game/CLAUDE.md 项目级指令已建。**必建 ADR 10 条**（0001 定点化仿真核心为编码前置条件）
- **一致性审计+Pre-ADR 收敛完成（2026-08-31）**：docs/architecture/audit-spec-consistency-v1.md（487 技 A162/B265/C60/D0；几率字段 vs D02 = BLOCKER B-1）+ pre-adr-resolution-v1.md（**B-1 方案=Per-Stream 计数型 RNG 流键隔离**；口径漂移 9 处定位 501→487/94→96；9 项 Core 接口（UnitSystem/Visibility/ShieldPool/Reflection/CD操作/资源伤害路由/每场限额/飞行/阵营）；衰减系数=精确有理数→Q16.16 预计算定点表禁 Math.Pow；Instant Window vs Persistent Modifier 两类时间本体）。**ADR-0001 READY**（议题：定点单位制/系数表/有序容器/三取整政策/RNG 声明）。OQ-4「36 点加点 vs D37」待用户裁定
- **ADR-0001 Accepted（2026-08-31）**：docs/architecture/adr/ADR-0001-deterministic-simulation.md——**Fixed=Q32.16 统一定点（int64）**、MulShift RoundHalfEven 唯一舍入、速度/加速度预量化为 per-Tick（运行时积分纯加法）、cos 阈值表替代 trig、三衰减系数预计算表（浮空 n≤8/递减 n≤64，版本 hash 入 dataVersionHash）、Math.Pow/trig 全禁+CI 门禁、容器纪律（ID 序/事件总序/签名注册序）、Per-Stream Counter RNG（流键=(seed,Fighter,Skill)，Snapshot 持久化计数器）、Snapshot 完备清单、Determinism Contract（同初始+同数据版本+同种子+同指令流⇒同快照同事件）+10 违约项、T1-T12 测试要求。自审 7/7。**TD 编码前置条件解除**
- **ADR-0002 Accepted（2026-08-31）**：docs/architecture/adr/ADR-0002-data-pipeline.md——Source-of-Truth 六层（CSV=事实源/learn_levels.py=provenance/文档=语义/报告产物=可再生）；Compiler 九段管线八维度确定性（Ordinal 排序/固定文件清单/InvariantCulture/Raw int64 落盘/Fraction 解析禁 float 语义字段）；**One Compiler Two Emitters**（JSON 承载 dataVersionHash；.tres 隔离 Godot UID 非确定性，启动 re-hash 不变式）；Core 零 IO 构造注入、Headless 只用 JSON、Client 禁读 CSV；L1-L4 校验 fail-fast 禁静默修正；数据问题 15 项登记（新发现：hitbox 17 kind 超字典 58 行、status ~40 自由文本、class-base 13 列漂移）。自审 15/15

- **ADR-0003 Accepted（2026-08-31）**：docs/architecture/adr/ADR-0003-event-protocol.md——事件两层身份（EventId=(Tick,Seq) 去重键/SemanticKey 命中族幂等，精化 architecture.md 五元组表述=文档同步项 D-1）、封闭枚举 7 族（白盒 26 种为底本）、Snapshot 连续量/事件离散事实分工、签名派发 FighterId×注册序+变更入队、单一可靠有序通道承载全部事件、EVENT_PROTOCOL_VERSION 与 dataVersionHash 分立、T13-T19 测试。自审 14/14
- 下一步：ADR-0009→0004→0008；OQ 与字典补录、F1 二轮、文档同步 D-1 待处理
- **ADR-0009 Accepted（2026-08-31）**：docs/architecture/adr/ADR-0009-tick-loop-and-scene-architecture.md——固定步进累积器（Stopwatch/MAX_CATCHUP=10）、_PhysicsProcess 不承载 Sim、ISimDriver 四模式共用循环骨架、Node↔Sim 单向拉取（Fixed→float 仅表现层）、**C# Solution 布局：Arena.Core/Infra/Infra.Godot/Client/Headless/Tests（Infra 拆纯核心+Godot 适配层=文档同步项 D-2）**、MatchRoot 显式装配禁 autoload、T20-T24。自审 15/15。OQ-10 新登记
- 下一步：ADR-0004→0008→0006→0010→0005；OQ/字典补录、F1 二轮、文档同步 D-1/D-2 待处理

**Why:** 项目方向和 IP 边界是用户明确诉求，后续所有设计/实现都要守住「只做战斗 + 竞技公平（无随机/无局外成长）」两条线。
**How to apply:** 在该项目工作时先读 GDD 的 §1.8 对照表与附录 A 设计决策清单（D01–D37），新增设计不得违反已定决策；数值改动走 CSV（skills/weapons/class-base）+ 重跑对应校验/审计脚本，不改代码。
