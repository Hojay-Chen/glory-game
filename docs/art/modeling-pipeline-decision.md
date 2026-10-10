# 3D 角色建模管线选型决策

> 状态：**已推荐（待用户确认）** · 建立 2026-10-11 · 对应 OPEN-ISSUES **ART-01 / ART-02 / ART-03**、CN-03
> 范围：只服务「战斗与竞技」（1v1）。**不含**捏脸 / 换装 / 外观商城。

---

## 0. 背景与约束（事实核实，非臆测）

- **现状**：角色 = `FighterView.cs` 里代码构造的 1 个 `CapsuleMesh` + 1 个 `BoxMesh`（武器）。
  全部 25 职业共用一套 `FighterVisualProfile.Blade` 档案 + 同一种武器外形（OPEN-ISSUES CN-03 / ART-01 / ART-03）。
- **引擎**：Godot **4.3 stable (mono)**，渲染器 = `gl_compatibility`（见 `src/Arena.Client/project.godot`）。
  GLTF 导入 / `Skeleton3D` / `AnimationPlayer` / `AnimationTree` 均为 4.3 稳定 API（`VERSION.md` 判定 LOW risk，无需另查引擎参考库）。
- **工程形态**：**全代码构造**——无 .tscn 场景树（仅 `MainScene.tscn`）、无 `.tres`、无任何外部资产文件。
  表现层与 Sim 严格分离；`FighterVisualProfile` 本身即是为「按职业替换、Sim 零修改」预留的接缝（VS-6.5）。
- **开发机**：
  - 云 Linux（本项目主体，`godot-mono` 已装）。
  - Windows 主机 `ssh win`；其上的 WSL `ssh wsl`（RTX 5070）。
  - Windows 侧**已有运行工作流**：`D:\luxera\glory-game\repo\`（仓库检出）+ `D:\luxera\glory-game\tools\Godot_v4.3-stable_mono_win64\`（编辑器）+ `editor.bat` / `play.bat`。用户在 **Windows 上跑编辑器/游戏**。
- **Blender 实测**：**三台机器都没有安装**（云 `which blender` 为空、WSL 为空、Windows `where blender` 找不到、`Program Files` 无 `Blender*`）。apt 有候选 `4.0.2`，但按本次约束不安装。
- **GDD 已定管线**（不得违反）：§28.3 = 系统规则 → 帧数据骨架 → **白盒动画(胶囊人验证手感)** → 灰盒验证 → **正式美术/动画/VFX** → 打磨与音频 → QA；
  附录决策 **D24：白盒胶囊人先行，美术后置**（「手感问题在白盒期解决成本最低」）。
- **IP**：复刻的是**机制不是造型**——不得使用原作角色/战队/专有道具形象。任何"真模型"都必须是**原创造型**。

---

## 1. 三条候选管线对比

| 维度 | **A. 程序化基本体**（C# 拼 Capsule/Box/Sphere…） | **B. 外部建模 → GLB**（Blender → .glb → Godot） | **C. 混合**（程序化占位 + 关键职业真模型） |
|---|---|---|---|
| 上手速度 | **立即**（今天改、编译即见、零安装） | **慢**（先装 Blender + 学/写 bpy + 建模骨架刷权重 + 导出 + 导入 + AnimationTree） | 中（先做 A 的接缝，再逐职业接 B） |
| 可维护性 | **高**：纯 C#、进 git、diff 可读、无二进制资产、与全代码架构一致 | 中：`.glb` 二进制进 git 难 diff；需 `.blend` 源 + 导出脚本双份；脚本化后可复现 | 取决于接缝设计；若用"每职业 Provider"则 A/B 并存、可渐进增强 → 长期最好 |
| 骨骼动画 | **不能**（无骨架/蒙皮）；可做**代码驱动程序化动画**（tween 部件旋转/位移/缩放），上限见 §4 | **能**（真骨架/蒙皮/AnimationTree 混合），上限最高 | 部分能（真模型部分） |
| IP 合规 | **天然安全**（几何体不构成 IP 复刻）；剪影设计需自我约束 | 需自行确保**原创**；外部素材站有授权风险（本项目已禁下载） | 同 B（真模型部分需原创） |
| 当前环境可行性 | **100%**：云/WSL/Windows 任意机器编译即用，无需 Blender | **未就绪**：三机无 Blender；需先装（建议 WSL，可 headless 脚本化） | 长期最佳，即 GDD §28.3 的实际形态（白盒 → 正式美术逐步替换）；但今天起不来 |
| **失败风险** | **低**——仅"观感上限低"（盒子感），非工程风险 | **中**——工具链空窗 + 首次搭管线成本 + 二进制资产管理 + GLB 导入踩坑（材质/坐标/缩放/动画名） | 中——接缝若设计错，后替换会牵动 FighterView + 动画驱动 |

### 各条要点

**A. 程序化基本体**
- 优势：零依赖、零安装、与现有"一切用代码构造"的架构完全一致；表现层确定性友好；迭代以秒计。
- 劣势：无关节弯曲（膝/肘），观感偏"机器人/盒子"；上限受限于能 tween 的整部件。
- 关键：**不要停在"一个胶囊"**——升级为**多部件人形 + 每职业剪影**，可读性收益极大而成本极低。

**B. 外部建模 → GLB**
- 优势：唯一能拿到真骨骼动画的路线；造型表达力无上限。
- 劣势/风险：Blender 三机全无（工具链空窗）；二进制资产进 git；GLB 导入的材质/朝向/缩放/动画命名易踩坑；首次搭建成本高。
- 关键：若走 B，**必须脚本化（`blender -b -P`）**而非纯手工，否则 25 职业不可持续。

**C. 混合**
- 优势：渐进增强——占位先解决"25 职业可读性"，真模型逐个替换，风险分散；这正是 §28.3 描述的自然演进。
- 前提：**接缝必须先设计对**（一个"每职业视觉来源"抽象：程序化构造 or 加载 GLB 场景）。

---

## 2. 推荐：**A 现在落地，按 C 的结构预留接缝**

> **一句话**：立刻用程序化基本体把"一个胶囊"升级为"多部件人形 + 每职业视觉档案"；
> 把"外部 GLB 真模型"作为 §28.3 里**"正式美术"阶段的既定后续**，通过一个 **Provider 接缝**
> 在**不改 FighterView 主体、不改 Sim** 的前提下逐职业替换。

### 为什么是 A 而不是 B/C（今天）

1. **阶段对齐**：GDD **D24 + §28.3** 明确「白盒胶囊人先行、美术后置」。当前主要矛盾是
   **"25 职业长得一模一样"（可读性）**，而不是"不够精细"。用高模解决可读性是杀鸡用牛刀。
2. **工具就绪度**：**Blender 三机全无** → B/C 今天起不来；A 今天就能改。
3. **架构一致**：项目**全代码构造、零外部资产**；A 与之一致，B 会引入**首批二进制资产 + 导入管线**（属于"正式美术"阶段的工程）。
4. **接缝已存在**：`FighterVisualProfile` 就是为"按职业替换、Sim 零修改"设计的（VS-6.5）。
   **A 和 B 都能挂在同一条缝上**——所以先做 A 不会堵死 B，二者是**先后关系而非二选一**。
5. **风险可控**：A 的唯一短板（观感上限）恰是后续 B 要解决的；先 A 后 B 是把风险摊到两个阶段，而不是一次性全押。

### 现在这一步具体做什么（可执行粒度）

**改动清单（纯 `.cs`，无新资产文件）：**

| 文件 | 动作 |
|---|---|
| `src/Arena.Client/Visuals/FighterMeshBuilder.cs` | **新建**。纯 C# 用基本体拼"多部件人形"（头/躯干/上臂/前臂/腿/武器挂点），部件尺寸与颜色由 profile 参数驱动。返回一个 `Node3D`。 |
| `src/Arena.Client/FighterView.cs` | 把 `_body`（单 `CapsuleMesh`）替换为 `FighterMeshBuilder.Build(profile, teamColor)`。**保留**六段结构（Body/Weapon/Trail/EffectRoot/StateVisual/AnimationDriver）与**全部事件接口**不变（`Sync` / `OnAttackStarted` / `SetFrozen` …）。武器仍挂 `_weaponPivot`。 |
| `src/Arena.Client/Visuals/FighterVisualProfile.cs` | 把 record 扩到"部件尺度 + 剪影参数"（躯干宽高、头形、肢体长、武器偏置、武器外形枚举…）；新增工厂 `For(string classAbbrev)` + 一张 25 职业档案表（先做 3–5 个代表系：剑客/枪手/法师/格斗/术士，其余回落默认）。 |
| `src/Arena.Client/MatchRoot.cs:159` | 把硬编码的 `FighterVisualProfile.Blade` 换成按**该 Fight 的职业**取 `FighterVisualProfile.For(...)`（`_playerClass` / `_enemyClass` 已在手；需把 `fighter.Id → class` 映射传入）。 |

**为 C 预留的接缝（与 A 同一批落地，成本极小）：**
- `FighterMeshBuilder.Build(...)` 内部先查 `profile.ModelScenePath`：
  - 为空 → 走**程序化基本体**（A 路径）；
  - 非空 → `GD.Load<PackedScene>(path).Instantiate()`（B 路径）。
- 这样"某职业换成真模型"只需在档案里填一个 `res://assets/models/<class>/md_<class>.glb` 路径，**FighterView 与 Sim 零改动**。

**产物与落点**：纯 `.cs`，落仓库 `src/Arena.Client/`（Linux 主体），同步到 Windows `D:\luxera\glory-game\repo\` 供编辑器运行。

**验收**：`godot-mono --headless` 或 `dotnet build` 编译通过；`play.bat` 跑一局，肉眼可见 5 个职业剪影明显不同。

---

## 3. GLB 路线完整步骤（B/C 阶段到来时执行）

> 本节为**后续阶段**（§28.3「正式美术」）的执行手册。现在**不执行**，仅固化步骤，避免届时重新调研。

### 3.1 在哪跑 Blender
- **首选 WSL**：Linux 原生、有 GPU、`ssh wsl` 从云直连、且可 **`blender --background`** 无头脚本化。重/批量任务本来就该放 WSL。
- 云 Linux：apt 有 `4.0.2`（需 sudo）；非首选（版本偏旧、云无 GPU 但建模不需 GPU）。
- Windows 侧 Blender：仅在需要 **GUI 手动捏/看** 时用；安装落用户目录（账号 `devops` 非管理员，装系统盘/改 HKLM 受限）。产物一律走 §3.4 的 `D:\luxera\`。

### 3.2 脚本化建模是否可行 —— **可行且强烈推荐**
`bpy` 可在无头模式生成**网格 + 骨架 + 蒙皮权重 + 动画 + 导出 GLB**，全自动、可复现、可进 CI：
```bash
# 在 WSL 中
blender --background --python tools/blender/gen_class_model.py -- \
        --class BLA --out /path/to/md_BLA.glb
```
这才是"程序化思路借用 Blender 拓扑/骨骼能力"的正确姿势；**纯手工 25 职业不可持续**。

### 3.3 同步进 Godot 项目
1. Blender 导出 `md_<class>.glb`。
2. 放进**仓库**（git 追踪）：`src/Arena.Client/assets/models/<class>/md_<class>.glb`。
3. 同步到 Windows 检出：`D:\luxera\glory-game\repo\src\Arena.Client\assets\...`，供编辑器导入。
4. **`.blend` 源文件**放主系统侧 `D:\luxera\glory-game\assets\blend\<class>\`（用户可在 Windows 用 Blender 直接打开，符合 §9.4 约定）。
   - **是否把 `.blend` 入 git**：二选一——(a) 入 git 但用 **git-lfs**（二进制、大）；(b) 只留 `.glb` 入 git、`.blend` 作为派生产物可从生成脚本重建（**推荐 b**，配 §3.2 的生成脚本即可复现）。

### 3.4 Godot 侧导入设置
- 文件面板选中 `.glb` → **Import**：
  - **Import As: Scene**；
  - 勾选所需动画（来自 GLB 的 clip 会进导入场景的 `AnimationPlayer`）；
  - 材质：确认生成 `StandardMaterial3D` 还是保留内嵌材质。
- C# 侧：`GD.Load<PackedScene>("res://assets/models/<class>/md_<class>.glb").Instantiate()`。
- **坐标/缩放约定**：Blender Z-up 导出 GLB（Y-up）由 Godot 自动换算；但需**统一朝向**（建议角色面向 `-Z`，与 `FighterView.Sync` 的 `HeadingQuantum` 朝向约定对齐并实测）与**缩放**（1 unit = 1m；角色高 ≈1.8m，与 GDD §205「站立碰撞体 半径0.45×高1.8」一致）。

### 3.5 命名与目录约定（沿用 GDD §28.2）
- 模型：`MD_<职业缩写>`；动画：`AM_<职业>_<技能缩写>`；武器：`W_<职业缩写>_<序号>`。
- 目录：`src/Arena.Client/assets/models/<class>/`、`.../assets/weapons/`。

### 3.6 `D:\luxera\` 落点汇总
| 产物 | 落点 |
|---|---|
| `.blend` 源（Blender 直开） | `D:\luxera\glory-game\assets\blend\<class>\` |
| 生成的 `.glb`（游戏资产，git 为准） | 仓库 `src/Arena.Client/assets/models/<class>/`，并镜像到 `D:\luxera\glory-game\repo\...` |
| 生成脚本 | 仓库 `tools/blender/gen_class_model.py` |

---

## 4. 骨骼动画的现实评估

### 本作需要的动画（只做战斗范围）
| 场景 | 触发 | 现在怎么表现 | 骨骼能否更优 |
|---|---|---|---|
| 待机 Idle | 常驻 | 无（胶囊不呼吸） | 是（轻微呼吸/武器待机位） |
| 移动 Move | 位移输入 | 无（平移 + 朝向） | 是（走/跑姿） |
| 攻击挥摆 Attack | `OnAttackStarted` | **已有**：武器 pivot tween（约 0.12s 甩出 + 0.15s 收回） | 是（全身挥砍/施法） |
| 受击 Hitstun | `Sync` 状态 | 材质闪白 + 火花 | 是（受击僵直姿态） |
| 倒地/击飞 Down/Launch | `Sync` 状态 | 躯干 -90° 躺倒 + 提亮 | 是（抛体/落地） |
| 死亡 Dead | `Sync` | 变灰 + KO 字 | 是（倒地不起） |
| 格挡 Guard | 事件 | 火花 | 是（举盾/架械） |

### 成本 vs 效果上限

| | **代码驱动程序化动画**（现有 weapon tween 路线） | **骨骼动画**（GLB + `Skeleton3D` + `AnimationTree`） |
|---|---|---|
| 单次成本 | **几十分钟/动作**；即时迭代；无 DCC 依赖 | 一次性大投入（骨架+蒙皮+导出+AnimationTree 状态机+每 clip），之后每 clip 较快 |
| 关键杠杆 | — | **全职业共享一套骨架** → `1 骨架 × N clip` 复用，**不是 25×6** |
| 表现上限 | 只能旋转/位移/缩放**整块或整部件**；无关节弯曲（膝/肘）；偏"机器人"；难做呼吸/惯性/二次动力学 | 接近正式动作游戏（关节、IK、混合、叠加） |
| 与架构契合 | **高**——事件驱动反馈、表现确定性 | 需引入资产 + AnimationTree |

### 现实结论
- **当前阶段（白盒/灰盒、手感未冻结）用程序化动画足够，且更便宜**。手感会变，此时做骨骼动作 = 白做（过早优化）。
- 骨骼动画的正确时机 = §28.3 的**"正式美术"阶段**，届时走 §3 的 GLB 管线。
- **A 内部的可做升级**（无需 Blender）：把"单点武器 tween"升级为"多部件程序化动作"——躯干前倾 + 持械臂前送 + 双腿交替。**无骨骼即可显著提升可读性**，成本仍极低。

---

## 5. 性能预算

**事实**：同屏**最多 2 个角色**（1v1 竞技）。「25」是**选人池**不是同屏数 → **多边形预算极宽松**。

| 项 | 建议 | 理由 |
|---|---|---|
| 每角色三角面 | 目标 **5k–15k**，硬上限 **30k** | 2×30k = 60k tri，2015 年后任何 GPU 无压力（含 RTX 5070 与 `gl_compatibility`） |
| **Draw call / 材质（真正要管的）** | **每角色 = 1 蒙皮网格 + 1–2 材质（≤ ~2 surface）** | `gl_compatibility` + 竞技场道具全是独立 `MeshInstance3D`，draw call 比多边形更敏感。**程序化 A 注意**：别用 25 个部件/人拼（2 人 = +50 draw call，可接受但应按需合并） |
| 贴图 | 每角色 ≤ 2048²、≤ 2 张（albedo + 可选 mask） | 风格化漫画风，无需高分辨率 PBR 集 |
| 骨骼数 | ≤ 60 骨（人形 + 武器骨），2 角色 = 120 骨 | 无压力 |
| 粒子/VFX | 另立预算 | 现有 `SpawnSpark` 每次新建 `MeshInstance3D` + tween，量大时应**池化**（记入性能债，不阻塞建模选型） |

**结论**：本项目**不缺多边形预算**，缺的是"职业可读性"与"动作表达"——这进一步支持**先做 A**（提升剪影）而非先追高模。

---

## 6. 对 OPEN-ISSUES 的回填建议
- **ART-01**（角色建模）：采纳 **A**（多部件人形 + 每职业档案），状态 → **待落地**。
- **ART-03**（视觉档案）：`FighterVisualProfile` 扩为每职业/系别档案 + `For(class)` 工厂。
- **CN-03**（职业缺手感标识）：随 ART-01/03 一并解决（不再共用 `Blade`）。
- 新增（可选性能债）：`SpawnSpark` 粒子/网格池化。

---

## 附：今天就能落地的第一步
1. 新建 `src/Arena.Client/Visuals/FighterMeshBuilder.cs`（多部件人形构造器）。
2. 改 `src/Arena.Client/FighterView.cs`：body 段由单胶囊改为调用该构造器。
3. 加 `FighterVisualProfile.For(class)`；**先只做 2 个代表职业剪影**（剑客 = 细长持剑人形；枪炮师 = 壮硕持炮）。
4. 改 `MatchRoot.cs:159` 传职业档案；`play.bat` 跑一局肉眼对比。
