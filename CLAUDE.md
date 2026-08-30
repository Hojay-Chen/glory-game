# 《廿四争锋》(Project ARENA·24) — 项目指令

> 本文件是 glory-game 项目级 CLAUDE.md。Workspace 级共享文档（`../CLAUDE.md`）与本游戏无关时以其为准。

## 项目一句话

3D 竞技场动作对战游戏，**完全复刻《全职高手》「荣耀」战斗机制**（24 职业/技能取消/浮空连招/武器系统），其余网游玩法全部不做（无养成/副本/经济），地图只有一张竞技场。

## 文档体系

| 文档 | 内容 | 状态 |
|------|------|------|
| `docs/GDD-Gameplay-v0.1.md` | 玩法总案（内部版本 v0.3.7） | ✅ |
| `docs/skill-spec/` | 技能规格书：skills.csv 487 行 = 唯一数值源 + 字段字典 36 列 | ✅ v0.1 (+v0.4/v0.4.1 补丁) |
| `docs/weapon-spec/` | 武器规格书：weapons.csv 73 行 + D12 规则级校验 | ✅ v0.1 |
| `docs/balance-sheet/` | 平衡账本：class-base.csv 25 职业 + balance_audit.py | ✅ v0.1 (PASS) |
| `docs/复刻对照审计-v1.md` | 原著机制逐条对照 | ✅ v1+v2 |
| `docs/reference/荣耀职业介绍-ycyoc-wiki.md` | 原著考据基准 | ✅ |
| `docs/architecture/` | 技术架构（本目录由 /create-architecture 产出） | 🚧 进行中 |
| `prototypes/bmg-whitebox/` | BMG 白盒原型（Godot 4.3 GDScript，仅原型不迁移） | ✅ 无头层 concluded |

## 技术栈

- **Engine**: Godot 4.3 stable
- **Language**: C# (.NET 8+, primary), C++ via GDExtension (native plugins only)
- **Build System**: .NET SDK + Godot Export Templates
- **Asset Pipeline**: Godot Import System + custom resource pipeline
- **Testing**: GdUnit4Net（无头可跑，战斗逻辑测试的主载体）

## 工作铁律

1. **先读 GDD**：任何设计/实现前读 `docs/GDD-Gameplay-v0.1.md` 的 §1.8 对照表与附录 A 设计决策清单（D01–D37）；新设计不得违反已定决策（待评审项 D07/D20 须先裁定再实现）
2. **CSV 唯一数值源**：战斗数值只在 skills.csv / weapons.csv / class-base.csv 改，改后必须重跑对应校验/审计（tools/validate_*.py、tools/balance_audit.py）；程序代码出现魔法数值 = QA 拒收（GDD §28.1）
3. **数据驱动**：技能帧数据以 Simulation Tick 为时间单位（60 Tick/s），0 帧误差确定性（§28.4）
4. **原型隔离**：`prototypes/` 代码永不迁入生产、生产永不引用 prototypes；生产实现从零重写（C#）
5. **IP 合规**：复刻机制不抄 IP——不用原作角色名/战队名/专有道具名（千机伞 → 万象伞）

## 引擎版本参考

@docs/engine-reference/godot/VERSION.md

## Engine Specialists

- **Primary**: godot-specialist
- **Language/Code Specialist**: godot-csharp-specialist (all .cs files)
- **Shader Specialist**: godot-shader-specialist (.gdshader files, VisualShader resources)
- **UI Specialist**: godot-specialist (no dedicated UI specialist — primary covers all UI)
- **Additional Specialists**: godot-gdextension-specialist (GDExtension / native C++ bindings only)
- **Routing Notes**: Invoke primary for architecture decisions, ADR validation, and cross-cutting code review. Invoke C# specialist for code quality, [Signal] delegate patterns, [Export] attributes, .csproj management, and C#-specific Godot idioms. Invoke shader specialist for material design and shader code. Invoke GDExtension specialist only when native C++ plugins are involved.

### File Extension Routing

| File Extension / Type | Specialist to Spawn |
|-----------------------|---------------------|
| Game code (.cs files) | godot-csharp-specialist |
| Shader / material files (.gdshader, VisualShader) | godot-shader-specialist |
| UI / screen files (Control nodes, CanvasLayer) | godot-specialist |
| Scene / prefab / level files (.tscn, .tres) | godot-specialist |
| Native extension / plugin files (.gdextension, C++) | godot-gdextension-specialist |
| General architecture review | godot-specialist |

## Git 规范

- Commit 消息：中文描述，格式 `类型: 描述`（feat/fix/docs/refactor）
- 白盒原型结论回填 GDD 时，GDD 尾注追加版本条目（v0.3.x 惯例）
