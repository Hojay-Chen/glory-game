# Pre-ADR Resolution — 裁定前收敛文档

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-08-31 |
| 上游 | audit-spec-consistency-v1.md（B-1/H-1/H-2/H-3/M-*、9 项能力缺口） |
| 性质 | 技术落地设计（Decision Proposal）——不创建 ADR、不改 GDD/Skill-Spec/数据、不编码 |
| 结论 | **ADR-0001 READY**（见 §9） |

---

# 1. B-1 Deterministic RNG Decision Proposal（确定性随机）

**方向（用户 2026-08-31 设定）**：允许「种子化确定性随机（Seeded Deterministic RNG）」，禁止非确定性随机。原著几率系技能（涂毒/出血/眩晕类）保留玩法，随机进入结算但完全可复现。

## 1.1 RNG 形态选型

| 方案 | 描述 | 确定性 | 抗污染 | 结论 |
|---|---|---|---|---|
| A. 全局单一状态流（`rng.Next()` 全局推进） | 一个 state 顺序消费 | ✅（有纪律时） | ❌ 任一技能多调一次 → 后续全部序列移位 | 弃用 |
| B. **Per-Stream 计数型 RNG（推荐）** | `value = SplitMix64(Mix(seed, streamKey, counter[streamKey]))`——纯函数，value 只依赖 (seed, 流键, 该流已消费次数) | ✅ 纯 int64 | ✅ 流间隔离：技能 A 多调一次不影响技能 B 的任何 roll | **采用** |
| C. 预分配 roll 表（每技能按 (skillId, useCount) 哈希） | B 的等价表述，键空间更粗 | ✅ | ✅ | 并入 B，流键粒度见下 |

## 1.2 流键（Stream Key）设计

```
streamKey = hash64(matchSeed, StreamClass, FighterId, SkillId[, InstanceSeq])
```

| StreamClass | 键 | 用途 |
|---|---|---|
| SKILL_CHANCE | (FighterId, SkillId) | @N% 几率结算（连突出血、砖袭眩晕…）——每次施放的 InstanceSeq 递增 |
| UNIT_AI | (UnitId) | 召唤兽 AI 决策随机（哥布林扔石头目标扰动等） |
| AMBIENT | 全局单流 | 替身术传送点等无宿主技能的场景随机（NJA 替身） |
| PRESERVE | — | 表现层/AI 决策如需随机走**各自独立流**，绝不与结算流共享键空间 |

## 1.3 接口 / 状态 / 顺序 / 快照

```csharp
public interface ISimRng {
    /// 在指定流上消费一次 roll：返回 [0, 100) 的整数刻度；调用方与技能数据的 @N% 比较
    int Roll100(RollScope scope);          // scope = (class, fighterId, skillId)
    /// 当前各流计数器（随 Snapshot 持久化——Rollback/回放恢复用）
    RngState Capture();
}
```

- **状态**：每个 streamKey 的 `consumed` 计数器（int）。RNG **无隐藏状态**——纯函数 + 计数器，因此天然支持 Rollback（计数器随快照回退）与快照序列化
- **消费顺序**：同一 Tick 内多消费按「事件结算顺序」（与 SimEvent 唯一键同序，ADR-0003 固化）；跨 Tick 互不影响（流隔离）
- **Tick/事件关联**：roll 发生在几率效果结算的权威 Tick；`SimEvent.Id` 已含 (Tick, Attacker, Skill, Window, Segment)——同一键**至多消费一次**，幂等性天然覆盖 RNG
- **防污染验证**：技能 A 增加一次 RNG 调用 → 仅 A 流计数器 +1；B 流的 (seed,key,counter) 三元组不变 → B 的 roll 值不变。**这就是流隔离的数学保证**

## 1.4 Replay / Server / Client

- **Replay 记录**：`matchSeed(64bit) + dataVersionHash`。**roll 结果不记录**（重演时按相同流键重算）。数据版本变化导致 roll 数变化 → 回放校验 dataVersionHash 不匹配即拒绝（显式失效，不静默错位）
- **Server**：唯一 RNG 消费方（TR-net-001 权威）
- **Client**：**从不为战斗结果消费 RNG**——几率结果作为 SimEvent 从可靠通道到达（与命中不可预测 TR-net-002 同构）；客户端预测仅限本机移动/前摇表现，不预测几率结果 → **不存在随机状态分叉问题**
- **Signature Plugin**：仅经 `ISimContext.Roll100(scope)` 获取，scope 由 Sim 按调用者身份强制绑定（插件无法伪造他人流键）；禁止把 RNG 句柄泄漏到插件侧
- **反作弊**：输入流不携带随机，服务端 roll → Replay 审计可完整复算每一次几率判定（TR-net-006 强化）

## 1.5 待办（裁定后）

- GDD P1/D02 措辞修订：「禁止**非确定性**随机；种子化确定性随机仅限技能几率机制，种子随回放保存」——GDD 修订不在本阶段执行
- 6 行「几率」无数值技能补 @N%（Skill-Spec 数据补丁）

---

# 2. Data Canonicalization Report（数据数量口径漂移清单）

事实来源 = 当前仓库真实数据：**skills.csv = 487 行 × 36 列；散人池 = 96**（validate_skills 管线产出 + 本次脚本核实）。不为旧文档补造数据。

| 旧口径 | 当前真实 | 出现位置 | 需修改 | 建议 |
|---|---|---|---|---|
| 501 条目 | 487 | GDD L113（§1.8 总表） | ✅ | 487（BAS79/T1 101/T2 133/T3 96/T4 41/U 23/PAS 14） |
| 「501 行 × 35 列」 | 487 行 × 36 列 | GDD L1051 | ✅ | 同步 |
| 散人池 94 个 | **96** | GDD L113 / L169 / L3057 / L3471 / L4173；skill-spec/README L134；skill-spec/实现注记 L189 | ✅ | 96（validation-report L37 已是 96 ✓） |
| 「携带 10 格」（树） | 已废除（D37，§18.1 L3476 表述正确） | GDD L4045（§30 树） | ✅ | 树行改「构筑（加点/武器/信息公平）」 |
| 「36 点/10 技能×5 级=50 点」 | **OPEN-QUESTION OQ-4** | GDD §18.2（L3479–3491） | ❓ | §18.2 仍是现行规则但推导基于 10 技能携带模型，与 D37 的关系（无携带上限后加点对象是什么？全技能池？）无法从仓库自洽判定——**待用户裁定，不猜** |
| 「30 字段规范」 | 36 列 | GDD §17.3 标题（L3407）、§30 树 L4044 | ✅ | 36 列（README L40 已是 36 ✓） |
| 天击=7.5 标准参照 | 9.0（v0.3.7） | tools/validate_skills.py L163 注释 | ✅ | 注释更新（校验带 [5.5,9.5] 本身覆盖 9.0，无功能影响） |
| 487/36/96（项目 README、CLAUDE.md、architecture.md、validation-report） | ✓ 正确 | — | ❌ | 无需改动 |

---

# 3. Core Capability Interfaces（9 项能力最小接口）

原则：全部为**通用规则接口**（参数化），禁止按 skillId 写特殊分支。设计目标=证明 Core 通用能力边界足够覆盖现有 487 技。

| # | Capability | 需要 Core？ | Interface（最小签名） | Consumer | 影响确定性？ |
|---|---|---|---|---|---|
| 1 | **UnitSystem 单位系统** | **是**（新模块 Sim.Units） | `SpawnUnit(UnitSpec{hp,moveSpeed,attack,aiProfile,lifetime,inheritPct})` / `UnitStep(tick)`（tick 确定性效用 AI）/ 事件 `UnitDied` | SUM 召唤兽 11+指挥 4、MEH 机械单位 3、修鲁鲁 | AI 决策必须 Tick 定序（效用分相同→按 UnitId 序），**确定** |
| 2 | **VisibilitySystem 可见性** | **是**（新模块 Sim.Visibility） | `SetHidden(fighterId, bool, sourceId)` / `SpawnDecoy(DecoySpec)` / `IsVisible(observerId, targetId)` | THF 潜行、NJA 分身/替身、SPF 烟雾、BLA 剑影步、QIM 气刃 | 隐身位姿与假身为 Sim 状态，锁定/AI 消费投影，**确定** |
| 3 | **ShieldPool 护盾池** | 是（Fighter 资源槽扩展） | `AddShield(src, amount, breakRule)` / `AbsorbDamage(incoming)→residual` | BLA 格挡、GBL 残影、UNS 伞护、WIT 药剂护盾、QIM 念气罩 | 吸收顺序=添加序（有序结构），**确定** |
| 4 | **Reflection 反射** | 是（Core 防御规则） | `CounterWindow(kind: Melee/Spell/All, duration, reflectPct, includeControl)` | KNI 法术反射/盾反/风暴反击、WRK 魔镜、WIT 药剂护盾 | 反射产生的新命中走正常 HitResolve（事件唯一键去重），**确定** |
| 5 | **Cooldown Operation CD 操作** | 是（SkillRuntime API） | `ResetCooldown(fighterId, skillId)` / `ModifyCooldown(fighterId, skillId, deltaTicks)` / `SetCooldownExempt(fighterId, tierCap, duration)` | SRP 双重控制/承前启后、ROG 街头风暴、KNI 骑士精神 | 纯状态操作，**确定** |
| 6 | **Resource Damage Routing 资源伤害路由** | 是（DamageCalc 出口分流） | `DamageRoute{hp, mp, shield, statTarget}`——截脉伤 MP、法力护盾 30% 转 MP、鬼影缠身 33% 转治疗 | QIM 截脉、BMG 法力护盾、WRK 吸血术/鬼影缠身 | 路由规则数据化，**确定** |
| 7 | **Per-Match Usage Limit 每场限额** | 是（Fighter 匹配级计数） | `ClaimUse(skillId)→bool`（预算表 from CSV 新列或 special 语法 `permatch:N`） | NJA 地心斩首(2)、EXO 封禁符(1)、WRK 六星光牢(1)、ROG 以牙还牙(3) | 计数器随快照持久化，**确定** |
| 8 | **Flight Coverage 飞行覆盖** | 是（Fighter 移动状态扩展） | `MovementOverride{hover:duration, groundFollow:bool, glide:bool}`——清理时机由 buff 生命周期管理 | WIT 扫把掌握、PRI 天使之翼、MEH 机械旋翼、UNS 伞护滑翔 | 覆盖值进 Snapshot，**确定** |
| 9 | **Faction Query 阵营查询** | 是（Sim 阵营模型） | `Faction.Of(fighterId)` / `IsAlly(a,b)`——Match.Flow 注入分队 | GAN 守护美德、SPF 闪光弹同队豁免、QIM 念龙波友军增益、PRI 神佑之光 | Match 阶段固定，对局内不变，**确定** |

**边界判定（§七 的问题）**：上述 9 项全部进 **Arena.Core**（战斗语义，签名插件与 AI 都要消费）而非职业插件——职业插件只做**组合**这些原语。已按「不为实现方便把 Core 规则塞进插件」原则复核：无一项是单职业专属（ closest 的 骑士精神=CD 操作+buff 组合，原语仍在 Core）。

---

# 4. Deterministic Math Proposal（H-1 公式确定性）

**禁令确认**：核心战斗结算禁止 `Math.Pow` / `MathF` / libm 任何调用。

## 4.1 关键观察：三个衰减系数全是精确有理数

- ×0.8ⁿ = (4/5)ⁿ、×0.97ⁿ = (97/100)ⁿ、×0.94ⁿ = (47/50)ⁿ——可**离线用整数算术**精确生成任意精度的定点表。

## 4.2 三方案对比

| 方案 | 做法 | 精度 | 跨平台 | 性能 | 溢出 | 结论 |
|---|---|---|---|---|---|---|
| A. 运行时 Math.Pow | float pow | libm 相关 | ❌ | 最差 | — | **禁止** |
| B. 运行时整数递推 `x = x*p/q`（每击一次） | 累乘+定义舍入 | 舍入误差随 n 累积（n=30 时相对偏差 ~10⁻⁴ 级，可接受但与离线真值有系统差） | ✅ | O(n)/查询 | 需 int64 界 | 备选（适用于 n 无界的场景） |
| C. **预计算定点表（推荐）** | 构建期用整数运算生成 `table[n] = RoundHalfEven((p/q)ⁿ × 2¹⁶)`，运行时 `y = (x * table[n]) >> 16`（int64） | 表生成即量化一次，运行时零漂移；Q16.16 对伤害值（≤10⁴）相对误差 ≤1.6×10⁻⁵ | ✅ 纯 int64 乘移，C#/IL 规范定死 | O(1) 查表 + 1 乘 1 移 | 伤害≤10⁴×表值≤2¹⁶ → 2³⁰ 级，int64 富余 | **采用** |

## 4.3 表与界

| 公式 | 最大 n | 依据 | 表规模 |
|---|---:|---|---|
| 浮空衰减 (4/5)ⁿ | **8** | floor 3.0 触达：9.0×0.8⁷<3.0（n=7 达下限），取 8 留裕量 | 9 项 |
| 硬直递减 (97/100)ⁿ | **64** | 连段击数设计上限（§8.5 连段 ≤6s，多段技撑满 <64 击），下限系数兜底 | 65 项 |
| 伤害递减 (47/50)ⁿ | **64** | 同上（下限 0.40 在 n≈16 触达，64 全覆盖） | 65 项 |
| 一次性修正（×1.2 背击 / ×1.15 对空 / ×1.05 空中 / ×0.7 扫地 / ×0.88 冻结衰减 / ×0.9 起身保护 / ×1.5 弱点…） | n=1 | 常量表，同 Q16.16 | ~15 项 |

## 4.4 语义规则（进 ADR-0001）

1. 所有比率修正常量以**有理数（分子/分母）**写在设计层，构建期生成 Q16.16 表；运行时只有 `mul_shift(x, m) = (x*m + 0x8000) >> 16`（RoundHalfEven——与表生成一致）
2. 连乘顺序固定：按 GDD §2.5.2 修正项**表列顺序**应用（顺序本身写入代码常量，禁止编译器重排语义——int64 定点天然免疫浮点重排）
3. 物理常量定点化：位置/速度采用 **毫分米单位（1/10⁴ m，Q-format 每量一个）**，g=22 m/s² → per-Tick² 定点常量构建期量化一次（量化误差 ~0.1%，**常量化后确定**；最终 Q 格式与取整方向为 ADR-0001 议题 OQ-1 收口）
4. 溢出策略：全部结算路径 int64 + 构建期断言上界（伤害 ≤ 10⁵、系数 ≤ 2¹⁶、连乘 ≤ 12 次 → 最大 2⁷⁶ 需中途右移——乘移操作内部按「先乘后移」单步执行保证 ≤ 2⁵⁰）
5. 跨平台：int64 加乘移位在 IEEE/IL 规范内无平台差异；**禁用** `unchecked` 浮点、`Math.*`、SIMD 隐式重排
6. 性能：每 Tick 全量结算 < 10⁴ 次乘移，相对 16.667ms 预算 < 0.1%

---

# 5. Armor / Duration Semantics（H-2 单位与语义）

## 5.1 两类时间的本体区分（统一数据模型）

```
┌─ Instant Frame Window（瞬时帧窗）───────────────────────────┐
│ 语义：相对「动作开始 Tick」的区间，生命周期=动作本身          │
│ 字段：startup_f / active_f / recovery_f / hit_interval_f /  │
│       hitstun_f / cancel windows / invincible_f /           │
│       armor 的「动作内霸体」（如伏龙翔天 SA:30-40f）         │
│ 单位：Tick（=设计帧，恒等映射）                              │
├─ Persistent Modifier（持续修饰）────────────────────────────┤
│ 语义：随自身状态存续，可被驱散/刷新/独立到期，与动作无关      │
│ 成员：status 时长、buff active 时长（法力护盾 6s）、          │
│       持续霸体（嗜血/稳定炮架期间的 SA）、护盾池、潜行、飞行  │
│ 单位：Tick（秒×60）+ 驱散/互斥规则引用                       │
└─────────────────────────────────────────────────────────────┘
```

## 5.2 现状违规清单（本次全量扫描）

| 数据 | 行 | 现状 | 问题 |
|---|---|---|---|
| `SA:10-210f` `SA:8-388f` `SA:10-498f` `SSA:24-244f` `SSA:30-510f` `SSA:24-744f` `SA:8-368f` | 7 | 用**绝对帧区间**表达「buff 期间的持续霸体」 | ①>动作时长的窗= Persistent 语义误用 Instant 表达 ②buff 被驱散/刷新时区间失真 ③绝对帧起止与释放 Tick 耦合 |
| `SA:12-26s` | 1 | 单位 s 混入 f 列 | 唯一 s 单位 armor；且 26s 霸体疑似 Persistent 语义（OQ-2 待裁定本意） |

## 5.3 统一模型（建议的 Canonical Form，不改现有 CSV）

```text
Instant 窗：  armor = SA|SSA : <tick0>-<tick1>        （约束 tick1 ≤ 动作总长，Data.Validate 检查）
Persistent： armor = SA|SSA : buff:<buffSkillId>      （霸体随该 buff 存续；驱散即失效）
              status = <kind>:<p1>:<tick时长>          （时长一律 Tick；@N% 几率前缀）
```

Runtime 数据模型相应为两个概念：`WindowModifier`（挂 SkillRuntime 的动作时间轴）与 `PersistentModifier`（挂 Fighter 状态表，带 dispel 分类）。7 行超长区间 + 1 行 s 单位按此模型转换（**数据补丁待裁定后执行**）。

---

# 6. Data Format Normalization（H-3 格式噪声）

| 数据 | 当前格式 | 问题 | 推荐 Canonical Format |
|---|---|---|---|
| `15(0.25s)`（UNS 万象切换 startup） | 整数(秒注释) | 注释混入数值列 | `15`（0.25s 移 notes；15T=0.25s 自明） |
| `invincible:地底16-146f`（NJA 地心斩首） | 文本修饰+区间 | 状态词混入 | `invincible:16-146f` + special 增 `潜行状态:地底` |
| `invincible:0f` | 0 宽区间 | 语义未定（OQ-5：无帧？全程？） | 待裁定后写显式值 |
| `invincible:8-16f` 等 8 行 | `invincible:` 前缀冗余 | 列名已表意 | `8-16f`（前缀删除） |
| `SA:12-26s` | 单位混用 | 见 §5 | 待 OQ-2 裁定 → `SA:12-26f` 或 `SA:buff:<id>` |
| `@50%` / `@100%` / 裸「几率」 | status/special 内缀 | 语法未入字典；6 行无数值 | 字典收录 `@N%`；裸「几率」补数值（B-1 待办） |
| active `维持` / `持续` | 状态词 | SkillRuntime 需「按住维持」模式 | `hold`（按住维持，松开进 recovery） |
| active `可控` | 状态词 | 「操控型生效窗」（方向/范围随输入） | `controlled`（生效期消费转向/范围指令，ADR-0010 关联） |
| active `飞行`（魂御） | 状态词 | 弹道阶段而非本体动作 | `projectilePhase`（active 交给 Projectiles 模块计时） |
| cooldown `公共1.5` | 中文前缀 | 可解析但非规范 | `shared:90`（Tick）+ 字典收录「装填系共享 CD 组」 |
| notes 内 `0.68s/0.82s` | 文档注释 | 非字段值但易误读 | 移入实现注记 |
| hitbox `circle:r3.5:耐久2000` | 判定体+耐久混写 | 耐久属 Unit/Deploy 语义 | deploy 类拆 `DeploySpec{shape, hp, lifetime}`（§3-1 UnitSystem） |
| `20s(满阶18s)` 类满阶注记（若干） | 括号注记 | 加点成长嵌在文本 | 加点成长表（Loadout 参数化，关联 OQ-4 加点模型裁定） |

> 以上全部为**建议格式**；CSV 修改在裁定后走 Skill-Spec 补丁流程统一执行并重跑校验器。

---

# 7. Design Time → Runtime Tick Policy（时间系统收敛）

## 7.1 四层时间概念（明确区分，不混淆）

| 层 | 单位 | 例子 | 存在位置 |
|---|---|---|---|
| ① 原著明确帧/时间 | 原著文本口径（如「手速 170+」「蓄力 1.2s」） | 巴雷特蓄力 1.2s、幻影龙牙 5 段 | GDD 考据注 |
| ② Skill-Spec 设计帧/秒 | **60fps 设计帧（整数）或秒** | startup_f=12、active_f="2s"、status 4s | skills.csv（唯一事实源） |
| ③ Runtime Tick | **60 Tick/s 离散战斗时间** | SkillDef 全部时长字段 | Arena.Core |
| ④ 渲染帧 | 60 FPS 表现帧（可波动、可 Hitstop 慢放） | 动画/插值 | Arena.Client |

## 7.2 映射政策

1. **恒等映射（现行）**：因设计帧恰好定义在 60fps 且仿真为 60 Tick/s，`1 设计帧 ≡ 1 Runtime Tick`，帧字段零量化误差。**这是当前数据版本的巧合性便利，不是永久绑定**——CSV 字段语义应理解为「设计帧（60fps 口径）」，Runtime SkillDef 属性命名为 `*Ticks`，转换函数集中在 Data.Catalog 一处
2. **秒值映射**：`ticks = round_half_even(seconds × 60)`；Data.Validate 强制 `×60 ∈ ℤ`（当前 100% 满足，防回归）
3. **Tick 率变更预案**：若未来仿真 Tick 率改变（当前无此计划），只重跑 Catalog 量化层，②层设计数据不动——**设计数据永久绑定的是「秒/设计帧」语义，不是 Tick**
4. **绑定校验**：回放文件记录 dataVersionHash——设计数据或量化政策任何变化使旧回放显式失效（不静默）

---

# 8. Remaining OPEN-QUESTIONS（全部为设计裁定项，本阶段不做技术假设）

| ID | 问题 | 影响面 | 建议裁定时机 |
|---|---|---|---|
| OQ-2 | `SA:12-26s` 本意：12-26f 瞬时窗（疑笔误）还是 26s 持续霸体（疑缺 buff 挂接） | 1 行数据 | Skill-Spec 补丁时 |
| OQ-4 | 加点模型：D37 无携带上限后，§18.2「36 点/10 技能」如何自洽（全池加点？点数不变对象变？） | Loadout/构筑 UI/平衡基准「平均 3.6 级」 | **用户设计裁定**（架构侧只要求 Loadout 参数化接口可承载任意模型） |
| OQ-5 | `invincible:0f` 语义（无无敌 vs 全程无敌） | 1 行数据 | Skill-Spec 补丁时 |
| OQ-6 | B-1 六行裸「几率」的具体数值（作者补录） | 6 行数据 | 数据补丁时 |
| OQ-7 | GDD【滚取消】规则保留（等未来技能消费）还是删除 | GDD §8.2 | 低优先 |
| OQ-8 | cancel_min_tier=`counter` 的精确语义（反击技专属取消窗 §4.6）进字典 | 2 行数据 | 字典补录时 |
| OQ-9 | 跨技能变异类 buff（骑士精神/街头风暴）归 Signature 还是标准 buff 组合——技术两可，按「原语在 Core、组合在插件」原则默认 Signature，待 ADR-0008 复核 | 2 技 | ADR-0008 |

---

# 9. ADR-0001 Preconditions & Verdict

## 9.1 ADR-0001 必须涵盖（议题清单，全部已有推荐方案）

1. **定点数值单位制**（OQ-1）：各物理量 Q-format 表 + 常量构建期量化政策（§4.4-3）
2. **预计算系数表**替代 pow：三个衰减表 + 一次性修正常量表，RoundHalfEven 统一（§4）
3. **有序容器纪律**：Sim 状态一律数组/有序结构 + 稳定索引，禁 Dictionary 遍历序依赖（audit C-10）
4. **三项量化政策**：武器攻速取整 `max(1, round(base/atk_spd))`（M-4）、多段命中时刻表公式（M-2）、active=- ⇒ 2T（M-3）
5. **RNG 决议采纳**：Per-Stream 计数型 RNG 进 P1 修订表述（§1，正式 ADR 归属 ADR-0003/0008 亦可，需在 ADR-0001 原则中声明）

## 9.2 不阻塞 ADR-0001 的挂起项

- OQ-2/4/5/6/7/8/9（设计裁定/数据补丁）——均不影响 Core 数值体系设计
- GDD 措辞修订（P1/D02、§24.2 Hitstop、§30 树）——GDD 修订流程另行执行

## 9.3 Verdict

```
ADR-0001 READY
```

依据：B-1 技术方案闭环（流隔离 RNG，数学保证抗污染）；确定性公式方案确定（预计算定点表，精确有理数离线生成）；时间四层模型与映射政策成文；9 项 Core 能力接口确认全部可通用化且不破坏确定性；全部遗留项均为设计裁定或数据补丁，无一项需要先写 ADR-0001 才能回答。

*本阶段未创建任何 ADR、未修改 GDD/Skill-Spec/技能数据/架构文档正文、未开始编码。*
