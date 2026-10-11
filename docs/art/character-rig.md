# 角色资产与骨骼绑定（Character Rig）

> 建立于 2026-10-11。取代此前"纯代码基本体堆叠"的角色方案（评审 §2.2 判 FAIL 的主因之一）。

## 1. 资产来源（可复现）

| 步 | 做什么 | 工具 |
|----|--------|------|
| 1 | 生成白模 | **Blender MCP → `generate_3d`（Hyper3D / Rodin）** |
| 2 | 建 19 骨骼人形骨架 | Blender MCP → `execute_blender_code`（bpy） |
| 3 | 刚性壳片绑定 | 同上（见 §3） |
| 4 | 导出 GLB | Blender MCP → `execute_blender_code`（`export_scene.gltf`） |

产物：`src/Arena.Client/assets/fighter_knight.glb`（2.3 MB：1 网格 / 1 skin / 19 关节 / 3 张 PBR 贴图内嵌）。

**生成提示词**（`generate_3d`，quality=standard）：

> Full-body stylized game character, armored guardian in ornate silver plate armor with deep blue
> tabard and cloth skirt, standing straight in A-pose with arms relaxed at side and hands completely
> empty, unarmed holding nothing no sword no staff no shield no item in hands, clean game-ready silhouette

⚠️ 经验：提示词里出现 "warrior / knight / swordsman" 时，Rodin **会自动给模型加刀剑**（试了两版都加），
且这些刀剑与手臂**焊在同一个壳片**里、无法事后剔除。必须显式写 **empty hands / unarmed / no sword**。

## 2. 骨架

19 根，命名与位置（身高比例，脚底为 0）：

```
hips(0.50→0.57) spine(0.57→0.66) chest(0.66→0.79) neck(0.79→0.85) head(0.85→1.00)
shoulder.L/R(±0.03,0.78→±0.09,0.795)
upperarm.L/R(±0.09,0.795→±0.145,0.645)
forearm.L/R(±0.145,0.645→±0.175,0.520)
hand.L/R(±0.175,0.520→±0.190,0.455)
thigh.L/R(±0.058,0.50→±0.068,0.28)
shin.L/R(±0.068,0.28→±0.074,0.055)
foot.L/R(±0.074,0.055→±0.080,-0.045,0.012)
```

模型本体 1.902 m，脚底 y=0（已归零），面向 −Z（导出后整体转 180°，见 `FighterMeshBuilder.Build`）。

## 3. 绑定策略：**刚性壳片**（关键）

该 AI 网格是"**壳片汤**"——实测 **3370 个互不连通的 shell**。由此：

| 方案 | 结果 |
|------|------|
| Blender 自动权重（热扩散 `ARMATURE_AUTO`） | ❌ **全 0 权重**（`Bone Heat Weighting: failed to find solution`），25565 个顶点一个都没绑上 |
| 包络权重（envelope） | ⚠️ 能动，但相邻壳片被拉到不同骨骼，铠甲**撕裂**、手臂糊成一团 |
| **刚性壳片绑定**（采用） | ✅ 每个 shell 的质心找**最近骨骼**，整片 weight=1.0 |

刚性绑定的额外好处：**硬表面铠甲本就该是刚性的**，成品是"关节可动的盔甲人"，无撕裂、无糖化。
实现在 Blender 侧一次算完（连通分量 → 最近骨骼线段 → 顶点组），导出时就是普通蒙皮。

## 4. ⚠️ Godot 侧大坑：`SetBonePoseRotation` 不是增量

**Godot 的 `bone_pose` 是骨骼相对父级的"完整局部变换"，不是相对 rest 的增量。**
`Skeleton3D.SetBonePoseRotation(idx, Quaternion.Identity)` 会把骨骼自带的 rest 局部旋转**一并抹掉**——
实测把 `upperarm.L` 的 pose 置为 identity，手臂会立刻对齐到父级 `shoulder.L`（指向 +X）的朝向，
也就是**横着张开**（现场表现为"角色变 T-pose，且与传入的四元数几乎无关"）。

正确做法（`BodyRig.Around()`）：先把世界轴旋转搬到**父级坐标系**，再**左乘 rest 局部旋转**：

```csharp
var rest   = Skel.GetBoneRest(boneIdx).Basis.Orthonormalized();   // 骨骼自带 rest（局部）
int parent = Skel.GetBoneParent(boneIdx);
var P      = parent >= 0 ? Skel.GetBoneGlobalRest(parent).Basis.Orthonormalized() : Basis.Identity;
var R      = new Basis(worldAxis.Normalized(), Mathf.DegToRad(deg));   // 想要的世界旋转
var delta  = P.Inverse() * R * P;                                      // 搬到父级坐标系
return (delta * rest).GetRotationQuaternion();                          // ← 必须乘上 rest
```

自检方式（`ARENA_RIGTEST=1` 时可复现）：对 `upperarm.L` 绕世界 +X 转 40°，
`hand.L` 应从 (0.333, 0.989, 0) 移到 ≈ (0.333, 1.112, −0.336)；绕 +X 90° → ≈ (0.333, 1.512, −0.523)。

语义轴（`BodyRigAxes`，骨骼空间）：`Fwd = +X`（前后摆，腿/臂）、`Side = +Z`（左右张合）、`Up = +Y`（扭转）。
动作代码只用这三条语义轴，由 `Around()` 负责翻译。

## 5. 材质与阵营

- 资产自带 3 张贴图（diffuse / metallic-roughness / normal），逐实例 **Duplicate** 后挂 `MaterialOverride` 再加染色。
- 阵营染色是**乘算调制**（`FighterMeshBuilder.Tint`）：按**最大通道**归一化取向阵营色相（不能按亮度归一化——
  会产生 >1 分量，钳制后既偏色又过曝），再 `lerp(白, 色相, 0.30) × 0.90`。
- ⚠️ `FighterView.Sync` 里的状态染色必须**乘在**阵营基础色之上，**不能直接覆盖** `AlbedoColor`——
  覆盖会把整张贴图压成一块纯色（曾导致角色变成一团饱和蓝）。
- 阵营识别件：地面光环（形状冗余：方角 vs 圆环）+ 肩后小徽记（方 vs 圆）。

## 5.5 动作库（CC0 动捕重定向）——2026-10-11

角色本体之外，**46 个动作 clip** 来自 CC0 社区动作库，重定向到本骨架：

| 项 | 值 |
|----|-----|
| 来源 | **Quaternius Universal Animation Library（Standard 免费版）**，CC0 |
| 镜像 | `https://github.com/J-Ponzo/gltf-universal-animation-library`（glTF 导出，Godot 版） |
| 文件 | `glTF/AnimationLibrary_Godot_Standard.gltf` + `.bin` |
| 源骨架 | 53 关节（含手指 15×2 / 脚趾），本项目只取其中 19 根 |
| 重定向 | `tools/retarget_anim.py`（无头 Blender，世界增量法） |
| 校验 | `tools/render_clips.py` 把若干 clip 各渲一格拼图目视检查 |

**重定向原理（世界增量）**：源骨骼 S 与目标骨骼 T，各自 rest 世界矩阵 S₀ / T₀。
第 f 帧的"世界增量" `delta = S(f) · S₀⁻¹`，套到目标 rest 上 `T(f) = delta · T₀`。
给 Blender `pose_bone.matrix` 赋该值即自动解出局部 pose，**与两套骨架 rest 朝向的差异无关**——
这正是跨骨架重定向能成立的原因。骨骼须按层级顺序处理（父先于子）。

只有 `hips` 跟随平移（跑/跳/翻滚的身体位移），其余骨骼只取旋转，避免和 Sim 的位置权威打架。

**已重定向的 clip**（供 `FighterView.DriveClips` 使用）：
`Idle` / `Walk` / `Jog_Fwd` / `Sprint` / `Jump` / `Jump_Start` / `Jump_Land` / `Roll` /
`Sword_Attack` / `Sword_Attack_RM` / `Sword_Idle` / `Punch_Jab` / `Punch_Cross` / `Punch_Enter` /
`Hit_Chest` / `Hit_Head` / `Death01` / `Crouch_Idle` / `Crouch_Fwd` / `Spell_Simple_*` /
`Pistol_*`（5 段）/ `Sitting_*` / `Swim_*` / `Dance` / `Interact` / `PickUp_Table` / `Push` …（共 46）

**动作与判定的对齐**：`FighterView.OnAttackStarted` 收到技能 id + **技能实际时长**
（`MatchRoot.SkillSeconds` 按 `skills.csv` 的 startup+active+recovery 帧算），
对攻击 clip 做 `SpeedScale = clipLen / skillLen` → 动作与判定窗口对齐。

**Blender 运行方式**：本机（云服务器）装了无头 Blender 4.2.1 LTS（`/home/ubuntu/tools/`），
比走 Windows 上的 GUI MCP 可靠得多（MCP 桥曾卡死且非管理员账号杀不掉进程）。

## 6. 待办

| 项 | 说明 |
|----|------|
| 分支专属外观 | 目前 24 职业共用这一具"铠甲守卫"；后续按系别各生成一版（提示词须带 empty hands） |
| 分支专属攻击动作 | 现在所有技能都用 `Sword_Attack`；应按武器家族（枪/杖/枪械/拳）挑不同 clip |
| 取消窗 / 浮空连招的**动作表现** | 系统侧帧数据已就位，动作侧还需接：取消时的动作切换、空中追打 clip |
| Mixamo 补充 | 用户要的 Mixamo 免费动作库需要 Adobe 登录后才能下载；CC0 库缺口部分先用自研补 |
| 刚性绑定的极限姿态 | 大幅跨步时护腿壳片会分离（硬表面盔甲的固有代价）；后续可对少数关节做软权重 |
| 动作过渡 | 现在只靠 `PlaybackDefaultBlendTime` 全局混合；后续该上 AnimationTree/状态机做分层混合 |

