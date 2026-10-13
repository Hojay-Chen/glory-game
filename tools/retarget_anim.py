#!/usr/bin/env python3
"""把 Quaternius Universal Animation Library（CC0）的动作重定向到本项目的铠甲骨架。

用法（无头）:
    blender --background --python tools/retarget_anim.py -- <target.glb> <source.gltf> <out.glb>

原理（**世界增量重定向**）:
    源骨骼在某一帧的世界矩阵记为 S，其 rest 世界矩阵记为 S0，
    则"这一帧相对 rest 在世界空间里转了/移了多少" = delta = S · S0⁻¹。
    把它整个套到目标骨骼的 rest 上：T_world = delta · T0。
    Blender 里给 pose_bone.matrix 赋值即可自动解出局部 pose，**与两套骨架的 rest
    朝向差异无关**——这正是能跨骨架重定向的原因。

    骨骼按层级顺序处理（父先于子），否则子骨骼读到的父矩阵还是旧的。

源骨架: 53 关节（含手指/脚趾），本项目骨架: 19 关节（手指脚趾忽略）。
"""
import bpy
import sys
import os
from mathutils import Matrix

# 目标骨骼 → 源骨骼
BONE_MAP = {
    "hips":       "DEF-hips",
    "spine":      "DEF-spine.001",
    "chest":      "DEF-spine.003",
    "neck":       "DEF-neck",
    "head":       "DEF-head",
    "shoulder.L": "DEF-shoulder.L",
    "upperarm.L": "DEF-upper_arm.L",
    "forearm.L":  "DEF-forearm.L",
    "hand.L":     "DEF-hand.L",
    "shoulder.R": "DEF-shoulder.R",
    "upperarm.R": "DEF-upper_arm.R",
    "forearm.R":  "DEF-forearm.R",
    "hand.R":     "DEF-hand.R",
    "thigh.L":    "DEF-thigh.L",
    "shin.L":     "DEF-shin.L",
    "foot.L":     "DEF-foot.L",
    "thigh.R":    "DEF-thigh.R",
    "shin.R":     "DEF-shin.R",
    "foot.R":     "DEF-foot.R",
}
# 只有髋允许跟随平移（跳/翻滚/下蹲的身体位移）；其余只取旋转
ROOT_BONE = "hips"

# 这些源动画名会做轻微改名（去掉 _Loop 等后缀、统一大小写习惯）
def action_out_name(src_name: str) -> str:
    n = src_name
    for suf in ("_Rig", "_rig"):
        if n.endswith(suf):
            n = n[: -len(suf)]
    for suf in ("_Loop", "_loop"):
        if n.endswith(suf):
            n = n[: -len(suf)]
    return n


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_gltf(path: str):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def find_armature(objs, bone_probe: str):
    for o in objs:
        if o.type == "ARMATURE" and bone_probe in o.data.bones:
            return o
    # 回退：全场景找
    for o in bpy.data.objects:
        if o.type == "ARMATURE" and bone_probe in o.data.bones:
            return o
    return None


def hierarchy_order(arm):
    """骨骼索引按层级（父先子后）排序。"""
    out, seen = [], set()

    def walk(b):
        if b.name in seen:
            return
        for c in b.children:
            walk(c)
        seen.add(b.name)
        out.append(b.name)

    for b in arm.data.bones:
        if b.parent is None:
            walk(b)
    return out


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    target_glb, source_gltf, out_glb = argv[0], argv[1], argv[2]

    clear_scene()

    # ---- 1. 源：动画库 ----
    src_objs = import_gltf(source_gltf)
    src_arm = find_armature(src_objs, "DEF-hips")
    if src_arm is None:
        raise RuntimeError("找不到源骨架（DEF-hips）")
    src_actions = list(bpy.data.actions)
    print(f"[src] armature={src_arm.name} bones={len(src_arm.data.bones)} actions={len(src_actions)}")

    # ---- 2. 目标：本项目的铠甲角色 ----
    tgt_objs = import_gltf(target_glb)
    tgt_arm = find_armature(tgt_objs, "hips")
    if tgt_arm is None:
        raise RuntimeError("找不到目标骨架（hips）")
    tgt_mesh = next((o for o in tgt_objs if o.type == "MESH"), None)
    print(f"[tgt] armature={tgt_arm.name} bones={len(tgt_arm.data.bones)} mesh={tgt_mesh and tgt_mesh.name}")

    missing = [t for t in BONE_MAP if t not in tgt_arm.data.bones]
    if missing:
        raise RuntimeError(f"目标骨架缺少骨骼: {missing}")

    # ---- 3. 目标骨架的 rest（armature 空间）与层级顺序 ----
    rest = {n: tgt_arm.data.bones[n].matrix_local.copy() for n in BONE_MAP}
    order = [n for n in hierarchy_order(tgt_arm) if n in BONE_MAP]

    # 源 rest 同上
    s_rest = {s: src_arm.data.bones[s].matrix_local.copy() for s in BONE_MAP.values()}

    for pb in tgt_arm.pose.bones:
        pb.rotation_mode = "QUATERNION"

    if tgt_arm.animation_data is None:
        tgt_arm.animation_data_create()
    if src_arm.animation_data is None:
        src_arm.animation_data_create()

    # 清掉导入时可能自动生成的 NLA，避免干扰
    for o in (src_arm, tgt_arm):
        if o.animation_data:
            o.animation_data.action = None
            for tr in list(o.animation_data.nla_tracks):
                o.animation_data.nla_tracks.remove(tr)

    scene = bpy.context.scene
    made = []
    for act in src_actions:
        # 源动作挂到源骨架
        src_arm.animation_data.action = act
        f0, f1 = act.frame_range
        f0, f1 = int(round(f0)), int(round(f1))
        if f1 <= f0:
            continue

        out_name = action_out_name(act.name)
        tgt_action = bpy.data.actions.new(out_name)
        tgt_action.use_fake_user = True   # 保证导出时不被回收
        tgt_arm.animation_data.action = tgt_action

        for f in range(f0, f1 + 1):
            scene.frame_set(f)
            bpy.context.view_layer.update()
            for tname in order:
                sname = BONE_MAP[tname]
                S = src_arm.pose.bones[sname].matrix
                delta = S @ s_rest[sname].inverted()
                T = delta @ rest[tname]
                if tname != ROOT_BONE:
                    # 只取旋转：把位移还原成 rest 位置
                    T = Matrix.Translation(rest[tname].to_translation()) @ T.to_3x3().to_4x4()
                pb = tgt_arm.pose.bones[tname]
                pb.matrix = T
                bpy.context.view_layer.update()
                pb.keyframe_insert("rotation_quaternion", frame=f)
                if tname == ROOT_BONE:
                    pb.keyframe_insert("location", frame=f)

        # 四元数**符号连续性**修正：烘焙出的逐帧四元数若在相邻帧间反号（q 与 -q 同一旋转），
        # 引擎插值会走"长路径"绕 360° → 单帧骨转上百度的假跳变（实测 forearm.R 达 164°/帧）。
        # 逐骨骼沿时间轴把符号翻正，保证相邻帧 dot >= 0。
        fixed = 0
        for tname in order:
            pb = tgt_arm.pose.bones[tname]
            path = pb.path_from_id("rotation_quaternion")
            curves = [tgt_action.fcurves.find(path, index=i) for i in range(4)]
            if any(c is None for c in curves):
                continue
            prev = None
            n = len(curves[0].keyframe_points)
            for k in range(n):
                q = [curves[i].keyframe_points[k].co[1] for i in range(4)]
                if prev is not None:
                    d = sum(q[i] * prev[i] for i in range(4))
                    if d < 0.0:
                        for i in range(4):
                            kp = curves[i].keyframe_points[k]
                            kp.co[1] = -kp.co[1]
                            kp.handle_left[1] = -kp.handle_left[1]
                            kp.handle_right[1] = -kp.handle_right[1]
                        q = [-v for v in q]
                        fixed += 1
                prev = q
        if fixed:
            print(f"[quat] {out_name}: 修正 {fixed} 处符号翻转")
        tgt_action.frame_range  # touch
        made.append((out_name, f0, f1))
        print(f"[bake] {act.name:28s} -> {out_name:24s} frames {f0}..{f1}")

    print(f"[done] baked {len(made)} actions")

    # ---- 4. 清场：丢掉源骨架/源网格/源动作，否则它们会被一起导出（曾出现 92 条动画 = 46×2）----
    src_arm.animation_data.action = None
    keep_actions = {n for n, _, _ in made}
    for act in list(bpy.data.actions):
        if act.name not in keep_actions:
            bpy.data.actions.remove(act)
    for o in list(bpy.data.objects):
        if o not in (tgt_arm, tgt_mesh):
            bpy.data.objects.remove(o, do_unlink=True)
    print(f"[clean] actions left={len(bpy.data.actions)} objects={[o.name for o in bpy.data.objects]}")

    bpy.ops.object.select_all(action="DESELECT")
    keep = [o for o in (tgt_arm, tgt_mesh) if o]
    for o in keep:
        o.select_set(True)
    bpy.context.view_layer.objects.active = tgt_arm

    bpy.ops.export_scene.gltf(
        filepath=out_glb,
        export_format="GLB",
        use_selection=True,
        export_yup=True,
        export_skins=True,
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_nla_strips=False,
        export_bake_animation=False,
        export_apply=False,
        export_image_format="AUTO",
        export_materials="EXPORT",
    )
    print(f"[export] {out_glb} exists={os.path.exists(out_glb)} size={os.path.getsize(out_glb) if os.path.exists(out_glb) else 0}")


main()
