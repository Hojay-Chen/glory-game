# ADR-0002 — 数据管线：CSV→双导出与启动校验 Fail-Fast

| 项 | 值 |
|---|---|
| ADR 编号 | ADR-0002 |
| 状态 | **Accepted**（2026-08-31） |
| 日期 | 2026-08-31 |
| 决策层 | Arena.Infra（Foundation 级）+ 构建期工具链 |
| 上游 | ADR-0001（§1 量化政策 P-1/P-2/P-3、§2 系数表版本、§9 Determinism Contract）；audit-spec-consistency-v1.md；pre-adr-resolution-v1.md |
| 关联 TR | TR-sim-003（CSV 数据驱动）/ TR-live-002（启动校验 fail-fast）/ TR-live-003（ID 命名）/ TR-net-005（dataVersionHash 绑定回放） |
| 实测基线 | master `14cea35`（2026-08-31，全部数字经脚本实测，非引用历史汇报） |
| 后续 ADR | ADR-0003（事件协议：本 ADR 的 dataVersionHash 进入回放头）、ADR-0008（签名协议：opaque 效果文本的路由） |

---

## 0. 背景与问题

ADR-0001 固化了 Runtime 数值体系与量化政策，但「CSV 如何变成 Core 消费的强类型 Runtime Definition」尚未定义。GDD §28.1 要求数据驱动铁律与启动校验；TR-live-002 要求 fail-fast。本 ADR 定义**构建期编译器**与**运行时校验**的完整契约。

**本 ADR 不是数据修订轮**：所有当前数据问题只登记分类（§8），不修改 GDD/Skill-Spec/CSV，不裁定游戏设计问题。

---

## 1. Canonical Data Source（事实源表）

### 1.1 分层定义

| 层 | 文件 | 地位 | 修改规则 |
|---|---|---|---|
| **Canonical Data（战斗数值事实源）** | `docs/skill-spec/skills.csv`（487×36）<br>`docs/weapon-spec/weapons.csv`（73×13）<br>`docs/balance-sheet/class-base.csv`（25×13） | **唯一**战斗数值事实源。语义 = 「编译器解析规则 + 本文件内容」，与任何文档陈述无关 | 平衡/数据补丁流程（GDD §25.2）+ 重跑校验器 |
| **Canonical Provenance（出处事实源）** | `tools/learn_levels.py`（习得等级 LEARN 表） | skills.csv 的 learn_level 列的**上游出处**——管线重建（validate_skills 分片重构）时必需；已烘焙进当前 CSV | 随 CSV 补丁同步 |
| **规范文档（语义解释权=人）** | GDD-Gameplay v0.3.7；skill-spec/README（字段字典）；weapon-spec/README；balance-sheet/README；skill-spec/实现注记 | 定义字段**语义意图**与设计依据；**数值冲突时一律 CSV 优先**（既有原则） | 文档修订流程 |
| **派生报告（可再生）** | validation-report.md / balance-report.md / 复刻对照审计 / audit-spec-consistency-v1 / pre-adr-resolution | 校验器与审计的输出或快照；**不进入事实链** | 重跑即再生 |
| **构建产物（可再生，非事实源）** | Runtime Catalog：JSON + Godot 资源（§6） | 由编译器从 Canonical Data 生成；**随时可删除重建**；不手工编辑 | 只由编译器写 |
| 工具 | tools/validate_skills.py、validate_weapons.py、balance_audit.py | 校验/审计器（Python 构建期）；ADR-0002 定义的 C# Compiler 落地后与之长期共存（双端校验互证） | 工具链演进 |

### 1.2 最终数值解释权

> **战斗数值的唯一解释权 = Canonical Data + 编译器解析规则（本 ADR §4 语法白名单）。** README/GDD 陈述与 CSV 冲突时：不修改 CSV，登记为 Documentation Drift（§8），由文档修订流程清理。**本 ADR 不选择游戏规则。**

### 1.3 「当前仓库真实数据」与「历史文档数字」分离（实测 @14cea35）

| 项 | 实测值 | 文档漂移处 |
|---|---|---|
| skills.csv | **487 行 × 36 列** | GDD「501 条目」（L113）、「501 行×35 列」（L1051）；历史任务书「532」（旧 README 数字，仓库中已不存在） |
| 散人池 | **96** | GDD「94 个」×5 处（L113/169/3057/3471/4173）；skill-spec README L134；实现注记 L189 |
| weapons.csv | **73 行 × 13 列** | ✓ 无漂移 |
| class-base.csv | **25 行 × 13 列** | balance-sheet/README §2「12 列」（**本次新发现**） |
| v0.4/v0.4.1 修订 | 已发生并落盘：天击 launch_v=9.0、幻影龙牙 active=15 等（notes 带 v0.4 标记 5 处） | tools/validate_skills.py L163 注释「天击=7.5」（过期注释，校验带本身覆盖） |
| GDD §30 树 | 「携带 10 格/30 字段规范」残留 | 与 §18.1（已废除携带上限）及 36 列现状矛盾 |

---

## 2. 当前数据问题分类登记（只审计不修，详见 §8）

四类：**Documentation Drift**（文档陈述过期）/ **Schema Issue**（实际数据格式超出字段字典）/ **Data Issue**（数据内部矛盾或噪声）/ **Design Open Question**（需用户裁定）。完整清单见 §8；两个扫描新发现：

- **[Schema] hitbox 语法超集**：实际使用 17 种首词——`fan83/box104/none46/proj75/circle54/self29/unit14/zone11/deploy9/cone21/lob17/ally8/line4/aura4/cyl2/wall2/portal1` + 3 行自由文本（`形态三选`/`自身全部手雷种类`/`self/-`）；skill-spec README §3.4 只收录 10 种 → **58 行使用未收录 kind**
- **[Schema] status 列自由文本**：语法 `kind:p1:dur[@N%]` 仅覆盖 ~90 行；其余为中文自由文本首词 50+ 种（`破防/结界/随携带手雷/截脉/全异常满值/反射法术…`）→ 解析器无法全枚举，需 opaque 通道（§4.4）

---

## 3. Compiler 管线（构建期）

```
Canonical CSV (skills/weapons/class-base)
    ↓ ① Parser（§4 语法白名单）
    ↓ ② Canonical Model（强类型中间表示，字段顺序=schema 顺序）
    ↓ ③ Schema Validation（结构/类型/枚举/格式）
    ↓ ④ Semantic Validation（引用/时间/资源/职业关系）
    ↓ ⑤ Quantization（ADR-0001 P-1/P-2/P-3 + Fixed Q32.16 换算）
    ↓ ⑥ Deterministic Sort（全表按主键 Ordinal 升序）
    ↓ ⑦ Runtime Definition（强类型 record，Tick 单位）
    ↓ ⑧ Emitter ×2（JSON / Godot .tres，§6）
    ↓ ⑨ dataVersionHash = SHA-256(§5 输入范围)
```

### 3.1 确定性硬性要求（Same Source + Same Generator + Same Policy ⇒ Same Catalog + Same Hash）

| 维度 | 规定 |
|---|---|
| ID 排序 | 全表输出按主键 `skill_id / weapon_id / class_id` 的 **Ordinal（逐字节）升序**；UnitSpec 等内嵌列表按其 ID 升序 |
| 字段顺序 | 输出字段顺序 = Schema 定义序（编译器内常量表），禁止反射遍历决定顺序 |
| 文件遍历顺序 | **固定有序清单**（编译器内显式列出三个 CSV 路径），禁止文件系统 glob/目录枚举序 |
| Encoding / newline | 输入 UTF-8（无 BOM）+ LF（BOM/CRLF 存在即 Validation Failure——canonical 数据由 git 保证）；输出 JSON UTF-8 无 BOM + LF |
| Locale | 全部数值/日期/大小写操作 `CultureInfo.InvariantCulture`；字符串比较 `StringComparison.Ordinal`；禁止 `CurrentCulture` |
| Numeric formatting | Fixed 输出为 **Raw int64 字段**（不序列化浮点小数）；确需人类可读小数的调试字段与语义字段分离且不参与 hash |
| JSON 键序 | 按 Schema 序写入（手写序列化器或 OrderedDictionary），禁止 `JsonSerializer` 默认反射序 |
| 浮点 | 编译器内部允许 Python/float 解析**设计值**（如 damage_mult 字符串→精确有理数用 `Fraction`，不用 float）——**落盘结果只含整数 Raw**，浮点不进入任何产物字段 |
| hash 输入范围 | 见 §5.2 |

### 3.2 可复现验证

编译器自带 `--verify` 模式：两次编译（可跨进程/跨机器）比对 Catalog 字节与 dataVersionHash；CI 中以 T5 同矩阵执行（ADR-0001 §10 T1 同源要求）。

---

## 4. Parser 语法白名单（承认现状，正式字典补录另行）

编译器按下列白名单解析；**白名单外输入 = Schema Validation Failure（fail-fast）**，不做静默修正。白名单是「当前仓库真实格式」的显式承认——其中超出 Skill-Spec README 字典的项已登记 §8 Schema Issue，字典补录走文档流程（不在本 ADR 执行）。

### 4.1 active_f（5 形态）

`<int>` 帧数｜`<num>s` 秒（×60 RHE）｜`hold`（按住维持）｜`controlled`（操控型生效窗）｜`-`（判定即收 ⇒ **2 Tick 名义窗**，ADR-0001 P-2 配套）。现值 `维持/持续→hold`、`可控→controlled`、`飞行→projectilePhase`（该技能 active 交 Projectiles 计时）。

### 4.2 armor / invincible_f（两形态）

`SA|SSA:<t0>-<t1>`（Instant Window，tick）｜`SA|SSA:buff:<skillId>`（Persistent，随 buff 存续——现 7 行超长区间 + 1 行 `12-26s` 在转换前**先按字面解析为区间并标记待裁定**，不擅自转语义）。`invincible` 前缀冗余剥除；`地底16-146f` 这类文本修饰 → 解析失败登记（§8 Data Issue），fail-fast。

### 4.3 hitbox（kind 白名单 17+3 特例）

`fan:a<deg>:r<r>` / `box:w×h×l` / `circle:r<r>[:耐久<n>]` / `cyl:r<r>:h<h>` / `line:<len>` / `cone:...` / `lob:...` / `proj:<speed>m/s:<range>[:追踪]` / `zone:<shape>:<dur>` / `aura:r<r>` / `self` / `unit[:tag]` / `deploy:...` / `ally:...` / `wall:...` / `portal:...` / `none`。参数内数值一律 Q32.16 量化；`耐久`/存活时长迁入 DeploySpec（§4.5）。3 行自由文本 hitbox = fail-fast 登记。

### 4.4 status（两通道）

- **枚举通道**：`kind:p1:duration[@N%]`，kind ∈ 已知枚举（bleed/burn/slow/freeze/stun/root/blind/silence/sleep/confuse/taunt/poison/paralysis/weight/curse/shock/禁足…以编译器枚举表为准，覆盖现有 ~24 个规范写法）；duration 秒→Tick（RHE）
- **Opaque 通道**：非枚举首词（自由文本 ~40 种）解析为 `TextEffect(raw)`——**不丢弃、不猜测语义**，原样进入 Runtime Definition 的 `specialText` 字段，由 Rule 层/签名插件在实现期认领；Data.Validate 输出 opaque 清单作为实现工作池。**枚举通道永远优先，禁止把可枚举效果留在 opaque**

### 4.5 special / notes

special 关键词保留原文（Raw）+ 派生布尔标志位表（强制倒地/受身无效/破霸体/…以关键词表为准，关键词表=编译器常量）；notes **不进入 Runtime Definition**（纯文档），notes 变更不影响 dataVersionHash。

---

## 5. 量化与 Hash

### 5.1 Quantization（全部承接 ADR-0001，编译器是执行者）

- P-1 攻速：`effectiveTicks = max(1, RHE(B_raw, s.Raw))`——对 startup/active/recovery/hit_interval 四字段，应用时机=装配（Loadout 选武器后）而非 Catalog 预计算（不同武器不同结果）；Catalog 存基础值 + 武器表
- P-2 多段 hit timing：`offset_k = min(k×iv, W−1)` 或 `RHE(k×(W−1), n−1)`；**编译时预计算并存入 Runtime Definition**（`hitSchedule: int[]`），运行时零计算；间隔 ≥3T 违反 = Semantic Failure
- P-3 秒→Tick：`RHE(s×60)`，×60 非整数 = Schema Failure（现有 487 行 100% 对齐）
- Fixed 换算全部按 ADR-0001 §1.2 表（速度/加速度 per-Tick 预量化），RoundHalfEven 唯一舍入

### 5.2 dataVersionHash

```
dataVersionHash = SHA-256(
    "ArenaCatalog:v1"                         // 编译器管线版本
  + hash(skills.csv bytes) + hash(weapons.csv bytes) + hash(class-base.csv bytes)
  + hash(learn_levels.py bytes)               // provenance 源
  + DETERMINISTIC_CONST_VERSION + 系数表内容 hash   // ADR-0001 §2.2
  + QUANTIZATION_POLICY_VERSION               // P-1/P-2/P-3 文本版本
)
```

- **不纳入**：GDD/README/audit 等文档（文档漂移不改变战斗语义，见 §1.2）、notes 列、Runtime 产物自身（防自指）
- 用途：回放头（TR-net-005）、网络会话握手、Snapshot 兼容性判断（ADR-0001 §9 契约项）；**不匹配 = 显式拒绝，不静默**

### 5.3 确定性数学表（承接 ADR-0001 §2，编译器职责边界）

- 表由构建期脚本（精确有理数）生成并**以源码常量入 Core 仓库**；编译器校验表版本与内容 hash 一致
- Runtime O(1) 查表，**不重算**；`Math.Pow/Sqrt/trig` 禁令延续（ADR-0001 §2.4，本 ADR 无新增豁免）

---

## 6. Runtime 双导出：One Compiler, Two Emitters

```
                ┌─→ Emitter A → runtime-catalog.json（Arena.Core/Headless 消费）
Canonical CSV ─ Compiler ┤
                └─→ Emitter B → runtime-catalog.tres（Arena.Client Godot 资源）
```

| 保证 | 措施 |
|---|---|
| 来源一致 | 两个 Emitter 消费**同一个内存 Canonical Model**，禁止各自重读 CSV |
| Schema 一致 | 字段集合与顺序由同一 Schema 常量表驱动 |
| hash 一致 | **dataVersionHash 只由 JSON 产物承载与校验**；`.tres` 头部写入同一 hash 字符串 |
| 数值语义一致 | Client 启动时加载 `.tres` 后对**语义字段做 re-hash** 与 JSON 侧 hash 比对（不变式校验），不一致 = fail-fast |

### 各程序集消费规则

| 程序集 | 规则 |
|---|---|
| **Arena.Core** | **完全不知道** CSV/JSON/.tres/FileAccess/ResourceLoader 的存在——构造函数接收强类型 `RuntimeCatalog`（record 集合）。Core 测试直接以代码构造 Catalog（无 IO） |
| **Arena.Headless** | 只用 **JSON Emitter** 产物（纯 .NET `System.Text.Json` 定序读取），零 Godot 依赖 |
| **Arena.Client** | 用 Godot Resource（`.tres` 文本资源，键序确定）便利层；**不得存在第二套数据解释逻辑**——语义必须与 JSON 逐字段等价（§6 不变式校验是唯一桥梁）；禁止 Client 在运行时读取/解析 CSV |
| 工程纪律 | `runtime-catalog.*` 产物**不入 git**（可重建，防手改）；CI 构建期生成并校验 |

> **.res/.tres 确定性说明**：Godot 导入会生成非确定 UID/时间戳——因此 Godot 资源**不参与 dataVersionHash**（hash 只绑 JSON 语义面），等价性由启动 re-hash 保证。这是「构建产物不进事实链」原则的具体化。

---

## 7. Validation / Fail-Fast

### 7.1 四层校验

**L1 Schema Validation**（结构）：列数/列名精确匹配；类型（整数/有理数字符串/枚举）；格式白名单（§4）；空值约定（`-`）；BOM/CRLF 拒绝。

**L2 Semantic Validation**（关系与时间）：
- ID：全局唯一 + 格式（`^[A-Z]{3}_(BAS|T[1-4]|U|PAS)_\d{3}$` 等）
- 引用：weapons.class_id ∈ classes；trait_rules 引用 skill_id 存在；class-base.resource 与 §9.3 职业资源表一致
- 时间：P-2 间隔 ≥3 Tick；窗口 ≤ 动作总长（Instant 语义）；invincible/armor 区间合法
- 资源：cost_mp ≥ 0；cost_hp 语法；CD ≥ 0；`公共` CD 组引用合法
- 职业：每职业武器 3 把 + UNS 1；24+1 职业齐备
- 几率：`@N%` 数值存在（裸「几率」= Failure，待 OQ-6 补录后自然通过）
- 闸门红线：受身无效白名单、硬控 >1.5s 白名单（沿用 validate_skills 语义）

**L3 Determinism Validation**（管线自证）：同输入双编译产物逐字节一致；hash 稳定；排序稳定（等键不存在——主键唯一）；量化稳定（纯整数运算）；不依赖 Dictionary 序/文件系统序/locale/浮点（编译器内部数值用 Fraction/int，禁 float 进语义字段）。

**L4 Runtime Fail-Fast**：Arena.Client/Headless 启动序列 = `加载产物 → 校验 dataVersionHash（内嵌期望值）→ L1/L2 快速重校验（抽查+全量引用检查）→ re-hash 不变式（Client）`。**任何失败 = 拒绝启动**，输出结构化错误报告（文件/行/列/规则）。**禁止**：静默修正、默认值兜底、跳过坏行继续运行——错误数据宁可宕机不可错打（TR-live-002 fail-fast 精神 + GDD §28.1）。

### 7.2 校验器双端互证

Python 校验器（tools/，现行）与 C# Compiler 校验（本 ADR）**语义对齐**：CI 同时跑两套，结果分歧 = 缺陷。Python 管线保留（设计侧工作流），C# Compiler 是 Runtime 事实（两者从同一 CSV 出发，结论必须一致）。

---

## 8. 当前数据问题登记（只登记，不修不裁）

| Issue | Category | Current Evidence | Decision Required? | Impact |
|---|---|---|---|---|
| D-1 「501 条目/501 行×35 列」 | Documentation Drift | GDD L113/L1051 vs 实测 487×36 | 否（文档修订） | 无功能影响 |
| D-2 「94 个」×7 处 | Documentation Drift | GDD 5 处 + skill-spec README L134 + 实现注记 L189 vs 实测 96 | 否（文档修订） | 无功能影响 |
| D-3 GDD §30 树「携带 10 格/30 字段规范」 | Documentation Drift | GDD L4044/4045 | 否（文档修订） | 无功能影响 |
| D-4 balance-sheet README「12 列」 | Documentation Drift | README §2 vs 实测 13 列 | 否（文档修订） | 无功能影响 |
| D-5 validate_skills.py 天击 7.5 注释 | Documentation Drift | tools L163 vs CSV 9.0 | 否 | 校验带覆盖，无功能影响 |
| S-1 hitbox kind 超集（cone/lob/ally/wall/portal/deploy + 3 自由文本） | **Schema Issue** | §2 扫描（58 行未收录） | 字典补录（文档流程）；编译器白名单已承认 | 无——编译器已按 §4.3 处理 |
| S-2 status 列自由文本 ~40 种 | **Schema Issue** | §2 扫描 | 需逐条认领（枚举化 or 签名插件）→ 实现期工作池 | opaque 通道保证可运行；语义实现逐步收口 |
| S-3 `@N%` 语法未入字典；裸「几率」无数值 | Schema Issue + **Design OQ-6** | skills.csv 14 行 | 是（OQ-6 数值；字典补录） | 几率技不可实现直至补录 |
| S-4 armor `SA:12-26s` 单位混用 | Data Issue（**Design OQ-2**） | skills.csv 1 行 | 是（OQ-2） | 解析为待裁定标记 |
| S-5 超长 armor 区间（7 行 >360f） | Schema Issue（Persistent 误用 Instant） | §5.3 Pre-ADR | 语义转换待裁定（数据补丁） | fail-fast 前先按字面区间解析+标记 |
| S-6 `invincible:地底16-146f` / `invincible:0f` / startup `15(0.25s)` | Data Issue（部分 **OQ-5**） | skills.csv 3 行 | 部分（OQ-5） | 格式正名后可解析 |
| S-7 active 形态词（维持/持续/可控/飞行）未入字典 | Schema Issue | 11 行 | 字典补录 | 编译器已按 §4.1 映射 |
| S-8 cancel_min_tier `-`/`counter` 未入枚举 | Schema Issue（**OQ-8**） | 72 行 | 字典补录 + OQ-8 语义 | 编译器按字面接受 |
| G-1 几率字段 vs D02 | **Design Open Question（已定方向待 GDD 修订）** | audit B-1 / Pre-ADR §1（用户已定方向：种子化确定性随机；GDD 措辞修订待执行） | GDD 修订流程 | ADR-0001 §4 已容纳 |
| G-2 OQ-4 加点模型 vs D37 | **Design Open Question** | GDD §18.2 | 是（用户） | Loadout 接口可承载任意模型 |
| G-3 F1 二轮（空中快刺形态 vs 接受三刺） | **Design Open Question** | session-state | 是（用户） | 不影响数据管线 |

---

## 9. F1 状态（正确口径，不裁定）

F1 **不是**「浮空四连刺不可复现」的旧问题。当前状态：

```
F1 第一轮（已解决）：launch_v 7.5 → 9.0
    → 三刺（天击>直刺×2）可复现 ✓
F1 第二轮（挂起，用户裁定）：
    选项 A：空中快刺专用形态实现四刺
    选项 B：接受三刺、修订模板
```

本 ADR 不裁定 F1。对数据管线的唯一关联：选项 A 若成立将新增空中普攻形态数据行——走正常 CSV 补丁流程，Compiler 白名单已可容纳。

---

## 10. Open Questions 引用

OQ-2 / OQ-4 / OQ-5 / OQ-6 / OQ-7 / OQ-8 / OQ-9 全部维持未裁定状态（pre-adr-resolution-v1 §8 定义）；本 ADR 仅将其映射为数据管线的处理策略（fail-fast 标记 / opaque 通道 / 待补录），未做任何设计裁定。

---

## 11. 决策后果

- 正面：设计数据→Runtime Definition 的转换**可复现、可校验、可证伪**；Core 保持零 IO/零 Godot；JSON/.res 语义分叉被启动不变式封死；文档漂移不再能污染战斗语义
- 代价：新增专用 C# 编译器工具（构建期）；Schema 白名单需要随数据演进维护；两套校验器（Python/C#）需语义同步
- 中性：产物不入 git（CI 生成）；Godot UID 非确定性被 hash 策略隔离

---

## 附：ADR 自审（15 项，2026-08-31）

| # | 检查 | 结果 | 依据 |
|---|---|---|---|
| 1 | Canonical Source 明确 | ✅ | §1.1 六层表；CSV/learn_levels.py=事实源，文档=语义，报告/产物=可再生 |
| 2 | 当前真实数据口径核实 | ✅ | §1.3 全部脚本实测 @14cea35（487×36 / 73×13 / 25×13 / 96 / v0.4 痕迹） |
| 3 | 未误用历史 README 数字 | ✅ | 532/501/94 均标记为 Drift；全文数值引用实测值 |
| 4 | CSV→Runtime 确定性 | ✅ | §3.1 八维度规定 + §3.2 --verify 双编译比对 + §7.1-L3 |
| 5 | P-1/P-2/P-3 完整 | ✅ | §5.1 逐条承接（含应用时机：P-1 装配期、P-2 编译期预计算） |
| 6 | 无隐式 Float | ✅ | §3.1 编译器语义值用 Fraction/int；产物只含 Raw int64；浮点仅限解析瞬时且不落盘 |
| 7 | 无平台相关行为 | ✅ | InvariantCulture/Ordinal/LF/固定文件清单/键序 Schema 序 |
| 8 | 无不稳定排序 | ✅ | 主键 Ordinal 升序；主键唯一 ⇒ 无等键歧义 |
| 9 | JSON/.res 语义分叉 | ✅ | One Compiler Two Emitters + Client 启动 re-hash 不变式 + Godot UID 排除出 hash |
| 10 | dataVersionHash 完整 | ✅ | §5.2 输入范围显式（含系数表/政策版本），显式排除项（文档/notes/产物自身）也有理由 |
| 11 | Fail-Fast 完整 | ✅ | §7.1-L4 四道启动关卡 + 禁止静默修正条款 |
| 12 | Core 保持 Godot 无关 | ✅ | §6 消费规则——Core 构造注入强类型 Catalog，测试可无 IO 构造 |
| 13 | 未修改游戏设计 | ✅ | 全文无规则变更；S/G 类只登记；白名单=承认现状非改设计 |
| 14 | F1 保持二轮 Open | ✅ | §9 按当前状态陈述，不裁定 |
| 15 | OQ 维持未裁定 | ✅ | §10 显式声明 |

**自审结论：通过（15/15）。**

*本 ADR 创建过程中未修改 GDD/Skill-Spec/CSV/class-base/技能数值/职业归属，未裁定任何设计问题，未开始 Arena.Core/Data Catalog/Godot 客户端实现。*
