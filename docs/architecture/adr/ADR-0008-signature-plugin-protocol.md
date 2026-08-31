# ADR-0008 — 签名机制插件协议：ISignature / ISimContext

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0008 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Core（Classes.Signatures） |
| 上游 | ADR-0001（§3.2 总序/§3.4 签名注册序/§4 RNG scope）、ADR-0003（事件派发/入队变更）、architecture.md（API Boundaries：ISimContext 受限 API）；TR-char-001；pre-adr-resolution §3（9 项 Core 接口）；audit C 类 60 技清单 |
| 事实依据 | audit-spec-consistency-v1 §3：C 类 60 技分布于 13 职业（SUM 20/BMG 7/UNS 7/NJA 5/SPF 5/MEH 4/BER 4/WIT 2/THF 2/KNI·ROG·SBL·SRP 各 1） |

---

## 0. 背景与问题

487 技中 60 条（12.3%）依赖职业专属状态机/资源/单位/输入序列。architecture.md 已立「原语在 Core、组合在插件」原则与 ISimContext 受限 API 骨架。本 ADR 固化：完整插件生命周期、受限 API 全集、边界纪律（Core 通用规则不塞进插件）、OQ-9 的处理框架。

## 1. 插件生命周期

```
Match 装配期：
  Classes.Registry 读 ClassDef → 按职业实例化 ISignature → 注册表按 (ClassId, PluginId) 排序冻结
对局中（每 Tick ⑤ 阶段，ADR-0001 §3.2）：
  每个 SimEvent 按 FighterId 升序提供给全体签名 → 签名自过滤 → 经 ISimContext 产生变更 → 入队
  队列按入队序（=事件序）统一结算 → 次级事件 seq 续接（ADR-0003 §4）
结算/回放：
  签名实例无独立持久状态——全部状态存于 Fighter（orbs/buffs/资源），随 Snapshot 持久化
```

**注册序即确定序**（ADR-0001 §3.4）；插件实例**无字段状态**（无 stateful 插件类字段）——所有可变状态走 Fighter 快照域，保证 Rollback 天然正确。

## 2. ISimContext 受限 API 全集（固化 pre-adr-resolution §3 九项原语）

```csharp
public interface ISimContext {
    // —— 伤害与状态（写点唯一原则：全部汇入 Sim.HitResolve/StatusSystem）——
    void ApplyDamage(int victimId, Fixed damage, DamageFlags flags);   // flags: 破霸体/无视防御/资源路由目标(MP/护盾)…
    void ApplyStatus(int victimId, StatusKind kind, TickDuration dur, Chance chance = default);
    void ApplyBuff(int fighterId, BuffKind kind, TickDuration dur);
    // —— 实体生成 ——
    void SpawnUnit(in UnitSpec spec);        // 9-1 UnitSystem（SUM/MEH；含 AI profile/继承面板）
    void SpawnDeploy(in DeploySpec spec);    // 部署/结界/陷阱/假身（GBL 阵/THF 陷阱/NJA 影分身）
    void SpawnProjectile(in ProjectileSpec spec); // 追踪/往返/定时/遥控参数
    // —— Core 原语操作 ——
    void SetCooldown(int fighterId, SkillId id, TickDuration ticks);       // 9-5（SRP 双重控制/KNI 骑士精神）
    bool ClaimUse(int fighterId, SkillId id);                              // 9-7 每场限额
    void SetVisibility(int targetId, bool hidden, VisibilityScope scope);  // 9-2 潜行/隐身
    void SetMovementOverride(int fighterId, in MovementOverride ov);       // 9-8 飞行/滞空/滑翔
    void AddShield(int fighterId, in ShieldSpec spec);                     // 9-3 护盾池
    void RouteResource(int fighterId, ResourceKind from, ResourceKind to, Fixed amount); // 9-6 法力护盾/截脉
    void Steer(int actorId, in SteerInput input);                          // 主动技可控段（魂御/猛虎乱舞/念龙波）
    // —— 读取与随机 ——
    ISimView View { get; }                     // 只读投影（含 Faction 查询 9-9）
    uint Roll100(RollScope scope);             // ADR-0001 §4：scope 由调用者身份绑定，插件无法伪造
}
```

**关键纪律**：
1. **写点唯一**：插件永远不直接改 Fighter 字段——全部经 context 汇入 Sim 内部裁决（闸门/纪元/事件键照常生效）；模拟「绕过闸门的插件伤害」在结构上不可表达
2. **RNG scope 绑定**：Roll100 的 streamKey 由 Sim 按 (调用者 FighterId, 触发 SkillId) 强制——插件传什么都以身份为准
3. **Chance 类型**：`Chance = {numer, denom}` 精确有理数——CSV `@50%` 编译为 `50/100`，与 ADR-0001 §4.2 Roll100 语义对齐
4. 禁止插件持有：Random/Godot 类型/可变集合/Direct Fighter 引用

## 3. 边界纪律：什么在 Core、什么在插件（回应 audit §七）

| 归属 | 内容 | 判据 |
|---|---|---|
| **Arena.Core 通用规则** | 五道闸门、16 类异常+控制值、取消系统、格挡/完美格挡/霸体、抓取框架、投射物框架、部署框架、UnitSystem、VisibilitySystem、护盾池、反射、CD 操作、资源路由、每场限额、阵营、飞行覆盖、伤害公式与全部修正项 | 被多个职业消费的通用原语（§3 九项能力接口全在此层） |
| **Classes.Signatures（插件）** | 炫纹铸造与发射语义、血气三态互斥、波动共鸣叠层、召唤兽指挥意图、弹种切换表、结印输入序列、六形态切换表、潜行策略、以牙还牙复制、骑士精神跨技能强化、幻影/替身行为 | 职业专属**状态组合与触发规则**——单职业私有 |
| **判定原则** | 若某机制被 ≥2 职业以相同语义使用 → 升格 Core 原语；单职业私有 → 插件；插件需要新原语 → 先修本 ADR 或新增 Core 接口 ADR | 「不为实现方便把 Core 规则塞进插件」的反向亦然：不为省事把职业私有逻辑写死在 Core |

**OQ-9 处理框架**（骑士精神/街头风暴类「跨技能变异 buff」）：技术两可——默认按本表归**插件**（单职业私有组合）；若后续出现第二职业需要同类机制，按判定原则升格 Core 的 `SkillModifierTable` 原语。**维持待裁定不预设终局**，本 ADR 只保证两条路径都不破坏确定性（插件路径经 ISimContext；Core 路径经数据表）。

## 4. C 类 60 技的插件归属映射（实现工作池，v1）

| 插件 | 职业 | 覆盖技能 | 依赖 Core 原语 |
|---|---|---|---|
| BMG.Orbs | 战斗法师 | 7（炫纹触发×5+发射+斗者意志） | 资源槽（orbs）、Projectile（发射弹）、事件钩子 |
| BER.BloodQi | 狂剑士 | 4（狂暴/嗜血/嗜血奋战/血气唤醒） | Buff+HP 自伤路由 |
| KNI.Virtues | 骑士 | 1（骑士精神） | CD 操作+SkillModifier |
| MEH.Machines | 机械师 | 4（机械单位×3+电子眼） | UnitSystem+Visibility(共享视角→表现) |
| NJA.Ninjutsu | 忍者 | 5（影分身/地底/替身/结印系/影舞） | Visibility+Decoy+Roll100+输入序列 |
| ROG.Mirror | 流氓 | 1（以牙还牙） | 事件回溯（记录最近受击技能）+动态施放 |
| SBL.Resonance | 魔剑士 | 1（杀意波动/波动共鸣） | Buff 叠层 |
| SPF.Ammo | 弹药师 | 5（装填×3+乱雷+弹药扩充） | 弹匣资源+切换表 |
| SRP.Magazine | 神枪手 | 1（左轮弹匣） | 弹匣资源 |
| SUM.Legion | 召唤师 | 20（召唤兽+指挥+合体+流派 U） | UnitSystem 全量+指挥指令 |
| THF.Stealth | 盗贼 | 2（潜行+陷阱精通） | Visibility |
| UNS.Umbrella | 散人 | 7（六形态+切换） | 形态切换表+普攻模组绑定 |
| WIT.Broom | 魔道学者 | 2（扫把掌握+空袭俯冲） | Flight 覆盖 |

13 个插件、60 技——B 类 265 条**不依赖任何插件**（ADR-0002 白名单+§3 Core 原语即达）。

## 5. 测试

| # | 测试 | 验证 |
|---|---|---|
| T29 | 写点唯一 | 静态+运行期：签名路径外无 Fighter 可变写；ISimContext 之外的变更 API 不可达 |
| T30 | Roll100 身份绑定 | 插件伪造 scope ⇒ Sim 以调用者身份覆写，roll 流键正确 |
| T31 | 注册序确定 | 同 dataVersion 两次装配注册表逐位一致；乱序注册被排序归一 |
| T32 | C 类覆盖 | 60 条 C 技逐条冒烟（触发/结算/回滚重演一致）——并入 ADR-0001 T1/T3 体系 |
| T33 | 无状态插件 | 插件实例字段反射检查为空（状态全在 Fighter 快照域） |

## 6. Open Questions

- **OQ-9 维持**（§3 框架已给两条确定性路径，终局待第二职业出现时裁定）
- OQ-6（几率数值）补录后，Chance 有理数直接可编译——无新增 OQ
- 其余 OQ 维持；F1 未涉

## 附：自审（12/12）——生命周期冻结序✓ 无状态插件（Rollback 正确）✓ 写点唯一✓ RNG 身份绑定✓ Chance 有理数对齐 ADR-0001✓ 九原语全部 API 化✓ 边界判定原则双向成立✓ OQ-9 不预设✓ 60 技归属映射可执行✓ C 类 265 条不依赖插件✓ 测试落地✓ 未动其他文档/数据✓
