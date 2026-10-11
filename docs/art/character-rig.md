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

## 6. 待办

| 项 | 说明 |
|----|------|
| 分支专属外观 | 目前 24 职业共用这一具"铠甲守卫"；后续按系别各生成一版（提示词须带 empty hands） |
| 武器朝向微调 | 剑挂在 `hand.R` 的 `BoneAttachment3D` 上，当前朝向可接受但未精修 |
| 格挡/受击/倒地动作 | 目前只有待机 / 步态 / 倒地摊开；攻击是武器 tween + 骨骼未联动 |
