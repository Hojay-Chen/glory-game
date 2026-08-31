# Active Session — BMG 白盒原型（GDD §29 Prototype 阶段）

- **日期**: 2026-08-31
- **概念**: 《廿四争锋》战斗地基白盒验证（战斗法师单职业）
- **路径**: Engine — Godot 4（无头优先；实机手感层待用户在 Windows 侧运行）
- **假设**: 战斗法师标准浮空连（天击起浮→矛击衔接→技能取消→刷新浮空）产生「连得上、但受五道闸门递减压力」的荣耀式体验。可测信号：①§14.1.7 三条连招模板逐帧复现（0 帧误差）②穷举连段下五道闸门 0 违规 ③真人手感投票 ≥70%（待实机）
- **最危险假设**: launch_v 7.5 + 浮空衰减 ×0.8ⁿ + cancel_min_tier 在真实 60fps 下的手感成立性
- **范围**: 平地竞技场 / 胶囊人×2 / BMG 全技能（读 skills.csv）/ 移动跳跃翻滚受身 / 浮空硬直倒地撞墙状态机 / 取消窗 / 五道闸门 / 控制值 / 无头审计器
- **明确砍掉**: 其余 23 职业 / 武器三选一（固定银月枪）/ 网络 / UI / 存档 / 美术 / 音频
- **当前阶段**: Phase 6/7 — 无头审计完成，待实机手感层
- **无头审计结论（2026-08-31）**: 帧一致性 20/20；闸门 0 违规（BMG 穷举 6000 节点，峰值 27.9% HP，最长 1.6s）；假设1 部分成立（模板 2/4 断链：F1 浮空四连刺空气窗不足、F2 标准起手末端扫地缺失）；数据发现 F3（吹飞硬直<后摇）/F4（幻影龙牙 active 12f<15f）；实现修复 F5。报告：prototypes/bmg-whitebox/AUDIT-REPORT.md
- **v0.3.7 裁定已落地（2026-08-31）**: F1 launch_v 7.5→9.0（三刺可复现，四刺差 9f 待二轮裁定：空中快刺形态/接受三刺）；F2 改模板（圆舞棍>强龙压 双扫地收尾，T2b 验证 ✓）；F3 反转定义（浮空吹飞=天击>落花掌，T5 验证 ✓）；F4 幻影龙牙 active 12→15（CSV+GDD 同步）；Sim 补 §4.2 普攻取消→技能
- **Tech-Architecture v1.0 已完成（2026-08-31，commit c9af006）**: docs/architecture/architecture.md——TR 基线 45 条/五层架构（Core 纯 C# 引擎无关+Headless 纯 .NET 服务器）/模块所有权/五数据流（用户三修正：Hitstop 仅表现层、网络补偿与 Hitstop 无关、Rollback 幂等契约）/10 条必建 ADR。TD 自审 APPROVED WITH CONDITIONS（ADR-0001 定点化须先行）。引擎四件套落地：CLAUDE.md(项目级, Godot 4.3+C#+GdUnit4Net)+technical-preferences+engine-reference+review-mode lean
- **ADR-0001 Accepted（2026-08-31）**: docs/architecture/adr/ADR-0001-deterministic-simulation.md——Q32.16 统一定点/MulShift RoundHalfEven 唯一舍入/预计算系数表禁 Math.Pow/容器纪律/Per-Stream Counter RNG 固化/四层时间模型/取整政策 P-1~P-3/Snapshot 完备清单/Determinism Contract+10 违约项/T1-T12 测试要求。自审 7/7 通过。TD 编码前置条件已解除
- **ADR-0002 Accepted（2026-08-31，cfc61e3）**: docs/architecture/adr/ADR-0002-data-pipeline.md——Source-of-Truth 六层表/Compiler 九段管线/确定性八维度/One Compiler Two Emitters（JSON 承载 hash，.tres 隔离 Godot UID 非确定性+启动 re-hash 不变式）/Core 零 IO 构造注入/四层校验 fail-fast/数据问题 15 项登记四类。实测新发现：hitbox 17 种 kind（58 行超字典）、status ~40 种自由文本、class-base 13 列 vs README 12 列。自审 15/15
- **下一步**: ①ADR-0003 事件协议 → 0009 Tick 循环 → 0004 网络 ②OQ-2/4/5/6 数据裁定 + S-1/S-2/S-7/S-8 字典补录待用户 ③F1 二轮裁定仍挂起
- **裁定记录**: 用户 2026-08-31 选定 Engine/Godot 4 + 暂不实机先无头验证
