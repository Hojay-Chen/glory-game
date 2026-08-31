# ADR-0009 — Godot Tick 循环与场景架构：固定步进累积器、Node↔Sim 同步边界

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0009 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Client（Platform）+ Arena.Headless（Platform）+ C# Solution 布局 |
| 上游 | ADR-0001（TR-sim-001 固定 60Tick、§9 契约）；architecture.md（DF-1 帧更新路径、Platform 模块、关键立场 2）；TR-net-002（预测挂接点）；GDD §24.2（逻辑 60Hz/渲染分离/插值显示） |
| 事实依据 | 白盒 project.godot 已钉 `physics_ticks_per_second=60`（HEAD 3813227）；仓库当前无 .sln/.csproj——本 ADR 定义之 |
| 后续 ADR | ADR-0010（输入系统：本 ADR 只定接入点）、ADR-0006（预测/和解：挂接点已预留）、ADR-0004（Headless 部署） |

---

## 0. 背景与问题

ADR-0001 定死了 Sim 的语义（60 Tick/s 纯函数），ADR-0002 定死了数据如何进入，但「**谁、在什么时刻、用什么节奏调用 `Step(tick)`**」未定义——这是 Arena.Client/Arena.Headless 的循环职责。GDD §24.2 要求逻辑 60Hz 固定、渲染 ≥60fps 分离插值。本 ADR 同时定义 C# Solution 布局（仓库当前无任何 .csproj）。

---

## 1. 决策一：固定步进累积器（Fixed-Step Accumulator）

### 1.1 循环

```csharp
// Arena.Client: MatchRoot._Process(delta) 内（Godot 节点，仅作宿主）
_accumulator += _clock.DeltaTicks();          // 单调时钟（Stopwatch），非引擎 delta
_accumulator = Math.Min(_accumulator, MAX_CATCHUP_TICKS);
while (_accumulator >= 1) {
    var commands = _inputPipe.DrainForTick(_sim.Tick);   // 本 Tick 指令（ADR-0010 接入点）
    _sim.Step(_sim.Tick + 1, commands);                  // 纯函数推进（ADR-0001）
    _prevView = _currView; _currView = _sim.CurrentView; // 表现插值快照对
    _accumulator -= 1;
}
_interpAlpha = _accumulator;                 // 渲染插值系数 ∈ [0,1)
// 渲染层：lerp(_prevView, _currView, _interpAlpha) —— Fixed→float 转换只发生在这里
```

### 1.2 条款

1. **时钟源唯一**：Arena.Client 用 `Stopwatch`（单调、跨平台）；**禁止**用 Godot `delta` 参数、`Time.GetTicksMsec` 之外的时源参与累积——`delta` 仅作宿主回调形式，不参与计算
2. **Sim 是 Tick 的纯函数**：累积器抖动只影响「哪些 Tick 在哪个墙钟时刻执行」，**不影响任何 Tick 的结果**（ADR-0001 §9 契约天然免疫循环抖动）
3. **螺旋死亡防线**：`MAX_CATCHUP_TICKS = 10`（≈167ms 工程常量，命名常量非魔法数）。超限策略：**本地权威模式**（训练/单机/AI）快速追帧（连续 Step 不渲染）；**在线客户端**不追帧——权威在服务器（ADR-0004），客户端预测副本按 ADR-0006 对齐，本循环只驱动本地预测模拟
4. **Tick 计数权威**：`_sim.Tick` 是唯一时间轴；禁止任何代码读墙钟决定游戏内时序（ADR-0001 §5.2 的 Core 禁令在 Platform 侧的对应物）
5. **`_PhysicsProcess` 不承载 Sim**：Godot 物理节点/物理步进系统不使用（ADR-0001 §7）——`project.godot` 保留默认 60 配置但全工程**零 PhysicsBody/零 Area**（白盒同款约束）

## 2. 决策二：ISimDriver 抽象（Client/Headless 共用循环骨架）

```csharp
public interface ISimDriver {
    DriverMode Mode { get; }                  // LocalAuthoritative / ReplayPlayback / OnlinePredicted / DedicatedServer
    void AdvanceTo(int targetTick);           // 驱动 Step（各模式差异仅在指令源与目标推进策略）
    ISimView CurrentView { get; }
}
```

| 模式 | 指令源 | 时钟 | 消费方 |
|---|---|---|---|
| LocalAuthoritative | 本地输入 + AI Controller | 累积器（§1） | Arena.Client（训练/单机/白盒平替） |
| ReplayPlayback | Replay 文件（ADR-0002 产物+种子） | 逐 Tick 手动步进（帧级步进/0.25×） | Arena.Client 回放播放器 |
| OnlinePredicted | 服务器事件流 + 本地预测 | 服务器 Tick（ADR-0006 细节） | Arena.Client 联机 |
| DedicatedServer | 网络指令流 | `Thread.Sleep` 精调 60Hz 循环（Arena.Headless Program.cs） | 服务器 |

- **循环骨架代码共用**：累积器/追帧/插值快照对的逻辑在 Arena.Core（或 Infra）以 `SimClock` 工具类实现，Client/Headless 各自喂时钟——消除两套循环实现漂移
- DedicatedServer 无渲染概念，`AdvanceTo` 直接推 Tick；其 60Hz 节奏是**传输节奏**而非仿真语义（Sim 结果只依赖 Tick 数）

## 3. 决策三：Node↔Sim 同步边界（Arena.Client）

1. **单向拉取**：`SimViewSync` 每渲染帧从 `CurrentView` 读一次；**Fixed→float 转换只发生在表现层**（ADR-0001 §5.2 禁令的 Platform 侧执行点）；任何 Node/Godot 类型不得传入 Sim API
2. **实体 Node 池**：FighterView 按 FighterId 池化复用（24+单位上限），避免每 Tick 实例化；Node 树变化仅由 Sim 实体生死事件驱动（ADR-0003 UnitSpawned/Died/DeployPlaced）
3. **插值显示**：位置/朝向渲染 = `lerp(_prevView, _currView, alpha)`；动画状态机由 Sim 状态投影驱动（Act 技能名+阶段 → 动画），**动画不回写判定**（GDD §28.4 独立性）
4. **输入接入点**：`_Process` 内采样 → `InputPipe` 聚合为 Command → `DrainForTick` 交付；输入映射细节归 ADR-0010，本 ADR 只固定「指令以 Tick 定址、在 Step 前序列化完毕」（ADR-0001 DF-1 线程条款）
5. **暂停/失焦**：本地权威模式允许暂停（停止累积器推进，Sim 状态零变化——暂停不是状态）；在线模式无暂停；应用失焦 → 同螺旋死亡防线处理
6. **Hitstop/慢动作**：表现层冻结渲染时轴（ADR-0003 DF-2 边界），**累积器照常推进**（Sim 时间轴独立）——实现上 Hitstop 表现为插值 alpha 冻结在 0 的若干渲染帧

## 4. 决策四：C# Solution 布局

```
glory-game/
├── arena.sln
├── src/
│   ├── Arena.Core/            Arena.Core.csproj        net8.0（零 Godot；Sim.*+签名+AI+Match 纯逻辑+ADR-0001 Fixed）
│   ├── Arena.Infra/           Arena.Infra.csproj        net8.0（EventBus/Replay/Net 协议/Rollback/Predict/Audit/Telemetry/Catalog 模型+编译器——全纯 C#）
│   ├── Arena.Infra.Godot/     Arena.Infra.Godot.csproj  Godot 适配层（GodotFileSource/ConfigFilePrefs/AudioServerBus——实现 Infra 定义的接口）
│   ├── Arena.Client/          Godot 4.3 工程（project.godot+scenes+.csproj；MatchRoot/SimViewSync/反馈/镜头/HUD）
│   ├── Arena.Headless/        Arena.Headless.csproj     net8.0 控制台（Program.cs：ISimDriver DedicatedServer 循环）
│   └── Arena.Tests/           Arena.Tests.csproj        GdUnit4Net（T1–T19 全套；Core 测试零 Godot 可无头跑）
├── prototypes/                白盒（隔离，永不引用）
└── tools/                     Python 构建期工具链
```

- **程序集依赖**（收紧 architecture.md 依赖图）：`Client → Infra.Godot → Infra → Core`；`Headless → Infra → Core`；`Tests → 全部`
- **文档同步项 D-2（本 ADR 登记）**：architecture.md Module Ownership 的「Arena.Infra」拆分为 **Arena.Infra（纯）+ Arena.Infra.Godot（适配层）**——原表述中 Save.Prefs(ConfigFile)/Data.Catalog(FileAccess)/Audio.Manager(AudioServer) 三个 Godot 触点迁入适配层；Infra 核心保持纯 .NET 以满足 Headless 复用（关键立场 2）。功能归属不变，仅物理分层收紧
- Godot C# 约定：Client 内 `partial class`（源生成器）、场景根节点即入口、`project.godot` 主场景 = Boot（MatchRoot 装配）
- whitebox 的 `physics_ticks_per_second=60` 沿用至 Client 工程（语义：Godot 内部节拍与 Sim 同步率一致，但 Sim 不依赖它）

## 5. 决策五：场景树（Arena.Client v1）

```
Boot (autoload-free; 显式装配)
└── MatchRoot                      ← ISimDriver 持有者 + 累积器（§1）
    ├── ArenaView                  ← ArenaViewSync：Node 池 + 插值渲染（§3）
    │   └── FighterView×N（池化）
    ├── CameraRig                  ← 锁定镜头（TR-feel-002）
    ├── HudLayer (CanvasLayer)     ← HUD Presenter（§27）
    └── DebugOverlay               ← 帧数据覆盖层/审计视图（训练模式，§23.4）
```

- 装配显式化：MatchRoot 构造时注入 `RuntimeCatalog`（ADR-0002）+ `ISimDriver` + EventBus 订阅表——**禁止 autoload 单例承载战斗状态**
- 场景与逻辑的对应关系：一个 MatchRoot = 一场对局；局外界面（大厅/构筑）独立场景树，不触碰 Sim

## 6. 测试要求（并入 T 体系）

| # | 测试 | 验证 |
|---|---|---|
| T20 | 累积器节奏 | 模拟抖动帧序列（delta 乱序喂入）⇒ Tick 执行序列确定、结果与 ADR-0001 T1 逐位一致 |
| T21 | 追帧上限 | 连续 30 Tick 缺口 ⇒ 触发 MAX_CATCHUP 策略，无累积爆炸、无状态丢失 |
| T22 | 插值边界 | alpha∈{0,1} 与跨 Tick 切换时渲染无跳变；Fixed→float 仅在 SimViewSync 出现（静态检查） |
| T23 | 双宿主等价 | 同指令流经 Client 累积器与 Headless 固定循环分别驱动 ⇒ 事件流逐位一致（消除两套循环漂移的回归防线） |
| T24 | Node 隔离 | 静态检查：Arena.Core/Arena.Infra 无 Godot 类型引用；Sim API 签名无 Node/float |

## 7. Open Questions

- 无新增。OQ-2/4/5/6/7/8/9 维持；F1 二轮维持；文档同步项 D-1（ADR-0003）、**D-2（本 ADR Infra 拆分）**待下轮文档同步
- OQ-10（新登记）：Godot 主场景的局外流程（大厅/匹配 UI）与 MatchRoot 的切换载体（场景切换 vs 子视口）——属于 Client 实现细节，不阻塞本 ADR，随 UX spec（/ux-design）定

## 8. 决策后果

- 正面：Sim 与宿主解耦到「喂 Tick 即可」——白盒/Client/Headless/Replay 四种宿主共用一套 Step 语义；表现插值与确定性互不干扰；C# 工程布局一次定清
- 代价：Infra 拆分引入适配层样板；累积器/插值代码需严谨（T20–T23 守护）
- 中性：Godot 物理系统全工程弃用（表现也不默认用）

---

## 附：ADR 自审（15 项，2026-08-31）

| # | 检查 | 结果 |
|---|---|---|
| 1 | 时钟源唯一且引擎无关 | ✅ Stopwatch 累积器；delta 不参与计算（§1.2-1） |
| 2 | 累积器防螺旋 | ✅ MAX_CATCHUP=10 + 模式化降级（本地追帧/在线对齐服务器）（§1.2-3，T21） |
| 3 | Sim 主线程固定步进 | ✅ 与 DF-1/ADR-0001 一致；Step 纯函数（§1.2-2） |
| 4 | Node 不写 Sim | ✅ §3-1 单向拉取 + T24 静态检查 |
| 5 | Fixed→float 仅在表现层 | ✅ §3-1/§3-3 + T22 |
| 6 | Headless 与 Client 循环不漂移 | ✅ §2 ISimDriver 共用骨架 + T23 双宿主等价 |
| 7 | 场景树边界清晰 | ✅ §5 MatchRoot 显式装配、禁 autoload 战斗状态 |
| 8 | 掉帧/失焦/暂停语义 | ✅ §1.2-3 + §3-5（暂停=不推进，非状态操作） |
| 9 | 测试入口可无头运行 | ✅ Arena.Tests 零 Godot Core 测试 + Headless 控制台入口（§4） |
| 10 | 输入接入点与 ADR-0010 边界 | ✅ §1.1 DrainForTick + §3-4（映射细节外移） |
| 11 | 回放/预测挂接点预留 | ✅ §2 DriverMode 四模式（ADR-0006/回放细节各归其 ADR） |
| 12 | 无魔法数值 | ✅ TICK_RATE=60（TR-sim-001 引用）、MAX_CATCHUP=10（命名常量） |
| 13 | Godot 依赖仅限指定程序集 | ✅ §4 依赖链 + T24；白盒工程配置沿用在 Client |
| 14 | 未裁定设计问题/F1/OQ | ✅ 无涉设计裁定；OQ-10 新登记为实现细节项；F1 未动 |
| 15 | 与 ADR-0001/0002/0003 无冲突 | ✅ 时间语义、hash、事件序全部承接；D-2 为分层收紧（功能不变）登记文档同步 |

**自审结论：通过（15/15）。**

*本 ADR 创建过程中未修改 GDD/Skill-Spec/CSV/architecture.md（D-2 仅登记），未创建任何实现代码/工程文件。*
