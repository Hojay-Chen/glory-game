# SPEC-0005 — Deterministic Collision & Sweep Model（确定性碰撞与扫掠模型）

| 项 | 值 |
|---|---|
| 编号 | SPEC-0005 |
| 状态 | **Accepted**（2026-09-01） |
| 性质 | ADR-0001 / ADR-0009 的 Implementation Specification Appendix（combat-granularity-collision-audit-v1 §12 建议，用户批准后创建） |
| 上游 | ADR-0001（Determinism Contract/Q32.16/整数白名单/IntegrateMove 语义）、ADR-0009（ISimDriver/场景）、SPEC-0004（ArenaDef 事实源）、combat-granularity-collision-audit-v1（tunneling 实证） |
| 决策层 | Arena.Core（Sim.Collision 新模块） |

---

## 1. CollisionSystem 唯一职责边界

**Sim.Collision 是全工程所有空间相交判定的唯一实现点**——Client 预测副本、服务器权威、Replay 重演调用同一份代码（结构性排除「三套碰撞算法」）。

| Owns | Exposes | Consumes |
|---|---|---|
| ShapeLibrary（8 类 ShapePrimitive 的谓词/解析 Sweep 实现） | `Sweep(shape, from, to, layerMask) → ContactList` | 世界实体注册表（Fighter/Unit/Deploy/静态地形，Id 有序） |
| BroadPhase（均匀网格 8m cell；实体按 Id 有序入格） | `Overlap(a, b) → bool`（静态相交，供推挤/部署校验） | ArenaDef（静态障碍）+ Runtime Catalog（形状参数） |
| SweepSolver（TOI/接触点/法线/排序） | `IntegrateMove(mover, vel) → MoveResult{终态位置, contacts}` | Sim 的 per-Tick 速度（Compiler 预量化） |

**职责收窄声明**（audit v1 §6 裁定）：Sim.Terrain 收窄为「地形数据 + 高度/阻挡/坠落规则」（几何计算全部移交）；Sim.HitResolve 收窄为「命中裁决（区段/次数/伤害/部位修正）」——不再自行做相交测试，输入为 `CollisionResult`。

## 2. 统一运动路径（IntegrateMove）

**七种运动全部经过同一路径，禁止旁路**：

```
IntegrateMove(mover):                                     // 每 Tick 对每个运动实体执行一次
  from = mover.pos（Fixed Q32.16，米）
  to   = from + mover.velTick                              // velTick=Compiler 预量化的 米/Tick（ADR-0001 §1.2）
  contacts = Sweep(mover.shape, from, to, layerMask)       // 本 SPEC §5/§6
  result = ResolveContacts(contacts, mover.responsePolicy) // §8 排序与响应
  mover.pos = result.终态位置
```

| 运动 | mover.shape | responsePolicy |
|---|---|---|
| Fighter 行走/疾跑 | Circle(r=0.45) | Push+Stop |
| Roll/受身位移 | Circle | Push+Stop |
| Dash 突进（蓄力突进 8m 等） | Circle（+随体 Hitbox，SPEC-0006） | Push+Stop |
| Knockback | Circle | Bounce（×0.6，GDD §5.8）+Push |
| Launch（垂直分量） | Point+height（水平分量走上两行） | GroundStop |
| Projectile | Point+height（弹体半径并入命中谓词） | HitDestroy / Pierce（【穿透】标签）/ HitBounce（魔镜类） |
| Skill Wall / Deployable | 静态（不积分；作为障碍参与他者 Sweep） | — |

## 3. Intra-Tick 线性运动公理（本 SPEC 的基 axiom）

> **速度在 Tick 内恒定，Tick 边界更新。** 所有实体（含抛物线 lob——重力在 Tick 边界以量化增量作用于 vel）在单个 Tick 内的相对运动 = 常向量平移。Hitbox 的位置/朝向按 **Tick 粒度**离散（Tick 内不旋转、不渐变）——扇形/圆锥的朝向在 active 窗口内逐 Tick 采样。

推论：
1. 任意一对实体在 Tick 内的**相对运动恒为线性** ⇒ 全部 Sweep 归约为「线性扫掠 vs 静态膨胀障碍」——**解析可解，无逐类二分**
2. 旋转 Hitbox 的连续扫掠（Hitbox(t0)→Hitbox(t1) 旋转体扫掠）**被公理排除**——旋转是 Tick 离散的（GDD 帧量化战斗语义的自然延伸）；攻击轨迹的连续性由「随体 hitbox 的线性平移扫掠」+「多段 hitSchedule」表达
3. 该公理是数据/规则层裁定（帧量化战斗的自然结果），**不改变 60 Tick 架构**，也不把设计帧绑定 Tick（SPEC-0005 §12）

## 4. ShapeLibrary（ShapePrimitive → 语法映射）

| Primitive | 来源 hitbox/Hurtbox | 几何定义 |
|---|---|---|
| Point | proj 弹体（半径并入目标谓词） | 2.5D 点 + 高度 |
| Circle(r) | circle/aura/zone/Fighter 体 | 水平圆 + 高度带 |
| AABB(halfW, halfD) | box/wall/掩体/躯干（水平投影） | 水平轴对齐矩形 + 高度带 |
| ConvexPoly(≤8 顶点) | fan/cone（扇形/圆锥水平投影） | 顶点由 cos 阈值表预计算（ADR-0001 §1.5）|
| Sphere(r) | 头部 Hurtbox | 球心高度 + 水平圆 |
| Segment(len) | line | 线段（薄体） |

约束：ConvexPoly 顶点扇角 ≤180°（凸性保证——CSV 现值 a90–a160 ✓；>180° = Schema Failure）。全部坐标/半径 Q32.16。

## 5. SweepSolver——逐类可解性证明（核心章节）

**总原则（响应用户禁令）**：不采用「统一 K=16 二分」万能近似。在 §3 公理下**全部碰撞对都可解析求解**（线性相对运动 + 凸障碍 = 区间裁剪）；二分仅作为**测试对照 oracle**（K≥32，见 §5.5 误差分析），不进入生产路径。

### 5.1 逐类求解表

| 碰撞对（mover → 障碍） | 求解方法 | 单调性证明 | 解析性 |
|---|---|---|---|
| Point → Circle（弹→Fighter/立柱/单位；弹头半径并入障碍 R=R_h+R_p） | 二次方程：`\|P0+tD−C\|² = R²`，系数 a=\|D\|², b=−2D·(C−P0), c=\|C−P0\|²−R² 全 int64；Δ=b²−4ac（坐标≤2¹⁶ → Δ≤2⁴⁵ 不会溢出）；Δ<0 无接触；t=(−b−√Δ)/2a 取 RHE 量化 | f(t)=a·t²+bt+c 为凸二次（a>0），f≤0 区间 [t_in,t_out] 连续 ⇒ 「扫掠前缀已接触」对 t 单调 | **解析**（ISqrt，§5.4） |
| Point → Sphere（弹→头部 r=0.18，含高度带） | 水平同上；附加高度带谓词：接触点高度 ∈ [Cy−Rh, Cy+Rh] 在 TOI 处验证 | 同上（高度带为附加过滤，不破坏单调） | **解析** |
| Circle(r) → Circle(r2)（Fighter↔立柱/推挤） | Minkowski：mover 中心视为点，障碍半径 R=r1+r2 → 同第一行 | 同上 | **解析** |
| Point/Circle → AABB（弹/Fighter→掩体/技能墙） | Minkowski 膨胀 = 圆角矩形 = **4 个半平面（线性）+ 4 个角圆（二次）**：维护接触区间 [tLo,tHi]——对每条边半平面做线性区间裁剪（t 使中心在半平面内侧）；对每个角圆做二次区间裁剪（同第一行）；裁剪后区间非空 ⇒ 接触，tLo=TOI | 每个半平面/角圆的「在内部」集合都是 t 的区间（凸）；区间交仍为区间 ⇒ 「前缀接触」单调 | **解析**（线性裁剪+ISqrt） |
| Point/Circle → ConvexPoly（fan/cone 旋转体；mover 点） | n 条边半平面裁剪（线性）+ n 个顶点圆（二次）——同上通用化（凸多边形 Minkowski） | 凸 ∩ 线性扫掠 ⇒ 区间 | **解析** |
| 任意 → Segment(line) | mover 中心到线段的最近点距离 ≤ r+R 的平方谓词（int64，无 sqrt——比较平方值）；TOI 二次（同第一行，障碍退化为最近点轨迹的分段线性——**分段求解**：线段端点把 t 轴分为 ≤3 段，每段最近点固定为端点或投影点，各段解析） | 分段后每段内谓词为凸二次 | **分段解析**（≤3 段） |
| Lob 抛物线弹 | §3 公理：Tick 内线性 → 每 Tick 用线性求解器；跨 Tick 抛物线由 vel 逐 Tick 递减自然形成 | 每 Tick 内同上 | **逐 Tick 解析** |

### 5.2 K 值误差分析（响应用户质询——为什么二分不能当万能）

若对某类碰撞使用二分（K 次迭代，t 分辨率 1/2^K）：

```
空间误差 = |D| × 2⁻ᴷ
最坏 |D| = 巴雷特 1.333 m/Tick：
  K=16 → 1.333/65536 ≈ 2.04×10⁻⁵ m = **1.33 个 Fixed 量子**（Q32.16 分辨率 1.53×10⁻⁵ m）
  → HitPoint 偏差 >1 量子 ⇒ 接触点落在 Head/Torso 边界 1 量子内时可**翻转 HitRegion ⇒ 翻转 Damage（×1.5/×2）**
  K=32 → 3.1×10⁻¹⁰ m ≪ 1 量子 → 安全
```

**裁定**：
1. 生产路径**全解析**（§5.1），TOI/HitPoint 在 Fixed 精度内精确——不存在误差影响 HitRegion/Damage 的问题
2. 二分仅作为 **T54 系列的对照 oracle**（K=32），验证解析实现正确性
3. 未来若引入非凸/旋转连续体（当前公理排除），必须先扩展本 SPEC 的可解性表，禁止先写二分实现

### 5.3 ISqrt / 法线（ADR-0001 Errata 承接）

```
ISqrt(n)：n < 2⁶² 的整数平方根（floor）——Newton 迭代固定至收敛稳定（≤33 次必有整数不动点，迭代数固定为 33 取末值）
  ⇒ int64 纯整数运算，逐位跨平台一致
FSqrtFixed(x)：y = ISqrt(x.Raw × ONE)；RHE 修正：若 (y+1)²−N < N−y² 则 y+=1；若相等且 y 为偶数则 y+=1（RoundHalfEven）
Contact Normal：n_raw=(dx,dz)（最近点→mover 中心，int64）→ len=ISqrt(dx²+dz²) →
  normal = (DivRoundHalfEven(dx,len), DivRoundHalfEven(dz,len))——各分量 Q32.16 比率
```

**零向量法线**（同心/正碰 dx=dz=0）：法线取运动反方向 −D 归一化（构造确定）。

### 5.4 接触点定义

- Point mover：`HitPoint = P0 + toiFixed × D`（MulShift，弹体中心——弹体半径已并入障碍 R）
- Circle mover：`HitPoint = TOI 位置 + normal × r`（表面投影）
- 高度分量：接触点 y = mover 当前高度（垂直运动在高度带谓词内验证，见 §5.1 第二行）

### 5.5 TOI 精度与 Fixed 对齐

解析 t = (−b−√Δ)/2a：√Δ 经 FSqrtFixed（Q32.16），除法 DivRoundHalfEven ⇒ **toiFixed 精确到 Q32.16（1/65536 Tick）**，与 Fixed 空间分辨率匹配——解析路径无「误差是否影响 HitRegion」问题（对照 §5.2 的二分缺陷）。

## 6. 多碰撞排序（确定性总序）

```
ContactList 排序键（全 int64/枚举，Ordinal 可比）：
  (toiFixed 升序, layerRank 升序, attackerId/ownerId 升序, defenderId 升序,
   hitboxId 升序, hurtboxId 升序, collisionKind 枚举序)
layerRank：TerrainConstraint=0 < SoftPush=1 < CombatContact=2
```

### 6.1 Resolution Order（同 Tick）

```
ResolveContacts(contacts, policy):
  L1 TerrainConstraint（边界/掩体/立柱）：按序结算位置修正（stop/bounce）
     → 若产生新接触（墙角双面）：重复一轮（**迭代上限 2**，固定）；仍冲突取最先 toiFixed 面
  L2 SoftPush（Fighter↔Fighter，GDD §2.1 允许重叠）：成对对称分离，对 (minId,maxId) 序逐对处理
  L3 CombatContact（Hitbox×Hurtbox / Projectile×Fighter）：按序产出 CollisionResult → HitResolve
  每层结算结果作为下层的输入状态（层间硬序）
```

### 6.2 典型场景裁定

| 场景 | 规则 |
|---|---|
| Fighter 同 Tick 撞墙 + 撞人 | L1 先修正位置，L2 后推挤（对已修正位置）——层间硬序，无自由度 |
| 一个 Projectile 同 Tick 触两个 Fighter | TOI 序逐个结算；第 1 次命中后按 SkillDef 的穿透/消亡属性决定是否继续 |
| 一个 Hitbox 同 Tick 命中多个 Hurtbox | 全数成立（多目标技合法），victimId 升序；`hitNumber` 按 victim 各自连段独立 |
| Fighter 同 Tick 连续多次地形接触（墙角） | §6.1 L1 迭代 ≤2 轮 |
| 同一对实体同 Tick 重复接触 | 一次结算（SemanticKey/实体对幂等，ADR-0003） |

### 6.3 Tick 内多次碰撞与剩余运动（响应用户「一个 Tick 可存在 Sweep→TOI→多碰撞→反弹→剩余运动」）

```
 bounce 场景：TOI 处反弹后，剩余 (1−toiFixed) × |vel| 沿反射方向**重积分一次**
   （反射后新 Sweep 只考虑剩余位移；再接触 = 第二次反弹 → 本 Tick 不再处理，余量留到下 Tick——
     「每 Tick 至多一次反弹」为固定规则，角落双面由 §6.1 迭代 ≤2 轮覆盖）
 确定性保持：全部步骤（TOI/反射/重积分/迭代数）固定且由状态唯一决定——结果仍为 Tick 的纯函数
```

## 7. 碰撞归属矩阵（Layer × Layer）

```
                Terrain   Fighter    Projectile  Hitbox    Deploy
Terrain           —       Push(§2)   Destroy     None      Block
Fighter          Push      SoftPush   Hit(被击)   Hit(被击)  Block
Projectile      Destroy   Hit(判定)     —         None      Block
Hitbox           None     Hit(判定)     —          —         —
Deploy           Block     Block        —          —         —
```

- Fighter×Fighter = 软推挤（GDD §2.1：允许重叠、无实体阻挡）——非阻挡、非战斗
- Projectile×Terrain = Destroy（【穿透】标签则 Continue——SkillDef 属性）
- Hitbox×Terrain = None（v1 裁定：近战 hitbox 不被地形遮挡；GDD §19.5 仅投射物/移动被阻挡）——记录为明确裁定
- Hitbox×Hitbox / Projectile×Projectile = None（攻击不互相碰撞；拦截走 SkillDef 拦截列的 Special 规则）

## 8. 高速防穿透验证（巴雷特案例，SPEC-0006 §7 端到端）

解析 TOI 的数学保证：线性扫掠下「弹体与目标接触」的 t 集合是闭区间 [t_in, t_out]，**无论区间多么窄**（1.33m/Tick 步长 vs 0.36m 直径头部——区间长度可低至 0.27 Tick 甚至更短），解析求根直接命中区间端点，不存在「步长跳过」。量化误差 ≤1 Fixed 量子（1.5×10⁻⁵ m）≪ 头部半径 0.18m。**验证用例清单见 SPEC-0006 §7。**

## 9. 与 Rollback / Replay / Server Authority 对齐

- Sweep/Resolve 全部实现于 Arena.Core（纯 C#、零 Godot）→ 三宿主同源
- 输入 = 快照状态（确定）+ per-Tick 速度（Compiler 量化）+ ArenaDef/Catalog（hash 绑定）⇒ 同契约 ⇒ 同空间结果（ADR-0001 §9 空间面）
- ContactList 为 **Tick 内瞬态**（结算完成即弃），**不入 Snapshot**——重演重算（与 ADR-0001 §8.2 一致；对比：位置终态入快照）

## 10. 测试挂钩（并入 T 体系）

T54–T62 定义于 SPEC-0006 §7（巴雷特端到端 + 穿透/边界/近失/多目标/墙角矩阵）——碰撞算法与其共享谓词级用例（T54a：ISqrt 边界；T54b：二次判别 Δ=0 恰切）。


---

## Preflight Amendments（2026-09-01，collision-preflight-audit-v1）

### PA-1 数学性质表述修正（§5.1 全表「单调性证明」列的正确读法）

v1 表述「『扫掠前缀已接触』对 t 单调」**作为生产依据不够准确**，修正为如下三段精确性质（生产实现只依赖性质 P1/P2）：

- **P1（区间性）**：Intra-Tick 公理（§3 线性相对运动）+ 凸图元（§4 全部 Primitive 凸，ConvexPoly 扇角 ≤180° 强制）下，每条约束的「接触集」S_i = {t∈[0,1] : overlap_i(t)} 是**闭区间**（半平面→线性不等式解；角圆/圆→二次不等式解；线段→分段二次、各段闭区间取并——线段最近点轨迹分段但「前缀扫掠接触」仍为区间：线段障碍是凸的，Minkowski 差凸 ⇒ 接触集区间）。**凸性是前提**：非凸/旋转连续体进入 ShapeLibrary 前必须先过本条证明。
- **P2（可交性）**：S = ∩S_i 仍为闭区间（区间交封闭）——TOI = min S（S ≠ ∅ 时），**解析求出**（线性裁剪+二次求根），这就是生产实现——不依赖任何单调性论证。
- **P3（推论，仅供 oracle）**：前缀谓词 t ↦ (S∩[0,t] ≠ ∅) 非降——它是 P1 的推论，**只**被测试 oracle 的二分使用；生产代码禁止以「谓词单调」为理由实现二分。

### PA-2 [0,1] 裁剪、起点重叠、相切语义（§5.1 缺失的三条边界规则）

1. **[0,1] 裁剪**：解析区间先裁剪到 [0,1]；TOI = max(t_in, 0)。
2. **起点重叠（t_in ≤ 0）**：S 含 0 ⇒ 实体在 Tick 起点已接触 ⇒ **TOI=0，立即结算**（Hitbox 激活即与既有 Hurtbox 重叠、出生即重叠、被推入重叠均为合法接触——**spawn-overlap 不是错误**，消灭「出生在体内是否命中」的实现歧义）。
3. **相切（退化区间 t_in = t_out）**：闭区间语义 ⇒ **相切=接触**（≤ 判定，与 T54d 一致）；区间为空才无接触。
4. **接触结束早于 0**（t_out < 0）：无接触（扫掠发生在本 Tick 之前——不追溯）。

### PA-3 TOI 量化与「同一离散时刻」语义（§5.5/§6 补充）

1. toiFixed = RHE(t_in × ONE) ∈ [0, ONE]——比较/排序/幂等键**一律使用量化后的 toiFixed**，禁止用未量化解析值做任何决策
2. **两个不同真实 TOI 量化到同一 toiFixed** ⇒ 视为**同一离散时刻发生**（Tick 内时间分辨率即 Q32.16）——按排序键后续字段（layerRank/Id/kind）定序，语义上等价于「同时发生，按稳定序处理」；无实现者裁量
3. 同一 (mover, obstacle) 对在单 Tick 内至多一个接触区间 ⇒ 至多一个 toiFixed ⇒ 键内无自碰撞
4. toiFixed=0 与 toiFixed=ONE 的特殊语义：0=Tick 起点已接触（含起点重叠）；ONE=Tick 终点恰接触（下一 Tick 状态由该接触定）

### PA-4 排序键充分性与 Push 键（§6 补充）

1. **键充分性证明**：ContactList 内任意两个不同 ContactResult 的排序键必不同——同一 (mover, obstacle) 对单 Tick 单区间 ⇒ 单 TOI；同一 obstacle 不可能以两个身份出现（object_id 唯一）；键全字段相等 ⇒ 按 §2.2「同一对单 Tick 单区间」构造上必为同一事件 ⇒ **键相等即去重**（显式条款，非「实现时决定」）
2. **L2 Push 排序键**：Push 不产生 TOI（Tick 末重叠分离）——键 = (layerRank=1, minId, maxId)（成对无向键，升序规范化），对序即处理序
3. **L3 跨 mover 合并序**：多攻击者 Hitbox 同 Tick 命中同一 victim——键含 attackerId/hitboxId 已可全序；victim 侧同一 Hitbox 同 Tick 命中多 victim 由 victimId 升序

### PA-5 墙角迭代充分性证明与通用上限（§6.1 修正）

1. **ARENA001 几何证明（2 次迭代充分）**：mover 直径 0.9m；同 Tick 可同时接触的两个静态阻挡体中心距必须 ≤ 0.9+两体半径之和。实测 ARENA001 阻挡体两两间距全部 ≥8m（边界-掩体 8m/掩体-立柱 >20m/立柱-立柱 36m/掩体-木箱 >20m）——**唯二可同时接触组合 = 边界两邻边（墙角）+ 边界+单障碍**，均 ≤2 约束 ⇒ 2 次迭代对 ARENA001 v1 **数学充分**
2. **通用上限改 4 + 确定性降级**：未来 ArenaDef 扩展无法静态证明时，迭代上限统一为 **4**；超限 ⇒ snap 至排序键最首接触面、清零法向速度分量、Telemetry 记录——**确定性降级，无实现期裁量**
3. **Bounce 与角落交互明确化**：反弹（每 Tick ≤1 次）作用于第一 TOI 面；同 Tick 第二约束面做 **L1 clamp（无第二次反弹）**，剩余运动归下 Tick——两机制（bounce/iteration）职责分离，无歧义

### PA-6 BroadPhase 等价性条款（§新增，回应「性能层不得成为第二事实源」）

1. **保守性定义**：候选集必须 ⊇ 真接触集——实体按其 **swept-AABB**（from/to 端点与半径的并集包围盒）入格，格覆盖取 [minCell, maxCell] 闭区间（含端点）；格索引 = floor(DivRoundHalfEven(coord, 8m))——边界点归属单格（floor 语义），由 swept 跨格覆盖保证不漏
2. **等价性契约**：`BroadPhase+NarrowPhase` 的 CollisionResult 必须 ≡ `全量 NarrowPhase` 的结果（逐位）——**T55**：随机化场景（50 实体/随机速度/1000 Tick）双路径逐位比对，进 CI
3. BroadPhase 结果**不进任何事件/快照/hash**——纯瞬时性能缓存

### PA-7 全 Combat 对统一相对扫掠（§2/§7 增补，堵「Fighter 冲刺穿过静态 Hitbox」缺口）

v1 只写了「七种运动走 IntegrateMove」；补：**全部 CombatContact 对（Hitbox×Hurtbox / Projectile×Fighter / Fighter×静态Hitbox）的检测 = 相对运动线性扫掠**——静止侧速度贡献为零，运动侧（含 Dash 中 Fighter，8m/数 Tick=≤1m/Tick）作为 mover。消除「静止 fan 被 1m/Tick 突进穿越」的采样漏判（与弹体同级风险，v1 遗漏）。Fan 类技能无此风险（Owner 在 Act 中朝向锁定，hitbox 静止）——突进者作为 mover 被 sweep。
