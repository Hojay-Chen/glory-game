# UI / 交互设计规格 v1 ——《廿四争锋》(Project ARENA·24)

> **用途**：本文件是**界面/交互实现规格**，面向程序员直接落地（Godot 4.3 `Control` 节点，非概念稿）。
> 所有布局给到 1280×720 视口下的**像素数值**；风格气质：竞技场 / 电竞转播（不参考、不搬运任何现有游戏 UI 素材）。
> **数据源**：`docs/balance-sheet/class-base.csv`（职业面板）、`docs/weapon-spec/weapons.csv`（武器）、
> `docs/skill-spec/skills.csv`（技能）。**界面不得内联数值**（GDD §28.1：代码出现魔法数值 = QA 拒收）。
> **版本**：v1（2026-10-11）。范围 = 主菜单 → 选职业 → 选武器 → 对战 → 结算 → 暂停/设置。
> 不含养成/副本/经济/商店（本作只有战斗与竞技，GDD §1.4）。

---

## 0. 与 GDD / 现状的关系（先读）

| 项 | 说明 |
|---|---|
| 视口 / 渲染 | 1280×720，`gl_compatibility`（`src/Arena.Client/project.godot`）|
| 现有屏幕实现 | `ClassSelectScreen.cs`（`CanvasLayer, Layer=10`）、`Hud.cs`（`CanvasLayer`）——本规格沿此模式（每屏一个 `CanvasLayer`）|
| 输入现状 | 键位在 `Arena.Infra/Input/ClassBindings.cs` + `InputEdge.cs`（`InputMapper.KeyIds`）；**本文档输入表以此为准**，ADR-0010 的旧键位与之冲突（见 §4.3）|
| 状态色 | `src/Arena.Client/FighterView.cs` 的 `Sync()` 状态色（见 §5 可访问性）|
| GDD 冲突处理 | 本文档凡与简报（主菜单→选职业→选武器）不符处，**以 GDD 为准并在 §7 列出**——最主要三条：**① 赛前"技能加点 36 点"步骤（GDD §1.6/§18.2）；② 入场演出（GDD §1.7）；③ 单局时长 180s（GDD §1.7/§20.1）** |

---

## 1. 设计令牌（Design Tokens）

### 1.1 颜色（十六进制；Godot 用 `Color("RRGGBB")` 或 `Color(r,g,b,a)`）

| 令牌 | 值 | 用途 |
|---|---|---|
| `scrim` | `#000000` α0.72 | 弹层/结算遮罩、暂停遮罩 |
| `panel_bg` | `#0E1219` α0.92 | 面板底 |
| `panel_border` | `#2B3543` | 面板描边（2px） |
| `text_primary` | `#F4F7FB` | 主文本 |
| `text_secondary` | `#A7B2C2` | 次要文本 |
| `text_muted` | `#6B7686` | 提示/说明 |
| `text_disabled` | `#4A525E` | 禁用 |
| `focus` | `#FFD166` | 焦点高亮框（2px）+ 光标 ▶ |
| `team0`（你/蓝方） | `#2E9BFF` | P0 身份色 / HP 填充 |
| `team1`（对手/橙方） | `#FF8A2B` | P1 身份色 / HP 填充 |
| `hp_lag` | `#FFFFFF` α0.45 | 掉血残影（滞后条） |
| `mp` | `#6E7BFF` | MP 条（双方同色，左右区分归属） |
| `stamina` | `#46C2A0` | 耐力条（双方同色） |
| `cd_overlay` | `#000000` α0.62 | 技能冷却遮蔽 |
| `insufficient` | `#E0503B` | MP 不足/不可用的边框强调 |
| `win` | `#FFC24B` | 胜利 |
| `lose` | `#8A93A3` | 失败 |
| `draw` | `#C7D0DC` | 平局 |

**档位边框色（技能图标外框，1–2px）**：BAS `#58C7E0` · T1 `#6B7686` · T2 `#4FA3E3` · T3 `#9B7BE0` · T4 `#E0A63B` · U `#E0503B`。

**伤害数字色（必须配文字徽标，不可仅靠颜色——见 §5）**：
普通 `#FFFFFF` · 背击 `#FF9F43`+徽标「背」 · 弱点 `#B07BFF`+徽标「弱」 · 治疗 `#4CD97B`+「+」 · 被格挡 `#8FB8D8`+文字「格挡」。

### 1.2 字号层级（Godot `AddThemeFontSizeOverride("font_size", N)`）

| 令牌 | px | 用途 |
|---|---|---|
| `F_Display` | 64 | 主菜单主标题 / 结算「胜利/失败」|
| `F_Title` | 40 | 屏幕大标题 |
| `F_H1` | 32 | 屏内标题 |
| `F_H2` | 24 | 分区标题 / 职业名 |
| `F_Body` | 18 | 正文 / 列表项 |
| `F_Caption` | 15 | 提示行、快捷键说明 |
| `F_Min` | **14** | **绝对下限**（任何文本不得低于此值，见 §5）|

**UI 缩放档**：`0.9 / 1.0 / 1.15 / 1.3`——实现为根 `Control` 的 `scale`（或统一乘子应用到全部字号与间距）；默认 `1.0`。

### 1.3 间距与形状

- 基准单位 **8px**；安全边距 **24px**（左右/上下）。
- 常用间距：`gap_8` / `gap_12` / `gap_16` / `gap_24`。
- 圆角 **6px**（`StyleBoxFlat.corner_radius_*`）；面板描边 **2px**。

---

## 2. 信息架构与状态流

### 2.1 状态机

```
        ┌──────────┐
        │   Boot   │
        └────┬─────┘
             ▼
        ┌──────────┐  设置   ┌───────────────┐
        │ MainMenu │◄───────►│ SettingsPanel │  (模态，覆盖主菜单/暂停)
        └────┬─────┘         └───────────────┘
             │ 开始对战
             ▼
   ┌────────────────────┐
   │ ClassSelect (P1 我) │──Backspace(步骤内)/Esc────┐
   └─────────┬──────────┘                          │
             ▼                                     │ 返回主菜单
   ┌────────────────────┐                          │
   │ ClassSelect (P2 敌) │──────────────────────────┤
   └─────────┬──────────┘                          │
             ▼                                     │
   ┌────────────────────┐                          │
   │ WeaponSelect(P1 我) │                          │
   └─────────┬──────────┘                          │
             ▼                                     │
   ┌────────────────────┐                          │
   │ WeaponSelect(P2 敌) │                          │
   └─────────┬──────────┘                          │
             ▼                                     │
   ┌────────────────────┐  (GDD 强制；简报未列)      │
   │ Build 加点 36 点    │   —— 见 §7 D-01         │
   └─────────┬──────────┘                          │
             ▼                                     │
   ┌────────────────────┐                          │
   │  MatchIntro 入场    │  (GDD §1.7；见 §7 D-02)  │
   └─────────┬──────────┘                          │
             ▼                                     │
   ┌────────────────────┐   Esc   ┌──────────────┐ │
   │      Battle        │◄───────►│   Paused     │─┘
   └─────────┬──────────┘  继续    │ (含设置/键位) │
             │ 胜负判定            └──────┬───────┘
             ▼                           │ 重开 / 换职业 / 主菜单
   ┌────────────────────┐◄───────────────┘
   │      Results       │
   └───┬────┬──────┬────┘
       │    │      └────────────► MainMenu
       │    └───────────────────► ClassSelect (P1 我)
       └────────────────────────► WeaponSelect (P1 我)  [同职业换武器再战]
```

### 2.2 状态转移表

| 源状态 | 触发 | 目标 | 条件/说明 |
|---|---|---|---|
| Boot | 装配完成 | MainMenu | 读完 `class-base.csv` / `weapons.csv` / `skills.csv` |
| MainMenu | 选「开始对战」 | ClassSelect(P1) | 默认我方 = 剑客（BLA） |
| MainMenu | 选「设置」 | SettingsPanel | 模态覆盖 |
| MainMenu | 选「退出」 | 退出 | `GetTree().Quit()` |
| ClassSelect(P1) | Enter 确认 | ClassSelect(P2) | 记录 `_playerClass` |
| ClassSelect(P2) | Enter 确认 | WeaponSelect(P1) | 用 `_playerClass` 过滤武器 |
| ClassSelect(P2) | Backspace | ClassSelect(P1) | 步骤内回退 |
| ClassSelect | Esc | MainMenu | 整段取消 |
| WeaponSelect(P1) | Enter | WeaponSelect(P2) | 记录 `_playerWeapon` |
| WeaponSelect(P2) | Enter | Build | 记录 `_enemyWeapon` |
| WeaponSelect | Backspace | 上一步 | |
| WeaponSelect | Esc | MainMenu | |
| Build | Enter（确认/自动满） | MatchIntro | 见 §7 D-01（本期可直通） |
| MatchIntro | 倒计时结束 | Battle | 3-2-1；镜头拉升；双方 2s 无敌（GDD §1.7）|
| Battle | 出手流程 `_PhysicsProcess` | Battle | 每物理帧收 Command → `SimWorld.Step` |
| Battle | Esc | Paused | **冻结 Sim 步进**（复用 `Selecting` 早退模式）|
| Battle | 胜负判定命中 | Results | `!alive0 \|\| !alive1 \|\| tick≥MatchLength` |
| Paused | 继续 | Battle | 解冻 |
| Paused | 重开 | MatchIntro | 同职业/武器再战 |
| Paused | 换职业 | ClassSelect(P1) | |
| Paused | 回主菜单 | MainMenu | |
| Paused | 设置 | SettingsPanel | 返回后回到 Paused |
| Results | R 再战 | MatchIntro | 同构筑 |
| Results | C 换武器 | WeaponSelect(P1) | 同职业 |
| Results | Esc 换职业 | ClassSelect(P1) | |
| Results | M 主菜单 | MainMenu | |

> **暂停实现要点**：`Paused` 只暂停 Sim 步进与表现帧，**不卸载**对局节点；`MatchRoot._PhysicsProcess` 顶部 `if (_paused) return;`（与现有 `if (Selecting) return;` 同构，零状态残留原则见 ADR-0009）。

---

## 3. 逐屏规格

> 约定：坐标为视口左上原点，单位 px。每条 `Control` 建议锚点：顶/左屏用 `anchor 0,0`，居中块用 `anchor 0.5,0.5`+`pivot`。

### 3.1 主菜单 MainMenu

| 区域 | 矩形 (x,y,w,h) | 控件 | 内容 |
|---|---|---|---|
| 背景 | 0,0,1280,720 | `ColorRect`/`TextureRect` | 竞技场渲染或渐变（ART 批次，先占位）|
| 主标题 | 0,132,1280,72 | `Label` `F_Display` | 「廿四争锋」居中 |
| 副标题 | 0,214,1280,32 | `Label` `F_H2` | 「竞技场对战」居中 `text_secondary` |
| 菜单列 | 460,300,360,180 | `VBoxContainer`(separation 12) | 见下 |
| └ 开始对战 | 460,300,360,56 | `Button` `F_Body` | 默认焦点 |
| └ 设置 | 460,368,360,56 | `Button` | 打开 SettingsPanel |
| └ 退出 | 460,436,360,56 | `Button` | 退出 |
| 页脚提示 | 0,660,1280,28 | `Label` `F_Caption` | 「↑/↓ 选择　Enter 确认」|

- **焦点/导航**：默认第 1 项；↑/↓ 循环（含环绕）；Enter / KpEnter / Space 激活；鼠标悬停即聚焦。
- **焦点样式**：`focus` 色 2px 描边 + 左侧 `▶`（8px）。

### 3.2 选职业 ClassSelect（改造 `ClassSelectScreen.cs`）

两步：步骤 1 选我方 → 步骤 2 选对手。**加宽为双栏**（列表 + 详情）。

| 区域 | 矩形 | 控件 | 内容 |
|---|---|---|---|
| 标题 | 0,30,1280,40 | `Label` `F_H1` 居中 | 步骤 1：「选择你的职业」；步骤 2：「你的职业：<名>　—　选择对手职业」|
| 步骤指示 | 1140,30,116,32 | `Label` `F_Caption` 右对齐 | 「步骤 1/2」|
| 列表栏 | 24,96,520,520 | `PanelContainer` | 见下 |
| └ 可见 9 行 | 每行 (24, 96+i*52, 520, 52) | `Label` `F_Body` | 文本见「列表行格式」|
| 详情栏 | 568,96,688,520 | `PanelContainer` | 见「详情栏」 |
| 页脚提示 | 0,648,1280,28 | `Label` `F_Caption` 居中 | 「↑/↓ 选择　Enter 确认　Backspace 返回」|

**列表行格式**（等宽对齐，`ClassSelectScreen` 现用 `,-4`/`,-8` 填充——保留）：
`▶ <class_id>` (x=32, w=64) · `<class_name>` (x=104, w=180, `F_Body`) · `<branch>` (x=292, w=120, `text_secondary`) · 末行「【随机】」。
- 选中行：`team0` 半透明底 `#2E9BFF` α0.18 + 左侧 `▶` 用 `focus` 色。
- 滚动窗口 = 9 行（对齐现有 `Window=9`）；光标居中，边界夹紧（现逻辑可保留）。

**详情栏（新增，随光标实时刷新）**：

| 块 | 矩形（相对详情栏） | 字号 | 内容 |
|---|---|---|---|
| 职业名 | 16,16,656,36 | `F_H2` | `class_name` |
| 系别·护甲 | 16,56,656,24 | `F_Body` | `<branch>　<armor>`　`text_secondary` |
| 定位三联 | 16,88,656,24 | `F_Body` | `<weapon_family>　射程 <range_band>　移速 <move_speed>` |
| 八维条 | 16,124,656,180 | `F_Caption` | `eight_dim` 逐项文字条（见下）|
| 技能预览标题 | 16,316,656,24 | `F_H2` | 「技能预览」|
| 技能预览列表 | 16,348,656,156 | `F_Caption` | 该职业 T1–U 各档代表技，每行：`<档位徽标> <技能名>　MP<n>　CD<ns>` |

八维条：`eight_dim` 形如 `攻距A− 机动A 爆发A− ...`，正则出「维度+评级」；每项渲染为
`Label(维度,w=56) + 评级徽标(字母 A/B/C/D，色: A#4CD97B B#4FA3E3 C#E0A63B D#E0503B) + 微条`。
评级→条长映射：A=100% B=75% C=50% D=25%（`+`/`−` 各 ±15%）。

**焦点/导航**：↑/↓ 移 1；PgUp/PgDn 移 9；Home/End 首尾；Enter 确认；Backspace/← 步骤回退；Esc 回主菜单。

**数据源**：`class-base.csv`（`ClassDefParser` 已解析 `class_id/name/branch`）；技能预览取 `skills.csv` 按 `class_id` 过滤、按 `tier`（T1<T2<T3<T4<U）取每档序号最小者，最多 5 行。

### 3.3 选武器 WeaponSelect

每职业 3 把（制式 / 特化 / 精工），选 1；**散人（UNS）只有 1 把「万象伞」，标记「不可更换」**（GDD §18.3 / D13）。

| 区域 | 矩形 | 控件 | 内容 |
|---|---|---|---|
| 标题 | 0,30,1280,40 | `Label` `F_H1` 居中 | 「选择武器　—　<class_name>」|
| 步骤指示 | 1140,30,116,32 | `Label` `F_Caption` | 「步骤 1/2」|
| 武器卡列 | 24,104,520,420 | `VBoxContainer`(sep 16) | 每卡 520×128（3 张 = 420 高）|
| └ 卡片 | 24,104+i*144,520,128 | `PanelContainer`+子 | 见「卡片格式」|
| 详情栏 | 568,104,688,420 | `PanelContainer` | 见「详情栏」|
| 页脚提示 | 0,648,1280,28 | `Label` `F_Caption` | 「↑/↓ 选择　Enter 确认　Backspace 返回」|

**卡片格式**（`weapons.csv` 字段）：

| 子块 | 相对卡片 | 字号 | 内容 |
|---|---|---|---|
| 武器名 | 16,12,300,28 | `F_H2` | `weapon_name` |
| 档位徽标 | 372,14,132,24 | `F_Caption` 右对齐 | 制式/特化/精工（色：`#6B7686`/`#4FA3E3`/`#E0A63B`）|
| 参数行 | 16,46,488,22 | `F_Body` | `攻速 <atk_spd>×　攻修 <atk_mod>　射程 <range_m>m　重量 <weight>` |
| 特性行 | 16,72,488,44 | `F_Caption` | `trait`（`-` 时显示「—」）|

**详情栏**：

| 块 | 相对 | 字号 | 内容 |
|---|---|---|---|
| 普攻模组 | 16,16,656,72 | `F_Body` | `basic_attack`（含段数/判定形状）|
| 特性 | 16,96,656,120 | `F_Caption` | `trait`；`trait_rules` 逐条解析为「旧值→新值」列表 |
| 备注/出处 | 16,224,656,180 | `F_Caption` `text_muted` | `notes`（含 `GDD-GAP`/`TODO` 标记原样显示，便于评审）|

**焦点/导航**：同上；散人时列表只有 1 项、Enter 直接确认。

**数据源**：`weapons.csv` 按 `class_id` 过滤（每职业 3 行；UNS 1 行）。

### 3.4 对战 HUD（Battle HUD）

> 保留现有 `Hud.cs` 的**左对齐血条几何**；本规格**修正敌方血条为镜像对称**（现为 774，右留白 106 ≠ 左 30，见 §7 D-06）。
> 全部条为 `ColorRect`（或 `TextureProgressBar`）；建议改为 `TextureProgressBar` 以支持残影与径向，但数值坐标不变。

**顶部资源区（双方镜像）**：

| 元素 | P0 我方 (x,y,w,h) | P1 对手 (x,y,w,h) | 说明 |
|---|---|---|---|
| 职业名/标签 | 30,2,400,18 | 850,2,400,18 | 我方左对齐「你 · <名>」；对手右对齐「对手 · <名>」|
| HP 底框 | 26,22,408,28 | 846,22,408,28 | `panel_border` |
| HP 残影 | 30,24,400,24 | 850,24,400,24 | `hp_lag`（滞后条）|
| HP 填充 | 30,24,⇐比值,24 | 850,24,⇐比值,24 | `team0` / `team1`；**P1 加 45° 斜条纹**（色盲安全，§5）|
| MP 条 | 30,52,400,10 | 850,52,400,10 | `mp` |
| 耐力条 | 30,66,400,8 | 850,66,400,8 | `stamina` |
| HP 数值 | 30,80,400,18 | 850,80,400,18 | 「<hp>/<hpmax>」（`F_Caption`）|

- **HP 填充方向**：P0 从左向右；P1 从右向左（镜像，均为「贴在屏幕外侧」）。
- **掉血残影**：受击后残影条 0.3s 内线性收缩到当前值（`hp_lag`）。
- **比赛计时**：590,14,100,32 `Label` `F_H2` 居中，`MM:SS`（`3600 - tick` 换算）；<30s 时用 `insufficient` 色。

**技能栏（我方，底部左侧）**：

| 元素 | 矩形 | 说明 |
|---|---|---|
| 槽行 | 30,628,440,64 | 7 槽 `HBoxContainer`(sep 8)，每槽 56×56 |
| 槽顺序 | J(普攻) · K(T1) · L(T2) · I(T3) · O(T4) · P(U) · U(格挡) | 顺序与 `ClassBindings.TierSlots` 一致；无该档技能则**空槽**（显示「—」置灰）|
| 图标 | 56×56 圆角6 | 无美术资源期：`#1A1F28` 底 + 技能名前 1–2 字（`F_H2`），外框用**档位色** |
| 键位标签 | 槽右下 (36,36,18,18) | `F_Caption`「J/K/…/U」|
| 技能名 | 槽下 (0,58,72,20) 居中 | `F_Caption`，超宽省略号 |
| CD 转圈 | 覆盖槽 | 径向扇形（`TextureProgressBar` `fill_mode=FILL_CLOCKWISE` 或 `Control._Draw`+`draw_arc`）；`cd_overlay`；剩余秒数居中 `F_H2` |
| MP 不足 | 槽整体 | 边框 `insufficient` + 图标 α0.5 |
| 高亮 | 命中/可取消窗 | 槽外发光（`focus` 色，0.1s）——**表现线索**可选（P2）|

> **CD 数据依赖**：需要每槽技能当前 CD 剩余（tick）。建议 Sim 暴露 `FighterStateData` 或并行只读结构提供每槽剩余 CD；若暂缺，则表现层由「施放事件 + `cooldown_s`」自行累计（见 §7 D-04）。

**连段计数（Combo）**：两条。
- 我方连段：590,86,100,44 `Label` `F_H2` 居中，`<n> HIT`（`focus` 色）；≥7 击显示 `text_muted`（连击递减 GDD §2.5.2）。
- 对手连段：镜像于 590,150（`insufficient` 色），位置错开避免重叠。
- 归零条件：连段中断（脱离/落地/超时）→ 隐藏。

**引导条（Channel Bar，SK-09 / GDD D10）**：

| 元素 | 矩形 | 说明 |
|---|---|---|
| 引导条 | 520,596,240,14 | `TextureProgressBar`；仅当活跃技能 `type == channel` 时显示 |
| 底框 | 516,592,248,22 | `panel_border` |
| 填充 | 等比 | 时长 = 该技能 `active_f`（4 个 channel 技：寒冰降雨 2.5s / 火焰喷射 1.5s / 格林机枪 2.5s / 神佑之光 4s）|
| 标签 | 520,570,240,22 居中 | `F_Caption`：技能名 + 剩余秒 |

> **判定口径**：以 `type == channel` 为准（skills.csv 恰好 4 行，吻合 SK-09「4 技」）。**不要**用 `channel` 列（第 27 列，45 行为 1，含义不同）——见 §7 D-07。

**伤害数字（Damage Popup）**：
- 世界空间 `Label3D`，挂命中点（复用 `FighterView` 命中特效点，`_effectRoot`）。
- 字体 `F_H2`（24），**向上升 0.6m / 0.5s**，末 0.2s 淡出。
- 变体与徽标：见 §1.1「伤害数字色」+ 徽标「背/弱/格挡」。
- 暴击不存在（GDD D02 确定性替代）——不要做暴击闪字。

**控制状态提示（可选 P1）**：受控单位（Hitstun/Launch/Grabbed）头顶显示状态徽标（见 §5），与 `FighterView` 状态色双通道。

### 3.5 结算 Results

| 区域 | 矩形 | 控件 | 内容 |
|---|---|---|---|
| 遮罩 | 0,0,1280,720 | `ColorRect` | `scrim` |
| 结果横幅 | 0,150,1280,80 | `Label` `F_Display` 居中 | 「胜利 / 失败 / 平局」（`win`/`lose`/`draw`）|
| 副标 | 0,232,1280,28 | `Label` `F_Body` 居中 | 「<我方职业> vs <对手职业>　·　<我方武器> vs <对手武器>」|
| 数据面板 | 380,286,520,236 | `PanelContainer` | 5 行两列（见下，行高 40）|
| └ 行 | 380+24, 286+24+i*40, 472, 40 | `HBox`: `Label`(标签,左) + `Label`(值,右) | 见下 |
| 按钮行 | 340,560,600,52 | `HBoxContainer`(sep 24, 居中) | 3×`Button` 200×52 |
| └ [R 再战] / [C 换武器] / [M 主菜单] | | `Button` `F_Body` | Esc=换职业（见 §4）|
| 页脚 | 0,660,1280,28 | `Label` `F_Caption` | 「R 再战　C 换武器　Esc 换职业　M 主菜单」|

**数据面板行**（全部来自 Sim 事件流累计，非硬编码）：输出伤害 · 承受伤害 · 最大连段 · 命中次数 · 对局用时。

> 当前 `Hud.ShowResult` 只显示一段文字 + 提示——本屏为**新增**（UI-04）。数据统计依赖 `SimWorld.Events`（`EventKind.Hit` 等）聚合，见 §7 D-05。

### 3.6 暂停 / 设置 Paused & Settings

**暂停面板**（覆盖 Battle）：

| 区域 | 矩形 | 控件 |
|---|---|---|
| 遮罩 | 0,0,1280,720 | `ColorRect` `scrim` |
| 面板 | 360,120,560,480 | `PanelContainer` |
| 标题 | 360,144,560,40 | `Label` `F_H1` 居中「暂停」|
| 按钮列 | 440,212,400,300 | `VBoxContainer`(sep 12)，6×`Button` 400×48：继续 / 设置 / 重开 / 换武器 / 换职业 / 回主菜单 |
| 页脚 | 0,648,1280,28 | `Label` `F_Caption`「Esc 返回 / 继续」|

> **注意**：`Esc` 在 Battle 中开暂停、在 Paused 中关闭（回到 Battle）；在菜单类界面中 `Esc` = 返回上一级。`Esc` **不再**在对局中直接「换职业」（现仅终局读取 Esc，改动安全——见 §7 D-03）。

**设置面板**（主菜单「设置」或暂停「设置」进入，同一组件）：

| 区域 | 矩形 | 控件 | 选项 |
|---|---|---|---|
| 面板 | 320,100,640,520 | `PanelContainer` | |
| 标题 | | `Label` `F_H1` | 「设置」|
| 分辨率 | 行 y=180 | `OptionButton` | 1280×720 / 1600×900 / 1920×1080 / 全屏（`DisplayServer.WindowSetSize`）|
| UI 缩放 | 行 y=240 | `OptionButton` | 0.9 / 1.0（默认）/ 1.15 / 1.3 |
| 色盲模式 | 行 y=300 | `OptionButton` | 关闭 / 红绿型（Deuteranopia）/ 蓝黄型（Tritanopia）/ 高对比 |
| 键位查看 | 行 y=360 | `Button` → 子面板 | 分组列表：移动 / 通用 / 技能 / 界面（见 §4）|
| 音量 | 行 y=420 | `HSlider`（占位） | 音频系统未建（EN-04）——先灰置 |
| 关闭 | 行 y=560 | `Button` | 返回来源界面 |

- 所有设置项即时生效；持久化到 `Save.Prefs`（ADR-0002 产物链）。
- 键位查看子面板：只读列表（**重映射 UI 留 P2**，见 §6）。

---

## 4. 输入映射表

### 4.1 键盘（**现状为准**——`ClassBindings.cs` / `InputEdge.cs`）

| 动作 | 键 | `KeyIds` 常量 | 角色 (role) | 语义 | 占用者 |
|---|---|---|---|---|---|
| 移动 | W/A/S/D | `W=87,A=65,S=83,D=68` | move | Held，8 向量化 | `InputMapper.MoveKeys` |
| 跳跃 | Space | `Space=32` | jump | Press 沿 | `ClassBindings` |
| 翻滚/受身 | Shift | `Shift=4194325` | roll | Press 沿 | `ClassBindings` |
| 普攻 | J | `J=74` | basic | Press 沿 | `ClassBindings` |
| T1 技 | K | `K=75` | skill | Press 沿 | `ClassBindings.TierSlots` |
| T2 技 | L | `L=76` | skill | Press 沿 | 同上 |
| T3 技 | I | `I=73` | skill | Press 沿 | 同上 |
| T4 技 | O | `O=79` | skill | Press 沿 | 同上 |
| U 档技 | P | `P=80` | skill | Press 沿 | 同上 |
| 格挡 | U | `U=85` | guard | Press=起姿 / Release=放（hold 型） | `ClassBindings` 兜底选 Guard/Hold 技 |
| 重开（终局） | R | — | — | `MatchRoot` 读 `Key.R` | `MatchRoot` |
| 换职业（终局） | Esc | — | — | `MatchRoot` 读 `Key.Escape` | `MatchRoot` ← **本规格改为「Battle 内 Esc=暂停」**|

**界面导航键**（所有菜单屏）：`↑/↓` 移动 · `←/Backspace` 返回 · `Enter/KpEnter/Space` 确认 · `Esc` 返回上级 · `PgUp/PgDn/Home/End` 列表快移。

> **冲突提示**：Skill 键 K/L/I/O/P 与格挡 U **已由 `ClassBindings` 占用且每局重算**——界面层**不得**再绑定这些键做别的事。`R` 在 ADR-0010 里是 BMG 炫纹键，但代码里 `R=重开`（见 §4.3）。

### 4.2 手柄（**规划**，当前代码未实现）

> 依 ADR-0010（手柄完整支持，GDD §1.1）。当前需覆盖动作 = 移动/跳/翻滚/普攻/T1–T4/U档/格挡 共 **10 个 Cmd 动作**。
> 摇杆死区 0.25 → 8 向量化（阈值表为编译器常量，两端一致，ADR-0010 §2）。

| 动作 | 手柄键 | 备注 |
|---|---|---|
| 移动 | 左摇杆（8 向） | 死区 0.25 |
| 跳跃 | A / ✕（Cross） | ADR-0010 定 |
| 翻滚/受身 | B / ○（Circle） | ADR-0010 定（受身方向判定在 Sim）|
| 普攻 | X / □（Square） | ADR-0010 定 |
| T1 | LB / L1 | |
| T2 | LT / L2 | |
| T3 | RB / R1 | |
| T4 | RT / R2 | |
| U 档技 | L3+R3 同时按 / Y+RB 组合 | （5 个技能槽 → 肩键优先）|
| 格挡 | Y / △（Triangle） | hold 型 |
| 暂停 | Start / Options | 新增（对应 Esc）|
| 界面导航 | 十字键 + A 确认 + B 返回 | 与键盘菜单导航等价 |

> **待裁定**：ADR-0010 原文把技能设为「数字键 1–0 / 十字键+肩键组合（10 槽）」，但本作现已废除携带上限、每职业技能不限于 10 且按**档位**绑键（T1–U 各一键）。手柄方案需随之调整为**档位键**（上表），**不再对应 10 个固定槽**。此调整需更新 ADR-0010（见 §7 D-03）。

### 4.3 ⚠️ ADR-0010 与代码的键位冲突（必须以代码为准并修订 ADR）

ADR-0010 §1（**状态 Accepted**）规定的默认键位与现实现**完全不同**：

| 动作 | ADR-0010 写 | 代码实际 | 冲突 |
|---|---|---|---|
| Basic | **鼠标左键** | **J** | ❌ |
| Skill 1–0 | **数字键 1–0** | **K/L/I/O/P**（档位）| ❌ |
| Jump | 空格 | 空格 | ✅ |
| Roll | Shift | Shift | ✅ |
| ForceCancel | **Alt（按住）** | **未实现** | ❌ |
| Lock | **Tab** | **未实现** | ❌ |
| OrbFire | **R** | R = **重开** | ❌ |
| Move | WASD | WASD | ✅ |

结论：**实现与界面以代码/本规格 §4.1 为准**；ADR-0010 需出一版 errata，把「10 槽 Loadout」模型改为「档位键」模型（因 §18.1 已废除携带上限），并补 `Guard`/`Pause` 动作。→ 见 §7 D-03。

---

## 5. 可访问性基线

> 每项功能必须通过（对应职责清单）：键盘可用 / 手柄可用（手柄待 §4.2 落地）/ 文字 ≥14px / 不依赖颜色单一通道 / 无 3Hz 以上闪烁 / UI 全分辨率可缩放。

### 5.1 字号与缩放
- **下限 14px**（`F_Min`）；所有屏幕遵守 §1.2。
- UI 缩放档 0.9–1.3；缩放后**不得**出现文本截断（列表项需按字号重排或省略号）。

### 5.2 对比度
- 正文对背景 ≥ **4.5:1**；大字号（≥24px）≥ **3:1**。
- `text_muted #6B7686` 在 `panel_bg #0E1219` 上仅约 4.0:1 → **仅用于说明性文本**，不得承载关键信息。

### 5.3 色盲安全（血条/状态色）

**问题**：现 `Hud.cs` 用绿 `(0.2,0.8,0.3)` vs 红 `(0.9,0.3,0.25)` 区分双方——**红绿型（Deuteranopia/Protanopia）几乎不可分**。
**规范**：
- 双方 HP **不再用红/绿**；改 `team0 #2E9BFF`（蓝）vs `team1 #FF8A2B`（橙）——红绿色盲下可分辨；蓝黄型下亦靠**第二条通道**。
- **第二通道**：P1（对手）HP 填充叠加 **45° 斜条纹**；P0 纯色。另有文字标签「你 / 对手」+ 职业名。
- 蓝黄型（Tritanopia）：蓝/橙可能接近 → 若用户在设置里选「蓝黄型」，`team0` 切 `#2E9BFF`、`team1` 切 `#D14BFF`（紫）；条纹通道始终保留。

**状态色（`FighterView.Sync()` 现用色）与改良**：

| 状态 | 现值 | 风险 | 建议（颜色 + 形状双通道）|
|---|---|---|---|
| Hitstun | `(1,0.75,0.4)` 琥珀 | 与 Launch/Act 亮度接近 | 保留色 + **头顶「硬直」图标** |
| Launch | 队色 `Lightened(0.35)` | 与 Act/正常仅亮度差 | 保留色 + **头顶上升箭头** |
| Act | 队色 `Lightened(0.2)` | 同上 | 保留色 + 无图标（攻击即自明）|
| Down | 队色 `Darkened(0.5)` | 低亮度难辨 | 保留 + **头顶「倒地」图标**|
| Grabbed | `(0.8,0.2,0.8)` 品红 | 品红/琥珀色盲接近 | 保留 + **头顶「抓取」图标** |
| Dead | `(0.15,0.15,0.15)` | 与场景暗部混淆 | 保留 + 名牌显示「KO」|
| Hidden | `(0.35,0.35,0.45)` | | 保留 + 半透明降低不透明度（潜行语义）|

> 结论：**任何状态不得只靠颜色**——统一叠一层头顶图标（`Label3D` 或 `TextureRect` billboard）；图标系统可复用 `FighterView._nameTag` 的 billboard 机制。

### 5.4 闪烁与动态
- 命中闪白（`_flash` 6/s 衰减）远低于 3Hz 阈值，安全。
- 新增任何 ≤3Hz 闪烁需在设置提供「减少动态」开关（P2）。

### 5.5 字幕/音频
- 本作无剧情对白 → 字幕需求 **N/A**；音频系统未建（EN-04），若未来加语音/音效提示，需同步加视觉等价物。

---

## 6. 实现优先级

### P0 —— 让「主菜单 → 选职业 → 选武器 → 对战 → 结算」闭环可玩
1. **主菜单**（开始/设置入口/退出）—— §3.1
2. **选职业改造**（列表 + 详情栏：职业名/系别/技能预览）—— §3.2
3. **选武器**（3 选 1，含散人特例）—— §3.3
4. **战斗 HUD 核心**：HP/MP/耐力 ×2（含镜像修正）、双方职业名、**技能图标 + CD 转圈**、连段计数、伤害数字 —— §3.4
5. **结算界面**（胜负 + 5 项基础数据）—— §3.5
6. **暂停**（Esc 插入：继续/重开/换职业/主菜单）—— §3.6
7. 输入映射固化（§4.1）+ 菜单导航键统一

### P1
8. **引导条**（SK-09，4 个 channel 技）—— §3.4
9. 设置内容（分辨率 / UI 缩放 / 色盲模式 / 键位查看）—— §3.6
10. 可访问性基线全量（血条改蓝/橙 + 条纹；状态头顶图标）—— §5
11. 手柄支持（§4.2）
12. 入场演出 MatchIntro（GDD §1.7）——见 §7 D-02

### P2
13. 伤害数字变体（背击/弱点徽标）与技能图标高亮
14. 键位**重映射** UI（现仅查看）
15. 复盘 / 回放 / 关键帧标注（GDD §1.7「复盘」）——暂缓
16. 技能加点 Build 面板（§7 D-01，待裁定是否本期）

> **P0 与 P1 的切割线**：P0 = 「能不能完整打一局并看懂结果」；P1 = 「好不好看、能不能无障碍玩、能不能用手柄」。**引导条**（P1）虽在简报点名，但它只影响 4 个技能的表现，不阻塞闭环，故列 P1 首位。

---

## 7. 与现状 / GDD 的差异清单（供裁定）

| ID | 差异 | 依据 | 建议 |
|---|---|---|---|
| **D-01** | **GDD 有「技能加点 36 点」赛前步骤**（§1.6 核心循环、§18.2），**你的简报流程未列**（选职业→选武器→对战）。per 铁律「以 GDD 为准」，本规格在流程中**保留 Build 状态位**（§2），但**未展开其 UI**（避免越界设计养成向面板）。 | GDD §1.6/§18.2 | **需你裁定**：本期是否实现加点？若否，Build 状态直通（等价 0 级构筑，GDD §18.5 底线可玩）|
| **D-02** | GDD §1.7 有「入场 30s / 2s 无敌 / 镜头拉升」演出，代码无此状态。 | GDD §1.7 | 列为 P1 `MatchIntro`（3-2-1 倒计时 + 镜头）；不阻塞 P0 |
| **D-03** | **ADR-0010（Accepted）键位与代码完全不符**（鼠标左键/数字键/Alt/Tab/R=炫纹 vs J/K-L-I-O-P/U/R=重开）。 | ADR-0010 §1 vs `ClassBindings.cs`/`InputEdge.cs` | ADR-0010 出 errata：改「档位键」模型 + 补 Guard/Pause；本规格以代码为准 |
| **D-04** | HUD 的 **CD 剩余**需要 Sim 按槽暴露剩余 tick；当前未见此输出。 | `Hud.cs` 只读 Hp/Mp/Stamina | 需 Sim/Presentation 增一个只读「每槽 CD 剩余」；或表现层按 `cooldown_s` 自行累计 |
| **D-05** | 结算数据（输出/承受/最大连段/命中/用时）需从 `SimWorld.Events` 聚合，当前无聚合器。 | `MatchRoot`/`Hud` | 新增轻量 Stats 聚合（Infra 层，可测）|
| **D-06** | 现 HUD 敌方血条 x=774（右边距 106）≠ 我方 x=30（左边距 30），不对称。 | `Hud.cs` | 改为镜像 30px 边距（§3.4）|
| **D-07** | **`channel` 两义**：`type==channel` 恰好 **4 行**（吻合 SK-09「4 技」）；但 `channel` **列**（第 27 列）有 **45 行** 为 1。 | `skills.csv` | 引导条**按 `type==channel` 判定**；`channel` 列语义需澄清（建议改名或在 README 明确）|
| **D-08** | GDD §1.7/§20.1 单局 **180s**，代码 `MatchLengthTicks=3600`=**60s**。 | GDD vs `MatchRoot` | 规格 UI 按 GDD 180s（计时条映射 03:00）；代码值待确认是否为 VS 期临时值 |
| **D-09** | GDD §1.1 列多模式（1v1/2v2/3v3/5v5/训练）；当前仅 **1v1 vs AI**。 | GDD §1.1 | 主菜单暂不做「模式选择」屏；预留入口（P2）|
| **D-10** | 现有 `ClassSelectScreen` 的「Enter 跳过整块界面（自选我方+随机对手）」捷径与分步导航并存——改造后需明确保留/移除。 | `ClassSelectScreen.Confirm` | 建议保留（快速开打），但需在页脚提示 |

---

## 附录 A：建议的 Godot 节点树（每屏骨架）

```
CanvasLayer (Layer=10)              # 每屏一个，与现有 ClassSelectScreen 同构
└─ Control (full rect, mouse_filter=STOP)
   ├─ ColorRect  "Scrim"             # 弹层/结算才加
   ├─ PanelContainer "Panel"         # StyleBoxFlat(panel_bg/panel_border/r6)
   │  └─ MarginContainer (margin 16)
   │     └─ VBoxContainer
   ├─ Label "Title"                  # AddThemeFontSizeOverride("font_size", F_*)
   └─ HBoxContainer/VBoxContainer "FocusList"   # 光标索引驱动（沿用 ClassSelectScreen 的手动 _cursor）
```

- **HUD 专用**：`Hud : CanvasLayer`，内含顶部资源区 `Control`、技能栏 `HBoxContainer`、引导条 `TextureProgressBar`、连段 `Label`（见 §3.4）。
- **焦点模型**：菜单类沿用**手动 `_cursor` 索引**（确定性、可测，与 `ClassSelectScreen` 一致），而非 Godot 自动 focus；按钮屏（主菜单/暂停）可用 Godot focus（`grab_focus()`）。
- **字体**：使用引擎默认字体（暂无自定义字体资源）；字号全部走 `AddThemeFontSizeOverride`，便于统一缩放。

## 附录 B：待办（进入实现的先决条件）

1. 修 ADR-0010 键位 errata（D-03）——**先做**，否则文档与输入实现各说各话。
2. 确认 `type==channel` 判定口径（D-07）。
3. 裁定加点步骤是否本期实现（D-01）——决定 §2 状态流是否保留 Build 屏。
4. 提供 Sim 只读「每槽 CD 剩余」与 Stats 聚合（D-04/D-05）。

---

*本规格不含战斗数值；一切数值以 `skills.csv` / `weapons.csv` / `class-base.csv` 为唯一事实源。*
