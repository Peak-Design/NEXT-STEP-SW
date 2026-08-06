"""Can Blender show per-instance colour WITHOUT duplicating meshes or materials?

If STEPper NEXT is the priority consumer, the exporter should keep OCCT's
native encoding (which round-trips correctly, evidence/S1/roundtrip-labels.json)
and the importer should apply the component-label override. The recommended
mechanism is one shared material driven by per-object colour via an
Object Info -> Color node.

That was recommended but not verified. This checks it in Blender 5.1 for the
two hierarchy modes STEPper NEXT offers, because they behave differently:

  1. linked duplicates  -- two objects, shared mesh, shared material
  2. collection instance -- an empty instancing a collection

The question for each: does Object Info -> Color resolve per instance, so one
material can render two colours?

Run:  blender.exe -b --factory-startup --python tools\blender_instance_colour.py
"""
from __future__ import annotations

import json
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "evidence", "S2")
os.makedirs(OUT, exist_ok=True)

RED = (0.8, 0.1, 0.1, 1.0)
BLUE = (0.1, 0.2, 0.9, 1.0)


def shared_material():
    """One material whose Base Color comes from the object, not the material."""
    mat = bpy.data.materials.new("PeakShared")
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    info = nt.nodes.new("ShaderNodeObjectInfo")
    nt.links.new(info.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def render_probe(scene_name, pixel_samples):
    """Render tiny and report the colour at sample points."""
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 8
    scene.render.resolution_x = 200
    scene.render.resolution_y = 100
    scene.render.film_transparent = True
    # A dim render makes absolute channel differences tiny; lift exposure so
    # the sampled values are comparable to the colours that were set.
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.exposure = 4.0
    path = os.path.join(OUT, f"instcol_{scene_name}.png")
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)

    img = bpy.data.images.load(path)
    px = list(img.pixels)
    w = img.size[0]
    out = {}
    for label, (x, y) in pixel_samples.items():
        i = (y * w + x) * 4
        out[label] = [round(v, 3) for v in px[i:i + 3]]
    bpy.data.images.remove(img)
    return out


def setup_camera_and_light():
    bpy.ops.object.camera_add(location=(0, -12, 0), rotation=(1.5708, 0, 0))
    bpy.context.scene.camera = bpy.context.object
    bpy.ops.object.light_add(type="SUN", location=(0, -10, 10))
    bpy.context.object.data.energy = 5.0


results = {}

# ---------------------------------------------------------------- 1 ------
bpy.ops.wm.read_factory_settings(use_empty=True)
mat = shared_material()

bpy.ops.mesh.primitive_cube_add(size=2, location=(-3, 0, 0))
a = bpy.context.object
a.data.materials.append(mat)
a.color = RED

b = a.copy()               # linked duplicate: SAME mesh datablock
b.location = (3, 0, 0)
b.color = BLUE
bpy.context.collection.objects.link(b)

setup_camera_and_light()
shared_mesh = a.data is b.data
mat_count = len(bpy.data.materials)
samples = render_probe("linked_duplicates", {"left": (50, 50), "right": (150, 50)})
results["linked_duplicates"] = {
    "shared_mesh": shared_mesh,
    "material_datablocks": mat_count,
    "mesh_datablocks": len({o.data.name for o in bpy.data.objects if o.type == "MESH"}),
    "sampled": samples,
}

# ---------------------------------------------------------------- 2 ------
bpy.ops.wm.read_factory_settings(use_empty=True)
mat = shared_material()

src = bpy.data.collections.new("PartCollection")
bpy.context.scene.collection.children.link(src)
bpy.ops.mesh.primitive_cube_add(size=2, location=(0, 0, 0))
cube = bpy.context.object
cube.data.materials.append(mat)
for c in list(cube.users_collection):
    c.objects.unlink(cube)
src.objects.link(cube)

for x, colour, name in ((-3, RED, "instRed"), (3, BLUE, "instBlue")):
    e = bpy.data.objects.new(name, None)
    e.instance_type = "COLLECTION"
    e.instance_collection = src
    e.location = (x, 0, 0)
    e.color = colour
    bpy.context.scene.collection.objects.link(e)

setup_camera_and_light()
samples = render_probe("collection_instances", {"left": (50, 50), "right": (150, 50)})
results["collection_instances"] = {
    "instancer_colours_set": True,
    "sampled": samples,
}

# ---------------------------------------------------------------- report --
print("\n=== per-instance colour from ONE shared material ===")
for mode, rec in results.items():
    left = rec["sampled"]["left"]
    right = rec["sampled"]["right"]
    # Compare which channel dominates rather than absolute magnitude: a dim
    # render can be correct in hue while every channel is near zero.
    def dominant(c):
        return max(range(3), key=lambda i: c[i]) if max(c) > 1e-6 else None
    differ = (dominant(left) is not None
              and dominant(right) is not None
              and dominant(left) != dominant(right))
    rec["dominant_left"] = dominant(left)
    rec["dominant_right"] = dominant(right)
    rec["renders_two_colours"] = differ
    print(f"\n  {mode}")
    for k, v in rec.items():
        if k != "sampled":
            print(f"      {k}: {v}")
    print(f"      left={left}  right={right}")
    print(f"      => {'TWO COLOURS from one material' if differ else 'SAME colour -- mechanism does NOT work here'}")

with open(os.path.join(OUT, "blender-instance-colour.json"), "w", encoding="utf-8") as fh:
    json.dump(results, fh, indent=2)
print("\nBLENDER INSTANCE COLOUR DONE")
