# SPEC-0002 — AI 延迟队列的派生式确定性（F-3 闭合）

> 本文件是 **ADR-0007 的 Implementation Specification Appendix**（implementation-readiness-audit-v1 C-4）。不修改 AI 玩法设计（GDD §23）；不扩大 Snapshot。

## 1. 判定结论

**AI reaction-delay queue 是可完全派生的状态（derived state），不进入 Snapshot。**

推导：AI 决策函数的输入只有四类——

```
AICommand(t) = Decide( SelfView(t−D), EventHistory(≤t), AIParams(difficulty), AIStreamRolls )
```

| 输入 | 来源 | 已被 Snapshot 覆盖？ |
|---|---|---|
| SelfView(t−D)（延迟 D 的自己视角） | 模拟历史（命令流的确定函数） | 历史可由重演重建；运行时由 AI 自持的环形视图缓存提供 |
| EventHistory | SimEvent 流（确定） | ✅ 重演重算 |
| AIParams（难度权重/延迟/行为池） | Catalog 数据（ADR-0002 管线） | ✅ 数据面 |
| AIStreamRolls | ADR-0001 §4 RNG 流（AI_DECISION 类） | ✅ 计数器在 Snapshot（RNG State） |

因此：

```
Same Snapshot(任意历史起点)
+ Same AI parameters
+ Same RNG state（含 AI 流计数器）
+ Same Command Stream（含该 AI 已产出的历史指令）
⇒ Same AI-generated Commands（逐位）
```

**实现规范**：

1. AI 队列 = AI.Controller 运行时的**临时环形缓冲**（自视角快照 + 延迟队列），标记 `derived, rebuildable`——进程重启/快照恢复后从模拟历史重建，不是事实源
2. **AI 产出的 Command 与玩家指令同权进入录制命令流**（ADR-0005）——回放**不重跑 AI**（直接重放已录指令）；AI 队列确定性只在「训练分析重跑 AI 决策」时被消费
3. 服务器 Rollback 不重算 AI 决策（决策一次性，指令已录）；回溯仅影响命中判定（ADR-0006）
4. `AI_STREAM` 为 ADR-0001 §4.1 AMBIENT 类下的独立键空间（键 = (AI_DECISION, FighterId)），与 SKILL_CHANCE/UNIT_AI 互不污染
5. 效用并列打破序：行为 ID 升序（ADR-0007 §3 已定，此处重申为实现契约）

## 2. 快照边界声明（对 ADR-0001 §8.2 的澄清）

ADR-0001 §8.2 清单**不新增**字段；AI 队列以「派生状态」身份满足完备性原则——其全部输入（RNG 计数器/命令流/Catalog 参数）已在清单内。实现注记需在 AI.Controller 代码头注释引用本 spec。
