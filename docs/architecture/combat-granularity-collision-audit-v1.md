# Combat Granularity & Collision Audit v1 — 原著级细粒度战斗 + 确定性空间碰撞专项审计

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-09-01 |
| 基线 | master `cb6ee38`（工作树干净） |
| 性质 | 只读审计 + 架构分析 + SPEC 草案（**不创建 SPEC 文件、不实现、不修改任何文档/数据**） |
| 上游 | ADR-0001/0002/0003/0004/0006/0008/0009/0010、SPEC-0001~0004、GDD §2.1/2.4/2.5/3.5/4.1/4.5/4.6/4.7/5.x/19.x、skills.csv 空间数据实测、白盒 sim.gd 运动实现实测 |

---

## 1. Executive Summary

**结论：B——当前架构方向正确（确定性数值框架、ADR-0001 契约、事件协议、回放/回溯全部成立），但「细粒度空间战斗 + 防穿模」的能力尚未进入任何已验收文档：空间碰撞子系统存在 4 项实证缺口，其中穿模风险已被当前数据实测证实。**

核心判定（不是猜测，是计算）：

1. **巴雷特狙击弹速 80 m/s = 1.33 m/Tick**（80/60），而 GDD §4.6 头部 Hurtbox 是 **r=0.18m 球（直径 0.36m）**、躯干盒深度 0.6m——终点采样碰撞下，弹体在两 Tick 之间**整体越过目标**，概率性漏判。这不是理论风险：当前 CSV 最快弹速直接触发。
2. 白盒 `_move()` 是 `pos += vel/FPS` 后**只测终点**的径向 clamp——内部薄障碍（立柱 r0.8m/掩体 1.2m 半宽）同样存在穿越窗口。
3. **GDD 当前设计已经要求部位级命中**（§4.6 弱点头部 Hurtbox ×1.5/×1.65/×2、§14.1.5 豪龙破军「按命中部位结算：头部×1.5」）——这不是未来扩展，是已批准玩法；而 ADR-0003 的 Hit 事件载荷**没有 HitRegion/HitPoint**，当前架构无法表达已批准的玩法。
4. 单位间碰撞 GDD §2.1 明确「软推挤、无实体阻挡」——Fighter↔Fighter 无穿模问题（允许重叠），但推挤本身的确定性实现同样缺失。

因此进入 Core 前**必须新增 2 份 SPEC**（碰撞/Sweep 模型 + 命中空间结果扩展），并做 2 处 Errata 级补充（ADR-0001 整数 sqrt 白名单、ADR-0003 Hit 载荷增补）。数值基座/数据管线/事件协议不受影响，可按 Phase 0/1/2 照常先行。

---

## 2. 当前仓库实际状态（实测 @cb6ee38）

| 事实 | 来源 | 内容 |
|---|---|---|
| 运动积分 | prototypes/bmg-whitebox/scripts/sim.gd `_move()` | `pos += vel/FPS` → **终点径向 clamp** → 反弹按终点法线；无 sweep、无子步、无内部障碍碰撞（白盒只有圆形边界） |
| 生产架构碰撞归属 | architecture.md Module Ownership | `Sim.Terrain`（边界/反弹裁决）+ `Sim.HitResolve`（8 类判定体解析/相交）——**Broad Phase/SweepSolver/ShapeLibrary 未定义**；`Sim.Terrain` 与 `Sim.HitResolve` 的空间职责有交叠（判定体相交在 HitResolve，地形在 Terrain，但「移动体 vs 地形」与「Hitbox vs Hurtbox」是两套路径） |
| HitEvent 载荷 | ADR-0003 §3.2 | `{attackerId, skillId, windowId, seg, victimId, damageRaw, hitNumber, victimStateBefore, y, sweep:bool, airMod:bool}`——**无 HitPoint/HitNormal/HitRegion** |
| ResolveResult | architecture.md（字段未展开） | 仅「8 类判定体解析/相交、伤害公式链」——空间结果字段未定义 |
| Hurtbox | GDD §2.1（L206） | **站立：躯干盒 0.9×1.6×0.6m；头部球 r=0.18m**——只在 GDD 文本，未进任何 RuntimeDef/CSV/架构模块 |
| 部位玩法（当前已批准） | GDD §4.6/§14.1.5/§2.4 | 弱点头部 ×1.5（巴雷特 ×2、SRP 被动 ×1.65）；豪龙破军按部位结算；窝心脚「破下段」（注：GDD 无上/下段架势系统——见 §5 判定） |
| 单位间碰撞 | GDD §2.1 | 「软推挤，无实体阻挡，允许重叠」——**排除**了 Fighter↔Fighter 硬碰撞与穿模问题 |
| 弹速实测 | skills.csv | proj 弹速 top：**80**（巴雷特）/50/45/45/45 m/s → per-Tick 位移 0.75–**1.33m** |
| 位移技能 | skills.csv | 突进 1.5–8m、滑铲/云身 4m、伏虎 6m——伴随位移的 hitbox 随体移动 |
| Knockback | skills.csv | max **4.0m**（天使威光）→ KBDIST_VEL 9.0 → **36 m/s → 0.6m/Tick** |
| Launch | skills.csv | max 9.0 m/s → 0.15m/Tick（垂直，低速） |
| 地形对象 | arena.csv（SPEC-0004） | 结界墙=矩形边界（外侧厚域）、掩体墙 2.4m 厚、立柱 r0.8m、木箱/石块薄物体 |
| 判定体语法 | skills.csv hitbox 列 | fan 83/box 104/circle 54/proj 75/cone 21/lob 17/zone 11/unit 14/ally 8/line 4/aura 4/self 30/wall 2/cyl 2/portal 1/none 46——**17 类语法，无 sweep/轨迹语义** |

---

## 3. 原著级战斗粒度能力审计（问题 A）

### 3.1 攻击位置（攻击发生在哪里/覆盖什么空间/朝向/轨迹/是否随体/是否随阶段变化）

| 能力 | 判定 | 依据 |
|---|---|---|
| 攻击覆盖空间（静态形状） | ✅ 已支持 | hitbox 语法 17 kind 全部有形状参数（fan 角度/半径、box 三维、circle 半径…），ADR-0002 §4.3 白名单可编译 |
| 攻击朝向 | ✅ 已支持 | fan/cone 带 angle_deg；朝向锁定（GDD §4.7）+ SPEC-0001 AimQuantum |
| 攻击是否随 Fighter 移动 | ⚠️ 隐式 | 位移类技能（突进/滑铲）的 hitbox 随体移动在语义上成立，但**无 swept 检测**——移动中 hitbox 只在 active 帧离散采样（§7 证实漏判窗口） |
| 攻击是否随阶段变化 | ⚠️ 部分 | hitSchedule（P-2）定义了多段命中时刻，但每段的**形状/位置变化**（如三段斩 横斩→下劈→上挑 三种判定）在 hitbox 列无法表达——单 hitbox 定义全程复用 |
| 攻击轨迹（连续轨迹） | ❌ 缺口 | 无 Hitbox(t0)→Hitbox(t1)→Swept Volume 概念；proj 有速度但命中判定未定义 sweep |

### 3.2 攻击空间模型审计（问题 四：命中位置 A/B/C 分类）

| 能力 | 分类 | 判定 |
|---|---|---|
| HitPoint | **C（阻塞）** | HitEvent/ResolveResult 无此字段；弱点判定、豪龙破军部位结算、VFX 锚点都需要——GDD 已批准玩法直接依赖 |
| HitNormal | **C（阻塞）** | 墙角连（最高伤害地形）的反弹表现与魔镜/法术反射的方向语义需要；当前只有 Fighter↔边界径向法线（白盒内联计算，未进事件/结果） |
| HitRegion | **C（阻塞）** | 弱点头部判定（×1.5/×1.65/×2）需要 Head/Torso 二分区；当前 hitNumber/victimState 无法表达「击中头部」 |
| ContactTick | ✅ 自然扩展 | EventId 已含 (Tick,Seq)；结算发生在权威 Tick |
| AttackSegmentId | ✅ 已有 | windowId+seg 即段标识 |
| HitPart（头/躯干/左右臂/左右腿/武器 六分身位） | **B（可自然扩展，且当前不需要）** | GDD 全文只有 头部/躯干 二分区证据（弱点判定、部位结算）；无六分身位玩法。HitRegion 用 enum{None,Head,Torso} 起步，enum 可扩展——**不过度设计** |

---

## 4. Tunneling / 穿模专项审计（问题 七）——**逐对实测判定**

白盒与生产设计的运动模型均为「终点位置判定」（`Position(t)` → `Position(t+1)` 只测 P1）：

| 碰撞对 | 最大速度/位移 | 对方厚度 | 判定 |
|---|---|---|---|
| Fighter → Terrain(结界边界) | knockback 4.0m→36m/s→**0.6m/Tick** | 边界=半无限外侧域（clamp 语义） | ✅ **无穿透**（clamp 不会越过），但反弹法线/接触点按终点计算，位置误差 ≤0.6m——**墙角连（最高伤害地形）的接触精度受损** |
| Fighter → Fighter | 同上 | 软推挤（允许重叠） | ✅ 无穿模问题（GDD §2.1） |
| Fighter(位移技) → 内部障碍 | 突进 8m：若 ≤8T → **1m/Tick** | 立柱直径 1.6m / 掩体厚 2.4m | ⚠️ 1m/Tick 对立柱**临界**（相位好时跳过一半），对掩体安全——**有穿透窗口** |
| Projectile → Fighter(躯干 0.6m 深) | **80m/s → 1.33m/Tick** | 0.6m | ❌ **确认穿透**：弹体一步越过躯干盒 |
| Projectile → Fighter(头部球 d=0.36m) | 1.33m/Tick | 0.36m | ❌ **确认穿透**（且头部判定=已批准玩法 §4.6） |
| Projectile → 内部障碍 | 1.33m/Tick | 立柱 1.6m（临界）/ 掩体 2.4m | ⚠️ 立柱临界穿透窗口 |
| Hitbox → Hurtbox | hitbox 静态于 active 帧采样 | — | ⚠️ 随体移动 hitbox（突进类）离散采样漏判 |
| Knockback/Dash/Launch → Terrain | 0.6m/Tick / 1m/Tick / 0.15m/Tick | 见上 | 汇总：仅边界无穿透；**内部障碍全部需要 sweep** |

**结论：tunneling 风险成立**——「只测 P1」的模型在当前真实数据下必然漏判（最坏：巴雷特弹穿越头部 Hurtbox）。这也是「当前设计只是一个确定性数值框架，还不是细粒度空间战斗框架」的直接证据。

---

## 5. Deterministic Sweep 设计（问题 八——SPEC 草案内容，不创建文件）

### SPEC-0005（草案）Deterministic Collision & Sweep Model

**5.1 统一运动约束模型（回答 §十四）**：所有运动实体收敛到**同一条积分/碰撞路径**——

```
IntegrateMove(mover):
    from = 当前位置（Fixed Q32.16，米）
    to   = from + vel（vel 已是 米/Tick，ADR-0001 §1.2）
    contacts = CollisionWorld.Sweep(mover.shape, from, to)     // §5.3
    按 §九排序逐个结算（terrain 反弹/推挤/停止）
    终态位置 = 接触修正后位置
适用：行走移动 / 翻滚位移 / 突进 Dash / Knockback / Launch 垂直 / Projectile / 技能墙——
     七种运动共用一套 IntegrateMove+Solver，差异只在 mover.shape 与响应策略（stop/bounce/push/destroy）
```

**5.2 支持的 Shape（与 hitbox 语法一一映射）**：

| ShapePrimitive | 来源语法 | 几何 |
|---|---|---|
| Point / Circle(r) | circle/aura/zone | 2.5D：水平圆 + 高度区间 [y, y+h] |
| AABB(w×d) | box（轴对齐部分）/ wall | 水平矩形 + 高度区间 |
| ConvexPoly(n≤8) | fan/cone（旋转盒/扇）| 预计算顶点（cos 阈值表，ADR-0001 §1.5）|
| Segment(length) | line | 线段扫掠 |
| Sphere3D(r) | 头部 Hurtbox r=0.18 / cyl | 高度区间内圆 |
| Capsule(from,to,r) | **Sweep 的中间产物** | 点/圆的扫掠体（stadium） |

**5.3 Sweep(shape, from, to) 的确定性算法（无浮点、无 Math.\*）**：

```
Sweep(mover: Circle(r) | Point, from: Vec2Fixed, to: Vec2Fixed, world) → ContactList
  对每个候选对象（Broad Phase，§十一）：
    圆 vs 圆（立柱/另一 Fighter）：整数二次判别
      |P0 + t·D − C|² = (r1+r2)²，t ∈ [0,1]
      → a=|D|², b=−2D·(C−P0), c=|C−P0|²−(r1+r2)²（全 int64）
      → 判别式 Δ=b²−4ac（int64，构造上不溢出：坐标≤2¹⁶ m → 二次项 ≤2⁴⁵）
    圆 vs AABB（掩体/墙）：把圆心到 AABB 的最近点距离转为「点到线段距离 ≤ r」的平方谓词
      closestX = clamp(P0x + t·Dx, minX, maxX) —— t 依赖最近点，采用 §5.4 谓词法
    Point vs Sphere（头部 Hurtbox）：同圆 vs 圆
  Time Of Impact：**单调二分搜索**——谓词 f(t)=「swept 前缀已接触」对 t∈[0,1] 单调（凸体平移性质）
      迭代 K=16 次（分辨率 1/65536 Tick），每步只做 int64 平方距离比较——**全程无 sqrt、无除法歧义**，
      K 固定 ⇒ 跨平台逐位一致
  Contact Point：TOI 后 mover 中心位置 + 沿运动方向到对方表面最近点（整数 clamp/投影）
  Contact Normal：整数向量 (dx,dy,dy)（由最近点指向 mover 中心），
      归一化 = 各分量 DivRoundHalfEven(整数平方根)，整数平方根用**定点 Newton 固定 24 迭代**
      （int64 纯整数运算，逐位确定——列入 ADR-0001 §1.5 白名单增补）
  多接触：全部收集后按 §九 排序统一结算
```

**5.4 快速路径（性能）**：80m/s×1/60=1.33m/Tick 远小于场景尺度——每 Tick 每实体候选 ≤8（Broad Phase 网格），Sweep 谓词 ≤10 次 × int64 运算 ≈ 数百周期，60 Tick/s×10 实体 <0.5% 帧预算。二分 16 次仅用于「发生接触」的对象。

**5.5 高速通道特例**：Projectile（弹速 ≥45m/s）采用**解析 TOI**（圆 vs 点二次方程，整数判别式 + Newton sqrt）代替二分——一次求解；解析不可用形状（fan/box 旋转体）退化为 K=16 二分。两条路径都在同一 SPEC 固化，测试对齐（同一接触集）。

**5.6 旋转 Hitbox / 阶段变化 hitbox（三段斩三段不同判定）**：SkillDef 的 hitSchedule 升维为 `segmentShapes[]`（每段独立 shape/offset）——数据模型扩展（ADR-0002 hitbox 语法增补 `<seg>:<shape>` 分段语法），属 **Schema Issue 登记项**，不阻塞基础 Sweep。

**5.7 Swept Hitbox（攻击轨迹）**：随体移动 hitbox（突进类）= mover 的 Capsule 扫掠 vs 目标 Hurtbox——同一 Sweep 谓词（点/圆 vs 线段距离）；静态 hitbox 保持 active 窗口采样（每 Tick 一次接触测试，因 hitbox 不移动无 tunneling）。

---

## 6. Collision / HitResolve 职责边界（问题 十——基于仓库判断，未默认接受建议结构）

**当前问题证实**：白盒把「移动积分+边界反弹」写在 Fighter._move、「命中相交」写在 fighter tick 的 hit 检查——两处各自为政；architecture.md 的 Sim.Terrain「地形查询+反弹裁决」与 Sim.HitResolve「判定体解析/相交」存在职责交叠（弹体 vs 地形走谁？未定义）。

**裁定（新增 1 模块 + 收窄 2 模块）**：

| 模块 | Owns | Exposes | Consumes | 变化 |
|---|---|---|---|---|
| **Sim.Collision（新增）** | 形状库（ShapeLibrary：8 类 ShapePrimitive 的谓词/Sweep 实现）、Broad Phase（均匀网格 8m cell，实体 Id 有序）、SweepSolver（TOI/接触点/法线/排序） | `Sweep(shape, from, to) → ContactList`、`Overlap(a, b) → bool`、`ResolveContacts(list) → 终态位置+事件草案` | 世界实体注册表 | **新模块**——所有空间相交的**唯一实现点**（Client 预测/服务器/回放同源） |
| Sim.Terrain（收窄） | 地形**数据**（ArenaDef 解析：边界/掩体/立柱语义）+ 地形静态查询（高度/阻挡/坠落判定） | `QueryTerrain(shape, from, to)`（转调 Collision 的静态候选）/ 高度采样 | Collision | 收窄：反弹**裁决规则**留下（×0.6/硬直+10f），几何计算全部交 Collision |
| Sim.HitResolve（收窄） | 命中裁决（区段/次数/伤害公式/部位修正） | ResolveResult | **CollisionResult**（新增输入：HitPoint/HitNormal/HitRegion） | 收窄：不再自行做相交测试，消费 Collision 的接触结果 |
| Sim.Projectiles（不变） | 弹道推进/追踪/拦截 | 结算事件 | 每 Tick 调 Collision.Sweep（弹体位移） | 弹→地形/弹→Fighter 统一走 Sweep |
| Sim.Fighter / SkillRuntime | 不变 | — | 移动统一走 IntegrateMove（SPEC-0005 §5.1） | 白盒的 _move 内联反弹逻辑迁入 Collision |

数据流（证实审计建议结构**方向正确但需修正一处**）：`SkillRuntime → Hitbox/Projectile → Collision(Sweep) → CollisionResult → HitResolve → Gates → Events`——修正点：**地形反弹不是 HitResolve 的职责**（GDD §5.8 反弹是运动约束），故 Collision 输出分两路：`TerrainContact → IntegrateMove 终态`；`CombatContact → HitResolve`。

---

## 7. 多碰撞确定性排序（问题 十二）

全部规则固化进 SPEC-0005（此处为裁定结论）：

1. **碰撞优先级层**（层间硬序）：`L1 地形约束（边界/掩体/立柱——位置合法性） → L2 单位软推挤（GDD §2.1 允许重叠，推挤只做分离不做阻挡） → L3 战斗接触（Hitbox×Hurtbox 命中）`
2. **层内 Tie-Break**：`(TOI 升序, 实体 Id 升序, 接触类型枚举序)`——TOI 来自 §5.3 二分（同 TOI 视为相等，落到 Id 序）
3. **Resolution Order**：L1 先行（位置修正后的状态作为 L2/L3 输入）→ L2 推挤对称分离（成对按 (smallId,bigId) 序）→ L3 逐命中结算（伤害结算唯一写点=HitResolve，SemanticKey 幂等）
4. **同 Tick 多次地形接触**（墙角夹击）：L1 内至多迭代 2 轮（第 1 轮修正位置，第 2 轮复检新接触；仍冲突取最先 TOI 接触面）——迭代上限固定，确定
5. **同一 Hitbox 同时命中多个 Hurtbox**：全数成立（GDD 多目标技合法），排序按 victimId 升序逐个结算，`hitNumber` 按 victim 各自连段独立计数（victim 域计数器，非全局）
6. **一个 Projectile 同 Tick 触两 Fighter**：TOI 序结算，第 1 次命中后按投射物「穿透/消亡」属性（拦截/穿透列）决定继续——属性在 SkillDef，无实现期自由度

---

## 8. Knockback / Dash / Projectile / Terrain 统一模型验证（问题 十四）

| 运动 | 响应策略 | mover.shape | 走 IntegrateMove？ |
|---|---|---|---|
| 行走/奔跑 | L2 推挤 + 边界 | Circle(0.45) | ✅ |
| 翻滚/受身位移 | 同上 | Circle | ✅ |
| 突进 Dash（蓄力突进 8m 等） | 同上 + 自带 hitbox 随体（§3.1 swept） | Circle(+attack hitbox) | ✅ |
| Knockback | 边界 bounce ×0.6（L1） | Circle | ✅ |
| Launch 垂直 | 高度域 [0,∞) 重力积分（无水平障碍交互） | Point+height | ✅（水平分量走 L1/L2） |
| Projectile | 障碍阻挡（命中即毁）/穿透（【穿透】标签） | Point+height | ✅ |
| 击飞撞墙反弹 | bounce ×0.6 + 硬直 +10f（§5.8） | Circle | ✅ |
| 技能墙（冰墙/掩体同规则） | 静态 AABB | — | ✅ |

**结论：可以统一，且必须统一**——差异全部收敛为「mover.shape + 响应策略枚举」，消除多套碰撞逻辑。

---

## 9. Network / Rollback / Replay 一致性（问题 十六）

- Collision 全部实现于 **Arena.Core**（零 Godot/零 IO）→ Client 预测副本、服务器权威、Replay 重演调用**同一份代码**——「三套碰撞算法」结构性不可能
- Sweep 输入 = 快照状态（确定）+ 命令流（确定）+ Catalog（dataVersionHash 绑定）⇒ **Same Spatial Results** 成立（ADR-0001 §9 的空间面）
- 回溯判定（ADR-0006）用历史快照的**判定体**输入同一 Sweep——接触结果逐位可复算（T37 已覆盖，补充空间面断言）
- 表现层只读 Snapshot/事件（P3）——Godot 动画/Ragdoll/粒子不回写 Core 位置/命中 ✅ 当前设计已满足

---

## 10. 能力矩阵（问题 十七）

| 能力 | 当前支持 | 架构可支持 | 存在缺口 | 是否阻塞 Core |
|---|---|---|---|---|
| 精确攻击位置 | ⚠️ 形状有/位置锚定隐式 | ✅ | hitbox 无 anchor/offset 字段 | 否（数据语法增补） |
| HitPoint | ❌ | ✅ | HitEvent/ResolveResult 无字段 | **是（事件载荷+结果结构）** |
| HitNormal | ❌（仅白盒内联边界法线） | ✅ | 同上 | **是** |
| 身体区域(Head/Torso) | ❌（GDD 已要求，架构未承接） | ✅ | HitRegion 缺失 + Hurtbox 未进 Catalog | **是** |
| Swept Hitbox | ❌ | ✅ | 无 swept 概念 | **是（碰撞子系统）** |
| Projectile Sweep | ❌（终点采样，80m/s 实证穿透） | ✅ | 同上 | **是** |
| 高速移动防穿透 | ❌ | ✅ | 同上 | **是** |
| Wall Collision(边界) | ✅（clamp+反弹，精度受限） | ✅ | 接触精度 ≤0.6m | 否（SPEC 提升精度） |
| 内部障碍碰撞(掩体/立柱) | ❌（白盒无；生产未定义归属） | ✅ | Terrain/HitResolve 职责交叠 | **是** |
| Knockback Collision | ⚠️ 终点 clamp | ✅ | 无 sweep | 是（统一模型） |
| Bounce | ✅ 白盒实证 | ✅ | 法线精度 | 否 |
| Fighter Collision | ✅（软推挤语义，GDD §2.1） | ✅ | 推挤确定性实现未定义 | 否（SPEC 覆盖） |
| 多目标命中 | ✅（白盒/事件序） | ✅ | 排序规则待 SPEC 固化 | 否 |
| 多碰撞排序 | ❌ | ✅ | 未定义 | **是（SPEC 固化）** |
| Terrain Constraint | ⚠️（arena.csv 有数据、模块职责交叠） | ✅ | 收窄定义 | 否（SPEC 固化） |
| Rollback | ✅（事件/快照面） | ✅ | 空间面需随 SPEC 一致 | 否 |
| Replay | ✅ | ✅ | 同上 | 否 |
| Server Authority | ✅ | ✅ | 同上 | 否 |

---

## 11. 当前架构缺口汇总

1. **GAP-C1**：Hurtbox 模型（GDD §2.1 已有数值）未进入 Catalog/模块——需要进入 RuntimeDef（ClassDef 或常量表）
2. **GAP-C2**：Sweep/TOI/接触点/法线——无任何定义（本审计 §5 给出草案）
3. **GAP-C3**：Hit 事件空间结果字段（HitPoint/HitNormal/HitRegion）缺失
4. **GAP-C4**：多碰撞排序/优先级未定义
5. **GAP-C5**：Collision 模块未建立；Terrain/HitResolve 职责交叠
6. **GAP-C6**：分段 hitbox（segmentShapes）——数据语法缺口（§14.1.5 三段斩三判定等），Schema Issue 级
7. **GAP-C7**：AD-0001 数学白名单需增补「整数 Newton 平方根（固定迭代）」

## 12. SPEC 建议（草案要点已在 §5；正式文件待批准后创建）

| SPEC | 内容 | 对应缺口 |
|---|---|---|
| **SPEC-0005 Deterministic Collision & Sweep Model** | §5 全部内容：ShapePrimitive/Sweep/TOI 二分+解析/接触点法线/统一 IntegrateMove/排序 | C2/C4/C5/C7 |
| **SPEC-0006 Hit Spatial Result & Hurtbox Model** | Hurtbox 进 Catalog（躯干盒/头部球）+ ResolveResult/HitEvent 增补字段（§5 判定）+ 部位修正路由 | C1/C3 |

## 13. ADR 影响分析

| ADR | 影响 | 形式 |
|---|---|---|
| ADR-0001 | §1.5 白名单增补「整数 Newton 平方根（固定迭代，逐位确定）」；§8.2 Snapshot 增补 ContactList（Tick 内瞬态，可不持久化——接触由重演重算，**不需入快照**，仅说明） | **Errata**（不新建 ADR） |
| ADR-0003 | Hit 事件载荷增补 `hitRegion/hitPointRaw/hitNormalRaw`——EVENT_PROTOCOL_VERSION bump（既有机制） | **Errata** |
| ADR-0002 | hitbox 语法增补分段 `segmentShapes`（GAP-C6）+ Hurtbox 常量表进 Catalog | **Errata** |
| ADR-0004/0006/0008/0009/0010/0005 | 无影响（空间结果经事件/快照既有通道流动） | 无 |
| architecture.md | Module Ownership 增 Sim.Collision 行（文档同步项 **D-3** 登记，与 D-1/D-2 同批） | 文档同步 |

**不需要新增 ADR**：空间碰撞的全部决策都在「Arena.Core 纯 C# + 定点整数 + 确定性 sweep」的既有框架内，属实现规格层。

## 14. Implementation Readiness

- Phase 1（Core 数值基座）**不被阻塞**——Fixed/系数表照常
- Phase 3 的碰撞子系统**被阻塞**：SPEC-0005/0006 批准 + ADR-0001/0003 Errata 落地后方可实现
- 白盒现状（终点采样）**不可迁移**——Core 碰撞必须按 SPEC-0005 重写（白盒仅作行为对照）

## 15. Final Gate Decision

```
B — 当前架构方向正确，必须补充若干 SPEC 后再进入 Core 的空间碰撞子系统
```

- 确定性框架（ADR-0001）与事件/回放/网络协议完全兼容空间扩展——**不需要修改任何 ADR 主文**（Errata 级即可）
- 空间碰撞子系统 **CORE BLOCKED: YES**（SPEC-0005/0006 未批准前不得实现 Collision/HitResolve 空间路径）；Fixed/系数表/数据管线/事件协议**不阻塞**

## 16. OQ 关联（不裁定）

OQ-2/4/5/6/7/8/9/11 与 F1 二轮：**均不影响空间碰撞架构**（纯数据语义/设计裁定）；OQ-6 几率数值补录与 OQ-9 归属在对应技能实现阶段裁定即可。F1 二轮若选空中快刺形态，其 hitbox 为普通数据行，不改变本审计结论。

---

## 附：自审（20 项）

✅ 检查了最新 HEAD（cb6ee38）｜✅ 未引用旧报告替代当前仓库（全部实测/实读）｜✅ 验证 Hitbox 实际定义（17 kind 实测）｜✅ 验证 Terrain 实际定义（白盒+arena.csv+模块归属）｜✅ 验证 Projectile（弹速实测 top5）｜✅ 验证 Knockback（4.0m→36m/s→0.6m/Tick）｜✅ 验证 Dash（突进 8m/1m/Tick）｜✅ 高速 tunneling 实证（1.33m vs 0.36m/0.6m）｜✅ 定义 Sweep（二分 TOI+解析特例）｜✅ 定义 Contact Point｜✅ 定义 Contact Normal（整数 Newton）｜✅ 定义碰撞排序（层/TOI/ID 三级）｜✅ 未违反 ADR-0001（无 Math.\*，整数 Newton 入白名单需 Errata）｜✅ 未引入隐式 Float（浮点仅客户端表现层 atan2 瞬时量化）｜✅ Rollback/Replay 一致（同 Core 代码）｜✅ Server/Client 一致（同碰撞实现）｜✅ 未裁定任何 OQ｜✅ 未修改游戏数据｜✅ 未把 Godot Physics 当权威｜✅ Core 开工条件明确（SPEC-0005/0006 批准前碰撞子系统冻结）

*全程只读：未创建 SPEC 文件、未修改任何文档/数据/代码。*
