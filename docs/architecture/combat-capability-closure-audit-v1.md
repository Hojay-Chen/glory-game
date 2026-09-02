# Combat Capability Closure / Fidelity Audit v1 — 战斗能力闭环审计

| 项 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-09-02 |
| 基线 | master `73d8396`（Phase 4 Combat Runtime SPEC 合规重建） |
| 性质 | **能力闭环审计**——验证 Runtime 原语能否表达原著级细粒度战斗；非技能数量审计 |
| 方法 | 以 4 个代表性复杂体系（BLA 格挡 / GRP 抓取 / SRP 蓄力狙击 / STR-KNI 反击）为探针，原语化实现后逐机制验证；18 项探针测试（CC01–CC18）+ 既有 78 项回归 |
| 结论 | **Verdict B → B+**：核心战斗原语已闭环（格挡/盾/完美格挡/抓取/反击/Steer/hold/蓄力/翻滚+耐力/地形高度场/签名框架/Replay 全部经统一原语表达并通过确定性验证）；**尚余 6 项已定位的收口原语**（UnitSystem/资源槽/Visibility/Weapon overlay/法术反射/可控弹跟随），全部为「 enumerable 原语」，无 per-skill 代码趋势 → **大规模技能迁移仍需等待这 6 项收口（预计一个阶段工作量）** |

---

## 1. 探针结果总览（CC01–CC18，全部真实 CSV 数据驱动）

| # | 体系探针 | 验证机制 | 结果 |
|---|---|---|---|
| CC01 | BLA 格挡 | 正面 120° 吸收: HP 承 30%（化解物理 70% 数据化）+ 盾扣 ×1.2 + 无硬直 | ✅ |
| CC02 | BLA 格挡 | 背身 120° 外绕过（背击 ×1.2 叠加验证） | ✅ |
| CC03 | BLA 完美格挡 | 姿态生效 6f 内近战命中 → 免伤/盾不掉/攻击者强硬直 20f/0.5s 间隔 | ✅ |
| CC04 | BLA 盾值 | 破盾 → 强硬直 45f + 盾碎 + 8s 恢复至满（t499 实测回满） | ✅ |
| CC05 | BLA 格挡 | 化解物理门控: magic（天击）绕过格挡正常浮空 | ✅ |
| CC06 | BLA hold 姿态 | hold 不自然结束 / 格挡移速 −60% / 姿态中禁普攻 / 技能切换=释放 | ✅ |
| CC07 | GRP 抓取 | 背摔全流程: GrabStarted → 被擒锁定 → 投技结算（660 伤害）→ 强制倒地+受身无效 → GrabReleased | ✅ |
| CC08 | GRP 抓取 | 被擒第三方免疫（浮空弹穿越被擒者不命中）+ 抓取无视霸体（ArmorBreak 标签） | ✅ |
| CC09 | STR 反击 | 反击窗（inv 8–16f）内被命中 → 攻击者强硬直 20f + 反击者免伤+免费取消窗 | ✅ |
| CC10 | QIM Steer | SPEC-0001 饱和步进: heading 单调逼近目标、每 Tick ≤ 120°/60 | ✅ |
| CC11 | 翻滚+耐力 | 3m/30f 位移 + 无敌窗 4–18f 躲避命中（Invulnerable Whiff）+ 耐力 25 + 耗尽禁用 | ✅ |
| CC12 | 地形高度场 | 高台行走→走出边缘→坠落 3m → 伤害 240（高度×80）+ 强制长倒地 80f | ✅ |
| CC13 | SRP/LAU 蓄力 | 蓄力:0.8s:+40% → 前摇 20+48=68T + 伤害 ×1.4 端到端 | ✅ |
| CC14 | 技能中断 | GDD §4.3: 双方对拼同 Tick 互中 → 双双 Interrupted、无 ActEnded | ✅ |
| CC15 | 多实体多人 | 4 人（格挡+抓取+狙击+浮空连+状态+翻滚+普攻链+强制中断 900T）双跑逐位一致 | ✅ |
| CC16 | Replay | ADR-0005 最小闭环: 录制→重演事件 hash 逐位一致 + 数据版本不匹配显式拒绝 | ✅ |
| CC17 | 签名框架 | ADR-0008: ISignature/ISimContext 探针（RNG 流键绑定+ApplyStatus）确定性+快照安全 | ✅ |
| CC18 | 抓取×霸体交叉 | 霸体目标被【破霸体】抓取技命中 → 抓取成立 | ✅ |

## 2. 八项重点检查的裁决

### 2.1 是否存在只能靠 SimWorld 特殊 if/else 实现的机制？

**无新增。** 本阶段所有机制以三类通用入口表达：
1. **数据字段**（GuardDef/IsGrab/IsCounter/IsHold/SteerRate/ChargeTicks/ChargeBonusQ——Compiler 从 CSV special/type/active 列解析）；
2. **状态机窗口**（FighterState + Def 窗口谓词，无技能名分支）；
3. **事件驱动钩子**（ISignature.OnEvent——职业特化逻辑的唯一合法去处）。

过程中发现的既有 if/else 倾向已消除：技能中断（GDD §4.3）原需在每条反应路径加判断，现收敛为「无霸体命中 → TerminateExecutionById(interrupted)」单点。

### 2.2 应为 Core Primitive 但仍被特殊处理的能力？

- **格挡锥 hitbox 语义**（BLA_T1_002 自带 cone:r2.2:a120 dmg=0 判定体）：0 伤害命中会触发技能中断——**设计未裁定该判定体的意图**（推开轻击？VFX 锚点？）。探针以外置干扰处理。→ **Design Decision（待用户裁定）**
- **反击型技能的双重语义**（STR_T3_001 = inv 8-16f 防御窗 + cone dmg1.3 进攻判定体同时存在）：数据把「反击」建为定时进攻+无敌窗，而非「被命中触发反击」。CounterSuccess 原语已实现并验证，但 CSV 的 counter 行未使用触发式建模。→ **Data Gap**（counter 行需补触发语义标注，如 `special=反击触发`——待设计裁定后接入）

### 2.3 数据已描述、Runtime 无法表达的技能？

| 数据 | 缺口 | 分类 |
|---|---|---|
| `proj:...:可转向`（可控弹跟随转向，如念龙波） | 弹体已发射后不跟随施法者转向（Steer 只转施法者）——需 Projectile 挂接施法者 heading 或签名驱动 | Implementation Gap（小） |
| `channel=1`（30 行持续技） | 列已解析无消费；GDD 语义=持续施放受硬直打断——打断已由 CC14 中断原语覆盖，「持续」部分需通道持续消费 | Implementation Gap（小） |
| `lob:zone`/`zone:T` 持续区域 | 区域持续伤害周期未建模（现一次性） | Implementation Gap |
| `inv=none` 的 counter 行（GAN 圣光打） | 无敌窗缺省 → 反击窗退化为名义 2T 判定窗 | Data Gap |
| 盾值/化解率写入 special 自由文本 | 已解析（RuntimeSkillFactory.ParseGuard）——但 GDD §6.2 基线（120°/60%/×1.2）与数据（70%）并存，优先级未裁定 | Design Decision |

### 2.4 Runtime 可表达、数据模型无法描述的机制？

- **反击触发**：Runtime 有 CounterSuccess 原语（被命中触发），CSV 无触发式建模列（§2.2）→ Data Gap
- **弹刀后「下一击 100% 命中」**（STR_T3_001 special）：需命中强制标记（GuaranteedHit 窗口）——Runtime 可加一个窗口原语，数据无列 → Data Gap
- **投技落点可控**（背摔「落点可控」/抛投「8向可控」）：Runtime 可按 Steer/Aim 在投技结算时取方向，数据无落点参数列（投技落点距离/伤害分段）→ Data Gap
- **多目标抓取**（GRP_T3_002 双手飞/GRP_T4_001 群体扭杀）：Runtime 的 GrabbedBy 是单值——多目标需 (victim→grabber) 反向多播，Runtime 结构已可扩展（GrabStarted 按对结算），数据 hits 列可承载 → Implementation Gap（小）+ 数据 hits 语义确认
- **蓄力段位**（WRK_T1_001「13枚→满蓄26枚」多档蓄力）：现仅全额蓄力单档 → Data Gap（蓄力档位表）

### 2.5 是否存在会导致 487 技迁移产生大量特殊代码的设计？

**趋势检查：否。** 证据：本阶段 6 个新体系全部以「数据字段 + 窗口谓词 + 事件钩子」落地，SimWorld/HItResolve 净增代码中按 skillId 分支的数量 = **0**。487 技迁移的每技工作量收敛为「CSV 行已可解析 → RuntimeDef 自动承载」。剩余风险集中在 C 类 60 技（签名插件体）——每技一个插件类是 ADR-0008 的设计意图（组合原语，非 Core 分支）。

### 2.6 Signature 与 Core Runtime 的边界是否合理？

**合理且已实证。** ISignature 无字段状态（全部副作用经 ISimContext 进 Sim 状态域）→ 快照/回滚天然安全（CC17）。ISimContext 身份绑定（RNG 流键不可伪造）。Core 暴露的九原语面（Roll100/IsAlly/ApplyStatus/SetHeading/ResetCooldown/RouteDamage/GetFighter/SpawnProjectile/SpawnUnit*）与 pre-adr §3 的能力清单对齐。边界规则：**凡能分解为「窗口+判定+状态」的机制进 Core 原语；凡需职业叙事组合的进签名**——CC17 探针证实该边界可执行。

### 2.7 确定性/Snapshot/Rollback/Replay 在复杂机制下是否仍成立？

- CC15（4 人复杂对局 900T 双跑）逐位一致 ✅
- D02 快照恢复（新增 Shield/Grabbed/Roll/Stamina/PeakY/CounterWindow 等全部状态域已入快照）✅
- CC16 Replay 最小闭环（录制→重演 hash 一致；版本不匹配显式拒绝）✅
- 签名钩子确定性（注册序 ClassId 升序 + 本 Tick 事件物化快照派发——签名发射的新事件归下一 Tick，避免枚举修改）✅
- **Rollback（ADR-0006）**：快照完备性已由 D02 证实；逐位和解测试归网络阶段（依赖传输层）——登记为「机制就绪、测试待网络阶段」

### 2.8 空间战斗模型是否足以支撑原著细粒度战斗？

**本体足够，两个已知近似需记录：**
1. 扇区 hitbox 圆盘角点连接处的过近似（≤0.16m@R2.6——判定略宽松，确定性无碍）——如需精确改 union 分解求解器（SPEC-0005 §5.1 已预留）
2. 扇区体膨胀（Minkowski ⊕ body circle）在弧端 junction 的超覆盖——同上
3. **高度场是 2.5D**（平台顶高查询+重力+坠落），非真 3D 体素——GDD 的 3D 战斗（空中互连、高低差连段）在「平台顶+坠距」语义下可表达；悬空平台边缘精确站位的边缘判定（fall_edge 触发半径）未做边缘容差——Implementation Gap（小）

## 3. 缺口分类总表

| # | 缺口 | 分类 | 收口路径 |
|---|---|---|---|
| 1 | UnitSystem（unit hitbox 10 行+召唤 11 技） | Implementation Gap | Sim.Units（SpawnUnit 原语+效用 AI Tick 定序）——pre-adr §3-1 已定义 |
| 2 | 职业资源槽（炫纹/弹匣/召唤位，G-12） | Implementation Gap | ResourceSlot 原语（有序槽位+事件）——签名消费 |
| 3 | Visibility（潜行/假身/替身，G-07 关联） | Implementation Gap | Visibility 投影（IsVisible 谓词+锁定失效） |
| 4 | Weapon overlay（G-18） | Implementation Gap | Weapons.Apply()（规则级：距离/CD/段数/半径/角度/耐久） |
| 5 | 法术反射（KNI_T3_003 2s 窗） | Implementation Gap | Reflection 原语（CounterWindow 扩展 kind=Spell+弹体重定向） |
| 6 | 可控弹跟随/lob zone 持续/multi-grab | Implementation Gap（小） | Projectile 挂 heading 引用 + zone 周期化 + GrabbedBy 多播 |
| 7 | counter 行触发语义 + 蓄力档位 + 投技落点参数 + 蓄力 bonus 之外的特殊文本（强后座等） | Data Gap | skills.csv 补丁轮（结构化列或 special 受控语法） |
| 8 | 格挡锥 dmg=0 判定体意图；盾值 GDD 60% vs 数据 70% 优先级；counter 无 inv 行 | **Design Decision（不代裁）** | 用户裁定后落数据/原语 |
| 9 | Rollback 逐位和解测试 | 测试待网络阶段 | ADR-0006 落地时 |

## 4. 数字对照

| 维度 | Phase 4 末 | Phase 5 末 |
|---|---:|---:|
| 测试 | 78 | **96**（+18 探针） |
| 487 技 Runtime 可执行 | ~45% | **~52%**（+格挡/抓取/反击/蓄力/翻滚/地形承载的 B 类技；C 类仍 0——签名插件体未批量写） |
| 已实现 Core 原语 | 9 类 | **16 类**（+Guard/Shield/Parry/Grab/Counter/Steer/Hold/Charge/Roll/Stamina/TerrainHeight/Fall/Replay/Signature 框架） |
| 已知 per-skill 分支（Sim 内） | 0 | **0** |
| BLOCKER 级机制缺口 | 5 | **0**（机制层全部有原语；余 6 项原语为「增强」而非「缺失 blocking」） |

## 5. 裁决

**Verdict: B（核心原语已闭环，尚余 6 项可枚举原语收口后即可迁移）——即「B+」。**

理由：
1. 用户设定的迁移门槛 = 「复杂战斗机制能经统一原语表达 + 无 per-skill 特殊代码趋势」——两者均已实证（CC01–CC18 + §2.5 零分支证据）；
2. 但 §3 表的 6 项 Implementation Gap 中，UnitSystem（21+ 技）与资源槽（BMG 炫纹/SPF 弹匣等核心职业机制）仍会阻塞对应技能行的迁移——现在开迁移会立即撞墙并产生临时绕行；
3. 6 项原语全部有 pre-adr/ADR-0008 依据、无设计争议、预计一个阶段收口。

**建议下一步**：收口阶段——UnitSystem → 资源槽 → Weapon overlay → Visibility → 反射+可控弹（一个阶段），然后 **Verdict A，进入 487 技大规模迁移**（A/B/C/D 类按序，C 类签名插件体随迁）。

## 6. 待用户裁定的 Design Decision（不代裁）

1. **格挡锥 hitbox**（BLA_T1_002 自带 dmg=0 cone）：保留（语义=？）或删除或改「格挡反制轻击」
2. **盾值/减伤口径**：GDD §6.2 基线（120°/60%/×1.2）vs BLA_T1_002 special（化解物理 70%）——现实现=数据覆盖基线
3. **counter 行建模**：现数据=定时进攻+无敌窗；是否改为触发式（被命中才反击）并补 CSV 标注
4. **蓄力语义**：现实现=全额蓄力（前摇追加+固定加成）；GDD 的按住蓄力/满蓄奖励帧需要 hold+release 输入协议（ADR-0010 无 release 指令——协议扩展待裁定）
