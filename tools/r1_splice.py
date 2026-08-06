"""S1 step 4: is R1 reachable by emitting the entities ourselves?

S1 showed OCCT will not write reflectance alongside per-face colour: any second
styling source suppresses the rendering properties. The proposed escape is the
S4 arm B route -- write the presentation entities into the file text directly.

This is a miniature arm B. It takes the face_colour probe (which already has
per-face colour) and splices SURFACE_STYLE_RENDERING_WITH_PROPERTIES +
SURFACE_STYLE_TRANSPARENT onto every SURFACE_SIDE_STYLE, without touching a
single byte of geometry. Then it checks that:

    1. the file still parses,
    2. OCCT still reads every colour back (so we did not corrupt it),
    3. STEPper NEXT still imports it,
    4. the rendering entities are actually present.

If all four hold, R1 is reachable via arm B and R0 and R1 can coexist.

Run:  blender.exe -b --factory-startup --python tools\r1_splice.py
"""
from __future__ import annotations

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
S1 = os.path.join(ROOT, "evidence", "S1")

ADDON = os.path.join(
    os.environ["APPDATA"], "Blender Foundation", "Blender", "5.1",
    "scripts", "addons", "STEPper_NEXT")
for p in (ADDON, HERE):
    if p not in sys.path:
        sys.path.append(p)

import stepdump                                                # noqa: E402

SRC = os.path.join(S1, "face_colour_ap214.step")
DST = os.path.join(S1, "face_colour_r1_spliced.step")

TRANSPARENCY = 0.3


def splice(src: str, dst: str) -> dict:
    text = open(src, encoding="utf-8", errors="replace").read()
    doc = stepdump.parse(src)

    next_id = max(doc.entities) + 1
    additions: list[str] = []
    edits: list[tuple[str, str]] = []

    for eid in doc.by_type("SURFACE_SIDE_STYLE"):
        _t, args = doc.entities[eid]
        # SURFACE_SIDE_STYLE('',(#fill,...)) -- find the colour this style
        # already uses so the rendering colour agrees with the fill colour.
        members = [int(x) for x in re.findall(r"#(\d+)", args)]
        colour = None
        stack = list(members)
        seen = set()
        while stack and colour is None:
            cur = stack.pop(0)
            if cur in seen:
                continue
            seen.add(cur)
            if doc.type_of(cur) == "COLOUR_RGB":
                colour = cur
            else:
                stack.extend(doc.refs(cur) if cur in doc.entities else [])
        if colour is None:
            continue

        transp_id = next_id
        rend_id = next_id + 1
        next_id += 2
        additions.append(f"#{transp_id}=SURFACE_STYLE_TRANSPARENT({TRANSPARENCY});")
        additions.append(
            f"#{rend_id}=SURFACE_STYLE_RENDERING_WITH_PROPERTIES("
            f".NORMAL_SHADING.,#{colour},(#{transp_id}));")

        # Extend this SURFACE_SIDE_STYLE's member list with the new rendering.
        old_line = f"#{eid}=SURFACE_SIDE_STYLE{args}"
        new_args = args.rstrip()
        assert new_args.endswith(")"), new_args[-40:]
        # ...,(#a,#b))  ->  ...,(#a,#b,#rend))
        close = new_args.rfind("))")
        if close < 0:
            continue
        new_line = (f"#{eid}=SURFACE_SIDE_STYLE"
                    + new_args[:close] + f",#{rend_id}" + new_args[close:])
        edits.append((old_line, new_line))

    # Apply edits. The source is written one entity per line by OCCT, but an
    # entity may wrap, so match on the normalised text rather than by line.
    out = text
    applied = 0
    for old, new in edits:
        # Rebuild the on-disk form (OCCT wraps long lines); locate by id.
        eid = old.split("=", 1)[0]
        m = re.search(rf"^{re.escape(eid)}\s*=\s*SURFACE_SIDE_STYLE.*?;",
                      out, re.MULTILINE | re.DOTALL)
        if not m:
            continue
        chunk = m.group(0)
        close = chunk.rfind("))")
        if close < 0:
            continue
        rend = new.rsplit(",#", 1)[1].split(")")[0]
        out = (out[:m.start()] + chunk[:close] + f",#{rend}" + chunk[close:]
               + out[m.end():])
        applied += 1

    # Insert the new entities just before ENDSEC of the DATA section.
    idx = out.rfind("ENDSEC;")
    out = out[:idx] + "\n".join(additions) + "\n" + out[idx:]

    with open(dst, "w", encoding="utf-8") as fh:
        fh.write(out)
    return {"side_styles_edited": applied, "entities_added": len(additions)}


def verify_occt(path: str) -> dict:
    from OCP.Quantity import Quantity_Color
    from OCP.STEPCAFControl import STEPCAFControl_Reader
    from OCP.TCollection import TCollection_ExtendedString
    from OCP.TDF import TDF_LabelSequence
    from OCP.TDocStd import TDocStd_Document
    from OCP.XCAFApp import XCAFApp_Application
    from OCP.XCAFDoc import XCAFDoc_ColorTool, XCAFDoc_DocumentTool

    doc = TDocStd_Document(TCollection_ExtendedString("STEP"))
    XCAFApp_Application.GetApplication_s().NewDocument(
        TCollection_ExtendedString("MDTV-XCAF"), doc)
    r = STEPCAFControl_Reader()
    r.SetColorMode(True)
    r.SetNameMode(True)
    status = r.ReadFile(path)
    r.Transfer(doc)
    ct = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())
    labs = TDF_LabelSequence()
    ct.GetColors(labs)
    cols = []
    for i in range(1, labs.Length() + 1):
        c = Quantity_Color()
        if XCAFDoc_ColorTool.GetColor_s(labs.Value(i), c):
            cols.append((round(c.Red(), 3), round(c.Green(), 3),
                         round(c.Blue(), 3)))
    return {"read_status": str(status), "colours": sorted(cols)}


def verify_blender(path: str) -> dict:
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.preferences.addon_enable(module="STEPper_NEXT")
    res = bpy.ops.import_scene.occ_import_step(
        filepath=path, override_file=os.path.basename(path))
    mats = []
    for o in bpy.data.objects:
        if o.type != "MESH":
            continue
        for m in o.data.materials:
            if m is None or not m.use_nodes:
                continue
            b = next((n for n in m.node_tree.nodes
                      if n.type == "BSDF_PRINCIPLED"), None)
            if b is None:
                continue
            mats.append({
                "name": m.name,
                "base": [round(c, 4) for c in b.inputs["Base Color"].default_value[:3]],
                "alpha": round(float(b.inputs["Alpha"].default_value), 4),
            })
    return {"operator": list(res),
            "meshes": len([o for o in bpy.data.objects if o.type == "MESH"]),
            "materials": mats}


def main() -> int:
    print("=== splice ===")
    stats = splice(SRC, DST)
    print(f"  {stats}")

    print("\n=== 1. does it still parse, and what did we add? ===")
    before = stepdump.presentation(SRC)
    after = stepdump.presentation(DST)
    print(f"  before: {before['presentation_counts']}")
    print(f"  after : {after['presentation_counts']}")
    added = {k: after["presentation_counts"].get(k, 0) - before["presentation_counts"].get(k, 0)
             for k in set(after["presentation_counts"]) | set(before["presentation_counts"])}
    print(f"  added : { {k: v for k, v in added.items() if v} }")

    print("\n=== 2. geometry untouched? ===")
    geo_ok = (before["faces"] == after["faces"]
              and before["solids"] == after["solids"])
    print(f"  faces {before['faces']} -> {after['faces']}, "
          f"solids {before['solids']} -> {after['solids']}: "
          f"{'UNCHANGED' if geo_ok else 'CHANGED'}")

    print("\n=== 3. OCCT still reads every colour? ===")
    occt_before = verify_occt(SRC)
    occt_after = verify_occt(DST)
    print(f"  before: {occt_before}")
    print(f"  after : {occt_after}")
    colours_ok = occt_before["colours"] == occt_after["colours"]
    print(f"  colours preserved: {colours_ok}")

    print("\n=== 4. STEPper NEXT still imports? ===")
    bl = verify_blender(DST)
    print(f"  {bl['operator']} meshes={bl['meshes']}")
    for m in bl["materials"]:
        print(f"    {m['name']:<12} base={m['base']} alpha={m['alpha']}")

    verdict = (geo_ok and colours_ok
               and added.get("SURFACE_STYLE_RENDERING_WITH_PROPERTIES", 0) > 0
               and "FINISHED" in bl["operator"])
    print("\n=== verdict ===")
    print("  R1 via textual emission (arm B): "
          + ("REACHABLE -- rendering properties added alongside per-face "
             "colour, geometry untouched, all readers still fine"
             if verdict else "NOT DEMONSTRATED -- see above"))

    with open(os.path.join(S1, "r1-splice.json"), "w", encoding="utf-8") as fh:
        json.dump({"stats": stats, "before": before, "after": after,
                   "occt_before": occt_before, "occt_after": occt_after,
                   "blender": bl, "verdict": bool(verdict)}, fh, indent=2)
    print("\nR1 SPLICE DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
