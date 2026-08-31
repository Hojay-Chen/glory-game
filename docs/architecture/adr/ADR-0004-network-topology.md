# ADR-0004 — 网络形态：Server Authoritative + ENet + Arena.Headless 纯 .NET 服务器

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0004 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Infra.Net + Arena.Headless |
| 上游 | ADR-0001（Determinism Contract/回溯）、ADR-0003（事件通道映射/EventId 去重）、ADR-0009（ISimDriver.DedicatedServer）；TR-net-001/004/006；GDD §24.1/24.4/24.6 |
| 后续 ADR | ADR-0006（预测/和解：客户端侧协议消费） |

---

## 0. 背景

GDD §24.1 已定原则（Server Authoritative/60Hz 权威/状态同步/服务端判定），architecture.md 已定 Arena.Headless 纯 .NET 服务器（关键立场 2）。本 ADR 固化网络拓扑、会话生命周期、通道实现与反作弊挂接。

## 1. 拓扑与会话

```
Arena.Client(Godot) ←── ENet ──→ Arena.Headless（纯 .NET 控制台，ISimDriver.DedicatedServer）
   ↑ 输入：仅 Command 流（Tick 定址，可靠通道）        │ Sim=Arena.Core（同一套代码）
   ↓ 快照(20Hz unreliable)+事件(可靠有序)+部署物       │ Net.Rollback ring buffer
```

1. **服务器是唯一权威**：客户端零判定权（TR-net-001）；客户端预测副本仅本机表现（ADR-0006）
2. **传输层选型：ENetMultiplayerPeer**——Godot 内置、可靠+不可靠双通道原生支持、UDP 底座；Client 侧由 Infra.Godot 适配，Headless 侧用 ENet 的纯 C# 替代实现接口（`INetTransport` 抽象，两实现同协议）——协议层（封包/序列化）在 Arena.Infra 纯 C#，平台绑定被隔离
3. **会话生命周期**：`Matchmaking(轻量,OQ-2 缓) → Handshake(版本:dataVersionHash+EVENT_PROTOCOL_VERSION 不匹配拒接，ADR-0002/0003) → Loadout 同步(双方构筑，D15 信息公平投影) → Sim 初始化(seed 分配) → 对局(§2 通道) → 结算(Telemetry+Replay 归档) → 断线处置(§5)`
4. **服务器 Tick 节奏**：60Hz 传输节奏（ADR-0009 §2），Sim 结果只依赖 Tick 序号

## 2. 通道实现（承接 ADR-0003 §6 映射）

| ENet 通道 | 协议内容 | 可靠性 | 频率 |
|---|---|---|---|
| ch0 输入 | `CommandPacket{tick, commands[]}`（客户端→服务器，仅此方向） | 可靠有序 | 事件驱动（≤60/s） |
| ch1 快照 | `SnapshotPacket{tick, 插值字段}`（移动流 20Hz） | 不可靠 | 20Hz |
| ch2 事件 | `EventPacket{events[], 首尾 EventId}`（可靠有序；**全部 SimEvent**） | 可靠有序 | 事件驱动 |
| ch3 会话 | 握手/Loadout/部署物/心跳/结算 | 可靠有序 | 事件驱动 |

- 封包确定性：定长字段序（Schema 常量表）、整数 Raw 直写、`CultureInfo.InvariantCulture`（承接 ADR-0002 §3.1 纪律）
- **事件环**：服务器保留最近 720 Tick（12s）事件缓冲，供晚加入/重连补齐（ADR-0003 §6-3）
- 带宽预算：快照 20Hz×2-10 实体×~80B ≈ 3-16KB/s + 事件突发——预算内（GDD §24.4 无逐字节指标，实现期实测校准）

## 3. 反作弊挂接（TR-net-006，管线归 ADR-0005 §4）

1. **输入校验**（服务器收包即查）：Tick 单调性、指令频率上限（无输入压缩炸弹）、TargetTick 容差（ADR-0001 Command 不变式）、载荷白名单
2. **服务端权威数值**：CD/MP/位移全部 Sim 内裁决——客户端指令只是意图，越权意图自然被 Sim 拒绝（如 CD 未好 → SkillCast 失败事件）
3. **Replay 审计**：每局 Replay 服务端归档，离线重演检测帧级异常（ADR-0005 §4）
4. 移动距离/速度校验内生于 Sim（确定性运动学不可能超速——无需外挂检测器）

## 4. 断线与延迟

1. 断线重连：Handshake → 服务器发「最近 Snapshot + 事件环自队尾 Tick」→ 客户端重建预测状态
2. 掉线判负：1v1 计时放弃（§20 通用比赛流程）；团队模式等待 10s 后 AI 接管（GDD §23 三层 AI 复用）
3. 高延迟客户端：无特殊处理——预测/和解（ADR-0006）+ 回溯（同）已覆盖；服务器不做 per-client 逻辑分叉

## 5. Open Questions

- **OQ-2（服务器部署拓扑：Linux 容器/匹配服务）维持未裁定**——本 ADR 只定协议与进程形态，部署归 Beta 前裁定；OQ-11（新登记）：匹配服务形态（自研轻量 vs 第三方）——与 OQ-2 合并裁定
- OQ 其余全部维持；F1 未涉

## 6. 测试

| # | 测试 | 验证 |
|---|---|---|
| T25 | 双实现协议一致 | INetTransport 的 Godot/纯 C# 两实现互连互通，封包逐字节一致 |
| T26 | 事件环补齐 | 模拟晚加入：从环起点补齐后客户端事件序列与服务器全量一致 |
| T27 | 输入校验 | 非法 TargetTick/超频/未知 CmdKind ⇒ 服务器拒绝并断开计数 |
| T28 | 断线重连 | 重连后预测状态重建，事件序连续无缺口 |

## 附：自审（12/12）——拓扑明确✓ 纯 .NET 服务器保持✓（INetTransport 抽象隔离 ENet）通道映射与 ADR-0003 一致✓ 版本握手双 hash✓ 反作弊四层落位✓ 断线处置✓ 无新设计裁定（OQ-2 维持）✓ 带宽预算留实测✓ 序列化确定性纪律承接✓ 测试可落地✓ 未动其他文档✓
