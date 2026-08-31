# ADR-0005 — 回放文件格式与反作弊审计管线

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0005 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Infra（Replay.Recorder/Player + Audit.AntiCheat） |
| 上游 | ADR-0001（Determinism Contract/事件流 hash 诊断）、ADR-0002（dataVersionHash）、ADR-0003（EVENT_PROTOCOL_VERSION/事件不记录原则）、ADR-0009（ISimDriver.ReplayPlayback）；TR-net-005/006；GDD §24.5/24.6、§20.7（观战与 Replay） |
| 挂接 | 训练模式四件套（TR-feel-006：镜像对战/名场面挑战 = Replay 通道复用） |

---

## 0. 背景

GDD §24.5 定了框架：回放 = 输入流 + 随机种子（确定性重演）<1MB/局、帧级步进/0.25×/双视角/覆盖层、可作反作弊证据。ADR-0001/0003 已提供数学基础（重演重算、事件不记录、事件流 hash 诊断）。本 ADR 固化文件格式与审计管线。

## 1. 文件格式（ReplayFile v1）

```
Header（定长）
  magic "ARPL" | formatVersion:u16 = 1
  matchSeed:u64 | dataVersionHash:32B | eventProtocolVersion:u16 | replayFlags:u16
  mode:byte (1v1/2v2/3v3/5v5/训练) | fighterCount:byte
  loadouts[]：fighterId, classId, weaponId, skill 槽表（信息公平投影口径，D15）
  tickCount:u32 | eventStreamHash:32B（录制端事件流 SHA-256，诊断用，ADR-0003 §7）
Body
  command 差分流：逐 Tick 记录【变化的指令集】——
    无输入 Tick 跳过（游程编码）；有输入 Tick = {tick:u32, count:byte, commands[16B/条]}
  （编码 = ADR-0010 CommandPacket 的 Tick 稀疏化）
Footer
  zstd 压缩 Body | trailer: bodyCrc32 + footerLen
```

- **容量预算**：实测型估算——活跃输入密度 ~30%（竞技对局非每帧按键）、均值 1.5 条/Tick×16B×0.3×60×90s ≈ 2.3MB 原始 → zstd（指令流高重复）×4~6 → **≈400-600KB ≪ 1MB 预算** ✓；5v5 多 4 实体 ×~1.8 → 上限 ~1MB 贴边，保留 zstd level 调节余量（实现期实测校准，超限先升压缩级别不砍语义）
- **事件不记录**（ADR-0003 原则）：事件由重演重算；eventStreamHash 仅诊断——重演 hash ≠ 录制 hash ⇒ **确定性违约的精确定位证据**（ADR-0001 T1/T3 工程化）
- 双视角/自由镜头：视角是**消费侧**概念（同一重演流 + 不同观察者投影），文件不含视角数据

## 2. 录制（服务器权威侧）

- Recorder 订阅 CmdStream（Match 装配时挂接）→ Header + 差分流 → 局结束 Footer + zstd → 服务器归档（反作弊原始证据 + 玩家侧下载源）
- 录制在 worker 线程（DF-1 线程边界）；录制失败不阻塞对局（记 Telemetry 降级事件，本地模式回放功能降级为「无录制」）

## 3. 播放（ISimDriver.ReplayPlayback，ADR-0009）

```
加载 → 校验 Header（magic/版本/dataVersionHash/EVENT_PROTOCOL_VERSION——任一不匹配显式拒载）
  → 重演：ReplayReader 按文件指令流喂 ISimDriver → Sim 重算事件
  → 消费：帧级步进（逐 Tick 停）/0.25× 慢放（拉长渲染展示，Tick 照常）/双方视角切换/自由镜头
  → 覆盖层：帧数据叠加 = 当前 Tick 快照投影（SkillDef 表查询），零额外数据
  → 校验：重演 eventStreamHash vs Header.recorded hash（不一致 = 违约报告，证据级）
```

- **进度条/拖动**：跳至任意 Tick = 从头重演至目标 Tick（确定性保证 O(t) 重演，90s 局全量重演 <1s——白盒实测吞吐推算）+ 可选关键帧快照缓存优化（每 600 Tick 存 Snapshot，拖动就近恢复）
- 镜像对战 = Replay 指令流喂给含玩家输入的混合驱动（训练模式，TR-feel-006）

## 4. 反作弊审计管线（TR-net-006 第 4 层）

```
服务器归档 Replay → 离线审计批处理：
  ① 重演校验：重演 eventStreamHash == Header hash（数据/协议一致性）
  ② 输入合理性：Tick 单调/频率上限/TargetTick 容差（服务器已实时拒，此处复算留档）
  ③ 行为审计：重演产出的移动距离/CD/MP 消费逐 Tick 复核（Sim 权威数值 vs 输入意图）
  ④ 举报驱动 + 抽样全量（高分段 100%）
违规 ⇒ 违规报告（Tick 粒度证据链：输入 + 重演状态 diff）→ 人工复核队列
```

- 前三层（权威/输入校验/服务端数值）在 ADR-0004 §3 实时执行；本管线是**第 4 层离线证据链**——两层共用同一确定性重演机制

## 5. Open Questions

- OQ-12（新登记）：Replay 存储保留策略（全量归档时长/玩家侧下载窗口）——运营决策，Beta 前定
- 其余 OQ 维持；F1 未涉

## 6. 测试

| # | 测试 | 验证 |
|---|---|---|
| T44 | round-trip | 录制 → 重演：事件流 hash 逐位一致；重演终态快照 == 录制终态 |
| T45 | 容量 | 5v5 满强度 90s 对局录制 ≤1MB（超限告警） |
| T46 | 版本拒载 | dataVersionHash/EVENT_PROTOCOL_VERSION 任一不匹配 ⇒ 显式拒载 |
| T47 | 违约定位 | 注入确定性违约 ⇒ eventStreamHash 不匹配 + 首 diff Tick 定位报告 |
| T48 | 随机访问 | 关键帧快照跳转与全量重演结果一致 |

## 附：自审（10/10）——格式字段完备（seed/双版本/loadouts/tickCount）✓ 事件不记录原则承接✓ 容量预算有算式与实测计划✓ 稀疏差分编码✓ 拒载显式（三版本/hash）✓ 违约定位机制（事件流 hash）✓ 反作弊四层闭环（实时 3 层 + 离线证据链）✓ 训练模式复用✓ 关键帧优化不影响确定性✓ 录制降级不阻塞对局✓
