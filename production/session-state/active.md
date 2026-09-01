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

## 待处理（下一阶段优先序）

1. **ISignature/ISimContext**（ADR-0008）——60 C 类技 + 未路由语义载体；SimWorld 内部服务已按九原语形态暴露
2. 格挡+盾值+完美格挡（G-04/G-09）→ 3. 抓取+反击（G-08）→ 4. GdUnit4Net 迁移评估 → 5. 高台/坠落 TerrainSystem
6. 数据裁定: GBL_T2_001 a200、冻结@P% 中文别名 2 行、OQ-2/OQ-13 既有 3 行
7. 规格歧义请设计确认: 软推挤范围（走位重叠合法 vs L2 成对分离——已按 GDD 解释）；Hitstop 归属（ADR-0009 表现层 vs GDD §2.2.3）
