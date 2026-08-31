# ADR-0010 — 输入系统：键鼠/手柄映射与 CmdStream 序列化

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0010 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Client（输入采样/映射）+ Arena.Core（CmdStream 契约） |
| 上游 | ADR-0001（Command record/不变式）、ADR-0003（通道）、ADR-0009（InputPipe 接入点）；TR-feel-001；GDD §21（键鼠/手柄方案）、§4.2（段间缓冲 18f）、§8.2（取消窗）、§10.3（受身方向判定）、§10.4（强制中断） |
| 边界 | F1 二轮裁定（空中快刺形态）若成立 → 本 ADR 指令集增加空中形态条目（数据行，协议不变） |

---

## 0. 背景

architecture.md 已定义 `Command` record（CmdKind/SkillId/DirIndex/TargetTick）与「指令只表达意图、合法性由 Sim 裁决」不变式。本 ADR 定义：物理输入如何映射为 Command、缓冲语义、手柄方案、序列化格式。

## 1. 映射表（默认键位，全可重映射）

| CmdKind | 键鼠默认（GDD §21.1） | 手柄默认（§21.2） | 说明 |
|---|---|---|---|
| Move | WASD（8 向量化） | 左摇杆（死区 0.25 → 8 向量化） | DirIndex=8 向 |
| Jump | 空格 | A/交叉键 | §3.3 |
| Roll（翻滚/受身共用键） | Shift | B/圆圈键 | §10.1/10.3——倒地窗口内即受身（方向判定：输入方向与摔倒方向夹角 ≤90°，Sim 裁决） |
| Skill 1–0 | 数字键 1–0（10 键） | 十字键+肩键组合（10 槽） | SkillId 经 Loadout 槽位映射（携带技能槽 → skillId） |
| Basic Attack | 鼠标左键 | X/方块键 | 普攻链（自动推进段号） |
| ForceCancel | Alt（按住） | L1+R1 | §10.4 |
| Lock | Tab | 右摇杆按下 | §10.5 |
| OrbFire（职业特化） | 炫纹键 R | R1 | BMG 专属槽（职业插件定义映射表，ADR-0008） |
| Steering（操控型生效窗） | 鼠标移动 | 右摇杆 | 念龙波/魂御/猛虎乱舞的 active 期转向（SteerInput 连续量→Sim 内 8 向/角度量化） |

- 鼠标朝向：软锁定（默认，竞技推荐 §10.5）——攻击朝向=鼠标指向（世界坐标→DirIndex 量化/连续角度由 Sim 前摇 30° 修正规则消费，GDD §4.7）
- 键位重映射：Save.Prefs（ADR-0002 产物链），全键位无冲突校验

## 2. 缓冲语义（手感核心，确定性定义）

| 缓冲 | 窗口 | 语义（Sim 裁决，客户端只投递） |
|---|---|---|
| 普攻段间 | 生效帧后 **18f**（§4.2） | 窗口内输入在恢复可操作时自动衔接 |
| 普攻→技能取消 | 生效帧后 **4f** 起（§4.2） | cancel-lag 修正后仍有效 |
| 技能命中取消 | 后摇取消窗内（cancel_min_tier） | 命中确认后有效（D07 口径） |
| 受身 | 倒地 0–20f（§10.3） | 方向+时机双判定 |
| 强制中断 | 后摇任意帧 | 立即生效 |

**客户端缓冲实现**：输入到达即入 `InputPipe` 队列（带到达 Tick）；Sim 在每个判定点检查队列中是否有满足窗口的指令——**缓冲窗口判定在 Sim 内**（与帧数据同一 0 帧误差标准），客户端只负责不丢输入（队列无界上限 64 条，超出丢弃最旧并计数告警）。

## 3. 网络投递

- 采样于渲染帧（ADR-0009 `_Process`），Command 组装后按当前 Tick 定址立即发送（可靠通道 ch0，ADR-0004）
- **不攒批**（每渲染帧至多一条 CommandPacket，内含该帧全部新输入）；服务器容差：TargetTick 落后于服务器当前 ≤10 Tick 接受（超出 = 输入校验拒绝计数，TR-net-006）
- AI 产出 Command 走同一 InputPipe 抽象（P4 同权）——反应延迟（400/250/160/120ms）实现为 AI 侧输入延迟队列

## 4. 序列化格式

```
CommandPacket { tick:int64, count:byte, commands[]: {kind:byte, skillId:u16, dirIndex:byte, reserved:byte, targetTick:int64} }
```
- 定长 16B/条、定字段序（IntrinsicCulture 无涉、无字符串）——确定性序列化，与 ADR-0003 事件封包同纪律
- 手柄摇杆连续量 → DirIndex 8 向量化的阈值表为**编译器常量**（死区/角度分界），两端一致

## 5. F1 二轮关联（不裁定）

若 F1 二轮裁定「空中快刺形态」：新增空中普攻形态=数据行+一条 CmdKind 分支（Basic 的 airborne 变体，Sim 按状态路由）——**协议结构不变**；若裁定「接受三刺」：本表零改动。

## 6. 测试

| # | 测试 | 验证 |
|---|---|---|
| T39 | 缓冲窗口 | 18f/4f/20f 窗口边界 ±1 帧的表驱动用例，Sim 判定唯一 |
| T40 | 受身方向 | 8 向输入 × 摔倒方向矩阵 ⇒ 90° 判定边界确定 |
| T41 | 重映射 | 自定义键位下 Command 输出与默认键位逐位一致 |
| T42 | 序列化 round-trip | CommandPacket 全 Kind 编解码逐位一致；跨端（Client/Headless）一致 |
| T43 | 手柄量化 | 摇杆死区/角度分界阈值表两端一致 |

## 附：自审（10/10）——映射表完整覆盖 CmdKind✓ 缓冲语义 Sim 裁决（0 帧误差口径）✓ 网络投递不攒批+容差明确✓ 序列化确定性（16B 定长/无字符串）✓ 手柄 8 向量化阈值表编译器常量✓ AI 同权走 InputPipe（P4）✓ F1 关联不裁定✓ 重映射入 Save.Prefs✓ 受身方向判定边界成文✓ 测试表驱动可落地✓


---

## Errata（2026-09-01）

**CmdKind 增补 `Steer`**（F-1 闭合）：操控型生效窗的连续意图采样——`AimQuantum:u16`（0..65535=360°，RoundHalfEven 量化，wrap 与零角约定见 SPEC-0001）+ DirIndex 移动意图。定义全文：`docs/architecture/spec/SPEC-0001-steer-command-and-aim-quantization.md`（ADR-0010/ADR-0001 的 implementation specification appendix）。鼠标连续角在客户端表现层量化为 uint16 后进 Command，Core 只见整数。
