# Combat Fidelity Review v1 — 实现完成度与战斗还原度全面审查

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-09-01 |
| 基线 | master `6ebe041`（Phase 3A-3D 标称完成） |
| 审计性质 | **FULL IMPLEMENTATION & COMBAT FIDELITY REVIEW**——实测代码 vs 设计规格差距 |
| 核心问题 | 当前实现能否支撑《荣耀》式高细粒度战斗？ |

---

## 1. Executive Summary

**方向正确。实现严重不足。**

当前 Arena.Core 共 1298 行源码（不含 obj/.godot 生成物）。SPEC-0005/0006 和 ADR-0001~0010 定义了一个完整的确定性战斗运行时需要约 **15+ 个模块**。实测扫描结果：

- **有真实实现且被调用**：Fixed 数学（3 文件）、RNG（1 文件）、DeterministicTables（1 文件）、Snapshot 骨架（1 文件）——共 **6 个模块约 600 行**
- **有代码但从未被调用**：SweepSolver（独立类，SimWorld 零调用）、HurtboxModel（数据定义，零消费）
- **SimWorld 存在阻塞性 bug**：HandleMove 设速度后立即清零（角色无法移动）
- **28 个 SPEC/ADR 定义的能力中 25 个完全缺失**（0 代码引用）

**当前实现距离可玩的《荣耀》式战斗约 60–70% 的代码量差距**。这些差距不是可"未来扩展"的——它们是 Core 战斗循环的必要组成部分。

---

## 2. Runtime Reality（实测：真实实现 vs 仅定义）

| 模块 | ADR/SPEC 定义 | 源码文件 | 代码行数 | 被调用？ | 判定 |
|---|---|---|---|---|---|
| **Fixed Q32.16** | ADR-0001 §1 | Fixed.cs + DeterministicMath.cs | ~120 | ✅ 全 Core | **真实实现** |
| **系数表** | ADR-0001 §2 | DeterministicTables.cs | ~180 | ✅（但 SimWorld 内 `Math.Pow` 已修正为查表） | **真实实现** |
| **RoundHalfEven** | ADR-0001 §1.3 | DeterministicMath.cs | ~30 | ✅ | **真实实现** |
| **ISqrt/FSqrtFixed** | ADR-0001 E-3 | DeterministicMath.cs | ~40 | ⚠️ 仅 SweepSolver 调用（SweepSolver 自身未被调用） | 部分实现 |
| **Per-Stream RNG** | ADR-0001 §4 | SimRng.cs | ~80 | ⚠️ SimWorld 持有但 Roll100 从未被战斗逻辑调用 | **骨架** |
| **Snapshot 骨架** | ADR-0001 §8 | SnapshotData.cs | ~50 | ⚠️ 测试使用，Sim 未消费 | **骨架** |
| **Tick 类型** | ADR-0001 §5 | DeterministicMath.cs 内 | ~20 | ⚠️ 定义存在，SimWorld 用 `long Tick` | **骨架** |
| **SimWorld** | ADR-0009 | SimWorld.cs | 486 | — | **简化 stub（见 §3）** |
| **SkillTimeline** | ADR-0003/0009 | SkillTimeline.cs | ~50 | ⚠️ SimWorld 有内联简化版 | **骨架** |
| **EventLog** | ADR-0003 | EventLog.cs | ~40 | ✅ SimWorld 使用 | **真实实现（简化）** |
| **SweepSolver** | SPEC-0005 | SweepSolver.cs | ~120 | ❌ **SimWorld 零调用** | **孤岛代码** |
| **HurtboxModel** | SPEC-0006 | HurtboxModel.cs | ~30 | ❌ **零消费** | **数据定义** |
| **BroadPhase** | SPEC-0005 | — | 0 | ❌ | **缺失** |
| **CollisionSystem 整合** | SPEC-0005 | — | 0 | ❌ | **缺失** |
| **StatusSystem** | GDD §7.3 / pre-adr §3 | — | 0 | ❌ | **缺失** |
| **BuffSystem** | GDD / pre-adr §3 | — | 0 | ❌ | **缺失** |
| **ShieldPool** | pre-adr §3-3 | — | 0 | ❌ | **缺失** |
| **VisibilitySystem** | pre-adr §3-2 | — | 0 | ❌ | **缺失** |
| **UnitSystem** | pre-adr §3-1 | — | 0 | ❌ | **缺失** |
| **ISignature/ISimContext** | ADR-0008 | — | 0 | ❌ | **缺失** |
| **Data Compiler (C#)** | ADR-0002 | DataCompiler.cs + SkillParser.cs | ~250 | ⚠️ 仅测试调用，SimWorld 不消费 | **部分实现** |
| **CmdStream 序列化** | ADR-0010 | — | 0 | ❌ | **缺失** |
| **NetTransport** | ADR-0004 | — | 0 | ❌ | **缺失** |
| **Net.Predict** | ADR-0006 | — | 0 | ❌ | **缺失** |
| **Net.Rollback** | ADR-0006 | — | 0 | ❌ | **缺失** |
| **Replay.Recorder/Player** | ADR-0005 | — | 0 | ❌ | **缺失** |
| **Audit.AntiCheat** | ADR-0005 | — | 0 | ❌ | **缺失** |
| **Perf.Telemetry** | — | — | 0 | ❌ | **缺失** |
| **Save.Prefs** | — | — | 0 | ❌ | **缺失** |
| **Audio.Manager** | — | — | 0 | ❌ | **缺失** |

### 定量汇总

| 层级 | 定义模块数 | 有真实实现 | 骨架/部分 | 孤岛代码 | 完全缺失 |
|---|---:|---:|---:|---:|---:|
| 基础数学（Calc） | 4 | 3 | 1 | 0 | 0 |
| 碰撞（Collision） | 4 | 0 | 1 | 2 | 1 |
| 战斗循环（Sim） | 5 | 1 | 2 | 0 | 2 |
| 数据管线（Infra.Data） | 3 | 1 | 2 | 0 | 0 |
| 网络/回放/审计 | 6 | 0 | 0 | 0 | 6 |
| 基础设施（Godot/IO/Audio） | 4 | 0 | 0 | 0 | 4 |
| **总计** | **26** | **5** | **5** | **2** | **14** |

---

## 3. Combat Fidelity Gap（战斗还原度缺口清单）

### 3.1 SimWorld 关键 Bug（阻塞性）

| Bug ID | 描述 | 影响 | 严重程度 |
|---|---|---|---|
| **BUG-1** | `HandleMove` 设 VelX/VelZ 后立即 `Fixed.Zero`——角色无法移动 | 走位系统完全失效 | **BLOCKER** |
| **BUG-2** | `GetFighterIdForCommand` 用 `TargetTick % Fighters.Count` 选择 Fighter——指令路由错误（应为 per-Fighter CmdStream） | 多人战斗指令混乱 | **BLOCKER** |
| **BUG-3** | SweepSolver 存在但 SimWorld 使用 `distSq <= range²` 距离检查——**所有 SPEC-0005 定义的 Sweep 逻辑被绕过** | 无防穿模能力 | **BLOCKER** |
| **BUG-4** | HitRegion 由 SkillDef.HappyRegion 硬编码而非几何相交计算——攻击位置不影响命中部位 | 部位判定失效 | **HIGH** |
| **BUG-5** | HitPoint 设为 victim 位置而非实际接触点——VFX/反弹锚点错误 | 表现还原失真 | **HIGH** |

### 3.2 Combat Fidelity Gap Report

| Gap ID | 原著/设计依据 | 当前数据表达 | 当前 Runtime 能力 | 缺口 | 严重程度 | 阻塞 | 推荐解决 |
|---|---|---|---|---|---|---|---|
| G-01 | GDD §7.3 16 类异常 | status 列已解析 | 无 StatusSystem | 全部 16 类异常无法生效 | **BLOCKER** | ✅ | SPEC-0005 扩展 + Sim 状态域 |
| G-02 | GDD §6.4 霸体/超级霸体 | armor 列已解析 | 无霸体处理逻辑 | 霸体技全部无效 | **BLOCKER** | ✅ | Fighter 状态扩展 |
| G-03 | GDD §6.5 无敌帧 | invincible 列已解析 | 无无敌判定 | 受身/翻滚/反击全部失效 | **BLOCKER** | ✅ | Hurtbox 谓词扩展 |
| G-04 | GDD §6.2 格挡+完美格挡 | 格挡技能数据存在 | 无格挡系统 | 剑客/骑士/守护天使核心机制缺失 | HIGH | ⚠️ | 新模块 |
| G-05 | GDD §8.2 取消系统 | cancel_min_tier 已解析 | 无取消逻辑 | 连招系统核心缺失 | **BLOCKER** | ✅ | SkillRuntime 状态机 |
| G-06 | GDD §4.5 投射物 | proj 语法已解析 | 无投射物实体 | 75 条投射技无法运行 | **BLOCKER** | ✅ | Projectile 实体系统 |
| G-07 | pre-adr §3 UnitSystem | summon 语法已解析 | 无召唤实体 | 20+ 条召唤技无法运行 | HIGH | ⚠️ | UnitSystem 模块 |
| G-08 | GDD §6.3 反击/盾反 | counter 类型已解析 | 无反击窗口 | 反击技全失效 | HIGH | ⚠️ | Counter 系统 |
| G-09 | GDD §6.2 吸收盾 | 盾值数据存在 | 无护盾池 | 格挡/护盾技无效 | HIGH | ⚠️ | ShieldPool |
| G-10 | GDD §7.5 共存互斥 | status 语法可表达 | 无共存/互斥逻辑 | 异常状态管理缺失 | MEDIUM | ⚠️ | StatusSystem 子项 |
| G-11 | SPEC-0006 Hurtbox | 无 hurtbox 数据列 | 无 Hurtbox 实例化 | 命中=距离判定，无部位/体积 | HIGH | ⚠️ | ClassDef 装配扩展 |
| G-12 | GDD §9.3 职业资源 | resource 列已解析 | 无职业资源系统 | 炫纹/弹匣/召唤位等失效 | HIGH | ⚠️ | ResourceSystem |
| G-13 | GDD §19 地形交互 | arena.csv 已建 | 仅边界 clamp | 掩体/高台/坠落/立柱全部缺失 | HIGH | ⚠️ | TerrainSystem |
| G-14 | GDD §14 签名机制 | special 关键词已解析 | 无签名插件执行 | 60 条 C 类技能全部不工作 | **BLOCKER** | ✅ | ISignature 框架 |
| G-15 | GDD §21 手柄 | 映射表已定义 | 无输入系统 | 仅 Command 结构体 | MEDIUM | ❌ | ADR-0010 实现 |
| G-16 | GDD §20.7 回放 | 格式已定义 | 无回放系统 | 无录像/回放能力 | MEDIUM | ❌ | ADR-0005 实现 |
| G-17 | GDD §22 打击感 | 五要素已定义 | 无 Hitstop/震屏/音效 | 战斗手感为 0 | MEDIUM | ❌ | Presentation 层 |
| G-18 | ADR-0008 武器 overlay | weapons.csv 已解析 | 无武器属性注入 | 武器差异无效 | MEDIUM | ⚠️ | Weapons.Apply() |

### 3.3 按 A/B/C 分类重新统计

| 类型 | 总数 | Runtime 已支持 | 部分支持 | 未支持 |
|---|---:|---:|---:|---:|
| **A 纯数据驱动** | 162 | ~60（伤害+状态枚举路径） | ~60（缺状态效果路由） | ~42 |
| **B 数据+规则修饰** | 265 | ~20（基础命中+击退+浮空） | ~40（有数据但缺处理逻辑） | ~205 |
| **C Signature Plugin** | 60 | 0 | 0 | 60 |
| **合计** | **487** | **~80 (16%)** | **~100 (21%)** | **~307 (63%)** |

> v1 审计给出 A=162/B=265/C=60，但那是**数据格式分类**。本表是 **Runtime 实际可执行比例**——差距巨大。

---

## 4. Collision Fidelity（SPEC-0005 是否达标）

**判定：SPEC 正确，实现为 0% —— SweepSolver 是孤岛代码。**

| SPEC-0005 要求 | 实测 |
|---|---|
| CollisionSystem 唯一职责边界 | ❌ 未建立——SimWorld 内联简化距离判定 |
| IntegrateMove 统一路径 | ❌ 未实现——SimWorld 直接 `pos += vel/FPS` |
| 七种运动统一 | ❌ 仅 Fighter 简化移动 |
| ShapeLibrary | ⚠️ Shape struct 定义存在但未被消费 |
| BroadPhase | ❌ 完全缺失 |
| SweepSolver 调用 | ❌ **SimWorld 零调用 SweepSolver** |
| TOI | ❌ 未使用（SimWorld 用终点距离判定） |
| HitPoint | ❌ 设为 victim 位置（非几何接触点） |
| HitNormal | ❌ 未计算 |
| Hurtbox 实例化 | ❌ HurtboxModel 定义了但未实例化 |
| HitRegion 选取 | ⚠️ 从 SkillDef 硬编码而非几何计算 |
| 多碰撞排序 | ⚠️ 简化 for 循环 |
| 边界反弹 | ⚠️ 白盒式 clamp（无 sweep） |
| 角落迭代 | ❌ 未实现 |
| Projectile Sweep | ❌ 无投射物实体 |

---

## 5. Skill Fidelity（487 技 Runtime 覆盖率）

基于 audit-spec-consistency-v1 的 A/B/C 分类与本审计的 Runtime 实测：

| 类型 | 总数 | Runtime 可运行 | 缺什么 |
|---|---:|---:|---|
| A 纯数据 | 162 | ~60 | 剩余 ~100 缺状态效果路由（burn/bleed/slow/freeze 等实际效果）|
| B 数据+规则 | 265 | ~20 | 剩余 ~245 缺规则修饰器（蓄力/延迟/部署/反弹/护盾等）|
| C Signature | 60 | 0 | 全部需要 ISignature 框架（未实现）|
| **合计** | **487** | **~80 (16%)** | **~407 (84%) 需要额外实现** |

**阻塞最大的 3 项**：
1. StatusSystem（影响 ~100 条 A/B 技的状态效果路由）
2. Projectile 实体系统（影响 75 条 proj 技）
3. ISignature 框架（影响 60 条 C 类技）

---

## 6. Golden Slice 建议

当前最应验证的 4 职业/技能组合：

| 职业 | 选择理由 | 必须验证的战斗行为 |
|---|---|---|
| **战斗法师 BMG** | 默认教学职业；浮空引擎最直观；已有白盒数据 | 天击浮空→连突多段→圆舞棍倒地→炫纹资源→斗者意志被动 |
| **剑客 BLA** | 格挡/反击/多段/武器五类 | 格挡化解→银光落刃跳劈→剑气步→三段斩取消连 |
| **神枪手 SRP** | 唯一 80m/s 高速弹； 弹匣资源； 弱点判定 | 巴雷特头部×2→换弹→速射→散射多目标→膝撞霸体 |
| **柔道家 GRP** | 全 grab 体系； 投技方向控制 | 背摔→抛投方向→空绞杀浮空→螺旋多目标 |

**验证标准**：这 4 职业的 ~15 个核心技能在 Runtime 中产生正确的事件流和状态变化，且整个流程满足 Determinism Contract。

---

## 7. Player Experience Gap

即使当前代码全部修复到 ADR/SPEC 合规，玩家仍会遇到以下"不像《荣耀》"的问题：

| # | 问题 | 根因 | 解决阶段 |
|---|---|---|---|
| 1 | 没有打击感（无 Hitstop/震屏/音效/受击反馈） | Presentation 层完全缺失 | Phase 5+ |
| 2 | 没有视觉反馈（无角色模型/动画/VFX/伤害数字） | 白盒胶囊体 | Phase 5+ |
| 3 | 没有镜头系统（无锁定/追踪/特写） | CameraRig 未实现 | Phase 5+ |
| 4 | 技能只有伤害没有过程感（无弹道视觉/吟唱条/范围指示器） | Presentation 缺失 | Phase 5+ |
| 5 | 操作手感未验证（输入延迟/响应性/取消窗口手感） | 无实机测试 | Phase 3F+ |
| 6 | AI 对手不存在（无法测试 PVP 手感） | AI 系统未实现 | Phase 4+ |
| 7 | 没有竞技场视觉参考（高台/掩体/坡道不可见） | ArenaDef 仅有数据 | Phase 5+ |

---

## 8. Architecture Risk

| 风险 | 评估 |
|---|---|
| ADR/SPEC 方向 | ✅ 正确——Determinism Contract、Source-of-Truth、Server Authority 设计合理 |
| Fixed Q32.16 | ✅ 数学正确，测试通过 |
| SweepSolver 解析方法 | ✅ 数学正确（虽然未被调用） |
| SimWorld 架构 | ⚠️ 当前为简化 stub，需大幅扩展才能承载 SPEC-0005/0006 |
| 事件协议 | ✅ ADR-0003 设计合理 |
| 返工风险 | ⚠️ **中等**——SimWorld 当前实现过于简化，扩展到 SPEC 合规可能需要重写核心循环而非渐进修改。风险在于：如果 SimWorld 的数据结构不支持 Hurtbox/Status/Buff 扩展，需要推翻重写 |

---

## 9. Performance Risk

| 场景 | 预估 | 风险 |
|---|---|---|
| 2 Fighter + 0 Projectile | 低 | 当前 stub 足够 |
| 2 Fighter + 32 Projectile | 中 | Sweep 逐对检测 O(n²)，需要 BroadPhase |
| 8 Fighter + 128 Projectile | 高 | 需要 BroadPhase + 空间网格，否则 O(n²) 不可接受 |
| 16 Fighter + 512 Projectile | 极高 | Snapshot 序列化 + 事件流可能成为瓶颈 |
| **结论** | 当前**无法评估**——实际碰撞代码未连接，性能测试无意义 | **BroadPhase 实现后必须立即做 perf 基准** |

---

## 10. 60→120 Tick Change Impact Report

实测代码中引用 TICK_RATE/60/FPS 的位置：

| 位置 | 文件 | 当前值 | Tick 率变化时 |
|---|---|---|---|
| `GRAVITY_PER_TICK = 22 × ONE / TICK_RATE` | SimWorld.cs | 400562 | ✅ 自动适应（TICK_RATE 常量） |
| `TICK_RATE = 60` | SimWorld.cs | 60 | ⚠️ 改为 120 即可，但需同步改 Compiler |
| `KB_VEL_MULT = 9` | SimWorld.cs | 9 | ⚠️ 摩擦系数 85/100 是 per-Tick 的——120Hz 需重推导 |
| `ControlValue - 20/1` | SimWorld.cs | 20/s→per-Tick | ⚠️ 同上 |
| `f.AirTime >= 180` | SimWorld.cs | 3s×60 | ⚠️ 需按新 Tick 率重算 |
| SkillDef 时长 | Compiler 产物 | 设计帧→Tick | ✅ Compiler 重量化即可 |
| 碰撞 Sweep | SweepSolver | t∈[0,1] 相对参数 | ✅ 零改动 |
| RNG | SimRng | 无 Tick 依赖 | ✅ 零改动 |

**结论**：碰撞算法零改动 ✅；技能语义零改动 ✅（Compiler 重量化）；但 **per-Tick 常量（摩擦/回复/衰减）分散在 SimWorld 硬编码中，需集中到 Compiler/Runtime Constants**——这正是 ADR-0001 E-5 要求但尚未实现的。

---

## 11. 测试真实覆盖评估

| 测试类别 | 已有测试 | 真实验证力 |
|---|---|---|
| Fixed 数学 | 12 | ✅ 真实验证 |
| ISqrt 边界 | 5 | ✅ 真实验证 |
| RNG 隔离 | 6 | ✅ 真实验证 |
| Snapshot round-trip | 4 | ✅ 真实验证 |
| Compiler gate | 3 | ✅ 真实验证 |
| **Combat Vertical Slice** | **8（6 PASS 2 FAIL）** | ⚠️ **验证了简化 stub 的行为，非 SPEC 行为** |
| **Sweep/Hurtbox/HitRegion** | **0** | **❌ 完全缺失** |
| **Projectile 穿模** | **0（T54 仅 SPEC 定义）** | **❌ 完全缺失** |
| **Status/Buff/Gates** | **0** | **❌ 完全缺失** |
| **确定性闭环（T_VS7/8）** | **2** | ⚠️ 验证的是 stub 行为的确定性，非完整战斗 |

---

## 12. 最大的 10 个问题

| # | 问题 | 类型 | 影响 |
|---|---|---|---|
| 1 | SimWorld 不调用 SweepSolver——SPEC-0005 碰撞管线完全断路 | Architecture | 无防穿模 |
| 2 | HandleMove 设速度后立即清零——移动系统失效 | Runtime Bug | 角色无法走位 |
| 3 | Hurtbox 从未实例化——命中=距离判定，无部位/体积 | Fidelity | 部位判定失效 |
| 4 | StatusSystem 完全缺失——16 类异常无法生效 | Combat | 控制技全失效 |
| 5 | SkillRuntime 是简化列表而非 Timeline 状态机 | Architecture | 取消窗/霸体/无敌无法独立表达 |
| 6 | Compiler 输出未被 Runtime 消费——SimWorld 用硬编码测试数据 | Architecture | 数据驱动铁律断裂 |
| 7 | Projectile 无实体系统——75 条弹技无法运行 | Combat | 远程职业全失效 |
| 8 | ISignature/ISimContext 完全缺失——60 条 C 类技无法运行 | Combat | 13 职业核心机制失效 |
| 9 | 取消系统缺失——连招系统核心 | Combat | 连招无法执行 |
| 10 | 无 BroadPhase——大规模战斗性能无保障 | Performance | 8+ Fighter 不可用 |

---

## 13. Combat Fidelity Gap 统计

- **总缺口数**：18 项（G-01 ~ G-18）
- **BLOCKER**：5 项（G-01 异常/G-02 霸体/G-03 无敌/G-05 取消/G-14 签名）
- **HIGH**：7 项
- **MEDIUM**：6 项

---

## 14. Recommended Roadmap（按真正影响游戏质量排序）

### Phase 3E：Combat Runtime 修正（最紧迫——修 bug + 补核心缺失）
1. 修复 HandleMove bug（角色能移动）
2. 修复 GetFighterIdForCommand（正确路由）
3. 将 SweepSolver 接入 SimWorld（替换距离判定）
4. 实现 Hurtbox 实例化（Torso+Head 谓词）
5. 实现 HitRegion 几何选取（替换 SkillDef 硬编码）
6. 实现 StatusSystem（16 类异常最小集）
7. 实现取消窗（SkillRuntime 状态机）
8. Gate：巴雷特 80m/s Sweep 测试 + 天击浮空→连突→圆舞棍 连段测试

### Phase 3F：GdUnit4Net
1. 尝试安装 Godot .NET（网络允许时）
2. 成功 → 迁移 Godot/Integration 测试
3. 失败 → 维持 D-P0-1 + 报告暂缓项

### Phase 4：Golden Slice 扩展
1. 剑客 BLA 格挡/反击
2. 神枪手 SRP 弹匣/弱点
3. 柔道家 GRP grab 体系
4. 召唤师 SUM UnitSystem（最复杂签名）
5. Combat Scenario Tests（T54 矩阵全量）

### Phase 5：Presentation + Player Experience
1. Godot 场景渲染
2. Hitstop/震屏/音效
3. Camera/Lock
4. HUD
5. 实机手感验证

### Phase 6：网络 + 回放
1. NetTransport/Net.Predict/Net.Rollback
2. Replay.Recorder/Player
3. Audit.AntiCheat

---

## 15. 最终 Gate Decision

```
方向正确，实现严重不足
```

- ADR-0001~0010 + SPEC-0001~0006 的**设计**完全正确
- 基础数学层（Fixed/系数表/RNG）实现质量良好
- 但 **SimWorld 当前是简化 stub**——它验证了架构可行性，但不等于 Combat Runtime 完成
- 28 个定义模块中 5 个有真实实现、5 个骨架、2 个孤岛代码、14 个完全缺失
- 487 技中仅 ~80 (16%) 可被当前 Runtime 执行

**CORE BLOCKED: YES（碰撞子系统 + 战斗循环需从 stub 升级到 SPEC 合规）**

**下一步不是大规模技能迁移，而是将 SimWorld 从 stub 升级到 SPEC-0005/0006 合规。**
