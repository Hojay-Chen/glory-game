# Implementation Readiness Audit v1 — ADR-0001~0010 联合执行一致性审计

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-08-31 |
| 审计基线 | master `b48ebb7`（工作树干净，与 origin 同步） |
| 审计范围 | architecture.md + ADR-0001~0010 全文 + CLAUDE.md + GDD v0.3.7 + Skill-Spec + 白盒 prototype + technical-preferences |
| 性质 | 只读审计——不修改任何文档/数据，不创建 ADR，不开始实现 |
| 最终 Gate | **READY WITH CONDITIONS**（§10，条件逐条列出） |

---

## 1. Executive Summary

**10 条 ADR 单独看全部自洽；联合执行后发现 4 处跨 ADR 矛盾、3 处协议规格缺口、2 处 Snapshot/事件覆盖缺口、1 处 ADR 内部自相矛盾。** 全部可修复，且无一需要推翻既有决策——但其中 2 项（Command 缺 Steer Kind / 鼠标连续角度量化）如果直接开工 Core 会变成隐性确定性违约，必须在第一批实现前以「协议规格补充」形式收口（不需要新 ADR，属于 ADR-0001/0010 的规格附录）。

**结论：READY WITH CONDITIONS**——条件见 §10。数据侧有 6 行格式噪声会阻断 Data Compiler 的 L1 校验（fail-fast 是设计行为），需要你在数据补丁轮批准修正后，Compiler 阶段才能跑通。

---

## 2. ADR Cross-Consistency Matrix

| 交叉点 | ADR 组合 | 结论 |
|---|---|---|
| 量化政策 ↔ Skill 数据 | 0001 P-1/P-2/P-3 ↔ skills.csv | ✅ 一致（P-2 编译期预计算/间隔 ≥3T 校验；487 行全部 Tick 对齐） |
| 事件身份 ↔ 传输 | 0003 EventId ↔ 0004 通道 | ✅ 单可靠有序通道保持 (Tick,Seq) 序 |
| dataVersionHash ↔ 回放/握手 | 0002 ↔ 0003/0005/0004 | ✅ 三版本分立（data/protocol/replay-format）层次清晰 |
| RNG ↔ 签名/AI | 0001 §4 ↔ 0008/0007 | ⚠️ 术语对齐项：0007 称「AI_STREAM（AMBIENT 类）」——实现时统一 StreamClass 命名为 {SKILL_CHANCE, UNIT_AI, AMBIENT, AI_DECISION}，微小、不改结构 |
| 签名状态 ↔ Snapshot | 0008 ↔ 0001 §8.2 | ✅ 插件无字段状态+Fighter 域覆盖（orbs/buffs/护盾池/限额/隐形/飞行） |
| 预测 ↔ 权威 | 0006 ↔ 0004/0010 | ⚠️ 发现 F-4（预测事件丢弃规则未成文） |
| Tick 循环 ↔ 回放/网络 | 0009 ISimDriver ↔ 0005/0004 | ✅ 四模式共用骨架 |
| 回溯 ↔ Replay | 0006 ↔ 0005 | ✅ ring buffer 为确定重演产物，可复算闭环 |
| **Command 结构 ↔ 操控型技能** | 0001/0010 ↔ 0008/0006 | ❌ **发现 F-1：CmdKind 缺 Steer；鼠标连续角度量化未定义** |
| **Snapshot 完备性 ↔ AI** | 0001 §8.2 ↔ 0007 | ⚠️ **发现 F-3：AI 延迟队列未列入 Snapshot 清单** |
| **fail-fast ↔ 当前数据** | 0002 ↔ skills.csv | ❌ **发现 F-5：6 行格式噪声将使 L1 校验失败（数据补丁前 Compiler 无法通过）** |
| **ADR-0002 内部** | §4.2 ↔ §4.4 | ⚠️ **发现 F-6：`SA:12-26s` 一处说「按字面解析标记」一处说「白名单外 fail-fast」自相矛盾** |

---

## 3. Determinism Closure Audit

沿 0-divergence 契约逐通道检查：

| 通道/系统 | 结论 | 证据/缺口 |
|---|---|---|
| Fixed/Q32.16 全路径 | ✅ 设计层一致 | 0001 §1 唯一类型；0002 产物仅 Raw int64 |
| 隐式 float/double | ⚠️ 一处未定义 | **F-1b：鼠标瞄准角**。GDD §4.7 软锁定鼠标指向为连续量；ADR-0010 只定义了 DirIndex 8 向与「连续角度由 Sim 消费」——连续角度如何定点量化（分辨率/取整）未定义。若实现者用 float 角度进 Sim ⇒ 违约。**补规格**：Command 增 `aimQuantum:u16`（1/65536 转角）或 DirIndex 扩展 256 向，取整 RoundHalfEven |
| Math.* 禁令 | ✅ 无豁免 | 0001 §2.4；0008 插件同受约束 |
| 不稳定容器遍历 | ✅ 无 | 0001 §3.1（ID 序/总序/注册序封闭） |
| Entity ID 分配 | ✅ 确定 | 0001 §3.1-2（分配序入快照）；0008 UnitId/DecoyId 同规则 |
| Event 顺序 | ✅ 确定 | 0003 (Tick,Seq)=总序；T13 |
| Signature 顺序 | ✅ 确定 | 0001 §3.4 + 0008 §1（注册表 (ClassId,PluginId) 排序冻结；无字段状态） |
| RNG Stream 污染 | ✅ 结构性排除 | 0001 §4.1 流键隔离；0008 Roll100 身份绑定；0007 AI 决策流独立且入快照域 |
| Snapshot 完备性 | ⚠️ 一处缺口 | **F-3：AI 延迟队列**。0007 §3 反应层延迟队列影响未来 Command——0001 §8.2 未列入。**补**：AI 受控 Fighter 的快照增加 `aiQueueState`（或规定 AI 决策为纯函数：队列由 (seed, AI_STREAM counter, 快照) 派生——推荐后者，免增快照字段但需写入 0007 实现注记） |
| 事件不成为第二事实源 | ✅ | 0003 §1 分工 + 载荷不携状态终值 |
| 组件遍历（Tick 内） | ✅ | 0001 §3.2 六阶段 |

---

## 4. Network / Replay / Prediction Audit

**事实链完整性检查**（用户要求的主链）：

```
Command Stream → Server Sim → Snapshot+Event → Network → Client → Replay → Re-Sim
```

| 检查项 | 结论 |
|---|---|
| 事件成为第二事实源？ | ✅ 否——0003 §1：连续量在快照、事件仅离散事实；快照可独立恢复状态 |
| Replay 仅凭 seed+dataVersionHash+protocolVersion+command stream 可完整重演？ | ✅ 是——0001 契约 + 0005 §3（事件重算、eventStreamHash 作诊断断言） |
| Rollback 结果可被 Replay 复算？ | ✅ 是——0006 §3-4：ring buffer 为确定重演产物 |
| EventId/SemanticKey 重复冲突？ | ✅ 无——EventId 由总序分配天然唯一；SemanticKey 命中族一次结算（0001/0003） |
| 网络重发重复结算？ | ✅ 否——EventId 滑窗去重（0003 §2.1）+ 服务器事件环（0004 §2） |

**发现（协议规格缺口，实现阶段必须补充的 Protocol Specification——按你的要求不创建 ADR）**：

| # | 缺口 | 影响 | 归属 |
|---|---|---|---|
| P-1 | **时钟同步**：客户端如何得知服务器当前 Tick（OnlinePredicted 跟随服务器 Tick，但同步握手/漂移校正未定义） | 预测起点/TargetTick 语义 | 0004 实现规格 |
| P-2 | **Packet framing**：封包边界/粘包处理/MTU 分片未定义 | 传输层实现 | 0004 实现规格 |
| P-3 | **Handshake/Session identity**：会话 ID、重连票据、版本协商字段序未定 | 会话层 | 0004 实现规格 |
| P-4 | **TargetTick 前向缓冲**：0001/0010 定义了「落后 ≤10 Tick 接受」，但预测场景下客户端 TargetTick **领先**服务器——服务器必须缓冲未到 Tick 的指令。缓冲窗深度/溢出策略未定义 | 预测正确性 | 0004/0006 实现规格 |
| P-5 | **纯 C# ENet 替代实现的工程量**：ADR-0004 假定「两实现同协议」——市面无现成纯 C# ENet；自研可靠 UDP 子集（仅 ch0/ch2 可靠有序 + ch1 不可靠）是**真实工程项**，Alpha 前若不完成可降级 TCP/可靠通道-only（牺牲快照实时性）或临时用 Godot headless 当服务器（违反关键立场 2，需你裁定备选） | Headless 交付节奏 | 实现期决策点 |

**F-4（预测事件丢弃规则缺失，MEDIUM）**：客户端预测副本在本地 Step 也会产生事件（如预测 Hit 的表现）。ADR-0006 未明文规定「预测副本事件仅用于即时表现、和解后整批丢弃、以权威事件流为准」。不补此规则，实现者极可能让表现层重复消费（双倍音效/双倍 Hitstop）。**补**：预测副本事件打 `predicted` 标记，和解时全部作废重放权威事件。

---

## 5. Input / Signature Audit

### Input→Command→Sim

- 键鼠/手柄/AI **全部收敛为唯一 Command record** ✅（0010 §1 + 0007 §1 同一 InputPipe 抽象，Input Device ≠ Gameplay Logic 成立）
- 18f/4f/20f 缓冲与 Tick 定址：**无冲突**——缓冲窗口判定在 Sim 决策点（0010 §2），客户端只投递；缓冲窗口常量为 Tick（与 P-2 同体系）✅
- **F-1（HIGH）**：CmdKind 缺 `Steer`（操控型生效窗：魂御/猛虎乱舞/念龙波/星云波动剑/逆风刺——ADR-0008 SteerInput 有 API 无指令通道；ADR-0010 CmdKind 表无此项）。且鼠标连续角度量化未定义（§3 F-1b）。**补规格**：CmdKind 增 `Steer{aimQuantum, moveIdx}`；量化政策进 0001 附录

### Signature Plugin

| 检查 | 结论 |
|---|---|
| 不拥有 Sim 状态 | ✅ 0008 §1 无字段状态（T33 静态检查） |
| 不拥有 RNG | ✅ Roll100 scope 身份绑定（0008 §2-2） |
| 不直接修改 Fighter | ✅ 写点唯一（0008 §2-1，T29） |
| 不直接 EventBus 回写 | ✅ 0003 §5-2（消费者/SimContext 持有者集合不相交，T16） |
| 只经 ISimContext | ✅ 九原语 API 全集（0008 §2） |
| 写操作入确定性队列 | ✅ 0003 §4 ⑤ 入队统一结算 |
| 多签名同 Tick 顺序唯一 | ✅ FighterId 升序 × 注册序 × 事件序（T17） |
| **60 条 C 类技能覆盖** | ✅ 13 插件映射表（0008 §4）；B 类 265 条零插件依赖 |

---

## 6. Data Readiness（实测 @b48ebb7，只报告）

| 项 | 状态 |
|---|---|
| skills.csv | ✅ 487×36（文档旧数字 501/532/94 均已登记为 Drift，未污染 ADR） |
| weapons.csv | ✅ 73×13 |
| class-base.csv | ✅ 25×**13**（balance-sheet README「12 列」漂移未修，已登记） |
| 散人池 | ✅ 96 |
| hitbox 17 kind | ✅ ADR-0002 §4.3 白名单已承认；3 行自由文本 = fail-fast 项 |
| status 自由文本 | ✅ ADR-0002 §4.4 opaque 通道（TextEffect），枚举化=实现期工作池 |
| cancel 枚举 `-`/`counter` | ⚠️ 编译器按字面接受；语义补录待 OQ-8 |
| 几率字段 | ⚠️ 14 行；框架就绪（Chance 有理数），**裸「几率」6 行无数值 = OQ-6** |
| 单位混用 | ❌ `SA:12-26s`（OQ-2）+ `invincible:地底16-146f` 等 → **fail-fast 阻断项（F-5）** |
| F1 | 二轮挂起（空中快刺形态 vs 接受三刺）；对管线唯一影响=0010 指令集可选增量 |
| **ArenaDef 缺失** | ❌ **新发现 F-7：竞技场布局（形状/掩体/可破坏物/高台）无 Canonical 文件**——TR-arena-001 的数据源不存在，TerrainSystem 实现前必须建立 `arena.csv`（走 ADR-0002 补丁流程） |

---

## 7. Open Questions Classification（A=完全不阻塞 / B=Core 不阻塞但实现对应系统前必须裁定 / C=阻塞下一阶段实现）

| OQ | 分类 | 说明 |
|---|---|---|
| OQ-2 `SA:12-26s` 本意 | **C**（Data Compiler 阶段） | 该行 fail-fast——需裁定后数据补丁，Compiler 才能全量通过 |
| OQ-4 加点模型 vs D37 | **B**（Loadout 实现前） | 接口可承载任意模型 |
| OQ-5 `invincible:0f` | **C**（Data Compiler 阶段，1 行） | 同 OQ-2 |
| OQ-6 几率数值补录 | **B**（对应 6 技实现前；框架就绪） | 不阻塞 Compiler（opaque/标记通过） |
| OQ-7 滚取消去留 | **A** | 0 消费者 |
| OQ-8 counter 取消语义 | **B**（2 技实现前） | 编译器按字面接受 |
| OQ-9 跨技能变异归属 | **A**（现）/ B（KNI/ROG 实现前） | 0008 §3 双路径框架已备 |
| OQ-11 部署与匹配服务形态 | **B**（Beta 前；Alpha 自建/训练模式不阻塞） | 含 P-5 传输实现决策 |
| F1 二轮 | **B**（BMG 空中普攻实现前） | 0010 协议不变 |

---

## 8. Implementation Blockers

| # | 级别 | 阻塞什么 | 解除条件 |
|---|---|---|---|
| B-1 | **C** | Data Compiler L1 校验通过 | 6 行格式噪声数据补丁（OQ-2/5 裁定 + 正名）——**需用户批准的数据修订轮** |
| B-2 | HIGH | Core 操控型技能实现 | F-1 规格补充（Steer Kind + 角度量化政策）——规格附录，不需新 ADR |
| B-3 | HIGH | 联机表现层 | F-4 预测事件丢弃规则成文 |
| B-4 | MEDIUM | AI 对局 Rollback 一致性 | F-3 AI 队列快照策略成文（推荐派生式） |
| B-5 | MEDIUM | TerrainSystem 实现 | F-7 ArenaDef 事实源建立 |
| B-6 | MEDIUM | ADR-0002 内部矛盾 | F-6 澄清：`12-26s` 类=「解析+标记」而非 fail（或反之），二选一成文 |
| B-7 | LOW | 回放压缩 | zstd 库加入 Allowed Libraries（tech-preferences 登记动作） |

---

## 9. Recommended Implementation Order

**「如果明天开始让 Claude Code 连续自动开发」的依赖序**（每阶段含停止门）：

### Phase 0 — 工程脚手架（0.5 天）
- **做**：arena.sln + 5 个 csproj（ADR-0009 §4）+ GdUnit4Net 接入 + CI 骨架（含 ADR-0001 §2.4 Math.* grep 门禁）+ tech-preferences 补 zstd 登记
- **依赖**：无
- **测试门**：空测试可跑；门禁脚本工作
- **停**：任一 csproj 引用方向违反依赖图

### Phase 1 — Core 数值基座（1 天）
- **做**：Fixed Q32.16 + MulShift/DivRoundHalfEven + 三系数表生成器（精确有理数→C# 常量）+ cos 阈值表 + 有序容器原语
- **依赖**：Phase 0
- **测试门**：T9（溢出）/T10（取整边界）全绿；表与 Python 参考实现逐位比对
- **停**：任何 Math.\* 出现 / 表比对失败
- **前置条件**：B-2 规格附录先行（半小时文档工作）

### Phase 2 — Data Compiler（1 天）
- **做**：ADR-0002 九段管线 + JSON Emitter + L1-L3 校验
- **依赖**：Phase 1（Fixed）+ **B-1 解除（数据补丁批准）** + F-6 澄清
- **测试门**：双编译逐字节一致（--verify）；487 行全量通过 L1/L2；dataVersionHash 稳定
- **停**：任何静默修正冲动 / 校验失败被绕过

### Phase 3 — Skill Runtime + 战斗基座（3–5 天）
- **做**：SimWorld/Fighter 状态机/SkillRuntime（四 active 模式）/HitResolve/Gates/Status/Projectile/Terrain（**依赖 B-5 ArenaDef**，可先用白盒圆形场地参数）/CmdStream——**迁移白盒审计为 T11/T12**
- **依赖**：Phase 1+2
- **测试门**：T1/T2/T11/T12 全绿（BMG 全技能复现白盒结果）
- **停**：T1 出现任何分歧 / 魔法数值入库

### Phase 4 — Signature + AI（2–3 天）
- **做**：ISimContext/EventBus/签名框架 + 13 插件逐个（先 BMG.Orbs 验证管线，再 SUM.Legion 最大）+ AI 三层
- **依赖**：Phase 3 + OQ-9 框架（OQ-6 补录解锁几率技）
- **测试门**：T13–T19/T29–T33/T49–T53；C 类 60 技逐条冒烟（T32）
- **停**：插件出现字段状态 / Roll100 流键污染

### Phase 5 — Replay + 本地闭环（1–2 天）
- **做**：ReplayFile v1 Recorder/Player + 关键帧缓存 + 事件流 hash 断言
- **依赖**：Phase 3/4
- **测试门**：T44–T48（含容量 ≤1MB 实测）
- **停**：重演 hash 不一致（=确定性违约，必须归因）

### Phase 6 — Presentation（2–3 天，可与 Phase 5 并行）
- **做**：Arena.Client 工程（ADR-0009 场景树）+ SimViewSync/插值/FeedbackSystem 五要素/训练模式木桩
- **依赖**：Phase 3（+5 播放）
- **测试门**：T20–T24；**实机手感层补测（白盒假设 3：投票 ≥70%）——需 Windows 侧**
- **停**：Fixed→float 泄漏出 SimViewSync / 表现回写 Sim

### Phase 7 — Network（3–5 天）
- **做**：INetTransport 双实现（含 P-5 决策）+ 会话/握手/时钟同步（P-1~P-4 协议规格先行成文）+ Net.Predict/Rollback
- **依赖**：Phase 3/4/5 + **P-1~P-4 协议规格文档** + OQ-11（部署形态可缓）
- **测试门**：T25–T28/T34–T38
- **停**：预测路径触达 Roll100 / 和解后状态与权威有差

### Phase 8 — 收尾整合（1–2 天）
- **做**：Match.Flow 六模式/结算面板/新手教程骨架/平衡 Telemetry
- **依赖**：全前序
- **测试门**：全量 T1–T53 回归 + 端到端 AI 对局 10 万局采样（GDD §28.4 平衡模拟，Beta 门槛）

**总计**：核心路径 ≈ 12–18 个工作日的自动开发量（不含 Windows 侧手感验证与美术）。

---

## 10. Final Gate Decision

```
READY WITH CONDITIONS
```

架构本体（10 ADR 组合）**无不可修复矛盾**；以下为进入 **Arena.Core 编码（Phase 1 起）前必须完成的条件**，逐条列出：

| # | 条件 | 类型 | 工作量 |
|---|---|---|---|
| C-1 | **F-1 规格附录**：Command 增 Steer Kind + 鼠标瞄准角量化政策（写入 ADR-0001 附录/0010 修订，非新 ADR） | 文档补充 | ~0.5h |
| C-2 | **F-6 澄清**：ADR-0002 对格式噪声行的处置二选一（解析+标记 vs fail-fast）成文 | 文档补充 | ~0.5h |
| C-3 | **B-1 数据补丁批准**：6 行格式噪声（OQ-2/5 裁定 + 正名）走 Skill-Spec 补丁流程——**需要你的裁定** | 用户裁定+数据补丁 | 裁定后 ~0.5h |
| C-4 | **F-3/F-4 成文**：AI 队列快照策略（推荐派生式）+ 预测事件丢弃规则，分别补入 0007/0006 实现注记 | 文档补充 | ~0.5h |
| C-5 | **F-7 ArenaDef 事实源**：TerrainSystem 实现前建立 arena.csv（可后置到 Phase 3 中段） | 数据新建 | ~1h |

不阻塞但随阶段必须完成：P-1~P-4 网络协议规格（Phase 7 前）、OQ-6 几率数值（几率技前）、OQ-4（Loadout 前）、zstd 库登记（Phase 5 前）。

**按 C-1/C-2/C-4（纯文档补充）+ C-3（你的数据裁定）完成后，即可进入 Phase 0 工程脚手架——Arena.Core 的确定性契约在文档层面已闭合。**

---

*审计方法：10 ADR 全文交叉比对 + 白盒/CSV 实测引用复核 + 敏感点 grep 定位（TargetTick 容差/ring 深度/流键/快照清单）。全程只读，未修改任何文件。*
