# SPEC-0003 — Prediction Event 生命周期（F-4 闭合）

> 本文件是 **ADR-0006 / ADR-0003 的 Implementation Specification Appendix**（implementation-readiness-audit-v1 C-4）。边界重申：Hitstop 与网络补偿无关（用户修正 2026-08-31）。

## 1. 事件两界

| | 预测事件（Predicted Event） | 权威事件（Authoritative Event） |
|---|---|---|
| 产生者 | 客户端预测副本的本地 Sim（ADR-0009 OnlinePredicted） | 服务器权威 Sim |
| 标记 | `event.predicted = true` | `predicted = false` |
| 作用 | **仅即时表现**（本机出手动画/起手反馈） | 表现 + 签名 + 统计 + 录制的唯一事实 |
| 生命周期 | 和解时**整批作废** | 永久（重演可复算） |

预测副本**不消费战斗 RNG**（ADR-0001 §4.1）⇒ 预测事件中凡含几率/命中的结算均不产生——预测事件实际限于：SkillCast/BasicStep/ActEnded/移动派生表现。**命中类事件永远只有权威一份**。

## 2. 生命周期（状态机）

```
Predicted Event 产生（本地 Step）
   → Pending Presentation：表现层立即渲染（带 predicted 标记：音效/动画可即时，伤害数字/Hitstop 延迟判定）
   → Reconciliation（权威 Snapshot(s) 到达）：
       ① 作废：丢弃本地所有 predicted=true 且 Tick ≤ s 的事件（含 Pending）
       ② Replace：重放权威事件流中 Tick ≤ s 的事件
   → Authoritative Event：此后仅权威事件驱动表现
```

## 3. 防重复消费规则（表现层去重）

**问题**：本机出手在预测期已播过 CAST 音效/动画；权威 SkillCast 到达时会再触发一次 → 双倍。

**规则（按语义身份去重，而非 Tick）**：

```
表现层维护 RecentTriggered 集合，键 = (Kind, ActorId, SkillId, SegmentIndex)
  （即 ADR-0003 SemanticKey；Tick 不入键——服务器的回溯/ Tick 偏移会使权威 Tick ≠ 预测 Tick）
权威事件到达时：
  键 ∈ RecentTriggered（TTL 1s 内）→ 抑制重复表现（快照状态照常更新）
  键 ∉ → 正常触发
预测事件作废时：从 RecentTriggered 移除其键（若权威随后到达同键事件则重新触发一次完整表现）
```

- **Hitstop**：仅由**权威 Hit** 触发（预测期不进入 Hitstop——预测命中本就不产生，无此冲突）
- **SFX/VFX**：预测期播「出手侧」（挥击/吟唱起手）；「命中侧」（打击音/受击反馈/伤害数字）只由权威 Hit 触发——出手/命中天然分属两界，再以 RecentTriggered 兜底
- **CameraShake**：同 Hitstop（权威触发）
- 伤害数字：一律权威（数值正确性优先于即时性，且预测期本无权威数值）

## 4. 不变式

1. 预测事件**永不**进入 EventBus 的权威订阅通道/Telemetry/ReplayWriter（仅表现层的预测通道可见）
2. 和解后本地预测副本与权威逐位一致（ADR-0001 契约）⇒ 作废窗口内不会有「预测对了但被丢弃导致表现缺失」——权威事件立即补齐同一事实
3. 断线/重连：预测缓冲全清，重建后从权威快照+事件环恢复（ADR-0004 §4-1）
