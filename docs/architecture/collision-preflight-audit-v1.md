# Collision Preflight Audit v1 — SPEC-0005/0006 实现前最终预审

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-09-01 |
| 起点 HEAD | `a7ac55b`（SPEC-0005/0006 刚落盘）→ 完成后 HEAD 见文末 |
| 性质 | SPEC 数学/语义缺口预审（发现即修正 SPEC 本身——规格澄清，非重设计） |
| 产出 | 本报告 + SPEC-0005/0006 Preflight Amendments（PA-1~PA-7 / PA-H1~H5）+ ADR-0001 Errata E-3（已在 SPEC 创建时并入） |

---

## 1. 审计结果（10 项逐条）

### 1. CollisionPredicate 单调性——**发现表述缺陷，已修正（PA-1）**

v1 §5.1 以「扫掠前缀已接触对 t 单调」作为逐类证明依据——该表述把负载性质说反了。**生产实现真正依赖的性质是**：

- **P1 区间性**：凸图元 + 线性相对运动 ⇒ 每约束的接触集 S_i 是**闭区间**（Minkowski 差凸性；凸性是前提，ShapeLibrary 已强制扇角 ≤180°）
- **P2 可交性**：S = ∩S_i 仍为闭区间 ⇒ **TOI = min S 由区间裁剪+二次求根解析得出**——这才是生产路径
- **P3 前缀谓词单调**：仅为 P1 的推论，**只**被测试 oracle 的二分使用——生产代码不得以「谓词单调」为由实现二分

v1 表述的推论链没有产生错误设计（解析路径本来就正确），但若实现者按「单调谓词」先写二分会走弯路。已在 SPEC-0005 PA-1 修正为 P1/P2/P3 三段表述。

### 2. TOI 表示/量化/同值语义——**发现缺口，已补（PA-2/PA-3）**

- **表示**：toiFixed = Q32.16 ∈ [0, ONE]，解析值 RHE 量化
- **[0,1] 裁剪三规则**（v1 缺失）：t_in≤0 ⇒ TOI=0 **起点重叠是合法接触**（消灭「出生在体内是否命中」歧义）；相切 t_in=t_out ⇒ 闭区间语义 = 接触；t_out<0 ⇒ 无接触（不追溯）
- **同量化值语义**：两个不同真实 TOI 落入同一 toiFixed ⇒ **同一离散时刻**——按排序键后续字段定序，零裁量
- **比较/排序只用量化值**；键相等 ⇒ 构造上同一事件（去重条款，PA-4）

### 3. 排序键充分性——**充分，补两条成文（PA-4）**

(toiFixed, layerRank, AttackerId, DefenderId, hitboxId, Region, kind) 对 L1/L3 全序充分：
- 同 (mover, obstacle) 对单 Tick 单接触区间 ⇒ 单 toiFixed；obstacle object_id 唯一 ⇒ 键无自碰撞
- 补充：**键全等 ⇒ 同一事件（构造去重）**；**L2 Push 键 = (layerRank=1, minId, maxId)**（无 TOI，Tick 末分离，成对无向键升序规范化）——v1 未成文，已补

### 4. 墙角迭代充分性——**对 ARENA001 已证明，通用上限改 4（PA-5）**

几何证明：同时接触两静态阻挡体要求中心距 ≤ 0.9+两体半径。实测 ARENA001 阻挡体（边界/掩体×4/立柱×4/木箱×4/石块×2）两两间距 ≥8m > 1.8m 阈值 ⇒ **唯二组合 = 边界邻边角 + 边界+单障碍，均 ≤2 约束——2 次迭代对 ARENA001 数学充分**。通用化：迭代上限改 **4** + 超限确定性降级（snap 最首 TOI 面 + 清法向速度 + Telemetry）；**Bounce 与 corner 迭代职责分离**：反弹只发生一次（第一 TOI 面），角落第二面为 L1 clamp（无第二次反弹）——v1 未明确，已补。

### 5. BroadPhase 等价性——**发现条款缺失，已补（PA-6）**

- 保守性定义：实体按 **swept-AABB**（from/to 并集包围盒）入格，跨格覆盖含端点；格索引 floor 语义单归属
- **等价性契约**：BroadPhase+Narrow ≡ 全量 Narrow（逐位）——**T55**：50 实体×随机速度×1000 Tick 双路径比对进 CI
- BroadPhase 结果不进事件/快照/hash（纯瞬时缓存）

### 6. Tick 离散旋转 vs GDD/Skill-Spec——**零冲突，已记录（实测）**

实测全部旋转语义技能的判定体：旋风斩/旋风脚/旋风腿/旋风/风卷流云/扫把旋风/剑刃风暴/螺转旋风杀 = **circle（旋转不变）**；螺旋念气杀/樱杀碎月 = **静态锥**；回风式 = proj。**无一需要 Tick 内连续旋转判定体**——Intra-Tick 公理零冲突。fan/cone 多段技 11 个（百龙流星打/三段斩/十字军审判「移动中」等）：Owner 在 Act 中朝向锁定（GDD §4.7 修正仅前摇），hitbox 朝向按命中 Tick 位姿采样——与公理一致。十字军审判「保持冲锋移速」= hitbox 随 Owner 线性平移，仍在公理内。**无冲突技能清单已记录于报告 §6。**

### 7. 2.5D 高度模型——**发现 3 处未定义，已补（PA-H1）+ 1 项 Schema-15**

- 高度带双端 **inclusive**（≤ 语义，与 T54d 一致）；地面 y=0 强制；上方无界
- **头部 = 3D 球测试**（dy 参与三平方和）——v1 的「高度带内再查水平圆」近似会在球面边缘错判，已改
- **发现 Schema-15**：proj/lob 无 aimHeight 字段——直线弹恒定高度，**aimHeight 由 GDD §4.6 推导**（弱点/头部标注 ⇒ 1.6m 头部；默认 1.2m 躯干）——巴雷特 ×2 头部玩法由此可表达；数据补丁轮处理，未裁定前 Compiler 默认 1.2m 通过 + L2 标记
- 近战/横扫 hitbox 高度带默认 [0.2, 1.9]（覆盖头部球 1.42–1.78——**豪龙破军部位结算依赖**）——同批 Schema-15

### 8. HitRegion 唯一性——**发现选取规则缺失，已补（PA-H2）**

Head 球与 Torso 带可同时相交（颈部穿越）：**HitRegion = argmax(priority)（Head=20 > Torso=10，互异 ⇒ 唯一）**；并列保护预置（取 min toiFixed）；单 Defender 单 Segment 恰一个 Hit 事件；HitPoint/HitNormal 取所选 Region 接触值。

### 9. Collision→HitResolve 后处理——**发现时序未定义，已固化（PA-H3）**

- Destroy ⇒ 停止处理列表全部剩余条目；Pierce ⇒ 结算时扣减、=0 视同 Destroy；Bounce ⇒ 本 Tick 终态（剩余运动被反弹消耗）
- 同 Projectile 同 Tick 多目标：允许，TOI 序逐个；同一 Hurtbox 同 Segment 禁重复（跨 Segment 合法）；multi-hit 裁决阶段 = HitResolve
- **状态豁免前移**（PA-H4）：无敌/倒地保护/隐形资格过滤在 ContactList 生成前由 Sim 提供——Collision 纯几何不依赖规则状态

### 10. 三环境等价——✅ 结构性成立 + 测试固化

Sim.Collision 是唯一实现点（Arena.Core 纯 C#）；T55（BroadPhase 等价）+ T59（三环境 CollisionResult 序列逐位一致）进测试清单。

---

## 2. Critical Issues（本次预审发现并全部闭合）

| # | 级别 | 问题 | 闭合 |
|---|---|---|---|
| PF-1 | HIGH | 单调性表述不精确（「前缀谓词单调」作生产依据） | PA-1 重述为 P1 区间性/P2 可交性/P3 oracle 推论 |
| PF-2 | HIGH | [0,1] 裁剪/起点重叠/相切语义缺失 | PA-2 四条规则 |
| PF-3 | HIGH | TOI 同量化值语义未定义 | PA-3 同一离散时刻 + 排序键 |
| PF-4 | MEDIUM | 排序键充分性未证明 + Push 键缺失 | PA-4 |
| PF-5 | MEDIUM | 墙角迭代上限无证明/无降级 | PA-5（ARENA001 证明 + 通用 cap4 降级） |
| PF-6 | HIGH | BroadPhase 等价性契约缺失 | PA-6 + T55 |
| PF-7 | MEDIUM | 「Fighter 突进穿越静态 fan」采样漏判未覆盖 | PA-7 全 Combat 对相对扫掠 |
| PF-8 | HIGH | 头部判定=高度带近似（应为 3D 球）+ proj 无高度语义 | PA-H1 + Schema-15 |
| PF-9 | MEDIUM | HitRegion 多重叠选取规则缺失 | PA-H2 |
| PF-10 | MEDIUM | Destroy/Pierce/Bounce/MultiHit 时序未定义 | PA-H3/H4 |

## 3. Required SPEC Corrections（已执行）

- SPEC-0005：PA-1~PA-7（数学重述/边界规则/量化语义/排序键/墙角证明/BroadPhase 等价/全对相对扫掠）
- SPEC-0006：PA-H1~H5（高度模型/Region 选取/后处理顺序/资格过滤前移/Schema-15）

## 4. Mathematical Correctness

- 全部接触谓词 int64 平方形式（无 sqrt 参与判定）；TOI 解析 = 线性裁剪 + FSqrtFixed（整数 Newton，固定迭代）+ DivRoundHalfEven
- 二分 oracle K=32 误差 3.1×10⁻¹⁰ m ≪ 1 Fixed 量子——仅测试对照
- 溢出复核：二次系数坐标 ≤2¹⁶ Raw 域 → Δ ≤ 2⁴⁵；三平方和（头球）≤ 3×2⁴⁵ < 2⁶³ ✓

## 5. Determinism / BroadPhase Equivalence / Rotation Compatibility / Multi-Collision / 三环境

见 §1 各项——全部闭合；新增测试 **T55（BroadPhase 等价）**、**T56（三环境碰撞序列一致）**、T54a–j/T59 维持。

## 6. READY FOR COLLISION IMPLEMENTATION

```
READY FOR COLLISION IMPLEMENTATION
```

（实现归 Phase 3；本阶段未实现任何代码、未修改 GDD/Skill-Spec/CSV、未创建 ADR、未裁定任何 OQ。）

---

*自审 20 项要点：HEAD 复核 cb6ee38→a7ac55b✓ 未引旧报告✓ Hitbox/Hurtbox/Terrain/Projectile/Knockback/Dash 实测✓ tunneling 实证✓ Sweep 定义✓ ContactPoint/Normal✓ 排序✓ 无 ADR-0001 违反（整数 sqrt 为 Errata 增补）✓ 无隐式 float✓ Rollback/Replay 一致✓ Server/Client 一致✓ 未裁定 OQ✓ 未改数据✓ Godot Physics 非权威✓ Core 开工条件明确✓*
