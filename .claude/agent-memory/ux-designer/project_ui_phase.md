---
name: project-ui-phase
description: glory-game 于 2026-10-11 进入 UI/建模阶段；UI 规格的权威文件与前置阻塞
metadata:
  type: project
---

glory-game 于 **2026-10-11** 按用户要求转入 **UI / 建模批次**（技能侧暂缓），未决问题统一登记在
`docs/OPEN-ISSUES.md` 的 F 组（UI-01..UI-05 / ART-01..03）。

**UI 规格权威文件**：`docs/ux/ui-spec-v1.md`（本次由 ux-designer 产出）。
**Why**：本作只有战斗与竞技，界面即「选职业/武器 → 对战 HUD → 结算 → 暂停」闭环，无养成/经济屏。

**How to apply（未来做 UI 时先记住）**：
- 键位**以代码为准**（`src/Arena.Infra/Input/ClassBindings.cs` + `InputEdge.cs`：J 普攻 / K-L-I-O-P 档位技 /
  U 格挡 / Space 跳 / Shift 翻滚 / WASD / R 重开 / Esc）。**ADR-0010 的键位已过时**（数字键/鼠标左键/Alt/Tab），
  是本阶段一个真实阻塞，需 errata。
- 布局规格给到 1280×720 像素数值；数据源 = `class-base.csv` / `weapons.csv` / `skills.csv`（界面禁止内联数值）。
- 引导条判定按 `skills.csv` 的 `type==channel`（恰好 4 行，吻合 SK-09），**不是** `channel` 列（45 行）。
- 待裁定项（见 spec §7）：是否本阶段做「技能加点 36 点」（GDD §18.2 有、简报未列）、单局时长 60s vs GDD 180s。

相关：[[user-role]]
