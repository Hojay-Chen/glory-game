# ADR-0003 — 战报事件协议：SimEvent 唯一键与幂等契约

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0003 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Core（事件模型/生成/排序）+ Arena.Infra（EventBus/Transport 承载） |
| 上游 | ADR-0001（§3.2 结算总序、§3.3 变更入队、§4 RNG 流键、§9 Determinism Contract）；ADR-0002（dataVersionHash）；architecture.md（DF-1/DF-2/DF-3、EventBus、API Boundaries 幂等契约） |
| 关联 TR | TR-net-004（四通道同步）/ TR-net-005（回放）/ TR-feel-003（打击感事件消费）/ TR-live-001（统计） |
| 事实依据 | 白盒实测 26 种事件类型（prototypes/bmg-whitebox/scripts/sim.gd，HEAD d6d60a1）；GDD §22.2（控制值将满=双方共享信息）、§24.4（同步通道表） |
| 后续 ADR | ADR-0008（签名协议：OnEvent 派发细节）、ADR-0004/0006（传输与预测：本 ADR 的通道映射是其输入） |

---

## 0. 背景与问题

Sim 的对外语义输出是两条流：**Snapshot**（状态权威）与 **SimEvent**（离散事实/战报）。architecture.md 已定义事件唯一键与「一次结算恰好一个事件」的幂等意图；白盒已实证 26 种事件形态。本 ADR 固化：事件身份、封闭枚举、生成顺序、订阅契约、传输映射、与回放/RNG 的关系。

**一个有意的精化（文档同步项 D-1）**：architecture.md 原定义 `SimEvent.Id = (Tick, AttackerId, SkillId, ActiveWindowId, SegmentIndex)`——该五元组只对命中族事件有意义（LAND/WALL/BREAK 等无攻击方）。本 ADR 将身份泛化为两层（§2），**去重语义不变**，architecture.md 的表述随下轮文档同步更新。

---

## 1. Snapshot 与事件的分工（防止事件流变成第二事实源）

| 流 | 承载 | 原则 |
|---|---|---|
| **Snapshot**（TR-net-004 移动流 20Hz + 状态流 10Hz） | 一切**连续量**：位置/朝向/速度、资源当前值（HP/MP/耐力）、状态机当前态、控制值当前值 | 表现层的**状态权威**——HUD/插值读快照，不累计事件 |
| **SimEvent** | 一切**离散语义事实**：状态机迁移、命中/落空、挣脱、触发、渠道性资源变动 | 表现层的**瞬时触发器**（Hitstop/震屏/音效/伤害数字/预警）与签名/AI/统计的输入 |

推论：①MP 自然回复（20/s 连续量）**不逐 Tick 发事件**；②命中造成的 HP 变化由 `Hit` 事件载荷携带，**不另发** ResourceChanged；③事件**不携带目标状态终值**（消费方需要状态时读快照）——事件是「发生了什么」，不是「现在是什么」。

---

## 2. 事件身份与幂等

### 2.1 两层身份

```
EventId  = (Tick, SeqInTick)                    ← 全局唯一、传输去重键
SemanticKey（可选载荷字段，命中族专用）
         = (AttackerId, SkillId, ActiveWindowId, SegmentIndex)
```

- **SeqInTick**：由 ADR-0001 §3.2 结算总序在 Step 内顺序分配（0 起），**分配顺序即事件顺序**——重演天然逐位一致
- **EventId 去重**：Transport 层按 EventId 滑动窗口去重（网络重发、重放重复投递均无害）——承接 architecture.md 幂等契约
- **SemanticKey 语义**：命中族事件的业务幂等/分析键（同一 (Attacker,Skill,Window,Segment) 至多一次结算——ADR-0001 §2.4「判定时刻唯一」的对外可观测形式）；非命中族事件的业务身份即 EventId

### 2.2 不可变与载荷纪律

1. SimEvent 为**不可变 readonly record**，全部字段值类型：`long`（整数域/Fixed.Raw/ID/Tick）、封闭枚举、内嵌定长结构；**零字符串**（除 opaque 特效文本字段，见 §3.4）、零 Godot 类型、零引用
2. 事件在产生即定型：Sim 内任何后续状态变化**不回改**已产生的事件（含同行多段命中——各段独立事件）
3. 载荷禁止携带可变集合；列表型信息（如多目标命中）拆为多条事件，共享同一 SemanticKey 前缀

---

## 3. 事件目录（封闭枚举，v1）

**封闭枚举原则**：Kind 是编译期封闭枚举；新增 Kind = 事件协议版本升级（§6）。禁止 string 事件、禁止 «other» 兜底 Kind。枚举以**白盒实测 26 种**为底本，补齐 GDD 语义必需项：

### 3.1 施放族
`SkillCast{fighterId, skillId, mpCost, windowId}` / `SkillCanceled{fighterId, fromSkillId, toSkillId, windowId}` / `ForceCancel{fighterId}` / `BasicStep{fighterId, chainN}` / `ActEnded{fighterId, skillId}`

### 3.2 命中族（携带 SemanticKey）
`Hit{attackerId, skillId, windowId, seg, victimId, damageRaw, hitNumber, victimStateBefore, y, sweep:bool, airMod:bool}` / `Whiff{attackerId, skillId, reason(enum: DownProtected/Range/Angle/Invulnerable)}` / `DelayedHit{...同 Hit}`（斗破山河延迟点）

### 3.3 受击/运动族
`Launched{victimId, vTick, relaunchN}` / `Relaunched{n, vTick}` / `Landed{victimId, downTicks, airTime}` / `ForcedDown{victimId, ukemiIneffective}` / `Ukemi{victimId}` / `GetupDone{victimId}` / `Knockback{victimId, distMM}` / `WallBounced{fighterId}` / `FloatProtect{victimId, airTime}` / `GrabStarted{attackerId, victimId, skillId}` / `GrabReleased{victimId}` / `Died{fighterId}`

### 3.4 资源/状态/增益族
`ResourceChanged{fighterId, kind(enum HP/MP/Stamina/职业资源), deltaRaw, reason(enum Lifesteal/ShieldRedirect/Drain/Route...), sourceEventId}` / `StatusApplied{victimId, kind, durationTicks, chanceRolled:bool}` / `StatusExpired{victimId, kind}` / `BuffApplied{fighterId, buffKind}` / `BuffRemoved` / `ShieldAbsorbed{fighterId, amount, remaining}` / `OrbGained{fighterId, orbType, count}` / `OrbFired{fighterId, travelTicks}` / `OrbHit{...}` / `CooldownChanged{fighterId, skillId, newTicksLeft, reason}` / `PerMatchUseClaimed{fighterId, skillId, remaining}`

### 3.5 预警/环境/比赛族
`ControlValueNearFull{victimId, value}`（§22.2 双方共享预告）/ `BreakTriggered{victimId}` / `VisibilityChanged{targetId, hidden:bool}` / `UnitSpawned{unitId, spec}` / `UnitDied{unitId}` / `DeployPlaced{deployId}` / `DeployRemoved` / `TerrainDestroyed{terrainId}` / `MatchPhaseChanged{phase}`

### 3.6 opaque 效果通道
`SpecialTextEffect{fighterId, textRef}`——仅 ADR-0002 §4.4 的 opaque 状态/效果在未枚举化前的过渡载体（textRef 指向 Catalog 内静态文本索引，非运行时字符串）；**随枚举化逐条退役**。

### 3.7 非事件项（明确排除）
表现层自产效果（Hitstop 时轴/震屏/镜头）、AI 内部评估、Telemetry 聚合、连续量变化（§1）——全部不是 SimEvent。

---

## 4. 生成与总序（承接 ADR-0001 §3.2）

```
Step(tick):
  ① 指令处理（FighterId 升序）                    → 事件 seq 顺序分配
  ② 世界推进（Projectile/Unit/PendingHit，Id 升序） → 事件顺序分配
  ③ 命中结算（②产生序）                            → 事件顺序分配
  ④ 状态/闸门/资源 Tick 结算（FighterId 升序）       → 事件顺序分配
  ⑤ 签名钩子（注册序 × 事件序）+ 入队变更统一结算     → 次级事件继续顺序分配
  ⑥ 本 Tick 全部事件按 (Tick, Seq) 冻结并发布
```

- 事件在产生时即获得 `(Tick, SeqInTick)`，**产生序 = 总序 = seq 序**，无二次排序
- 签名钩子对每个事件被**全体 Fighter 的签名**按 FighterId 升序依次提供（签名自过滤相关性）；⑤ 内签名产生的变更（经 ISimContext）入队，队尾统一结算产生的次级事件 seq 续接——顺序仍确定
- **Step 结束前事件冻结**：EventBus 发布的是冻结副本；Sim 内部对已发布事件零修改（§2.2）

## 5. 订阅契约（EventBus）

```csharp
public interface IEventBus {
    /// 单线程派发（Sim 线程），按 EventId 总序；handler 收到不可变副本
    void Subscribe(EventKindMask mask, IEventConsumer consumer);
}
public interface IEventConsumer {
    void OnEvent(in SimEvent e);   // 禁止重入 Sim（P3）；耗时工作必须拷贝后转线程
}
```

1. 派发顺序 = EventId 总序；**多消费者顺序 = 订阅注册序**（Match 装配期固定：表现 → 签名 → AI → Telemetry → ReplayWriter，与 DF-1 一致）
2. **禁止回写**：消费者不得调用 Sim 任何变更 API（签名除外——签名经 ISimContext 入队，属 Sim 内部通道而非 EventBus 回写；架构测试断言 EventBus 消费者集合与 ISimContext 持有者集合不相交）
3. **禁止阻塞**：消费者违反（如落盘同步 IO）= 契约违约；ReplayWriter/Telemetry 按 DF-1 拷贝转 worker
4. 事件掩码（EventKindMask）允许消费者只收子集（表现层只要命中/运动族），掩码不影响他人生效

## 6. 传输映射与版本（承接 TR-net-004）

| 通道（GDD §24.4） | 承载 | 与 SimEvent 关系 |
|---|---|---|
| 移动流 20Hz（unreliable） | Snapshot 插值字段 | **不经事件**（§1） |
| 状态流 10Hz（reliable+快照） | 状态快照 + 离散状态事件 | StatusApplied/Expired/Break/Visibility… |
| 技能事件（**reliable ordered**） | 施放/命中/受击/资源全族 | **全部 SimEvent 走单一可靠有序通道** |
| 部署物（reliable） | DeployPlaced/Removed/UnitSpawned/Died | 同上可靠通道 |

1. **单可靠有序通道承载全部事件**：可靠有序天然保持 (Tick, Seq) 序，跨通道乱序问题不存在；不可靠通道只服务快照插值（丢失无害）
2. **去重**：客户端按 EventId 滑动窗口去重（防御重发；可靠有序下正常不重复）
3. **晚加入/恢复**：最近 Snapshot + 自该 Tick 起的事件流补齐（服务端事件环深度为 ADR-0004 议题）
4. **版本**：`EVENT_PROTOCOL_VERSION` 独立维护（Kind 集合/载荷结构变化 = bump）；与 dataVersionHash（数据面，ADR-0002 §5.2）分离，二者共同写入 **ReplayFormatVersion**（回放头）与网络会话握手——数据变了或协议变了，回放都显式失效。**这是对 ADR-0002 hash 输入的有意补充**：协议版本管「事实如何传输/重演」，dataVersionHash 管「事实如何计算」，二者不混装
5. 事件序列化：定长字段序（Schema 常量表），整数 Raw 直写——确定性序列化，无字符串字段（除 SpecialTextEffect 的静态索引）

## 7. 与 RNG / 回放 / 快照的关系

- **RNG**：几率结算的 roll 发生在效果结算 Tick（ADR-0001 §4.2）；`StatusApplied.chanceRolled` 标记该事件含 roll 结果——**roll 本身不进事件载荷**（重演重算），仅标记语义
- **回放**：Replay 不记录事件（重演重算，TR-net-005）；录制端可附带**事件流 hash**（诊断用：重演 hash 不一致 ⇒ 确定性违约定位，属 T1/T3 断言的工程化）
- **快照**：事件与快照由同一 Step 产出；**从 Snapshot+后续指令重演，事件流逐位重现**（ADR-0001 §9 契约的事件面）——T8 验证

## 8. 测试要求（并入 ADR-0001 §10 T 体系）

| # | 测试 | 验证 |
|---|---|---|
| T13 | 事件总序 | 构造多签名/多实体/多段同 Tick 场景，断言 (Tick,Seq) 序 = §4 总序 |
| T14 | 幂等去重 | 同 EventId 重复投递 N 次 ⇒ 消费者恰收 1 次；SemanticKey 重复 ⇒ 命中结算恰 1 次 |
| T15 | 封闭枚举 | 序列化 round-trip 全 Kind 覆盖；未注册 Kind 出现 = 协议版本错误（fail-fast） |
| T16 | 无重入 | 架构测试：EventBus 消费者调用 Sim 变更 API ⇒ 编译期/测试期拒绝 |
| T17 | 签名派发序 | 多签名多事件同 Tick：派发序 = FighterId 升序 × 注册序；入队变更次级事件 seq 续接 |
| T18 | 事件/快照一致 | 同契约重演：事件流 hash 与快照序列逐位一致（ADR-0001 §9 的事件面） |
| T19 | 载荷纯度 | 静态检查：SimEvent 及载荷无 float/string(除 textRef)/Godot 类型 |

## 9. Open Questions

- 无新增。OQ-2/4/5/6/7/8/9 维持未裁定（数据语义，与本 ADR 无关）；OQ-9（骑士精神类跨技能变异归属）由 ADR-0008 在本 ADR 的派发框架内复核
- **文档同步项 D-1**：architecture.md「SimEvent.Id 五元组」表述按本 ADR §2 两层身份更新（下轮文档同步执行，本 ADR 不改其他文档）

## 10. 决策后果

- 正面：事件身份/顺序/幂等/承载全部可证明；表现层、签名、AI、统计、回放共用同一战报语义；可靠单通道消除跨通道乱序
- 代价：封闭枚举的维护纪律（新增事实必须 bump 协议版本）；事件与快照的分工需要在实现期持续 review（防止把连续量塞进事件）
- 中性：白盒 26 种事件全部可映射到 v1 枚举（映射表在实现期由 sim.gd 对照产出，作为迁移清单）

---

## 附：ADR 自审（14 项，2026-08-31）

| # | 检查 | 结果 |
|---|---|---|
| 1 | 事件身份全局唯一且重演稳定 | ✅ (Tick,Seq) 由总序分配，seq 分配本身是确定性行为（ADR-0001 §3.2） |
| 2 | 幂等覆盖网络重发与重放 | ✅ EventId 去重（§2.1）+ SemanticKey 命中族业务幂等（§2.1） |
| 3 | 枚举封闭、无 string 事件 | ✅ §3 封闭枚举 + §3.6 opaque 过渡通道带退役条款 + T15 |
| 4 | 顺序确定且与 ADR-0001 总序一致 | ✅ §4 产生序=总序；无二次排序 |
| 5 | 签名交互不破坏确定性 | ✅ §4 ⑤ 注册序×FighterId 序；变更入队统一结算；T17 |
| 6 | P3 不回写可执行 | ✅ §5-2 消费者/SimContext 持有者集合不相交 + T16 架构测试 |
| 7 | 事件不成为第二事实源 | ✅ §1 分工表 + 连续量排除（§3.7）+ 载荷不携状态终值 |
| 8 | 传输映射符合 TR-net-004 | ✅ §6 四通道表；单可靠有序通道承载事件，移动流留快照 |
| 9 | 与 RNG/回放边界清晰 | ✅ §7 roll 不进载荷只标记；回放重演不记录事件 |
| 10 | 载荷纯度（无 float/string/Godot） | ✅ §2.2 + §6-5 定长序列化 + T19 静态检查 |
| 11 | 版本策略不与 ADR-0002 冲突 | ✅ §6-4 EVENT_PROTOCOL_VERSION 与 dataVersionHash 分立，共同构成 ReplayFormatVersion——补充而非修改 |
| 12 | 白盒事件全部可映射 | ✅ §3 以 26 种实测事件为底本；迁移清单列为实现期动作 |
| 13 | 未修改其他文档/未裁定设计问题 | ✅ 仅登记文档同步项 D-1；OQ 全部维持 |
| 14 | 测试可落地 | ✅ T13–T19 均为确定性断言，GdUnit4Net 可实现 |

**自审结论：通过（14/14）。**

*本 ADR 创建过程中未修改 GDD/Skill-Spec/CSV/architecture.md，未开始任何实现。*


---

## Errata（2026-09-01，SPEC-0006）

**Hit/DelayedHit 事件载荷增补空间字段**（combat-granularity-audit C 级缺口闭合）：

```
Hit{…既有字段…, hitRegion:byte, hitPointX/Y/Z:long(Fixed Raw), hitNormalX/Z:long}
DelayedHit{…同 Hit…}
```

- `hitRegion`：RegionId 枚举（None/Torso/Head/…可扩展，SPEC-0006 §1.2）——部位修正（弱点 ×1.5/×2、豪龙破军部位结算）的消费依据
- `hitPoint/hitNormal`：Fixed Raw 直写（SPEC-0005 §5.3/§5.4）——VFX 锚点/反射方向/法线语义
- **EVENT_PROTOCOL_VERSION bump 1→2**（既有机制，Kind 集合不变、载荷扩展）；与 dataVersionHash 分立原则不变（ADR-0003 §6-4）
- 其余事件载荷不变；Whiff 不带空间字段（未接触）
