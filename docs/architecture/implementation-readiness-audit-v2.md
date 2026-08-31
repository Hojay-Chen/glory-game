# Implementation Readiness Audit v2 — Pre-Implementation Closure 报告

| 项 | 值 |
|---|---|
| 版本 | v2.0（closure） |
| 日期 | 2026-09-01 |
| 基线 | 起点master `9094d19`（audit-v1）→ 完成后 HEAD 见文末 |
| 上游 | implementation-readiness-audit-v1.md（F-1~F-7/B-1~B-7） |
| 性质 | 关闭 v1 审计的实现阻塞项（规格附录 + 数据格式补丁 + 事实源建立），然后复审 |

---

## 1. 闭合项逐条核销（对应 v1 审计）

### F-1 Steer Command + 鼠标角度 ✅ CLOSED
- **SPEC-0001**（ADR-0010/0001 appendix）：`CmdKind.Steer{AimQuantum:u16, DirIndex, TargetTick}`；瞄准角 uint16 0..65535=360°、`RoundHalfEven(deg×65536/360) mod 2^16`、零角=+Z 顺时针、wrap 唯一实现 `((b−a+32768) mod 65536)−32768`、转速整数饱和步进；鼠标 float 角在客户端表现层量化后进 Command，**Core 只见 uint16**；Server/Client/AI/Replay/Prediction 五路径同一编码
- ADR-0010 Errata 已挂指针；invincible/startup/active 同批格式正名（PATCH-001）

### F-3 AI 延迟队列 ✅ CLOSED（派生式，Snapshot 不扩）
- **SPEC-0002**（ADR-0007 appendix）：AI 队列 = (Snapshot 历史 + AIParams + AI 流 RNG 计数器 + Command Stream) 的**确定性派生状态**——四输入全部已在 ADR-0001 §8.2 清单内，队列本身为可重建临时缓冲；AI 产出指令与玩家同入录制命令流（回放不重跑 AI）；AI_STREAM 为 AMBIENT 类独立键空间
- 契约成文：`Same Snapshot + Same AI params + Same RNG state + Same Command Stream ⇒ Same AI-generated Commands`
- ADR-0007 Errata 已挂指针

### F-4 预测事件生命周期 ✅ CLOSED
- **SPEC-0003**（ADR-0006/0003 appendix）：`predicted` 标记 → Pending Presentation → Reconciliation 整批作废 → 权威事件补台；去重键 = SemanticKey（不含 Tick，免疫回溯偏移）+ RecentTriggered TTL 1s；**命中侧表现（伤害数字/Hitstop/受击反馈/镜头）只由权威事件触发**，出手侧可预测即时；预测事件不入 EventBus 权威通道/Telemetry/ReplayWriter
- ADR-0006 Errata 已挂指针

### F-6 ADR-0002 内部矛盾 ✅ CLOSED
- ADR-0002 Errata E-1 统一规则（三判据分流）：**单位歧义 → fail-fast**（`SA:12-26s` 属此类）/ 格式超白名单但语义唯一 → Canonical 化 + L2 标记 / 语义合法但疑似笔误 → 进入 RuntimeDef + `pendingSemanticReview` 工作池
- 不允许静默修正、不允许猜语义、Compiler 不改数据——三条原则不变

### B-1 Compiler 数据阻塞 ✅ 解除至「3 行待裁定」
- **PATCH-001（19 处纯格式规范化，语义零变更）**：startup 注释剥离×1、invincible 前缀剥离×8（含 `地底` 状态词移入既有 special 语义）、active 形态词正名×8（维持/持续→hold、可控→controlled、飞行→projectilePhase）、hitbox `self/-`→`self`×1
- provenance：`docs/skill-spec/data-patch-log.md`（逐条旧值→新值→理由）+ git 同提交；dataVersionHash 经 ADR-0002 机制自然变更
- 复扫结果：**L1 fail-fast 残留 = 3 行，全部为设计裁定挂起**（见 §4）；balance_audit 重跑 PASS（396 DPS/TTK 63s/超线 0——数值面未动）
- 未修改项（设计裁定挂起，见 §4 未修改清单）

### B-7 zstd ✅ CLOSED
- tech-preferences Allowed Libraries 登记：ZstdSharp（MIT 纯托管，候选首选）或 ZstdNet；压缩仅 Replay Body，**不参与 dataVersionHash**（hash 对未压缩语义流）

### F-7 ArenaDef ✅ CLOSED
- **SPEC-0004**（ADR-0002 appendix）+ **`docs/balance-sheet/arena.csv` 新建（26 行 × 13 列）**：ARENA001 百炼竞技场全量对象（结界墙矩形 60×84/中央擂台 r8m/双高台/双坡道/掩体墙×4 800HP/立柱×4/木箱陶罐石块/出生点对角对称），交互枚举、校验规则（spawn 对称/连通性）、量化规则齐备
- 坐标推定值标注 `TODO-GAP`（GDD 只给尺寸与相对布局，精确坐标归美术白盒）；**TerrainSystem 本阶段未实现**（按指令）
- ADR-0002 Errata E-2：arena.csv 加入 dataVersionHash 输入（第 4 个 CSV）

## 2. 第二次复审（v1 §9 十项检查）

| # | 检查 | 结果 |
|---|---|---|
| 1 | F-1 完全闭合 | ✅ Steer Kind + AimQuantum + 五路径统一编码 + 转速整数饱和 |
| 2 | 鼠标角度完全整数化 | ✅ uint16 quantum；float 仅客户端表现层 atan2 瞬时计算，量化后即弃 |
| 3 | AI queue 满足 Snapshot Determinism | ✅ 派生闭包（四输入均在快照/数据面） |
| 4 | F-4 生命周期闭合 | ✅ predicted 标记/作废/权威补台/语义去重全成文 |
| 5 | F-6 无矛盾 | ✅ 三判据分流表唯一化；12-26s 归 fail-fast（OQ-2） |
| 6 | B-1 解除 | ✅ 至 3 行待裁定（原 6+ 行）——Phase 2 编译需 OQ-2/OQ-13 裁定后完成最后数据补丁 |
| 7 | ArenaDef Canonical Source | ✅ arena.csv 26×13 + SPEC-0004 schema/校验 |
| 8 | zstd 登记 | ✅ tech-preferences（双候选） |
| 9 | Core determinism blocker | ✅ 无残余（F-1/F-3 闭合后全路径定点/整数化） |
| 10 | Command protocol blocker | ✅ 无（Steer 补齐；P-1~P-4 网络协议细节为 Phase 7 前实现规格项，非 Core 阻塞） |

**新登记**：OQ-13（hitbox 运行时决定判定体语义 2 行：星云波动剑/乱雷——待裁定后转 controlled/scripted kind）；OQ-5 范围精确化（`0f` 一行，解析+L2 flag 通过，意图待裁）。

## 3. 修改/新增清单

- **新增规格**（4）：`docs/architecture/spec/SPEC-0001-steer-command-and-aim-quantization.md` / `SPEC-0002-ai-queue-determinism.md` / `SPEC-0003-prediction-event-lifecycle.md` / `SPEC-0004-arena-def-source.md`
- **Errata**（4 ADR）：ADR-0002（E-1 统一规则/E-2 arena.csv 入 hash/E-3 白名单增补）、ADR-0006、ADR-0007、ADR-0010（指针+条款）
- **数据修改**：skills.csv 19 处纯格式（PATCH-001，见 data-patch-log.md）；**新建** docs/balance-sheet/arena.csv（26×13）
- **配置**：tech-preferences.md（zstd 登记）
- **未修改数据**（设计裁定挂起）：`SA:12-26s`、`invincible:0f`、hitbox 自由文本 2 行、裸「几率」4 行、status 自由文本 ~40 种（opaque 通道承载）、notes 白盒注释
- **未创建**：任何新 ADR / 任何实现代码 / TerrainSystem / Godot 工程

## 4. Remaining Open Questions（全部维持未裁定，归用户）

| OQ | 内容 | 分级（v1 口径） |
|---|---|---|
| OQ-2 | `SA:12-26s` 单位/语义 | **C**（阻 Compiler 全量通过） |
| OQ-5 | `invincible:0f` 意图 | A（解析+flag 通过；该技实现前裁定） |
| OQ-13（新） | 星云波动剑/乱雷的运行时判定体语义 | **C**（阻 Compiler 全量通过） |
| OQ-4 | 加点模型 vs D37 | B（Loadout 前） |
| OQ-6 | 4 行裸几率数值 | B（对应技实现前） |
| OQ-7 滚取消 / OQ-8 counter 语义 / OQ-9 跨技能变异 | A / B / A-B | 同 v1 |
| OQ-11 + P-5 | 部署拓扑/匹配服务/纯 C# 传输实现路径 | B（Phase 7 前） |
| F1 二轮 | 空中快刺形态 vs 接受三刺 | B（BMG 空中普攻实现前） |

## 5. Remaining Blockers

| # | 阻塞 | 解除条件 |
|---|---|---|
| R-1 | Compiler 全量 L1 通过 | **用户裁定 OQ-2 + OQ-13**（3 行数据）→ 最小数据补丁 → Compiler 全绿 |
| R-2 | 无其他 Core/Command/协议阻塞 | — |

## 6. Final Verdict

```
READY FOR PHASE 0
```

- Phase 0（工程脚手架：arena.sln + 5 csproj + GdUnit4Net + CI 门禁 + zstd 依赖落地）**不依赖任何未裁定项**
- **下一步可以开始 Phase 0，但不得直接进入 Phase 1，必须先通过 Phase 0 Gate**（ADR-0009 §6：空测试可跑 + Math.\* 门禁脚本工作 + 依赖方向无违例）
- Phase 2（Data Compiler）开始前需完成 R-1：**用户裁定 OQ-2 与 OQ-13 的 3 行数据**

*本阶段未创建 ADR、未实现 Arena.Core/Fixed/SimWorld/SkillRuntime/Network/Replay/Godot Client、未修改 GDD/游戏设计、未裁定 OQ-4/6/8/9/11 与 F1 二轮、未改动 60 Tick 架构与 Design Frame/Runtime Tick 分离原则、未引入 float 到 Core。*
