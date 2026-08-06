"""S1b: engineering material and custom-property metadata in STEP.

Three questions, from the maintainer of STEPper NEXT:

  Q1  Can AP214 carry engineering material (name/description/density), or is
      AP242 genuinely required as one user believes?
  Q2  Can we write arbitrary custom properties (part number, revision,
      supplier...) for every component?
  Q3  Does any of it survive into STEPper NEXT today?

Cases:
  eng_material   XCAFDoc_MaterialTool.SetMaterial, written to AP214 and AP242
  custom_props   property_definition + descriptive_representation_item spliced
                 onto every PRODUCT_DEFINITION (the same pattern OCCT itself
                 uses for material, so it is a known-good encoding rather than
                 an invention)

Run:  blender.exe -b --factory-startup --python tools\metadata_probe.py
"""
from __future__ import annotations

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "evidence", "S1b")
os.makedirs(OUT, exist_ok=True)

ADDON = os.path.join(
    os.environ["APPDATA"], "Blender Foundation", "Blender", "5.1",
    "scripts", "addons", "STEPper_NEXT")
for p in (ADDON, HERE):
    if p not in sys.path:
        sys.path.append(p)

import stepdump                                                # noqa: E402
import occt_probe as probe                                     # noqa: E402

from OCP.Interface import Interface_Static                     # noqa: E402
from OCP.IFSelect import IFSelect_ReturnStatus                 # noqa: E402
from OCP.STEPCAFControl import STEPCAFControl_Writer           # noqa: E402
from OCP.STEPControl import STEPControl_StepModelType          # noqa: E402
from OCP.TCollection import TCollection_HAsciiString           # noqa: E402
from OCP.XCAFDoc import XCAFDoc_DocumentTool                   # noqa: E402

CUSTOM = {
    "PartNumber": "PD-1234-A",
    "Revision": "C",
    "Supplier": "Peak Design Services Ltd",
    "FinishSpec": "Anodised, matt black",
}


# ---------------------------------------------------------------- Q1 ------
def case_eng_material(doc):
    st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    mt = XCAFDoc_DocumentTool.MaterialTool_s(doc.Main())
    lab = st.AddShape(probe.box(), False)
    mt.SetMaterial(
        lab,
        TCollection_HAsciiString("Aluminium 6061-T6"),
        TCollection_HAsciiString("Aluminium alloy, solution heat treated"),
        2700.0,                                   # density
        TCollection_HAsciiString("kg/m^3"),
        TCollection_HAsciiString("DENSITY"))
    return "XCAFDoc_MaterialTool.SetMaterial on one solid"


def write_step(doc, path, token):
    Interface_Static.SetCVal_s("write.step.schema", token)
    assert Interface_Static.CVal_s("write.step.schema") == token
    w = STEPCAFControl_Writer()
    for setter in ("SetColorMode", "SetNameMode", "SetMaterialMode",
                   "SetLayerMode", "SetPropsMode"):
        getattr(w, setter)(True)
    w.Transfer(doc, STEPControl_StepModelType.STEPControl_AsIs)
    status = w.Write(path)
    if status != IFSelect_ReturnStatus.IFSelect_RetDone:
        raise RuntimeError(f"Write returned {status}")
    return path


def material_entities(path):
    d = stepdump.parse(path)
    out = {}
    for t in ("PROPERTY_DEFINITION", "PROPERTY_DEFINITION_REPRESENTATION",
              "DESCRIPTIVE_REPRESENTATION_ITEM", "MEASURE_REPRESENTATION_ITEM"):
        ids = d.by_type(t)
        if ids:
            out[t] = [d.entities[i][1].strip()[:100] for i in ids]
    return out


# ---------------------------------------------------------------- Q2 ------
def splice_custom_props(src, dst, props):
    """Attach descriptive properties to every PRODUCT_DEFINITION, using the
    same property_definition pattern OCCT emits for material."""
    text = open(src, encoding="utf-8", errors="replace").read()
    d = stepdump.parse(src)
    pds = d.by_type("PRODUCT_DEFINITION")
    if not pds:
        raise RuntimeError("no PRODUCT_DEFINITION to attach properties to")

    # Reuse the existing geometric representation context if there is one;
    # a descriptive item needs a representation, and representations need a
    # context. Borrow the one the shape representation already uses.
    ctx = d.by_type("COMPLEX:GEOMETRIC_REPRESENTATION_CONTEXT")
    ctx_ref = f"#{ctx[0]}" if ctx else None
    if ctx_ref is None:
        raise RuntimeError("no representation context found")

    nid = max(d.entities) + 1
    lines = []
    for pd in pds:
        for key, value in props.items():
            item, rep, pdef, pdr = nid, nid + 1, nid + 2, nid + 3
            nid += 4
            esc = value.replace("'", "''")
            lines.append(f"#{item}=DESCRIPTIVE_REPRESENTATION_ITEM('{key}','{esc}');")
            lines.append(f"#{rep}=REPRESENTATION('{key}',(#{item}),{ctx_ref});")
            lines.append(f"#{pdef}=PROPERTY_DEFINITION('user defined attribute','{key}',#{pd});")
            lines.append(f"#{pdr}=PROPERTY_DEFINITION_REPRESENTATION(#{pdef},#{rep});")

    idx = text.rfind("ENDSEC;")
    out = text[:idx] + "\n".join(lines) + "\n" + text[idx:]
    with open(dst, "w", encoding="utf-8") as fh:
        fh.write(out)
    return {"product_definitions": len(pds), "entities_added": len(lines),
            "properties_per_product": len(props)}


# ---------------------------------------------------------------- Q3 ------
def blender_import(path):
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.preferences.addon_enable(module="STEPper_NEXT")
    try:
        res = list(bpy.ops.import_scene.occ_import_step(
            filepath=path, override_file=os.path.basename(path)))
    except Exception as exc:                                   # noqa: BLE001
        return {"error": f"{type(exc).__name__}: {exc}"}
    objs = []
    for o in bpy.data.objects:
        if o.type != "MESH":
            continue
        objs.append({
            "name": o.name,
            "custom_props": {k: str(o[k]) for k in o.keys()
                             if k not in ("_RNA_UI",)},
            "mesh_props": {k: str(o.data[k]) for k in o.data.keys()},
            "materials": [m.name for m in o.data.materials if m],
        })
    return {"operator": res, "objects": objs}


def main() -> int:
    report = {}

    print("=== Q1: engineering material, AP214 vs AP242 ===")
    for ap, token in probe.SCHEMAS.items():
        doc = probe.new_doc()
        desc = case_eng_material(doc)
        path = os.path.join(OUT, f"eng_material_{ap.lower()}.step")
        write_step(doc, path, token)
        ents = material_entities(path)
        report[f"eng_material_{ap}"] = ents
        print(f"  {ap}: {desc}")
        for t, vals in ents.items():
            print(f"    {t} x{len(vals)}")
            for v in vals[:2]:
                print(f"       {v}")
    same = (report.get("eng_material_AP214") == report.get("eng_material_AP242"))
    print(f"  AP214 and AP242 emission identical: {same}")

    print("\n=== Q2: custom properties on every product ===")
    src = os.path.join(ROOT, "evidence", "S1", "instance_col_ap214.step")
    dst = os.path.join(OUT, "custom_props_ap214.step")
    stats = splice_custom_props(src, dst, CUSTOM)
    print(f"  {stats}")
    before, after = stepdump.parse(src), stepdump.parse(dst)
    print(f"  entities {len(before.entities)} -> {len(after.entities)}; "
          f"faces {before.presentation()['faces']} -> {after.presentation()['faces']}")
    print(f"  PROPERTY_DEFINITION: {len(before.by_type('PROPERTY_DEFINITION'))}"
          f" -> {len(after.by_type('PROPERTY_DEFINITION'))}")
    report["custom_props"] = stats

    print("\n=== Q3: does any of it reach STEPper NEXT today? ===")
    for label, path in (
            ("eng_material AP214", os.path.join(OUT, "eng_material_ap214.step")),
            ("eng_material AP242", os.path.join(OUT, "eng_material_ap242.step")),
            ("custom_props AP214", dst)):
        res = blender_import(path)
        report[f"blender_{label}"] = res
        if "error" in res:
            print(f"  {label}: FAILED {res['error']}")
            continue
        for o in res["objects"]:
            print(f"  {label}: obj={o['name']} materials={o['materials']}")
            print(f"      object custom props: {o['custom_props']}")
            print(f"      mesh   custom props: {o['mesh_props']}")

    with open(os.path.join(OUT, "metadata-probe.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)
    print("\nMETADATA PROBE DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
