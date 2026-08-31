# SPEC-0001 — Steer Command 与鼠标瞄准角确定性（F-1 闭合）

> 本文件是 **ADR-0010 / ADR-0001 的 Implementation Specification Appendix**（implementation-readiness-audit-v1 C-1）。不引入新 ADR；与两 ADR 冲突时以两 ADR 为纲、本 spec 为细则。

## 1. Steer Command（补齐 ADR-0010 CmdKind 表）

```csharp
// ADR-0001 Command record 增补 Kind：
//   CmdKind.Steer —— 操控型生效窗（controlled active）期间的连续意图采样
public readonly record struct Command(
    CmdKind Kind,          // …| Steer
    ushort SkillId,        // 被操控的技能（魂御/猛虎乱舞/念龙波/星云波动剑/逆风刺）
    ushort AimQuantum,     // 瞄准角，§2 量化（Kind=Steer 时有效）
    byte DirIndex,         // 移动意图 8 向（Steer 期间可同时移动）
    int TargetTick);
```

- **旧字段兼容**：`angle_deg` 位由 `AimQuantum` 取代——原 `byte DirIndex` 保持移动语义不变
- 产生时机：操控型技能 active 阶段内，客户端每渲染帧采样一次（有变化才发）；Sim 在该技能的 `controlled` 生效窗内消费，按 §2 量化值更新判定体朝向/弹道
- 合法性：非 controlled 窗口内的 Steer = Sim 忽略（指令只表达意图）；服务器输入校验同 ADR-0004 §3

## 2. 瞄准角量化（鼠标连续角绝对禁入 Core）

```
angleIndex ∈ [0, 65535]  （uint16，全文 360°）
angleIndex = RoundHalfEven( degrees × 65536 / 360 )  mod 65536
```

| 条款 | 定义 |
|---|---|
| 零角 | `angleIndex = 0` = 世界 **+Z 方向**；**顺时针**为正（自顶向下看，与 Godot yaw 约定一致） |
| 边界 0/360° | `360°` 量化后 `= 65536 → mod 65536 = 0`——零角与整圆角**同一编码**，无边界特判 |
| wrap-around | 角差计算唯一实现：`diff = ((b - a + 32768) mod 65536) - 32768`（结果 ∈ [-32768, 32767]，即最短弧带符号差）；禁止任何其他差值写法 |
| 量化点 | 鼠标射线与水平面交点 → `atan2` 在**客户端表现层**计算（float 允许），立刻量化为 uint16 后进 Command——**Core 只见 uint16** |
| Server/Client/AI 统一 | 三者只经 Command 携带 aim；AI 的转向决策同样产出 AimQuantum（效用输出角度→同一量化式）；无任何旁路 |
| Replay/Prediction 统一 | Command 是唯一载体（重演重算/预测重演自动一致）；无独立的「角度再解析」路径 |
| Sim 内消费 | 转向角速度限制（≤90°/s 或 120°/s per 技能）以 Tick 为单位做**整数饱和步进**：`heading += clamp(diff, -maxStep, +maxStep)`，无除法无浮点 |
| 精度 | 1 quantum = 360/65536° ≈ 0.0055°——远高于 GDD §4.7 的 30° 前摇修正与 90°/s 转速的语义粒度，无手感损失 |

## 3. 与既有规则的对齐

- 前摇 30° 转向修正（GDD §4.7）：比较 `|diff| ≤ 30°×65536/360`（uint16 常量），在 `controlled`/startup 修正窗内由 Sim 应用
- 受身方向判定（§10.3，90° 扇区）：DirIndex 8 向（45° 粒度）已足够，与 AimQuantum 并存不混用
- 软锁定（§10.5）：锁定目标方向 → 同一 AimQuantum 编码，无第二路径

## 4. 测试挂钩

- 表驱动：degrees ∈ {-180, -90, -0.0028(舍入边界), 0, 359.9972, 360, 720} ⇒ angleIndex 唯一且 wrap 正确
- 转速饱和：连续 Steer 序列下 heading 步进 ≤ maxStep
- 预测/权威/重演三方 aim 序列逐位一致（并入 ADR-0001 T1/T34）
