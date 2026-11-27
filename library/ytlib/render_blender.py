"""Blender ichida ishlaydi:  blender -b --factory-startup -P render_blender.py -- <model.gltf> <hdr> <chiqish_papka> <o'lcham> <rakurslar>

Model markazga qo'yiladi, kamera avtomatik ramkalaydi, fon shaffof (film_transparent), yorug'lik - studiya HDRI (Poly Haven, CC0).
Har rakurs: <chiqish>/view_<n>.png (RGBA). Hamma renderlar bir xil yorug'likda: bitta videodagi 3D elementlar bir uslubda ko'rinadi.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
model, hdr, out_dir = argv[0], argv[1], argv[2]
size = int(argv[3]) if len(argv) > 3 else 1600
views = argv[4].split(",") if len(argv) > 4 else ["front34", "front", "side", "top34"]
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=model)
scene = bpy.context.scene
objs = [o for o in scene.objects if o.type == "MESH"]
if not objs:
    raise SystemExit("modelda mesh yo'q")

# Chegaralar va markaz
mins = Vector((1e9, 1e9, 1e9))
maxs = Vector((-1e9, -1e9, -1e9))
for o in objs:
    for c in o.bound_box:
        w = o.matrix_world @ Vector(c)
        mins = Vector((min(mins.x, w.x), min(mins.y, w.y), min(mins.z, w.z)))
        maxs = Vector((max(maxs.x, w.x), max(maxs.y, w.y), max(maxs.z, w.z)))
center = (mins + maxs) / 2
radius = max((maxs - mins).length / 2, 1e-3)

# Yorug'lik: HDRI + yumshoq kalit chiroq
world = bpy.data.worlds.new("studio")
scene.world = world
world.use_nodes = True
nt = world.node_tree
env = nt.nodes.new("ShaderNodeTexEnvironment")
env.image = bpy.data.images.load(hdr)
bg = nt.nodes["Background"]
bg.inputs["Strength"].default_value = 1.1
nt.links.new(env.outputs["Color"], bg.inputs["Color"])
key = bpy.data.objects.new("key", bpy.data.lights.new("key", "AREA"))
key.data.energy = 400 * radius * radius
key.data.size = radius * 3
key.location = center + Vector((radius * 2.5, -radius * 2.5, radius * 3))
key.rotation_euler = (math.radians(50), 0, math.radians(45))
scene.collection.objects.link(key)

# Render sozlamalari: Cycles CPU (GPU shart emas), shaffof fon
scene.render.engine = "CYCLES"
scene.cycles.device = "CPU"
scene.cycles.samples = int(os.environ.get("YTLIB_SAMPLES", "48"))
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = size
scene.render.resolution_y = size
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "Filmic" if "Filmic" in [v.identifier for v in scene.view_settings.bl_rna.properties["view_transform"].enum_items] else "Standard"

cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
cam.data.lens = 70
scene.collection.objects.link(cam)
scene.camera = cam
track = cam.constraints.new("TRACK_TO")
target = bpy.data.objects.new("target", None)
target.location = center
scene.collection.objects.link(target)
track.target = target
track.track_axis = "TRACK_NEGATIVE_Z"
track.up_axis = "UP_Y"

ANGLES = {  # (azimut, balandlik) gradusda
    "front": (0, 8), "front34": (35, 18), "side": (90, 10), "back34": (145, 18), "top34": (35, 50), "top": (0, 85),
}
fov = 2 * math.atan(18 / cam.data.lens)  # 36mm sensor
dist = radius / math.sin(fov / 2) * 1.12
for i, name in enumerate(views):
    az, el = ANGLES.get(name, (35, 18))
    a, e = math.radians(az), math.radians(el)
    cam.location = center + Vector((dist * math.sin(a) * math.cos(e), -dist * math.cos(a) * math.cos(e), dist * math.sin(e)))
    cam.data.clip_end = dist * 10
    scene.render.filepath = os.path.join(out_dir, f"view_{name}.png")
    bpy.ops.render.render(write_still=True)
print("RENDER_OK", out_dir)
