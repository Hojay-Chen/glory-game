# SPEC-0006 — Hurtbox / Hitbox 空间模型与命中结果扩展

| 项 | 值 |
|---|---|
| 编号 | SPEC-0006 |
| 状态 | **Accepted**（2026-09-01） |
| 性质 | ADR-0003 / ADR-0002 的 Implementation Specification Appendix（combat-granularity-collision-audit-v1 §12，用户批准后创建） |
| 上游 | ADR-0003（SimEvent/EventId/SemanticKey）、ADR-0008（部位修正消费方）、SPEC-0005（Sweep/ContactList/CollisionResult 骨架）、GDD §2.1（Hurtbox 数值）/§4.6（弱点判定）/§14.1.5（部位结算） |
| 决策层 | Arena.Core（Sim.Collision/Sim.HitResolve）+ Arena.Infra（Catalog 增补） |

---

## 1. Hurtbox 模型

### 1.1 HurtboxDef（Catalog 层，随 ClassDef 装配）

| 字段 | 类型 | 说明 |
|---|---|---|
| regionId | enum | **可扩展枚举**（见 §1.2）——不为当前需求硬编码两个 |
| localShape | ShapePrimitive | 相对 Fighter 原点的形状（§1.3） |
| enabledByDefault | bool | 装配即启用（状态类禁用走运行时，见 §1.4） |
| priority | int | 同一 Hitbox 同时覆盖多 Region 时的取区优先级（大者胜；Head=20 > Torso=10） |

- **v1 数据**（来自 GDD §2.1，全体职业统一 profile，职业覆盖为未来扩展位）：
  - `Torso`：AABB halfW=0.45m × halfD=0.30m，高度带 [0, 1.6m]（GDD 站立躯干盒 0.9×1.6×0.6m）
  - `Head`：Sphere r=0.18m，球心高度 1.6m（躯干顶；GDD 未钉球心高度——**GDD-GAP 登记**，采用「躯干顶+0」约定，待 §2.1 修订精确化）
- **v1 未启用**（枚举占位，数据行不存在即不参与碰撞）：LeftArm/RightArm/LeftLeg/RightLeg/Weapon

### 1.2 Region 枚举（可扩展，封闭于协议版本）

```
RegionId: None=0, Torso=1, Head=2, LeftArm=3, RightArm=4, LeftLeg=5, RightLeg=6, Weapon=7
```
- 新增 Region = EVENT_PROTOCOL_VERSION bump（ADR-0003 §6-4 既有机制）——枚举封闭于版本，不做运行时字符串
- **优先级排序**：Head > Torso > 四肢 > Weapon（命中覆盖多 Region 时取 priority 最大者报告）——GDD 无「一击多部位分伤」玩法，单事件单 Region

### 1.3 Hurtbox 实例（运行时，非独立实体）

```
hurtboxId = (FighterId, RegionId) 复合键     // 稳定、零分配、随 Fighter 生命周期
World Transform = Fighter 位置/朝向/高度 + LocalShape     // 每 Tick 由 Sim 投影，无独立状态
```

- **Enabled Window**：v1 = Fighter 存活且非隐形即启用；Down/Launched 状态高度带跟随 fighter.pos.y 平移；死亡/移出禁用——窗口规则实现于 Sim 投影层，**不作为独立持久状态**
- **Collision Layer**：`FighterBody`（层矩阵见 SPEC-0005 §7）

### 1.4 Hitbox 实例

| 字段 | 来源 | 说明 |
|---|---|---|
| hitboxId | (Tick, OwnerId, SkillId, WindowId, SegmentIndex) | 稳定键（=ADR-0003 SemanticKey 载荷，零分配） |
| OwnerId | 施放 Fighter | — |
| Shape | SkillDef hitbox（SPEC-0005 §4 映射） | 位置锚定：v1 = Owner 原点 + Owner 朝向旋转（**Local Transform 数据位预留 anchor offset**——hitbox 列无此数据，GAP-C1 数据扩展路径已登记） |
| Local Transform | 同上 | — |
| Active Window | SkillRuntime 阶段（startup 后 / active 内，P-2 schedule） | — |
| Collision Layer | `Hitbox` | 层矩阵见 SPEC-0005 §7 |
| Damage/Rule ref | SkillId → DamageFlags（含部位结算规则：豪龙破军 Head×1.5；巴雷特 Head×2；SRP 被动 +10%） | HitResolve 消费 |
| Multi-hit policy | hits/hit_interval + SemanticKey per-victim 已命中表 | 同一 victim 同段不重复结算（ADR-0003 幂等） |

## 2. CollisionResult（完整字段，SPEC-0005 §5 骨架的填充）

```csharp
public readonly record struct CollisionResult {
    public long ToiFixed;              // Q32.16 ∈ [0,1]——Tick 内接触时间参数
    public byte LayerPair;             // 层对枚举（SPEC-0005 §7 矩阵）
    public int AttackerId;             // Hitbox/Projectile 所有者
    public int DefenderId;             // 被接触方（地形接触时 = 0/地形 Id）
    public ushort HitboxId;            // 复合键（Tick/Owner/Skill/Window/Seg 见 §1.4）——序列化为 WindowId+Seg
    public byte HurtboxRegion;         // RegionId（地形接触 = None）
    public long HitPointX, HitPointY, HitPointZ;   // Fixed Raw（SPEC-0005 §5.4）
    public long HitNormalX, HitNormalZ;            // Fixed Raw 归一化（SPEC-0005 §5.3；地形法线）
    public byte CollisionKind;         // enum: TerrainStop/TerrainBounce/CombatHit/Push
    // 稳定排序键 = (ToiFixed, LayerPair, AttackerId, DefenderId, HitboxId, HurtboxRegion, CollisionKind)
}
```

- 排序键全 int64/byte——Ordinal 可比；ContactList 排序 = 键升序（ADR-0001 §3.1 纪律）
- **进入 SimEvent**：`Hit` 事件载荷增补 `hitRegion:byte / hitPointX/Y/Z:long / hitNormalX/Z:long`（Raw 直写）——EVENT_PROTOCOL_VERSION bump（既有机制）；Damage/Knockback 载荷**不变**（已在），HitRegion 由 HitResolve 的部位修正规则消费（豪龙破军/巴雷特/窝心脚）

## 3. 权威链路（响应「攻击位置进入战斗结果链」）

```
SkillRuntime（active 窗口/schedule 到点）
  → 实例化 Hitbox（SPEC-0006 §1.4，hitboxId 绑定 SemanticKey）
  → Sim.Collision.Sweep（SPEC-0005，唯一空间实现点）
  → CollisionList（§2 排序键总序）
  → HitResolve（伤害公式/修正项——**不做任何几何计算**，消费 hitRegion/hitPoint）
      ├─ HitRegion → 部位修正（Head×1.5 / 巴雷特×2 / SRP 被动）进 DamageFlags 乘区
      ├─ HitPoint/HitNormal → 事件载荷 + 技能墙反射等规则输入
      └─ Gates → SimEvent（Hit{…+空间字段}）
```

**禁止 HitResolve 自算几何**：HitResolve 的输入签名只含 CollisionResult + SkillDef + Fighter 投影——结构上无几何重算可能（T54 系列的架构断言）。

## 4. 高速防穿透验证矩阵（巴雷特 80m/s 案例 + 全矩阵）

量化基础：`D_raw = RHE(80×65536/60) = 87381`（1.33328 m/Tick）；Head r_raw = RHE(0.18×65536)=11796；Torso 深度 0.6m、头部球心高度 1.6m。

| # | 用例 | 几何构造 | 必须断言 |
|---|---|---|---|
| T54a | **Head crossing（终点双侧皆失）** | 弹沿 +Z 过头部球心正上方路径：P_N = C −(0,0,1.0)，P_N+1 = C +(0,0,0.333)——两端 \|P−C\|>0.18 均 miss | 解析 TOI 命中：hitRegion=Head、hitPoint 在球面、t_in=(1.0−0.18)/1.333 段内；damage ×2 生效 |
| T54b | **Torso crossing** | 躯干盒前面 z_f：P_N = z_f−0.7（盒外 0.1m）、P_N+1 = z_f+0.733（盒外 0.133m）——两端皆在盒外 | sweep 命中躯干，hitRegion=Torso、hitPoint=前面 z_f 处 |
| T54c | **Near miss** | 弹道距头部球心横向 0.19m（> 0.18） | 无接触（零事件） |
| T54d | **Exact boundary** | 弹道距球心横向恰 = 0.18m | 谓词 ≤ ⇒ 接触，hitNormal 为纯横向 |
| T54e | **Multiple targets** | 两 Fighter 在弹道不同 TOI | 按 toiFixed 序各结算一次；穿透属性决定第二目标是否受击 |
| T54f | **Wall reflection** | 击飞 Fighter 撞结界墙（0.6m/Tick） | 接触点在墙面、法线为墙内向、速度 ×0.6、剩余运动重积分（SPEC-0005 §6.3）、WallBounced 事件 |
| T54g | **Corner collision** | Fighter 被推向矩形墙角（双面同 Tick） | L1 迭代 ≤2 轮，终态在两约束交集，接触序 (toiFixed, Id) 确定 |
| T54h | **Terrain destroy** | 弹遇掩体墙（穿透标签 vs 无标签） | 无标签：Destroy 事件+掩体 HP−；【穿透】：继续命中后续目标 |
| T54i | **Height band** | 弹高度带与头部球心错开（下穿） | 无头部接触；躯干按高度带判定 |
| T54j | **Determinism cross-check** | 上述全部 ×2 次运行 + K=32 二分 oracle 对照 | 解析结果与 oracle 逐位一致（oracle 误差 <1 Fixed 量子已证明） |

## 5. 测试落地

- T54a–j 归入 GdUnit4Net（Phase 3 碰撞子系统）；全部为**表驱动确定性断言**（输入几何 → TOI/HitPoint/HitRegion/事件，逐 Raw 比对）
- 静态门禁：Core 空间路径无 float/Math.\*（ADR-0001 §2.4 既有门禁扩展至 Sim.Collision）
- ISqrt 单测：n ∈ {0,1,2,3,4,2⁶²−1, 完美平方, 完美平方±1} 边界全绿（RHE 修正双向验证）

## 6. 与 Tick 可变性的关系（承接用户 §七）

碰撞算法操作数 = per-Tick 速度（Compiler 预量化）+ t∈[0,1]（Tick 相对参数）——**Tick 率变化零改动**。需重推导的是全部「每 Tick 常量」（摩擦/回复/衰减，见 SPEC-0005 §12 表）——详见 ADR-0001 Errata §E-4（摩擦秒制化）。


---

## Preflight Amendments（2026-09-01，collision-preflight-audit-v1）

### PA-H1 高度模型精确化（§1.1/§1.3 补充）

1. **高度带双端 inclusive**：Torso 带 [0, 1.6] 闭区间；接触判定 ≤ 语义（与 T54d 边界一致）
2. **头部 = 3D 球测试**：弹体/攻击点 P 与球心 C=(x,y,1.6)：3D 距离² ≤ r²（int64 三平方和）——非「高度带内再查水平圆」（该近似会在球面边缘漏判/错判）
3. **地面 y=0 强制下界**（GroundStop）；上方无界（跳跃/launch 峰值 1.84m+高台）
4. **Projectile 高度模型**：直线弹 = 恒定高度飞行；**aimHeight 语义由 GDD §4.6 推导**——special 含「头部/弱点」标注的弹技（巴雷特/暴射类）aimHeight=1.6m（头部）；其余默认 1.2m（躯干带内）；lob = 抛物线（vel_y 逐 Tick 量化递减）。**Schema-15 登记**：hitbox proj 语法需增 aimHeight 字段（当前缺失——数据补丁轮处理，未裁定前 Compiler 按默认 1.2m 通过并 L2 标记）
5. HitRegion 判定 = 3D 谓词逐 Region 测试（Torso 水平盒+带；Head 球）——**高度由 Sim 的 fighter.pos.y 与 proj 高度通道提供，均为状态面**

### PA-H2 HitRegion 唯一选取规则（§1.2 priority 的落地语义）

同一 (Hitbox, Defender, Tick) 的 ContactList 可能含多个 Region 接触（Head 球与 Torso 带同时相交的颈部穿越）：

```
HitRegion = argmax(priority) over {该 Defender 本 Tick 全部 Hurtbox 接触}
  （priority 全互异：Head=20 > Torso=10 ⇒ 唯一，无并列）
并列保护：若未来 priority 相同（不可能于 v1）→ 取 toiFixed 最小者——规则预置
HitPoint/HitNormal = 取所选 Region 对应接触的值
```

单 Defender 单 Segment **恰一个 Hit 事件**（multi-hit policy 按段计）——多 Region 不产生多事件。

### PA-H3 命中后处理顺序固化（§新增，回应 HitDestroy/Pierce/Bounce/MultiHit 时序）

```
ContactList（排序键总序）逐条处理：
  CombatHit 结算 → 若 mover=Projectile：
    Destroy 属性 ⇒ 停止处理本列表全部剩余条目（同 Tick 后续目标不受击）
    Pierce 属性 ⇒ 扣减剩余次数（次数在 Projectile 运行时状态，随快照）；>0 继续下一条；=0 视同 Destroy
    Bounce 属性 ⇒ 本 Tick 终态（SPEC-0005 §6.3：反弹消耗剩余运动）——不再处理剩余条目
  Multi-hit（hits/hit_interval）裁决阶段 = HitResolve：per (victim, SegmentIndex) 幂等；
    同一 Hurtbox 同 Segment 禁止重复；跨 Segment（间隔 ≥3T）合法
  同一 Projectile 同 Tick 命中多目标：允许（T54e），Pierce/Destroy 规则如上
```

### PA-H4 状态豁免核对（HitRegion 选取前的资格过滤）

接触结算前逐 Defender 过滤：无敌帧/倒地保护（仅扫地可打）/隐形（Visibility 投影）在 **ContactList 生成前**由 Sim 层提供资格——Collision 只做几何，资格豁免在 HitResolve 前置过滤器（避免 Collision 依赖规则状态）。

### PA-H5 Schema-15 登记（proj/近战 hitbox 高度语义）

- proj 语法增 `aimHeight`（推导规则：GDD §4.6 弱点标注 ⇒ 1.6 头部；默认 1.2 躯干）——数据补丁轮处理
- 近战/横扫 hitbox 高度带默认 [0.2, 1.9]（覆盖头部球 1.42–1.78 与躯干）——**部位结算（豪龙破军）依赖此默认**
- 两者均为 Schema 增补登记，**未修改 CSV**；编译器白名单在数据补丁轮同步
