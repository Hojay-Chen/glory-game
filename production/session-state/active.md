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

## 待处理（下一阶段优先序）

1. **批次 2 扩量**: MF-2 deploy / MF-4 ally / MF-5+7 heal 通道三个小原语先行 → Partial 54 行 + 剩余 Routed 行并行；C 类 60 技签名插件体按职业批次
2. Partial 54 行随 UnitSystem(14)/Deploy(9)/签名可控语义(8)批次收口
3. 数据裁定: GBL_T2_001 a200、冻结@P% 中文别名 2 行、OQ-2/OQ-13 既有 3 行、CC 审计 4 项 Design Decision、MF-5 heal 记法
4. 规格歧义请设计确认: 软推挤范围（已按 GDD 解释）；Hitstop 归属（按 ADR-0009 表现层）
