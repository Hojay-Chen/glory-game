# Technical Preferences — 《廿四争锋》

## Engine & Language
- **Engine**: Godot 4.3 stable（白盒原型已无头验证）
- **Language**: C# (.NET 8+, primary)；GDExtension 仅限原生插件（非项目语言）
- **Testing**: GdUnit4Net（支持无头运行；白盒的帧一致性/闸门穷举审计将迁移为 C# 测试）

## Naming Conventions (C#)
- Classes: PascalCase（`PlayerController`）—— Godot 源生成器要求 `partial`
- Public properties/fields: PascalCase（`MoveSpeed`, `JumpVelocity`）
- Private fields: `_camelCase`（`_currentHealth`, `_isGrounded`）
- Methods: PascalCase（`TakeDamage()`）
- Signal delegates: PascalCase + `EventHandler` 后缀（`HealthChangedEventHandler`）
- Files: PascalCase 随类名（`PlayerController.cs`）
- Constants: PascalCase（`MaxHealth`, `GravityConstant`）

## Input & Platform
- **Target Platforms**: PC (Steam)
- **Input Methods**: Keyboard/Mouse（主），Gamepad（部分支持，可重映射）
- **Primary Input**: Keyboard/Mouse（原著键鼠技能操作的基因；技能键位布局见 GDD §27）
- **Gamepad Support**: Partial（UI 导航 + 战斗操作映射，不做瞄准辅助差异）
- **Touch Support**: None
- **Platform Notes**: 全技能帧数据游戏内可查（信息对称，GDD §26.3）；键位全可重映射；色盲辅助默认开（GDD §27.2 设置项）

## Performance Budgets（用户 2026-08-31 裁定）
- **Combat Simulation**: 固定 60 Tick/s；单个 Simulation Tick = 16.667ms
- **所有技能帧数据以 Simulation Tick 为时间单位**
- **战斗逻辑与渲染帧率解耦**
- **相同输入和初始状态下，战斗帧结果必须达到 0 帧误差**（确定性 = 帧博弈成立前提，GDD §2.5.2 设计决策）
- 客户端目标渲染帧率 60 FPS，但渲染帧率不参与战斗时间计算

## Testing
- Framework: GdUnit4Net
- 战斗核心测试三件套（从白盒迁移）：帧数据一致性（0 帧误差）/ 连招保护闸门穷举 / 确定性验证（同种子同输入=同结果）

## Forbidden Patterns
（留空——随 ADR 与 control manifest 填充；已知红线：程序内魔法数值（§28.1）、随机数进入战斗结算路径（§2.5.2 无随机设计））

## Allowed Libraries
- Godot 4.3 内置 API only（不预填投机依赖；引入任何第三方库时在此登记并走 ADR）
- **zstd 压缩**（Replay Body，ADR-0005 §1；2026-09-01 登记）：候选 `ZstdSharp`（MIT，纯托管，跨平台一致）或 `ZstdNet`（P/Invoke 原生）——实现期选定其一并记录；压缩仅作用于 Replay Body 字节，不参与 dataVersionHash（hash 对未压缩语义流计算）

## Engine Specialists
见项目根 `CLAUDE.md` 的 Engine Specialists 节（C# 变体路由）。
