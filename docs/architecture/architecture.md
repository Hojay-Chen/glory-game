# 《廿四争锋》— Master Architecture

## Document Status

- Version: 1.0（终版）
- Last Updated: 2026-08-31
- Engine: Godot 4.3 stable / C# (.NET 8+) / GdUnit4Net
- GDDs Covered: GDD-Gameplay v0.3.7 + skill-spec v0.1(+v0.4) + weapon-spec v0.1 + balance-sheet v0.1
- ADRs Referenced: 无既有；必建清单 10 条（Required ADRs）
- Technical Director Sign-Off: 2026-08-31 — APPROVED WITH CONDITIONS

## Engine Knowledge Gap Summary

- Godot 4.3 stable 在 LLM 训练覆盖内（≤4.3），**全域 LOW RISK**，无需参考库修正
- 白盒实证：prototypes/bmg-whitebox 于 4.3 无头模式完成 6000 节点穷举审计 + 20/20 帧一致性
- 风险不在引擎版本，在**确定性架构纪律**（见原则 P1）

## Technical Requirements Baseline

从 GDD v0.3.7 + 三 spec + 技术偏好提取，45 条 / 8 域（TR-<域>-NNN）。溯源明细见附录 B（会话产出）。

| Req ID | 域 | 需求 |
|---|---|---|
| TR-sim-001 | 仿真核心 | 固定 60 Tick/s 权威仿真，Tick=16.667ms，与渲染解耦 |
| TR-sim-002 | 仿真核心 | 确定性 0 帧误差：同输入同初始状态=同结果；无随机进入战斗结算 |
| TR-sim-003 | 仿真核心 | 技能帧数据以 Tick 为单位，从 skills.csv 数据驱动加载（36 列），禁止硬编码 |
| TR-sim-004 | 仿真核心 | 帧数据一致性校验（实测动作帧 vs 数据表 0 误差，§28.4） |
| TR-sim-005 | 仿真核心 | 伤害公式 = 倍率×ATK×防御系数×修正项乘积（§2.5），无随机浮动 |
| TR-sim-006 | 仿真核心 | 8 类判定体紧凑记法解析与相交判定（fan/box/circle/cyl/line/proj/zone/aura） |
| TR-sim-007 | 仿真核心 | 状态机 8 级优先级 Dead>Break>Grabbed>Down>Airborne>Hitstun>Act>Normal（§7.1） |
| TR-sim-008 | 仿真核心 | 五道连招保护闸门 + 连段纪元/计数器（§8.5） |
| TR-sim-009 | 仿真核心 | 16 类控制异常 + 共存/互斥规则（§7.3/§7.5） |
| TR-sim-010 | 仿真核心 | 控制值 100 挣脱 + 起身保护 ×0.5（§7.4） |
| TR-sim-011 | 仿真核心 | 六类取消系统（普攻段间/普攻→技能/命中取消/跳取消/滚取消/强制中断）（§8.2/§10.4） |
| TR-sim-012 | 仿真核心 | HP/MP/耐力 + 五种职业资源（炫纹/弹匣/部署位/召唤位/舍命池）（§9） |
| TR-sim-013 | 仿真核心 | 投射物规范（速度/存活/拦截/同屏 8 上限）（§4.5） |
| TR-sim-014 | 仿真核心 | 地形交互五类（坡道/台阶/高台坠落/墙反弹/立柱破坏）（§3.5/§19.5） |
| TR-char-001 | 角色 | 24 职业全参数数据驱动，签名机制插件化（炫纹/血气/召唤/结印…） |
| TR-char-002 | 武器 | 普攻模组绑定 + 攻速基调 + 规则级精工特性 overlay（weapons.csv trait_rules，D12） |
| TR-char-003 | 技能 | 习得模型 learn_level/acq_type/散人池 96（skills.csv） |
| TR-char-004 | 构筑 | 赛前构筑（加点/携带/武器，3 预设）+ 信息公平（携带公开 D15） |
| TR-arena-001 | 竞技场 | 单图百炼竞技场 + 动态地形（可破坏物/恢复物）参数化（D17） |
| TR-match-001 | 比赛 | 六模式 + 比赛流程状态机（§20.2） |
| TR-match-002 | 比赛 | 结算面板数据采集（§20.6） |
| TR-match-003 | 比赛 | 治疗限 1 / 重复职业允许（D19） |
| TR-feel-001 | 操控 | 键鼠+手柄双方案、全键位重映射（§21） |
| TR-feel-002 | 操控 | 镜头系统，无技能特写（D21） |
| TR-feel-003 | 打击感 | Hitstop/震屏/受击反馈/伤害数字/音效分层 五要素（§22.1） |
| TR-feel-004 | 打击感 | 慢动作仅两处 + 击杀演出可跳过（§22.3/22.4） |
| TR-feel-005 | AI | 三层架构（效用决策/帧数据层/反应延迟 400/250/160/120ms），AI 不读输入（D25） |
| TR-feel-006 | 训练 | 木桩/连招实验室/对策训练/镜像对战（§23.4） |
| TR-net-001 | 网络 | Server Authoritative，客户端只发输入（§24.1） |
| TR-net-002 | 网络 | 客户端预测+和解，纠偏插值 ≤100ms，命中不可预测（§24.2） |
| TR-net-003 | 网络 | 回溯延迟补偿 ≤200ms，60 tick ring buffer（§24.3） |
| TR-net-004 | 网络 | 四通道状态同步（移动 20Hz/状态 10Hz+事件/技能事件可靠/部署物）（§24.4） |
| TR-net-005 | 回放 | 回放 = 输入流+随机种子，<1MB/局，帧级步进/慢放/双视角/覆盖层（§24.5） |
| TR-net-006 | 反作弊 | 四层：零信任权威/输入校验/服务端数值/Replay 审计（§24.6） |
| TR-live-001 | 运营 | 平衡监控六指标采集（§25.3） |
| TR-live-002 | 内容 | 启动时数据引用完整性校验器（§28.1） |
| TR-live-003 | 内容 | ID 命名体系（技能/武器/状态/动画/VFX/音效，§28.2） |
| TR-live-004 | 内容 | 白盒→灰盒→美术内容管线（§28.3，D24） |
| TR-live-005 | QA | QA 战斗专项自动化五项（§28.4） |
| TR-ui-001 | UI | 战斗 HUD + 局外六界面（§27） |
| TR-onboard-001 | 新手 | 90 分钟教程流程（§26） |

## System Layer Map（已批准 2026-08-31）

```
┌──────────────────────────────────────────────────────────┐
│ PRESENTATION  HUD/局外UI · 打击感五要素(VFX/SFX/震屏/Hitstop) │
│               · 角色渲染/动画同步 · 回放播放器 · 训练覆盖层    │
├──────────────────────────────────────────────────────────┤
│ FEATURE       24职业签名机制(插件) · 武器系统 · 构筑 · 比赛流程 │
│               · AI(效用决策/难度/反应延迟) · 训练模式          │
├──────────────────────────────────────────────────────────┤
│ CORE ⚡纯C#引擎无关    确定性战斗仿真：状态机·判定体·伤害·异常   │
│               · 闸门·取消·资源·投射物·地形交互 · 输入→指令流    │
├──────────────────────────────────────────────────────────┤
│ FOUNDATION    数据层(CSV→双导出+校验器) · 事件总线 · 回放记录器  │
│               · 网络传输+复制 · 反作弊审计 · 存档/设置 · 音频管理 │
├──────────────────────────────────────────────────────────┤
│ PLATFORM      Godot 集成层(Node同步渲染/ENet/导出) · .NET 服务端 │
└──────────────────────────────────────────────────────────┘
```

### 关键立场（已批准）

1. **Core 层 = 引擎无关纯 C# 程序集（Arena.Core）**：战斗仿真不使用 Godot 物理引擎——浮点跨平台不确定性与引擎版本耦合会破坏 0 帧误差（TR-sim-002）；白盒已实证自研 2.5D 运动学仿真可行。Godot 物理仅可承担非战斗表现（布娃娃等），战斗判定零依赖。
2. **专属服务器 = 纯 .NET 控制台进程（Arena.Headless）**：复用 Arena.Core + Arena.Infra，无 Godot 依赖、无渲染开销；客户端 Godot 集成层仅做表现。

## Architecture Principles

- **P1 确定性至上**：一切战斗计算在固定 Tick 内整数/定点化推进；禁止 `Random`、浮点累积进结算路径、字典遍历序依赖；同种子同输入跨平台同结果
- **P2 数据驱动铁律**：技能/武器/职业/面板全部来自 CSV→双导出数据表；代码出现魔法数值 = QA 拒收（§28.1）
- **P3 表现与仿真分离**：仿真只产出状态与事件战报；表现层（Hitstop/震屏/插值）消费事件，永不回写仿真状态
- **P4 AI 与玩家同权**：AI 通过与玩家相同的指令接口产入输入，不读对手输入（D25）
- **P5 白盒先行**：手感问题在白盒期解决成本最低（D24）；每个系统的第一版实现都是可无头审计的

## Module Ownership（已批准 2026-08-31）

程序集划分（依赖单向 Platform → Foundation → Core）：

| 程序集 | 内容 | Godot 依赖 |
|---|---|---|
| Arena.Core | Sim.*（仿真 8 模块）+ Signatures/Weapons/Loadout/AI/Match/Training 纯逻辑 | **零** |
| Arena.Infra | Data/Bus/Replay/Net/Audit/Perf/Save/Audio | 仅 Foundation 内触碰 |
| Arena.Client | Godot 工程表现层 | 全量 |
| Arena.Headless | 专属服务器控制台 | **零**（纯 .NET） |

### CORE（Arena.Core.Sim.*，全部零引擎 API）

| 模块 | Owns | Exposes | Consumes |
|---|---|---|---|
| Sim.SimWorld | Tick 计数、Fighter 全状态、WorldOrbs、PendingHits、连段纪元 | Step(Command[])、Snapshot、Events | CmdStream |
| Sim.Fighter | 状态机 8 态、资源 4 槽+职业资源、控制值、异常表、buffs | 状态只读投影 | — |
| Sim.SkillRuntime | 技能执行（阶段/命中表/取消窗/CD） | 命中判定请求 | SkillDef 表 |
| Sim.HitResolve | 8 类判定体解析/相交、伤害公式链、修正项 | ResolveResult | SkillDef+Fighter |
| Sim.Gates | 五道闸门计数器与公式 | 闸门裁决 | Fighter 状态 |
| Sim.Projectiles | 弹道推进/追踪/拦截/同屏上限 | 结算事件 | — |
| Sim.Terrain | 场地边界/高台/台阶/可破坏物（数据驱动） | 地形查询+反弹裁决 | ArenaDef |
| Sim.Input.CmdStream | 输入→指令流序列化（Tick 定址） | Command[] | 客户端/AI 注入 |

### FEATURE（纯 C#）

| 模块 | Owns | Exposes | Consumes |
|---|---|---|---|
| Classes.Registry | 24 职业装配（class-base.csv→Fighter 初始态） | CreateFighter(classId) | 数据层 |
| Classes.Signatures | 签名机制插件（炫纹/血气/波动共鸣/召唤/结印…），ISignature 事件钩子 | OnEvent(战报) | EventBus |
| Weapons | 武器装配（模组+trait 规则级 overlay，D12） | Apply(fighter, weaponId) | weapons.csv |
| Loadout | 赛前构筑（加点/携带/3 预设/信息公平投影 D15） | BuildPreset | skills.csv 习得模型 |
| Match.Flow | 比赛状态机（六模式规则，D19 治疗限1/重复职业） | MatchState 事件 | Sim |
| AI.Controller | 效用决策+难度分级+反应延迟队列，经 CmdStream 注入（D25 不读输入） | Step(自己视角快照)→Command | 自己视角投影 |
| Training | 木桩策略/实验室/对策行为集 | 训练配置 | Sim+Replay |

### FOUNDATION

| 模块 | Owns | Exposes | 引擎 API（4.3，LOW） |
|---|---|---|---|
| Data.Catalog | CSV→双导出（.json 运行时+.res）、启动校验（TR-live-002） | SkillDef/WeaponDef/ClassDef/ArenaDef 只读表 | FileAccess/ResourceLoader |
| Bus.EventBus | 类型化战报分发（Sim→表现/签名/AI/统计） | Publish/Subscribe<T> | 零（C# event） |
| Replay.Recorder | 输入流+种子编码 <1MB/局（TR-net-005） | 读写回放 | FileAccess+Compression |
| Net.Transport | ENet 双通道、四通道复制协议（TR-net-004） | Send/Receive | ENetMultiplayerPeer |
| Net.Rollback | 服务端 60tick ring buffer（≤200ms 回溯，TR-net-003） | 历史快照查询 | 零 |
| Net.Predict | 客户端预测副本+和解 ≤100ms 纠偏（TR-net-002） | 表现坐标 | 零 |
| Audit.AntiCheat | 输入频率/CD/MP/位移校验+Replay 离线审计（TR-net-006） | 违规报告 | 零 |
| Perf.Telemetry | 平衡六指标+对局统计（TR-live-001） | 指标导出 | 零 |
| Save.Prefs | 键位/设置/构筑预设 | 用户配置 | ConfigFile |
| Audio.Manager | 音频总线分层（挥击/命中材质/击飞/挣脱） | 播放接口 | AudioServer |

### PLATFORM

| 模块 | Owns | 引擎 API |
|---|---|---|
| Arena.Client（Godot 工程） | 固定步进累积器 Tick 循环、Node↔Sim 快照同步、动画状态机、镜头、HUD | 全量（LOW） |
| Arena.Headless（.NET 控制台） | 专属服务器：Sim 循环+Net 服务端 | **零**（纯 .NET） |

### 依赖图

```
Arena.Client(Godot) ──┐                        ┌── Arena.Headless(.NET)
                      ├─ Arena.Infra(Foundation) ─┤
                      └─ Arena.Core(Sim, 纯C#) ───┘
单向依赖 Platform → Foundation → Core；Feature 事件只进不出（P3）
```

⚠️ 引擎 API 风险标注：上列 4.3 API 全部在训练覆盖内（LOW），无 post-cutoff 项。

## Data Flow（已批准 2026-08-31，含用户修正三点）

### DF-1 帧更新路径（本地/单机/AI 对局共用）

```
[渲染帧 60fps]                    [Sim 60 Tick/s 独立]
输入设备 ──→ InputMap ──→ CmdBuffer(Tick定址)      │
                │                                  ▼
                └──（AI: Controller.Step→Command）→ SimWorld.Step(Tick)
                                                   │ 确定性状态转移（P1：整数/定点推进）
                                                   ├→ Snapshot（状态权威）
                                                   └→ Events（战报）──→ EventBus ─┬→ 表现同步(SimViewSync)
                                                                                  ├→ 签名机制钩子
                                                                                  ├→ AI 视角投影
                                                                                  ├→ Telemetry
                                                                                  └→ Replay 录制
同步方式：输入=同步调用打包；Sim→其余=事件（P3，永不回写）
线程边界：Sim 在主线程固定步进累积器驱动；Telemetry/Replay 落盘在 worker 线程（消费事件副本）
```

### DF-2 命中→反馈路径

```
Sim.HitResolve --HitEvent{atk,seg,dmg,hn,victimState,y}--> EventBus
   ├→ FeedbackSystem: Hitstop(3-10f 档) / 震屏(0.05-0.35 衰减0.15s) / 受击闪白2f / 伤害数字分色
   ├→ Audio.Manager: 命中材质分层（肉/甲/盾）
   └→ CameraRig: 浮空/大招 震幅档
【边界（用户修正 2026-08-31）】Hitstop/慢动作仅作用于 Presentation 时轴（冻结动画/镜头），
Simulation Tick 永不暂停——Sim 的时间轴只有 60 Tick/s 一条，表现层慢放不影响权威状态推进（§22.3 D22）。
```

### DF-3 网络权威对局

```
客户端                         专用服务器(Arena.Headless)
输入采样 ──Cmd(Tick n)──可靠通道──→ AntiCheat 校验 ──→ SimWorld.Step
   │（本地预测副本 Net.Predict）                      │ Snapshot
   ▼                                              ├─ 移动流 20Hz ──unreliable──→ 客户端插值
表现坐标 ◄──和解：权威快照 vs 预测，偏差>阈值平滑纠偏(≤100ms)◄──┤
   ▼                                              └─ 事件流 可靠通道 ──→ 表现层
命中判定：服务器在生效帧取 Rollback ring buffer 历史快照（12 tick=200ms）回溯判定（§24.3）
【边界（用户修正 2026-08-31）】网络延迟问题由 Rollback/Lag Compensation 与 Prediction/和解解决；
Hitstop 不承担任何网络补偿职责——它只是表现层时轴效果，与延迟无关（§24.2 的「掩盖」仅指观感）。
```

### DF-4 回放

```
录制：Replay.Recorder 订阅 CmdStream（种子+逐 Tick 指令差分，zstd）→ <1MB/局
重放：文件 → CmdStream → 全新 SimWorld 重演 → Events → 表现层（帧级步进/0.25×/双视角=消费同一事件流）
镜像对战/名场面挑战 = 同一 Replay 通道复用（TR-feel-006）
```

### DF-5 初始化顺序

```
① Data.Catalog 加载+校验（失败即拒启，TR-live-002）
② Classes.Registry 装配 24 职业 + Signatures 注册钩子
③ Loadout 应用构筑预设 → Fighter 初始态
④ Match.Flow 进入备战 → 对局：创建 SimWorld（种子）→ 进入 DF-1 循环
⑤ 结算：Telemetry 汇总 → Replay 存档
```

跨线程边界：Sim 主线程；Telemetry/Replay 写盘 worker（消费事件副本）；ENet 收发回调线程→主线程队列。所有进入 Sim 的数据在 Step 前序列化完毕，确定性不受多线程影响。

## API Boundaries（C# 接口契约）

### Core 唯一入口：ISimulation

```csharp
public interface ISimulation {
    /// 权威推进一 Tick。输入必须为该 Tick 定址的指令集；调用即确定性状态转移。
    /// 不变式：同 Tick + 同种子 + 同指令集 ⇒ 同 Snapshot + 同 Events（跨平台，P1）。
    StepResult Step(int tick, ReadOnlySpan<Command> commands);

    /// 只读权威快照（结构体集合，值语义；消费方不得持有引用跨越 Step）
    ISimView CurrentView { get; }

    /// 本 Tick 产生的战报（不可变；事件含唯一键保证幂等，见下）
    ReadOnlySpan<SimEvent> Events { get; }
}
```

### 指令流（玩家与 AI 唯一入口，P4）

```csharp
public readonly record struct Command(
    CmdKind Kind,        // Move/Jump/Roll/Skill/Basic/Fc/BindSoul(受身)/OrbFire…
    ushort SkillId,      // Kind=Skill 时有效（skills.csv ID）
    byte DirIndex,       // 8 向量化方向（受身方向判定 §10.3）
    int TargetTick);     // 指令生效 Tick——服务端校验 |TargetTick - now| ≤ 容差（防快进，TR-net-006）
```

**不变式**：Command 是纯数据；任何需要游戏状态的判断（CD/MP/取消合法性）都在 Sim 内裁决，指令侧只表达意图。

### 签名机制插件（TR-char-001）

```csharp
public interface ISignature {
    ClassId Class { get; }
    /// 只消费战报事件（含随机种子的确定性钩子），通过 ISimContext 申请状态变更
    void OnEvent(in SimEvent e, ISimContext ctx);
}
public interface ISimContext {
    /// 全部状态变更走 Sim 提供的受限 API（保证闸门/纪元/事件键不被绕过）
    void ApplyDamage(int victimId, Fixed damage, DamageFlags flags);
    void ApplyStatus(int victimId, StatusKind kind, TickDuration dur);
    void SpawnProjectile(in ProjectileSpec spec);
    void SpawnDeploy(in DeploySpec spec);      // 召唤/部署/结界统一入口
    uint NextDeterministicRandom();            // 种子驱动（AI 假动作等少数确定性随机）
}
```

**不变式**：签名机制不直接触碰 Fighter 内部状态；一切经 ISimContext → Sim 内部裁决（闸门/纪元照常生效）。

### Rollback 历史状态边界与结算幂等性（用户要求明确）

**历史状态边界**：
- `Net.Rollback` 环形缓冲（12 tick = 200ms）仅存 **只读 Snapshot**（Fighter 位置/朝向/状态/高度/判定体），不含可变逻辑
- 历史快照**只用于命中判定的位置回溯**（§24.3）；任何伤害/状态/资源写入**永远发生在当前 Tick 的活体状态上**，禁止写入历史快照
- 快照为值语义深拷贝，出队即弃；Sim 主体不依赖缓冲存在（缓冲缺失时回溯判定退化为当前位置判定并记 Telemetry 告警）

**结算幂等性（防重复扣血/重复事件）**：
1. **判定时刻唯一**：一次命中结算发生在「攻击方生效帧所在的权威 Tick」恰好一次；回溯只是给这个唯一结算提供目标的历史位置，不产生第二次结算
2. **事件唯一键**：`SimEvent.Id = (Tick, AttackerId, SkillId, ActiveWindowId, SegmentIndex)`——EventBus 按 Id 去重，网络重发/重放不会产生二次表现或二次统计
3. **HP 写入点唯一**：ApplyDamage 只在 Sim.HitResolve 内调用；签名机制经 ISimContext 汇入同一写点；Replay 重放是全新 SimWorld 的重演（非对活体状态重放），天然无重复
4. **网络层去重**：可靠通道事件带 Id 序号，Transport 层先去重再入 EventBus

### 数据契约（Foundation→全层）

```csharp
public sealed record SkillDef { /* skills.csv 36 列的强类型映射；帧字段为 Tick 单位 int */ }
public sealed record WeaponDef { /* weapons.csv 13 列 + trait_rules 解析后的规则级 overlay 列表 */ }
public sealed record ClassDef  { /* class-base.csv 12 列 + 资源定义 + ISignature 工厂 */ }
```

**不变式**：全部 record 不可变；Catalog 加载后只读共享；校验失败 = 拒启（fail-fast，TR-live-002）。

## ADR Audit（2026-08-31）

| 既有 ADR | 结论 |
|---|---|
| 无 | 尚无既有 ADR；本文档 Phases 1–4 的决策 = 首批 ADR 来源 |

### 溯源覆盖

| TR 域 | 覆盖方式 | 状态 |
|---|---|---|
| TR-sim-001/002 | P1 + ADR-0001（确定性仿真）+ ADR-0009（Tick 循环） | 需 ADR |
| TR-sim-003/005/006 | ADR-0002（数据管线）+ Sim.HitResolve 设计 | 需 ADR（数据部分） |
| TR-sim-004/008–014 | Module Ownership（Sim.Gates/HitResolve/Terrain…）+ Skill-Spec 校验器 | ✅ 架构覆盖 |
| TR-char-001/002/003/004 | ADR-0008（签名插件协议）+ CSV 管线 | 需 ADR（插件协议） |
| TR-arena/match/onboard/ui | Match.Flow/Training + §27 界面清单 | ✅ 架构覆盖 |
| TR-feel-001/002/003/004 | ADR-0010（输入）+ FeedbackSystem/CameraRig | 需 ADR（输入） |
| TR-feel-005/006 | ADR-0007（AI 同权接口） | 需 ADR |
| TR-net-001/004 | ADR-0004（Server Authoritative + ENet + Headless） | 需 ADR |
| TR-net-002/003 | ADR-0006（预测/和解/回溯）+ 幂等契约（API Boundaries） | 需 ADR |
| TR-net-005/006 | ADR-0005（回放格式 + 反作弊） | 需 ADR |
| TR-live-001–005 | ADR-0002 + Perf.Telemetry + §28 管线 | 需 ADR（数据部分） |

覆盖统计：45 条 TR 全部有架构归属；其中 12 条需要 ADR 固化决策，其余由本文档模块设计 + 实现期测试覆盖。

## Required ADRs（10 条，优先级序）

**编码前必须（Foundation/Core）：**
1. `ADR-0001 确定性仿真核心：定点化数值策略与 0 帧误差契约` → TR-sim-001/002
2. `ADR-0002 数据管线：CSV→双导出与启动校验 fail-fast` → TR-sim-003、TR-live-002/003
3. `ADR-0003 战报事件协议：SimEvent 唯一键与幂等契约` → DF-2/DF-3、TR-net-004
4. `ADR-0004 网络形态：Server Authoritative + ENet + Arena.Headless 纯 .NET 服务器` → TR-net-001/004
5. `ADR-0009 Godot Tick 循环与场景架构：固定步进累积器、Node↔Sim 同步边界` → TR-sim-001 客户端侧

**相关系统开工前：**

6. `ADR-0006 客户端预测与和解 + 回溯延迟补偿` → TR-net-002/003
7. `ADR-0008 签名机制插件协议（ISignature/ISimContext）` → TR-char-001
8. `ADR-0007 AI 同权指令接口（不读输入）` → TR-feel-005
9. `ADR-0010 输入系统：键鼠/手柄映射与 CmdStream 序列化` → TR-feel-001
10. `ADR-0005 回放文件格式与反作弊审计管线` → TR-net-005/006

**可推迟：** 非战斗表现物理（布娃娃）/ 反作弊统计 ML / 本地化方案

## Open Questions

- **F1 二轮裁定**：浮空四连刺第 4 刺（空中快刺形态 9→6f vs 接受三刺改模板）——影响 ADR-0010 指令集与空中动作形态设计
- **OQ-1 定点化方案**：Q32.16 定点 vs 缩放整数（伤害/位置/速度分别定标）——ADR-0001 的核心议题
- **OQ-2 服务器部署形态**：Arena.Headless 的 Linux 容器/匹配服务拓扑——ADR-0004 议题，Beta 前定即可
- **OQ-3 GDD §18 残留**：内容总树仍写「携带 10 格」，与 v0.3.4 无携带上限（D37）矛盾——下轮 GDD 修订清理

## Technical Director Sign-Off（TD-ARCHITECTURE 自审，2026-08-31）

| 准则 | 结论 |
|---|---|
| ① GDD 全系统/需求覆盖 | ✅ 45 TR 全归属；§2–§29 各章均映射到层/模块 |
| ② 引擎兼容一致性 | ✅ Godot 4.3 全 LOW 风险；Core/Headless 零引擎依赖的设计使引擎面最小化 |
| ③ 模块边界/循环依赖 | ✅ 依赖单向 Platform→Foundation→Core；签名机制经受限 ISimContext 防绕过 |
| ④ 风险与缓解 | ✅ 确定性纪律（P1+幂等契约）；闸门①⑤测试盲区已记录（Alpha 压测）；F1 二轮/OQ-1~3 显式挂起 |

**结论：APPROVED WITH CONDITIONS** —— 条件：`ADR-0001`（定点化数值策略）必须在任何 Arena.Core 编码开始前 Accepted；`ADR-0002/0003/0009` 须在数据加载与表现层编码前 Accepted。

LP-FEASIBILITY skipped — Lean mode。

## Document Status（终版）

- Version: 1.0
- Last Updated: 2026-08-31
- Engine: Godot 4.3 stable / C# (.NET 8+) / GdUnit4Net
- GDDs Covered: GDD-Gameplay v0.3.7 + skill-spec v0.1(+v0.4) + weapon-spec v0.1 + balance-sheet v0.1
- ADRs Referenced: 无既有；产出必建清单 10 条（见 Required ADRs）
- Technical Director Sign-Off: 2026-08-31 — APPROVED WITH CONDITIONS
- Lead Programmer Feasibility: skipped（Lean mode）
