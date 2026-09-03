# Active Session — glory-game 项目进度

- **项目**: 《廿四争锋》(Project ARENA·24) — 3D 竞技场动作对战，复刻荣耀战斗机制，仅战斗+竞技
- **日期**: 2026-09-02
- **引擎**: Godot 4.3 stable / C# (.NET 8+) / xUnit（D-P0-1 维持，GdUnit4Net 未安装）

## 设计文档状态

- GDD v0.3.7 / Skill-Spec v0.1(+v0.4 补丁) / Weapon-Spec v0.1 / Balance-Sheet v0.1(PASS) / 复刻审计 v1+v2
- Tech-Architecture v1.0 / ADR-0001~0010 全 Accepted / SPEC-0001~0006 全 Accepted
- combat-fidelity-review-v1（Gate: B）→ **combat-runtime-phase4-report（本阶段收口）**

## Phase 0+1+2+3（2026-09-01，已合入）

- Phase 0 工程骨架 / Phase 1 Fixed 基座 / Phase 2 Compiler / Phase 3A-3D Vertical Slice（stub）
- SPEC-0005/0006 碰撞专项规格（Accepted）

## Phase 4 Combat Runtime SPEC 合规重建（2026-09-02，本次）

- **SimWorld 从 stub 重建为唯一权威战斗链路**：Compiler(量化 RuntimeDef 483/487) → 结算总序(ADR-0001 §3.2) → SkillTimeline(hitSchedule 多段/取消窗/缓冲) → CollisionSystem(PA-7 相对扫掠 + IntegrateMove 统一路径 + BroadPhase PA-6) → ContactList 总序 → HitResolve(零几何) → SimEvent 协议 v2
- **fidelity-review 5 Bug 全修**（移动清零/指令路由/Sweep 断路/HitRegion 硬编码/HitPoint）
- **战斗能力**：浮空引擎(×0.8ⁿ/3s 闸)/击退撞墙反弹(×0.6+剩余运动重积分)/倒地受身起身(双条件击退)/强制倒地受身无效/扫地 ×0.7/弱点头部(×1.5/×2 GDD 门控)/背击 ×1.2(零除法)/霸体 SA/SSA+破霸体/控制异常 14 类+控制值挣脱/DoT 分数累积/投射物(直线+lob+穿透+地形摧毁)/结界墙反弹/普攻链/命中取消+档位递进/输入缓冲 12f
- **确定性**: D01 双跑逐位 / D02 快照恢复续跑 / D03 指令序不变 / D04 3000Tick soak / T55 BroadPhase 等价 ×3 复跑全绿
- **新增 Errata 提案**: E-6 整数 CORDIC 三角（SPEC-0005 §4 朝向旋转必需）、E-7 Int128 判别式域；半度 cos/sin 表入 DeterministicTables（gen_tables.py 扩展，sha 重生成）
- **测试 78/78**（新增 Collision 11 + 数学 8 + BroadPhase 2 + Golden Slice 17 + 确定性 4；旧 stub VS 测试删除由数据驱动 GS 继承）
- **487 技可跑占比 16% → ~45%**（A ~130 / B ~90 / C 0——签名未实现，诚实口径）
- **新 L1 阻塞**: GBL_T2_001 fan a200>180° 违反 SPEC-0005 §4 凸性 → Schema Failure（待设计裁定）
- **未路由注册表**（Compiler 显式登记）: status 47 / hitbox 48（unit/deploy/ally/可控弹/分身等 → ADR-0008 签名阶段）

## Phase 5 Combat Capability Closure（2026-09-02，本次）

- **核心战斗原语闭环**（全部数据驱动原语、零 per-skill 分支）: 格挡/盾值/完美格挡（120°/化解物理70%/×1.2/破盾45f/8s回满/弹刀20f+15f窗）/抓取体系（Grabbed锁定+投技结算+第三方免疫+无视霸体+死亡释放）/反击窗（inv窗内命中→攻击者强硬直20f+免费取消窗）/Steer（SPEC-0001 饱和步进）/hold 姿态（不自然结束+格挡移速-60%+禁普攻）/蓄力（前摇追加+伤害加成数据化）/翻滚+耐力（3m/30f+无敌4-18f+25耐力）/地形高度场（平台顶+坠落伤害高度×80+长倒地）/GDD §4.3 技能中断/ISignature+ISimContext 框架（ADR-0008 最小闭环）/Replay 原语（ADR-0005 最小闭环）
- **探针 18 项（CC01–CC18）+ 既有 78 全绿 = 96/96**；门禁 PASS
- **审计结论 Verdict B+**: 复杂机制全部经统一原语表达、Sim 内 per-skill 分支=0；余 6 项可枚举原语（UnitSystem/资源槽/Visibility/Weapon overlay/法术反射/可控弹）收口后 → Verdict A 进 487 技迁移
- **审计文档**: docs/architecture/combat-capability-closure-audit-v1.md（八项重点检查裁决+缺口分类表）
- **事件协议 v3**: +Parry/GuardHit/GuardBroken/GrabStarted/GrabReleased/Countered/Interrupted/FallLanded；FighterState +Roll
- **待用户裁定（Design Decision，不代裁）**: ①格挡锥 dmg=0 判定体意图 ②盾值 GDD 60% vs 数据 70% ③counter 行触发式建模 ④按住蓄力需 ADR-0010 release 指令协议扩展

## Phase 6 原语收口 + Pilot 迁移验证（2026-09-02，本次）

- **Phase 6 原语落地**（全部数据驱动、零 per-skill 分支）: UnitSystem（召唤位 4 消费+追击+投掷攻击+存在期回收+位满 Cap 回收最旧+面板挂主人）/ ResourceSlots 定长槽位（class-base resource 列）/ Visibility（潜行 hold→sweep 过滤→施法破隐）/ 法术反射（magic 弹体反弹 OwnerId 转移+反向）/ 可控弹跟随（念龙波 Steer→弹体方向实时跟随）/ Weapon overlay（weapons.csv 73 行解析 + atk_mod 面板 + 结构化 trait 规则）
- **Pilot 三层验证**: PF01 全表 483 行 Compiler 保真度门禁（独立双路径重解析逐字段比对，1 行 heal 记法豁免）/ PF02 全表分类 routed=429 (89%) partial=54 (11%) / PF03–PF09 代表技执行（部位×1.5/freeze/DoT/lob/抓取/受身无效/可控弹/蓄力/反射） / PF10 全原语混合 800T 双跑逐位一致
- **审计结论**: migration-fidelity-audit-v1.md——**Verdict A（有条件）**: 批量迁移可启动；Routed 429 行 A→B→C/D 分批；Partial 54 随 UnitSystem/Deploy/签名批次；MF-1 背身抓取角度门控随首批落地
- **测试 113/113**（+Phase6 探针 7 + Pilot 10）；事件协议 +UnitSpawned/UnitDied/StealthBroken/Reflected；Snapshot +Hidden/ReflectTicks/ResourceSlots/WeaponId
- **Fidelity Gap 登记**: MF-1 背身抓取角度门控 / MF-2 deploy 放置+耐久 / MF-3 可控弹特殊形态(签名) / MF-4 ally 弹道 / MF-5 heal 记法 / MF-6 时序敏感测试编排

## Phase 7 第一批迁移 + Combat Fidelity Review v2（2026-09-02，本次）

- **MF-1 修复**: RequireBehindDeg 数据化（NJA_T3_001 需背身120°）+ Whiff(Angle) 回归
- **执行矩阵**（B1 测试）: 469 可施法技逐技独立世界执行——hit 354 / whiff 81（几何合法）/ projectile 17 / non-damaging 16 / **EXCEPTION 0 / NO_CAST 0 / silent 2→0**
- **数量→复杂度转化**: 24 职业全部 ≥60% 连接率（中位 79%；GRP/QIM/SBL 100%）；职业特征机制在矩阵可见（SUM UnitSystem/THF 潜行/KNI 反射/QIM 可控弹/LAU 蓄力）
- **当场修复**: WRK_T1_001 蓄力「13枚」误读 13s（ParseCharge s 后缀强制）/ GAN_T1_002 heal 通道缺失 → MF-7 登记
- **Combat Fidelity Review v2**: combat-fidelity-review-v2.md——第一批稳定裁决，批次 2 可扩量（MF-2/4/5+7 小原语先行）
- **测试 116/116**（+Batch1 矩阵 3: 全量执行/伤害近战专项/1500T 40+技循环双跑确定性）

## Phase 7 Batch 2 Deploy/Heal/交互矩阵（2026-09-02，本次）

- **Deploy 原语**（统一实体载荷语义，UnitSpec 扩展）: 陷阱（触发半径+单次爆发+自毁）/ 光环（周期脉冲——敌伤/己益由 def 数据域决定）/ Wall/Scout/Mirror/Taunt 静置语义；deploy/wall/zone hitbox 全部路由
- **Heal 通道**: PRI 瞬发（直量 800/900/1400/1600/2400）+ GAN HoT（每3s脉冲×6=1200）+ 回蓝（30%蓝→maxMP 比例）+ HP 上限钳制
- **交互矩阵 10 项（D01–D08/IX01–IX10）**: 陷阱触发/主人豁免/光环脉冲/半径退出/增益阵（ATK+5% 阵内己方+敌方豁免+到期清零）/HoT 上限/瞬发直量/burn 穿透格挡(DotApplied 证明)/冰冻增伤+1.1/击退撞墙+poison 并存/反击先手打断抓取/光环不命中潜行/召唤不锁定潜行/冰冻灼烧互斥/破防→后续全额/全交互混合 1000T 双跑逐位一致
- **DoT 双量化修复**: StatusSystem per-Tick = RHE(PotencyQ/60)（PotencyQ 已是 Q32.16 每秒伤害——原 ×ONE 双量化使 DoT 瞬间致死）
- **测试 134/134**（+Batch2 探针 18: Deploy 5 + Heal 3 + 交互 10）；事件协议 +BuffApplied/BuffExpired/Healed；Snapshot +BuffAtkPct/HealPulse 域
- **数据歧义进 DDQ**: PRI_T3_005 回蓝比例（30%蓝 maxMP 基线=1000 待确认）/ GAN 恢复量 dmg=200 直接 HP 判读 / ally:单体 目标选择来源（无锁定系统 v1 自体）

## Phase 7 Batch 3 签名第一批 + 边界复核收口（2026-09-03，本次）

- **签名框架**: ISignature 新增 ModifyDamage 伤害修正乘区钩子（HitResolve 背击乘区后调用）+ ISimContext 新原语 GetSkillDef/GetResource/GetResourceCap/AddResource
- **4 真实职业签名**（覆盖 3 模式族）: BMG 斗者意志（OnEvent Hit→Orb 资源槽上限 7）/ BER 血气唤醒（OnTick HP 三档→BuffAtkPct）/ ASN 暗杀艺术（ModifyDamage 背击 ×1.2）/ QIM 护体真气（OnTick MP 三档→BuffDefPct）
- **边界复核 4 项收口**（用户 Review 裁定，2026-09-03）:
  1. QIM `_lastDef` 私有字段违反 ADR-0008「签名无字段战斗状态」→ 重设计为 BuffDefPct 权威状态域（可负/Snapshot 携带/HitResolve effDef 消费）
  2. 多人同职业隔离: DispatchSignatures 每 Fighter 独立 ctx + 独立派发（原实现只绑第一个同职业 Fighter）→ SG08 探针证明 BMG×2/BER×2 资源池与 buff 域完全隔离
  3. BER/QIM 阈值基准硬编码 10000 HP/1000 MP → 权威 HpMax/MpMax 状态域字段（Clone/Snapshot 全携带；短周期槽刷新 ticks≤2 不踩外部长周期 buff）
  4. BMG OrbSkills 硬编码 HashSet → OrbTag 数据驱动（Compiler 解析 special「炫纹:X」→ OrbTagKind）；当场修正保真度偏差：龙牙「炫纹:无属性」原被遗漏
- **测试 144/144**（+SG08 隔离 / SG09 同职业多人快照恢复+指令流重放逐位一致 / SG10 BuffDefPct 伤害链消费+基准 Def 不被改写）
- **踩坑**: 施法 aim 覆盖 heading——测试中 HeadingQuantum 预设无效，须 `Skill(f,id,aim:32768)` 朝 −Z
- **仓库卫生**: .gitignore 补齐——bin/obj/.godot 共 238 文件退出跟踪（此前每次构建污染工作树 80+ 文件）
- 双门禁 PASS（check_math_ban / check_deps）

## Phase 7 Batch 4 自增益通道 + 小签名插件批次（2026-09-03，本次）

- **通用自增益通道（B 类扩容，零签名）**: Compiler 解析 special「ATK+P%」/「-P%/s」/「正嗜血P%」→ 施法路径通用施加 BuffAtkPct/BuffDrainHpPct（60T 脉冲 ×HpMax，HoT 脉冲同律）/Lifesteal（HitResolve 伤害结算后回复）三域；passive/deploy/basic 门控排除。**嗜血/嗜血奋战完全数据驱动表达——C→B 重分类**（BER.BloodQi 插件仅剩血气唤醒=Batch 3 已做 → 插件收口）
- **3 新签名**: THF 陷阱精通（ShouldBreakStealth 门控——潜行设陷阱不解除，TerminateExecution keepStealth 参数）/ SBL 杀意波动（共鸣层 ResourceSlots.Resonance=6 + ModifyDamage SignaturePassive ×(1+4%×层) + ModifyStartupTicks 每层前摇 −1f）/ KNI 骑士精神（ResetAllCooldowns 除本技）
- **StartupDeltaTicks 管线**: SkillExecution 每 cast 前摇修正（EffectiveStartup 贯穿 TotalTicks/InStartup/段窗/发射点/取消窗/格挡窗 10 处——Def 共享引用不可变，cast 级 delta 独立）
- **2 个真实 bug 修复**:
  1. **霸体命中仍中断技能**——违反 GDD §4.3「（无霸体）→ 技能中断」；HitResolve 技能中断加 !armored 门控
  2. **投射物头部命中伤害归零**——ProjectileSystem useHead 不看 HeadMultQ 门控（近战 SelectHitRegion 有），非弱点技弹体擦过头球 → 乘 HeadMultQ=0 → dmg=0；补 headEligible 门控（SG14 探针暴露）
- **BER 血气唤醒写守卫**: BuffAtkPctTicks≤2 才写入——不踩嗜血 1200T 长周期槽
- **事件协议 v4**: +DrainPulse；Fighter 域 +BuffDrainHpPctQ/Ticks+LifestealPctQ/Ticks+LastCastSkillUid（SBL 连放判定，EndExecution/TerminateExecution 双路径更新）；Snapshot 全携带
- **class-base.csv**: SBL resource '共鸣:3'（balance_audit 重跑 PASS: nominal 399/observed 396）
- **测试 152/152**（+SG11/11b/12/13/13b/14/15/16 八探针；SG16 快照恢复+重放逐位一致）
- **踩坑**: 测试跳 Tick 会使 exec.CurrentOffset 与墙钟错位（armor/前摇窗口判定失真）——Batch 4 测试全部逐 Tick 步进+指令表；施法 aim 覆盖 heading（SG08 既有结论）

## Phase 7 Batch 5 解耦落地 + 资源闭环 + 动态施放（2026-09-03，本次）

- **DDQ-B4-①②裁定落地（动作窗/效果生命周期解耦）**: Compiler 新增 EffectDurationTicks（buff/stance act=Ns=效果持续借位事实）+ IsPureBuff 判定；纯 buff（无 hitbox 无伤害）动作窗收为名义 2T（parser 既有「判定即收」惯例）——嗜血从锁身 1200T → ~28T 恢复自由，效果持续 1200T 独立承载
- **BuffArmor 效果域**: 纯 buff 的 SSA/SA 窗口（24-244f 等）转 Fighter 域（Kind/Delay/Ticks，Snapshot 携带）——动作窗 2T 后霸体由域承载，HitResolve 双源消费（exec 窗口 ∪ 域）；**armor CSV 数据（24-1224f 之疑）仍待裁定不代填**
- **SPF 弹匣资源闭环**: class-base resource 格式归一 `弹匣+手雷库存`→`弹匣:20`（PATCH-002，GDD 1845 弹药扩充被动常驻有效值；手雷库存不接入）；普攻每击 −1、空匣干火失败、装填技（换弹匣解析）回满 cap；AddFighter 弹匣装满入场语义
- **BMG 炫纹发射资源闭环**: OrbTypeCounts[6] 类型分布域（Σ==Orb 槽不变式）→ 发射消耗全部炫纹 → 全弹幕（弹数=纹数）→ 按型增益 20s（冰=BuffDefPct+4%/枚、火=BuffAtkPct+5%/枚，GDD §14.1.3 表源签名常量）；BMG.Orbs 一插件合并获纹+发射（一职业一签名）
- **追踪弹运动原语**: ProjectileSystem 追踪:X°/s（数据化）——锁定目标饱和转向（SPEC-0001 同律）+ DeterministicMath.CordicAtan2（E-6 同族向量模式 CORDIC，heading 量子互逆）
- **ROG 以牙还牙动态施放**: CopiedSkillUids[3] 环形记录域（受击记录，排除普攻/U 档/自身）+ ResolveDynamicCast 签名钩子（按键技→运行时重定向执行体，MP=原技×2，CD 记按键技）——验证「技能运行时决定执行体」能力
- **ISignature/ISimContext 扩展**: ResolveDynamicCast 钩子 + SpawnProjectile 原语；StartExecution cdKey/mpCostMult 参数（动态施放 CD 记按键技）；Normal 路径补 CD/MP 预检（原缺——重试闸）
- **迁移构成报告（SG21，启发式 v1）**: **Data-driven=360 / Primitive-assisted=97 / Signature=26 / total=483**
- **测试 157/157**（+SG17 解耦 / SG18 弹匣 / SG19 炫纹发射+追踪 / SG20 动态施放 / SG21 构成）；双门禁 PASS；balance_audit PASS
- **踩坑**: ①一职业一签名强制——BMG 获纹/发射合并进 BmgFightingSpirit ②受击不可出招铁则让倒地期施法静默拒绝（测试须等 Normal）③资源计数 AddFighter 默认置 0 vs 弹匣装满入场 ④动态施放 CD 键≠解析后 def.RuntimeId——预检必须查按键技

## Phase 7 Batch 6 组合一致性矩阵（2026-09-03，本次）

- **验证重心迁移（用户 Batch 6 方向）**: 从「单技能可执行」→「技能组合与战斗状态交互正确」——12 项组合探针 IC01-IC12（格挡×伤害类型/格挡锥冲突/抓取者死亡释放/破隐时序/召唤物×主人状态/反射弹回击/DoT×倒地×死亡/蓄力打断/可控弹转向再碰撞/CD 重置连发/共鸣链边界/全机制混战 1500T 恢复重放）
- **5 个真实缺陷修复（组合压力暴露——用户预判兑现）**:
  1. **Units 快照完全缺失（D02 违约）**——单位/部署实体不进快照，恢复后蒸发；补 Units 序列化 + _nextUnitUid
  2. **OrbTypeCounts 快照 capture=5/restore=6 游标错位**——restore 每 fighter 少读 1 槽累积错位 → 后续 fighter HP 全歪（IC12 恢复即死 1/3/4/5 号）；统一 6
  3. **投射物追踪字段未进快照**（Batch 5 引入 TargetId/HeadingQuantum 漏序列化）
  4. **输入缓冲 _inputBuffers 未进快照**（ADR-0010 缓冲窗是战斗状态——Down/取消期的缓冲指令恢复后丢失）
  5. **反射弹永不回击**——追踪弹反射后 TargetId 未重锁（弹体绕回追反射者）+ HeadingQuantum 未重置（转弯弧 R=v/ω≈12.7m 追不上直线回程）+ Reflected 事件 VictimId 自指（OwnerId 转移后才读）——三连修：重锁新目标+朝向直设+事件改记 oldOwner
- **UnitSpec 单源化**: BuildFromSkillDef 静态派生（cast 与快照恢复共用；快照只存 AttackDef.RuntimeId，恢复重建）
- **DDQ-B6-1 设计冲突固化（IC02）**: 格挡锥判定体（BLA_T1_002 dmg=0 cone）先于来袭攻击命中 → 0 伤害命中触发 §4.3 技能中断 → **完美格挡/弹刀路径被锥体先手屏蔽**。IC02 固化当前行为待裁定（= CC 审计遗留「①格挡锥 dmg=0 判定体意图」的实证）
- **测试基础设施三教训（全部转纪律）**: ①跳 Tick 冻结物理时间线（exec offset 按步进数推进——Launch 悬停/ActEnded 顺延）②Command 默认 TargetTick=0 → ReplayRange 按 TargetTick 分组重放会全空转（SG09/SG16 历史通过均为假阳性——已改为全量日志打戳重放并在恢复点后补真实指令）③命中伤害含武器重量/部位加成——探针 HP 校准须实测
- **测试 169/169**（+IC01-IC12 12 探针；SG09/SG16 重放路径加固）；双门禁 PASS

## Phase 7 Batch 7 上半段 Snapshot/Restore/Replay Integrity Closure（2026-09-03，本次）

- **DDQ-B6-1 裁定落地（Guard Resolution）**: 格挡判定体按语义来源（def.Guard is not null）在 SweepCombat 过滤——不进入普通命中链（不产生 Hit/Interrupt/伤害），**禁以 dmg==0 数值判定**（裁定原文）；真实防御在攻击帧命中格挡者时经 Guard/Perfect Guard 路径化解（HitResolve guardExec 分支不变）。**弹刀链恢复**: IC02 改写验证 Parry→弹刀 20f→反击窗 15f→免费取消上挑→命中弹刀者全链
- **实体关系转换类别审查**（Batch 6 裁定「不大规模重构，类别审查」）: 法术反射（ReflectTicks+Projectile.OwnerId/TargetId/Heading）/抓取（GrabbedBy）/召唤物（Units 全字段+Spec 单源重建）/复制技（CopiedSkillUids）/可控弹/资源槽/输入缓冲/Buff 域/CD——**全部已进快照域，无新缺口**
- **Integrity 压力测试（IT01-IT03，Batch7IntegrityTests）**:
  - IT01: **3000T 全机制混战 × 5 次链式 restore × 全量打戳日志重放**——每段末态与权威同 tick 快照逐位一致 + 每次恢复自反一致
  - IT02: 重放真实性证明——恢复后重放段消费真实指令（replayedCommands>0）且产生 SkillCast≥3/Hit 事件（防 SG09/SG16 式假阳性空转）
  - IT03: 实体关系跨恢复延续——反射弹 OwnerId/TargetId/Heading 快照恢复后继续追击并命中原攻击者
- **核心承诺达成**: 复杂战斗状态（含实体关系转移）经长时运行、多次恢复、真实重放后逐位一致——Determinism Contract/Snapshot/Replay 无退化
- **测试 172/172**（+IT01/02/03；IC02 改写为 Guard Resolution 版）；三门禁 PASS（math/deps/balance）

## Phase 7 Batch 7 下半段 Partial 收口 + 语义裁定文书（2026-09-04，本次）

- **Partial 60 行证据化收口（IT04，Batch7IntegrityTests）**: PF02 关键词口径（Batch 2 前）的 partial 行逐行独立世界实测——**60/60 全部可执行、零静默、零异常**。「partial」分类整体过时（SUM 单位 12/THF 陷阱 5/PRI 治疗 7 等早已被 Batch 2-6 路由）
- **status 解析收口（数据驱动，无新机制）**: 「技能封印:Ns」/「水牢」→ Silence（GDD §7.3 技能封禁）；「僵直:Ns」→ Stun（含粘合时长「僵直0.3s」拆分）；「冻结@N%」→ Freeze（几率）；「paralysis@N%」格式（ParseChance 统一提取）；**Cleanse 原语**——「驱散全部异状态」/「解除被嘲讽」→ CleanseDebuffs（净化/静心符，施法时清除自身全部异常）
- **残留 13 行 = DDQ-B7 白名单固化**（IT04 断言：新出现未路由状态即回归失败）: 拉拽×3（悬磁炮/勾魂/死亡之门）/震地波（背摔）/截脉四围剥夺/全异常满值/对敌削攻削速/分身×2/操纵目标移动/冻结值+50/打断攻击/反射法术冗余 token
- **语义裁定文书（DDQ-B7——WIT Flight 与 Zone 生命周期，先裁定后编码）**:
  - **WIT 飞行（DDQ-B5-6）**: 已知=GDD §14.4.3「飞行 4s(+1s)、高度上限 6m、飞行中被击中→击坠（伤害封顶 1200）」+ 空袭俯冲「飞行中俯冲」+ 扫把冲刺「可空中」。**缺口=进入触发**（跳跃延展？冲刺进入？独立键？）。实现选项: A) 新 FighterState.Flight+独立域（最忠实，动状态机）B) 通用 FlightTicks 域+重力免除（最小，可被雷鹰等飞行单位复用）C) 纯 WIT 签名。**待裁**: 触发入口 + 方案 A/B/C
  - **Zone 生命周期（DDQ-B5-7）**: 现状=带 hitbox 的 zone/stance act=Ns 承载实体存在期→施法者锁身 N 秒。证据=念气罩「耐久2000」/魔界之花「受火伤+50%」→ zone 是**独立实体**（耐久=DeployHp）→ 建议裁定: zone 类走 Deploy 实体路径（DeployKind 扩展 Zone）+ act 重解释为实体存在期 + 施法者 2T 判定即收（与纯 buff 解耦同构）；channel 类 4 行单独语义
- **测试 173/173**（+IT04 证据基线；残留白名单断言=新未路由状态即回归失败）；三门禁 PASS

## DDQ-B5（Batch 5 新登记，待用户裁定）

1. BMG 炫纹三档（大中小——连击数定档）系统未实现
2. 炫纹增益: 无属性(移速)/光(攻速)/暗(暗伤)域 Sim v1 缺失——消耗但不施加；同型多枚=本次发射重置 N×枚（不跨发射叠加）
3. 0 炫纹时炫纹发射基线弹照常（增益语义待设计）；炫纹 30s 存在期（衰减）未实现
4. ROG 复制技「效果/次数随等阶」未实现；重复记录=刷新位置（环形覆盖最旧）；无记录时按键技按数据执行（counter 架势）
5. SPF 属性弹切换的弹种附着效果（冰弹射击冰冻减速等）未接入射击链；空匣干火=静默失败
6. WIT 飞行触发入口 GDD 未明（扫把掌握只写「飞行4s」无进入机制）——留后续批次，不猜
7. zone/stance 生命周期与 act 耦合（念气罩 30s 等带 hitbox buff）——本轮仅解耦纯 buff，带 hitbox 的待专项

## DDQ-B4 遗留（Batch 4 登记，①②已裁定落地，余项待裁定）


1. 嗜血奋战 armor `SSA:24-244f` vs GDD「全程霸体」（20s=1200f → 应为 24-1224f）——CSV 修正待裁定
2. 狂暴（BER_T3_004）Sim 域数值缺失（力量/攻速/异抗无 Sim 映射）+ 技能变异（招式变形）——CSV 补数据后实现
3. KNI 八美德强化幅度 CSV 未给出——强化乘区待数据（CD 重置已实现）
4. SBL 连放语义 v1: 施法读上一手累计层（本次 +1 归后效）；同把重放回 1 档；非波动插入不清层；无衰减——设计可复核
5. 自伤不可致死（钳 1 HP）——设计待确认
6. **buff 类技能 act=持续时长 → 施法锁死整个持续期**（41 个 buff 技共性；嗜血奋战锁 20s）——是否改短 active + 状态承载持续
7. 后续批次技能池: SRP 枪术精通（「射击类」分类歧义+「级」语义）、ROG 以牙还牙（动态施放）、WIT 扫把掌握（Flight 原语）、SPF 弹匣系、BMG 炫纹发射

## 待处理（下一阶段优先序）

1. Batch 5 签名: SPF.Ammo（弹匣资源已预留）+ BMG.Orbs 收尾（炫纹发射弹）+ WIT.Broom（Flight 原语）+ ROG.Mirror（事件回溯+动态施放）
2. Partial 54 行随 UnitSystem(14)/Deploy(9)/签名可控语义(8)批次收口
3. 数据裁定: DDQ-B4 七项 + GBL_T2_001 a200、冻结@P% 中文别名 2 行、OQ-2/OQ-13 既有 3 行、CC 审计 4 项 Design Decision、MF-5 heal 记法、DDQ 三项（PRI 回蓝基线/GAN 直量判读/ally 目标选择）
4. 规格歧义请设计确认: 软推挤范围（已按 GDD 解释）；Hitstop 归属（按 ADR-0009 表现层）
