# ADR-0001 — 确定性仿真核心：定点化数值策略与 0 帧误差契约

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0001 |
| 状态 | **Accepted**（2026-08-31，用户批准 Pre-ADR 收敛后按既定议题落地） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Core（Foundation 级，编码前置条件——TD-ARCHITECTURE 条款） |
| 上游 | docs/architecture/pre-adr-resolution-v1.md（全部采纳）；audit-spec-consistency-v1.md（H-1/C-9/C-10/M-2/M-3/M-4） |
| 关联 GDD | §2.5 伤害公式与无随机设计决策 / §8.5 五道闸门 / §28.1 数据驱动铁律 / §28.4 QA 帧一致性 |
| 关联 TR | TR-sim-001 / TR-sim-002 / TR-sim-005 / TR-net-005（种子） |
| 后续 ADR | ADR-0002（数据管线执行本 ADR 的量化政策）、ADR-0003（事件协议执行 §4/§8 顺序契约）、ADR-0008（签名协议执行 §3.5） |

---

## 0. 背景与问题

Arena.Core 是唯一战斗权威（architecture.md 关键立场 1），其输出必须满足：

- 相同初始状态 + 相同指令流 ⇒ **完全相同**的状态与事件（TR-sim-002，0 Tick 分歧）；
- 回放 = 输入流 + 种子重演（TR-net-005）；服务端权威回溯判定 ≤200ms（TR-net-003）。

浮点算术（IEEE 754 + libm + 编译器重排 + 平台差异）无法保证跨平台逐位一致；C# `Dictionary` 遍历序不稳定；`System.Random` 非确定。白盒原型（GDScript，单平台）已实证算法层可行，但其 float 实现方式**不可移植**（audit C-9）。本 ADR 固化 Core 的确定性数值与纪律契约。

## 1. Runtime 数值体系

### 1.1 决策：统一定点类型 Fixed = Q32.16（int64 容器）

```csharp
public readonly struct Fixed : IEquatable<Fixed> {
    public readonly long Raw;              // 实值 × 2^16
    public const int FRAC = 16;
    public const long ONE = 1L << 16;      // 65536
}
```

**选 Q32.16 的最终理由**（对比 Q16.16 / 缩放整数）：

| 维度 | Q32.16 | Q16.16 | 缩放整数（每量独立标度） |
|---|---|---|---|
| 整数域 | ±2³¹（≈2.1×10⁹）——伤害/HP/面板/坐标全部单层容纳，**连乘一步不溢出**（§1.4） | ±32k——伤害域勉强，中间量需频繁缩放 | 每量一个换算系数，转换规则发散，跨量运算（倍率×ATK×距离衰减）需逐对手工对齐 |
| 分辨率 | 1.5×10⁻⁵（0.015mm 距离 / 0.0015% 系数）——全部设计数据（最小分辨率 0.01）无损表示 | 同左 | 依量而定 |
| 心智成本 | **一个类型、一套乘移**，全 Core 统一 | 同左但域紧张 | 高，最易出错 |
| 乘法 | `(a.Raw * b.Raw) >> 16` 单步，int64 内安全（见 §1.4 证明） | 同 | 各异 |

Q16.16 与 Q32.16 运行时代价相同，Q32.16 用域换掉了全部缩放特例；缩放整数被否决于跨量乘法对齐成本。**Pre-ADR §4.2 的「Q16.16 系数表」升级为全局 Q32.16（系数存储同为 Q32.16，低 16 位语义不变，高位零扩展）**。

### 1.2 各量单位与定点格式（全表）

| 量 | 设计层单位 | Runtime 单位 | 格式 | 量化（Data.Catalog 一次性，RoundHalfEven） |
|---|---|---|---|---|
| 坐标 x/y(z) | 米 | 米 | Q32.16 | 恒等（CSV 已 ≤2 位小数） |
| **速度** | 米/秒 | **米/Tick** | Q32.16 | `round_half_even(mps × ONE / 60)`——运行时积分纯加法 |
| **加速度** | 米/秒² | **米/Tick²** | Q32.16 | `round_half_even(mpss × ONE / 3600)`（g=22 → 400562/Tick²） |
| 击退初速（knockback→vel） | 米（位移语义） | 米/Tick | Q32.16 | 按 Pre-ADR 假设换算式在 Catalog 完成 |
| launch_v | 米/秒 | 米/Tick | Q32.16 | 同速度 |
| ATK/DEF/HP/MP/耐力/控制值 | 整数 | 整数 | **原生 int64，非定点** | 恒等 |
| damage_mult / atk_mod / 修正常量 | 小数 | 比率 | Q32.16 | `round_half_even(v × ONE)` |
| 伤害中间量 | — | 伤害点 | Fixed（Q32.16） | 应用到 HP 时 RoundHalfEven → int64（§1.3） |
| 时间（所有时长字段） | 设计帧/秒 | **Tick** | int64 | §6 政策 |
| 角度 | 度（CSV 整数） | 度 | Q32.16 | 恒等；**扇形判定禁止三角函数**——用预计算 cos 阈值表（§1.5）做点积比较 |
| 百分比（减速 30% 等） | % | 比率 | Q32.16 | `pct/100 × ONE` |

### 1.3 舍入策略（唯一规则）

**RoundHalfEven（银行家舍入）**，全 Core 唯一舍入函数：

```csharp
static long MulShift(long x, long m) {           // (x * m) >> 16，带 half-even
    long p = x * m;                               // 溢出界见 §1.4
    long q = p >> FRAC, r = p & (ONE - 1);
    long half = ONE >> 1;
    if (r > half || (r == half && (q & 1) != 0)) q++;
    return q;
}
```

- 量化（Catalog 导入）、系数表生成、伤害应用、取整政策（§6）全部走此函数或其整数版 `DivRoundHalfEven(a,b)`——**不允许任何位置出现第二个舍入规则**
- 中间量不舍入（保持 Raw），仅两类落点舍入：①伤害写入 HP/MP 等整数资源时；②§6 的 Tick 换算

### 1.4 溢出处理

**构建期断言 + 有界证明**（运行时零检查开销）：

- 乘移单步界：`|x| ≤ 2³¹`（实值 ≈ ±3.3×10⁴，覆盖伤害≤10⁵/坐标≤40m×ONE）且 `|m| ≤ 2¹⁷`（比率 ≤4）→ 乘积 ≤ 2⁴⁸ ≪ int64 上限 2⁶³，**单步乘移数学上不可溢出**
- 连乘链上限：单次伤害结算修正常量 ≤ 12 个 → 逐步 MulShift 保持 `|x| ≤ 2³¹`（每步乘 ≤4 后经 §2 表/常量域约束不越界；Debug 构建插入 checked 断言，Release 依赖证明）
- 累计：伤害 Raw 累加使用 int64 checked（debug）；HP/MP 写入 clamp 到 [0, Max]
- **禁止**：`unchecked` 包裹战斗结算、`double/float` 中间量、`Math.Pow/MathF/Math.Sqrt`、依赖 FPU 环境的任何调用

### 1.5 平台无关数学白名单

Core 结算路径**仅允许**：int64 加减乘、比较、移位、MulShift/DivRoundHalfEven、预生成查表（系数表 §2、cos 阈值表）、`Math.DivRem`（整数语义，平台无关）。cos 阈值表：构建期对 CSV 实际使用的离散角度（扇形 angle_deg 值域）用整数 CORDIC 生成 Q32.16 cos 值，随系数表同版本管理。

## 2. 确定性数学：衰减与修正（禁 Math.Pow 进入权威结算）

### 2.1 事实基础（Pre-ADR §4.1）

三个幂衰减系数均为**精确有理数**：×0.8ⁿ=(4/5)ⁿ、×0.97ⁿ=(97/100)ⁿ、×0.94ⁿ=(47/50)ⁿ。

### 2.2 表的生成

- **生成方式**：构建期脚本（tools/ 下，随仓库版本化）用 Python `fractions.Fraction` 精确整数运算计算 (p/q)ⁿ，逐项 RoundHalfEven 量化为 Q32.16，输出为**源码常量文件**（C# `static readonly long[]`）——生成器与产物同仓库，产物可复核
- **运行时查询**：`Table[n]`，O(1)；n 超界（防御性）clamp 到表尾（等价下限系数语义）
- **表的版本**：常量文件头带 `DETERMINISTIC_CONST_VERSION = "DC-2026-08-31"` 与内容 hash；**hash 纳入 dataVersionHash**（§8/§9）——表参数任何变化 = 数据版本变化 = 旧回放显式失效

### 2.3 表与界

| 表 | 有理数 | 最大 n | 依据 | 表项 |
|---|---|---:|---|---:|
| 浮空衰减 | (4/5)ⁿ | **8** | launch_v 9.0：0.8⁷<3.0/9.0（n=7 触下限 3.0），8 留裕量 | 9 |
| 硬直递减 | (97/100)ⁿ | **64** | 连段击数上限（§8.5 连段 ≤6s 上界推算 <64 击；多段技满撑亦 <64） | 65 |
| 伤害递减 | (47/50)ⁿ | **64** | 下限 0.40 于 n≈16 触达，64 全覆盖 | 65 |
| 一次性修正 | ×1.2 背击 / ×1.15 对空 / ×1.05 空中 / ×0.7 扫地 / ×0.88 冻结衰减 / ×0.9 起身保护 / ×1.5 弱点 / ×0.94⁰… | n=1 | GDD §2.5.2 全表 | ~15 |

### 2.4 禁令

`Math.Pow` / `MathF.Pow` / `Math.Exp` / `Math.Log` / `Math.Sqrt` / `Math.Sin/Cos/Tan` **不得出现在 Arena.Core 任何代码路径**（含签名插件——插件同样受本禁令约束）；CI 加 Roslyn 分析器或源码 grep 门禁强制（归 /test-setup 落地）。

## 3. 确定性容器纪律

### 3.1 容器规则

1. **Dictionary/HashSet 不得参与任何影响战斗结果的遍历**——查找（TryGetValue）允许；需要遍历影响结果的状态集合时，使用**数组/List + 稳定索引**，或 `SortedDictionary`/预排序数组（排序键 = 稳定 ID）
2. **所有影响结算的实体拥有稳定 ID**：FighterId（创建时按阵营注册序分配）、UnitId（创建序递增）、ProjectileId（同）、DecoyId（同）、Buff/Status 实例 Id（同）——ID 分配顺序本身是确定性输入的一部分，随 Snapshot 持久化
3. 同 Tick 内的多实体处理一律按 **ID 升序**或本 ADR §3.4 定义的事件序，禁止依赖插入序以外的任何隐式顺序
4. 序列化（Snapshot/Replay/网络）只允许有序结构（数组/定长槽位）

### 3.2 事件处理顺序（同 Tick 内结算总序）

```
① 指令处理（CmdStream 顺序：Player0…PlayerN 按 FighterId 升序）
② Sim 主动推进（投射物/单位 AI/延迟命中/弹道，按实体 Id 升序）
③ 命中结算（按产生顺序 = ②的处理序）
④ 状态/闸门/资源 Tick 结算（按 FighterId 升序）
⑤ 签名钩子（见 §3.5）
⑥ 事件批量出队（SimEvent 按 (tick, ①→⑤ 类序, Id) 排序后发布）
```

该总序是 ADR-0003（事件协议）的输入；本 ADR 只固化「顺序确定存在且由 ID/类序定义」。

### 3.3 变更次序与幂等（沿 audit C-12 收口）

签名/闸门产生的状态变更**入队、本 Tick ⑤ 统一结算**（非即时生效），队列本身有序（入队序 = 触发事件序）；每个变更携带 `SimEvent.Id` 唯一键，重复投递幂等去重（与 ADR-0003 联合固化）。

### 3.4 Signature Plugin 执行顺序

- 注册序 = 比赛装配时按 `ClassId 升序 → 插件注册序`，装配完成即冻结，随 Snapshot 语义持久（同一 dataVersion 下注册序恒定）
- 同一 Tick 多个签名钩子按注册序依次执行；签名内部消费 RNG/产生变更一律经 ISimContext（§4.6/§3.3），无法越权

## 4. Deterministic RNG（Per-Stream Counter RNG）

### 4.1 核心形式（固化 Pre-ADR §1）

```
value(streamKey) = SplitMix64( Mix64(matchSeed, streamKey, consumed[streamKey]) )
consumed[streamKey] += 1        // 消费即推进该流计数器
```

- **Match Seed**：64bit，比赛创建时生成（专用服务器分配），随 Replay 存档（TR-net-005）
- **Stream Key**：`Hash64(StreamClass, FighterId, SkillId[, InstanceSeq])`——StreamClass ∈ {SKILL_CHANCE, UNIT_AI, AMBIENT}；InstanceSeq 由该 (Fighter,Skill) 的施放计数器派生
- **Counter**：每流一个 int64 计数器；**RNG 无其他状态**（纯函数 + 计数器）
- **隔离保证（本 ADR 显式条款）**：

> **新增一个技能的 RNG 调用，只推进该技能自身流键的计数器；其他任何 Skill / Fighter / Projectile 的流键三元组 (matchSeed, streamKey, consumed) 不变，其 roll 结果逐位不变。** 流键空间按 (StreamClass, FighterId, SkillId) 划分，不存在跨技能共享的流。

- **Snapshot 持久化**：全部流的计数器表随 Snapshot 序列化（§8）
- **Rollback**：计数器随快照回退 → 回溯重演产生相同 roll（纯函数性质）
- **Replay**：只记录 matchSeed + dataVersionHash；roll 结果重演重算；dataVersionHash 不匹配 → 回放**显式拒绝**（不静默错位）
- **Server Authority**：服务端为唯一战斗 RNG 消费方（TR-net-001）
- **Client 不消费战斗 RNG**：几率结果与命中同构，作为权威 SimEvent 从可靠通道到达（TR-net-002）；客户端预测副本不调用 Roll100——不存在随机状态分叉
- **Signature Plugin**：仅可经 `ISimContext.Roll100(scope)` 使用；scope 由 Sim 按调用者身份绑定，插件无法伪造他人流键；**禁止**任何插件/表现层调用 `System.Random`、`Guid.NewGuid`、时间熵
- **隐式随机禁令**：全 Core 禁止 `System.Random`/`Environment.TickCount`/`DateTime` 等一切熵源进入结算路径；Roll100 之外不存在获取「随机数」的 API

### 4.2 几率结算语义（供 ADR-0002 数据映射）

`roll = Roll100(scope)`；`触发 ⟺ roll < N`（N 为 CSV @N% 的整数百分位）。比较发生在效果结算的权威 Tick，一次效果至多一次 roll（幂等键覆盖）。

## 5. 时间体系

### 5.1 四层时间（固化 Pre-ADR §7，架构原则不变）

```
① 原著明确帧/时间（GDD 考据注）
② Skill-Spec 设计数据（60fps 设计帧整数 / 秒）——skills.csv 唯一事实源
        ↓ Quantization（Data.Catalog 一次性，§6 政策）
③ Runtime Tick（60 Tick/s，int64）——Arena.Core 唯一时间概念
④ 渲染帧（表现层，可波动/Hitstop/慢放）——永不参与战斗时间
```

### 5.2 概念隔离条款

1. **`1 设计帧 = 1 Tick` 是当前数据版本（60fps 设计 / 60Tick 仿真）的映射关系，不是语义绑定**。设计数据语义 = 「设计帧（60fps 口径）/秒」；Core API 一律以 `Tick`（int64）为时间类型
2. **Core API 禁止出现「设计帧」概念**：`SkillDef` 属性命名 `*Ticks`；Core 不 import 任何「frame」命名类型；换算函数唯一存在于 Data.Catalog
3. Tick 率若变更（当前无计划）：只改 Catalog 量化层常量并更新 dataVersionHash，②层设计数据与 GDD 不动
4. 绑定校验：回放/网络会话携带 dataVersionHash（含 §2.2 表版本），不匹配即显式失效

## 6. 三项取整政策（确定性数学定义）

通用舍入：`RHE(a/b) = RoundHalfEven(a ÷ b)`（整数实现：`DivRoundHalfEven`）。

### P-1 武器攻速 × 帧数

```
设基础帧字段值 B（Tick），武器攻速 s ∈ [0.80,1.25]（Q32.16）：
eff(B) = max(1, RHE(B × ONE, s.Raw))          // 即 max(1, RoundHalfEven(B / s))
```
- 适用于 startup_f / active_f / recovery_f / hit_interval_f 的武器修正
- 边界：B=0/`-` 不参与修正；结果下限 1 Tick；`atk_spd=1.00`（制式）时恒等
- 该政策同时定义「攻速对取消窗」：取消窗随所在阶段长度等比例换算后取整

### P-2 多段均匀分布 hit timing

```
多段 n 击、生效窗 W Tick、段间隔 iv（Tick）：
  iv > 0：offset_k = min(k × iv, W − 1)                        // k = 0..n−1
  iv 无值：offset_k = RHE(k × (W − 1), n − 1)                   // n > 1；整数实现无浮点
命中 Tick_k = castTick + su + 1 + offset_k
```
- 边界：n=1 → offset=0；n−1=0 不会出现（n>1 分支）；**生成后校验相邻间隔 ≥3 Tick**（Data.Validate，违反即数据错误——audit M-2 白盒假设自此成文）
- 多段间隔与 W 冲突时（iv×(n−1) > W−1）为数据错误，Validate 拒绝

### P-3 秒 → Design Frame / Runtime Tick

```
秒 s → Tick：ticks = RHE(s × 60)                // Catalog 层
设计帧 → Tick：恒等（当前版本映射，§5.2）
```
- **设计纪律**：设计数据应书写 Tick 对齐值（当前 487 行 100% 满足）；Data.Validate 对 `s×60 ∉ ℤ` 报错（防新增非对齐数据静默量化）——新增非整 Tick 需求时先走设计评审，不允许静默 round

## 7. Physics

1. **Core 不使用 Godot Physics 作为权威战斗仿真**——命中判定/移动/浮空/击退/反弹全部在 Core 确定性运动学内完成（白盒已实证：2.5D 运动学 + 圆形边界 + 解析反弹）
2. Godot Physics（若使用）仅限**非权威表现**：布娃娃、表现级碎片——其结果不进入任何 Snapshot/事件/判定，且默认不启用
3. 运动学积分：`pos += vel`（vel 已是米/Tick，§1.2）；`vel_y -= g_tick`；无 `dt` 乘法——时间在积分式中不存在

## 8. Snapshot

### 8.1 完备性原则

> **Snapshot 必须包含所有影响未来模拟结果的状态。从同一 Snapshot 出发、输入相同 Command Stream，必须得到完全相同的后续 Snapshot 与 Events。**

### 8.2 内容清单（v1，实现期按此核对）

| 类别 | 内容 |
|---|---|
| 元 | Tick、matchSeed、dataVersionHash、FighterId/UnitId 等下一个可分配 ID |
| Fighter ×N | 状态机状态+剩余帧、位置/速度（Fixed）、朝向、HP/MP/耐力、CD 表、FC CD、普攻链段号、控制值、异常表、buff/modifier 表（含 Persistent 霸体/护盾池/每场限额计数/隐形/飞行覆盖）、职业资源（炫纹/弹匣/部署位/召唤位/舍命池）、受击侧连段计数（hitstun_n/launch_n/air_time/forced_fall/no_ukemi）、protect_t |
| 连段 | Combo Epoch、各闸门计数器（§8.5 五道闸门全部状态） |
| 世界实体 | Projectiles（位置/速度/存活/追踪态）、PendingHits、Units（状态/AI 计数/HP）、Decoys、部署物/结界（含耐久）、可破坏地形状态 |
| RNG | 全部流键的 consumed 计数器表 |
| 可见性 | 隐身/假身状态（VisibilitySystem） |
| 比赛 | Match.Flow 阶段、阵营分配、比赛级计数（击杀/时限） |

### 8.3 快照纪律

- 值语义深拷贝；消费方不得持有引用跨 Step
- Rollback 缓冲（ADR-0006 落地）仅存只读快照，用于回溯判定的**位置**回溯；写入永远在当前活体状态（architecture.md API Boundaries 幂等契约不变）
- 快照序列化格式 = 有序结构（§3.1-4）

## 9. Determinism Contract

### 9.1 契约

```
Same Initial Snapshot
+ Same Data Version（dataVersionHash：CSV 内容 + 系数表版本 + 量化政策版本）
+ Same Match Seed
+ Same Command Stream（逐 Tick 指令集，含指令内全字段）
──────────────────────────────────────────────
⇒ Same Snapshot Sequence + Same Event Sequence（0 Tick / 0 Frame divergence）
```

### 9.2 Determinism Violation 清单（出现即缺陷，QA 拒收）

1. 同契约两次模拟产生任何 Snapshot 字段差异或事件序差异
2. Math.Pow/MathF/浮点类型/trig 出现在 Core 结算路径
3. Dictionary/HashSet 遍历影响结算；或依赖插入序、哈希序的结算
4. Roll100 流键伪造/跨流共享；System.Random 等熵源可达结算路径
5. 新增 RNG 消费点导致其他流 roll 值变化（隔离破坏）
6. 「设计帧」类型/概念进入 Core API；时间换算散落在 Catalog 之外
7. 取整绕过 MulShift/DivRoundHalfEven（任何第二舍入规则）
8. Snapshot 遗漏影响未来的状态（表现为回放长程漂移——以 §10 测试捕获）
9. 表现层事件回写 Sim 状态（P3 破坏）
10. 溢出（checked 触发）或依赖平台 FPU/编译器浮点行为

## 10. 测试要求（归 /test-setup 落地为 GdUnit4Net 套件）

| # | 测试 | 验证 |
|---|---|---|
| T1 | 重复模拟 | 同契约运行 ≥2 次全量 Snapshot/Event 哈希逐位一致（含整场 AI 对局） |
| T2 | Rollback 重演 | 任意历史 Tick 快照 + 后续指令重演 ⇒ 与原时间线逐位一致 |
| T3 | Replay 重演 | 归档回放文件重演 ⇒ 与录制端逐位一致；dataVersionHash 不匹配 ⇒ 拒载 |
| T4 | 预测对比 | 客户端预测副本与权威快照在预测窗口内逐位一致（无 RNG 消费路径） |
| T5 | 跨平台 | 同契约在 linux-x64 / win-x64 / osx（CI 矩阵）下事件流逐位一致 |
| T6 | 运行次数 | T1 扩展：不同进程/不同 JIT 状态重复 ≥10 次 |
| T7 | RNG 流隔离 | 变异测试：向技能 A 注入额外 Roll100 ⇒ 断言 B/C 技能全部 roll 值与基准不变 |
| T8 | 事件顺序 | 构造多签名/多实体同 Tick 场景 ⇒ 事件总序符合 §3.2 |
| T9 | 定点溢出 | 边界值（最大伤害×最大系数链）运行 + Debug checked 断言不触发 |
| T10 | 取整边界 | P-1/P-2/P-3 的 half 值/边界值表驱动用例（如 s=0.8×B 为 x.5 时结果唯一且=RHE 定义） |
| T11 | 帧一致性 | 实测动作帧 vs SkillDef（GDD §28.4 0 误差，从白盒审计迁移） |
| T12 | 闸门穷举 | 五道闸门穷举 0 违规（白盒审计迁移为 C#） |

## 11. Open Questions 引用（不在本 ADR 裁定）

OQ-2（SA:12-26s 本意）/ OQ-4（加点模型）/ OQ-5（invincible:0f）/ OQ-6（几率数值补录）/ OQ-7（滚取消）/ OQ-8（counter 取消语义）/ OQ-9（跨技能变异归属）——**均为游戏设计/数据裁定，不影响本 ADR 的确定性架构**；其最终数据语义确定后经 Data/Rule 层接入（对应 Pre-ADR §5.3/§6 的 Canonical Form 与 ADR-0002 数据补丁流程）。

## 12. 决策后果

- 正面：0 帧误差可证明、可测试（§10）；回放/回溯/反作弊复算获得数学基础；Q32.16 单类型消除缩放特例
- 代价：Core 全部实值运算定点化（实现纪律成本）；新增数据须过格式白名单与 Tick 对齐校验；禁用全部 .NET Math 浮点 API（CI 门禁）
- 中性：Q32.16 性能足够（每 Tick <10⁴ 次乘移，<0.1% 帧预算，Pre-ADR §4.4）

---

## 附：ADR 自审（7 项检查，2026-08-31）

| # | 检查 | 结果 | 依据 |
|---|---|---|---|
| 1 | 未定义的确定性来源 | ✅ 无 | 熵源禁令（§4.1/§9.2-4）；遍历序（§3.1）；事件总序（§3.2）；签名注册序（§3.4）；ID 分配序（§3.1-2）全部定义 |
| 2 | 隐式浮点计算 | ✅ 无 | §1.5 白名单封闭；§2.4 禁令；§9.2-2 违约项；cos 阈值预计算表替代 trig；速度/加速度 per-Tick 预量化消除运行时除 60 |
| 3 | 不稳定遍历 | ✅ 无 | §3.1-1/3/4：结果相关遍历仅 ID 序/事件序/注册序；序列化仅有序结构 |
| 4 | RNG 污染 | ✅ 无 | §4.1 流键隔离数学保证 + T7 变异测试固化；scope 身份绑定防伪造；Snapshot 持久化计数器保 Rollback 正确 |
| 5 | Design Frame/Tick 混淆 | ✅ 无 | §5.2-2 Core API 禁「设计帧」概念；换算唯一在 Catalog；`*Ticks` 命名规范；§9.2-6 违约项 |
| 6 | 未定义的取整行为 | ✅ 无 | §1.3 唯一舍入函数；§6 P-1/P-2/P-3 三个政策含边界行为（下限 1、half 值、n=1、iv 超窗=数据错误）；T10 表驱动覆盖 |
| 7 | Snapshot 缺失状态 | ✅ 无（就 v1 认知） | §8.2 清单覆盖用户列出的 11 类（Tick/Fighter/Resources/Status/Buff/Projectile/PendingHit/ComboEpoch/Gate/RNG/其他=单位/可见性/地形/比赛）+ ID 分配计数器 + matchSeed/dataVersion；§8.1 原则 + T2/T3 长程重演测试兜底捕获遗漏 |

**自审结论：通过（7/7）**——无未定义确定性来源。遗留数据语义项全部归 OQ（§11），不影响架构。

---

*决策记录人：Technical Director 流程（lean 模式）。本 ADR Accepted 后，ADR-0002（数据管线）方可依据 §6 政策与 §2.2 表版本机制编写。*


---

## Errata（2026-09-01，combat-granularity-collision-audit-v1 → SPEC-0005/0006）

### E-3 §1.5 数学白名单增补：整数平方根

碰撞 Sweep（SPEC-0005）需要平方根与法线归一化。增补白名单：

```
ISqrt(n)：n < 2⁶² 整数平方根（floor）——Newton 迭代固定 33 次取末值（整数域不动点，逐位跨平台一致）
FSqrtFixed(x)：y=ISqrt(x.Raw×ONE) + RoundHalfEven 修正（(y+1)²−N 与 N−y² 比较，半值取偶）
```

仍禁止：`Math.Sqrt`（浮点 libm）——本增补是**整数算法**，与 §2.4 禁令不冲突（`Math.Sqrt` 禁令语义=禁浮点实现）。

### E-4 §8.2 Snapshot 澄清：ContactList 为 Tick 内瞬态

碰撞接触列表（SPEC-0005 §5/§6）在结算 Tick 内消费后即弃——**不入 Snapshot**（重演由命令流+状态重算，逐位一致）。快照持久化的是积分**终态位置/速度**。§8.2 清单不因此增删。

### E-5 物理模型公理与 Tick 可变性（承接 ADR-0009/SPEC-0005）

1. **Intra-Tick 线性运动公理**（SPEC-0005 §3）：速度在 Tick 内恒定、Tick 边界更新；Hitbox 位姿按 Tick 粒度离散（Tick 内不旋转/不渐变）——推论：全部相对运动线性 ⇒ 碰撞解析可解。本公理为 §7「确定性运动学」的操作化定义
2. **Tick 可变性常量纪律**：所有「每 Tick 常量」（速度/加速度换算、KB_FRICTION、MP/耐力/控制值回复/衰减）**必须以秒制或物理模型表达于设计层，由 Compiler 按 TICK_RATE 推导 per-Tick 值**（如摩擦以半衰期秒数表达：0.85/Tick@60Hz ≈ 半衰期 71ms）——禁止在代码/数据中硬编码 per-Tick 系数。Tick 率变更时：Compiler 重推导 + QUANTIZATION_POLICY_VERSION 变更 ⇒ 旧回放显式失效；技能语义（设计帧/秒）与碰撞算法**零修改**（技能时长由 Compiler 重量化，碰撞操作数为 per-Tick 速度与 t∈[0,1] 相对参数）
