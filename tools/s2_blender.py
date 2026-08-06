"""S2, Blender arm: run every S1 probe file through STEPper NEXT and report
which appearance actually arrived.

STEPper NEXT is the primary consumer, and its reader resolves colour at three
levels (instance-override label, product label, per-face sub-shape). The S1
probe wrote at exactly those three levels, so this is the direct test of
whether our write levels land on its read levels.

Run:  blender.exe -b --factory-startup --python tools\s2_blender.py
Out:  evidence\S2\blender-consumer.json
"""
from __future__ import annotations

import glob
import json
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "evidence", "S2")
os.makedirs(OUT, exist_ok=True)

# What each case wrote, as sRGB triples, so the report can say "arrived" or not.
EXPECTED = {
    "control": [],
    "solid_colour": [(0.8, 0.1, 0.1), (0.1, 0.2, 0.9)],
    "face_colour": [(0.8, 0.1, 0.1), (0.1, 0.7, 0.2), (0.1, 0.2, 0.9)],
    "instance_col": [(0.8, 0.1, 0.1), (0.1, 0.2, 0.9), (0.1, 0.7, 0.2)],
    "vis_common": [(0.8, 0.1, 0.1)],
    "vis_pbr": [(0.1, 0.2, 0.9)],
    "combined": [(0.8, 0.1, 0.1)],
    "colour_common": [(0.1, 0.7, 0.2)],
    "common_pbr": [(0.8, 0.1, 0.1)],
}


def srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def close(a, b, tol=0.06) -> bool:
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def match_expected(found, expected):
    """Each expected colour matched by some found colour, in sRGB or linear."""
    hits = []
    for exp in expected:
        lin = tuple(srgb_to_linear(v) for v in exp)
        hit = any(close(f, exp) or close(f, lin) for f in found)
        hits.append((exp, hit))
    return hits


results = []
for path in sorted(glob.glob(os.path.join(ROOT, "evidence", "S1", "*.step"))):
    name = os.path.basename(path)
    case = name.rsplit("_ap", 1)[0]
    ap = "AP242" if "ap242" in name else "AP214"

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.preferences.addon_enable(module="STEPper_NEXT")

    rec = {"case": case, "ap": ap, "file": name}
    try:
        res = bpy.ops.import_scene.occ_import_step(
            filepath=path, override_file=name)
        rec["operator"] = list(res)
    except Exception as exc:                                   # noqa: BLE001
        rec["error"] = f"{type(exc).__name__}: {exc}"
        results.append(rec)
        print(f"[FAIL] {name}: {rec['error']}")
        continue

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    found, per_object = [], []
    for o in meshes:
        mats = []
        for m in o.data.materials:
            if m is None:
                continue
            # m.diffuse_color is only the viewport swatch; the imported colour
            # lives on the Principled BSDF. Read that, and fall back to the
            # swatch so a material with no node tree still reports something.
            rgb = tuple(round(c, 4) for c in m.diffuse_color[:3])
            alpha = round(m.diffuse_color[3], 4) if len(m.diffuse_color) > 3 else 1.0
            source = "diffuse_color"
            if m.use_nodes and m.node_tree:
                bsdf = next((n for n in m.node_tree.nodes
                             if n.type == "BSDF_PRINCIPLED"), None)
                if bsdf is not None:
                    base = bsdf.inputs.get("Base Color")
                    if base is not None:
                        rgb = tuple(round(c, 4) for c in base.default_value[:3])
                        source = "Principled.BaseColor"
                    a_in = bsdf.inputs.get("Alpha")
                    if a_in is not None:
                        alpha = round(float(a_in.default_value), 4)
            mats.append({"name": m.name, "base_color": list(rgb),
                         "alpha": alpha, "read_from": source})
            found.append(rgb)
        per_object.append({
            "object": o.name,
            "polys": len(o.data.polygons),
            "material_slots": len(o.data.materials),
            "materials": mats,
        })

    hits = match_expected(found, EXPECTED.get(case, []))
    rec.update({
        "mesh_objects": len(meshes),
        "distinct_materials": len({m["name"] for po in per_object
                                   for m in po["materials"]}),
        "objects": per_object,
        "expected_colours_arrived": [
            {"expected_srgb": list(e), "arrived": h} for e, h in hits],
        "all_expected_arrived": all(h for _e, h in hits),
    })
    results.append(rec)

    tick = "ok " if rec["all_expected_arrived"] else "MISS"
    print(f"[{tick}] {case:<13} {ap}  meshes={len(meshes)} "
          f"materials={rec['distinct_materials']} "
          f"arrived={sum(1 for _e, h in hits if h)}/{len(hits)}")

with open(os.path.join(OUT, "blender-consumer.json"), "w", encoding="utf-8") as fh:
    json.dump(results, fh, indent=2)

print("\n--- per-case detail (AP214) ---")
for r in results:
    if r["ap"] != "AP214" or "error" in r:
        continue
    slots = [f"{o['object']}:{o['material_slots']}slots" for o in r["objects"]]
    cols = sorted({tuple(m["base_color"]) for o in r["objects"]
                   for m in o["materials"]})
    print(f"    {r['case']:<13} {slots} colours={cols}")

print(f"\nwrote {os.path.join(OUT, 'blender-consumer.json')}")
print("S2 BLENDER DONE")
