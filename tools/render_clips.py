#!/usr/bin/env python3
"""把若干动作 clip 各渲一格拼图，用来目视校验重定向结果。

用法:
    blender --background --python tools/render_clips.py -- <glb> <outdir> <clip:frame> [...]

例: blender --background --python tools/render_clips.py -- a.glb /tmp/chk Idle:30 Walk:16 Sword_Attack:18
"""
import bpy, sys, os, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
glb, outdir = argv[0], argv[1]
specs = [s.split(":") for s in argv[2:]]
os.makedirs(outdir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
arm = next((o for o in bpy.data.objects if o.type == "ARMATURE"), None)
print("armature:", arm.name if arm else None, "actions:", len(bpy.data.actions))

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in [
    i.identifier for i in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE"
scene.render.resolution_x = 480
scene.render.resolution_y = 640
scene.render.film_transparent = False
scene.world = bpy.data.worlds.new("W")
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.06, 0.08, 1)
scene.world.node_tree.nodes["Background"].inputs[1].default_value = 1.0

# 相机：斜前方，框住 1.9m 身高
cam_data = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cam_data)
scene.collection.objects.link(cam); scene.camera = cam
tgt = Vector((0, 0, 0.95))
cam.location = tgt + Vector((2.6, -3.4, 0.9))
d = tgt - cam.location
cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()

for name, (x, y, z) in {
    "key": (3, -4, 4), "fill": (-4, -2, 2), "rim": (0, 5, 3)}.items():
    ld = bpy.data.lights.new(name, type="SUN"); ld.energy = 3.5 if name == "key" else 1.6
    lo = bpy.data.objects.new(name, ld); scene.collection.objects.link(lo)
    lo.location = Vector((x, y, z))
    lo.rotation_euler = (Vector((0, 0, 1)) - Vector((x, y, z))).to_track_quat("-Z", "Y").to_euler()

if arm and arm.animation_data is None:
    arm.animation_data_create()

for spec in specs:
    if len(spec) < 2:
        continue
    clip, frame = spec[0], int(spec[1])
    # Blender 导入 glTF 会把动作改名成 "<clip>_<object>"，按前缀匹配
    act = next((a for a in bpy.data.actions if a.name == clip or a.name.startswith(clip + "_")), None)
    if act is None:
        print("MISSING action:", clip)
        continue
    arm.animation_data.action = act
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    scene.render.filepath = os.path.join(outdir, f"{clip}.png")
    bpy.ops.render.render(write_still=True)
    print("rendered", clip, frame)
