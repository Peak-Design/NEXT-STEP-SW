"""S1 writer probe: what does OCCT 7.9.3 actually emit into a STEP file?

The question this answers is NOT "what can you set on an XCAF document" — it is
"what set values survive into file bytes, and as which entities". So every case
is written to disk and then re-read as text by stepdump.py, and each case is
diffed against a no-appearance control built from identical geometry.

Cases (each x AP214 x AP242):
    control        two boxes, no appearance at all
    solid_colour   colour on the shape label            (XCAFDoc_ColorSurf)
    face_colour    colour on one face sub-shape label
    instance_col   assembly, colour on ONE component label   <- the case
                   SolidWorks gets wrong
    vis_common     XCAFDoc_VisMaterialCommon (ambient/diffuse/specular/
                   shininess/transparency)
    vis_pbr        XCAFDoc_VisMaterialPBR (base colour, metallic, roughness)
    combined       colour + common + pbr together

Run:  blender.exe -b --factory-startup --python tools\occt_probe.py
Out:  evidence\S1\*.step  and  evidence\S1\probe-matrix.json
"""
from __future__ import annotations

import json
import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "evidence", "S1")
os.makedirs(OUT, exist_ok=True)

ADDON = os.path.join(
    os.environ["APPDATA"], "Blender Foundation", "Blender", "5.1",
    "scripts", "addons", "STEPper_NEXT")
for p in (ADDON, HERE):
    if p not in sys.path:
        sys.path.append(p)

import stepdump  # noqa: E402

from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox                # noqa: E402
from OCP.Interface import Interface_Static                     # noqa: E402
from OCP.IFSelect import IFSelect_ReturnStatus                 # noqa: E402
from OCP.Quantity import (                                     # noqa: E402
    Quantity_Color, Quantity_ColorRGBA, Quantity_TOC_sRGB)
from OCP.STEPCAFControl import STEPCAFControl_Writer           # noqa: E402
from OCP.STEPControl import (                                  # noqa: E402
    STEPControl_Controller, STEPControl_StepModelType)
from OCP.TCollection import (                                  # noqa: E402
    TCollection_AsciiString, TCollection_ExtendedString)
from OCP.TDocStd import TDocStd_Document                       # noqa: E402
from OCP.TopAbs import TopAbs_ShapeEnum                        # noqa: E402
from OCP.TopExp import TopExp_Explorer                         # noqa: E402
from OCP.TopLoc import TopLoc_Location                         # noqa: E402
from OCP.TopoDS import TopoDS                                  # noqa: E402
from OCP.XCAFApp import XCAFApp_Application                    # noqa: E402
from OCP.XCAFDoc import (                                      # noqa: E402
    XCAFDoc_ColorType, XCAFDoc_DocumentTool, XCAFDoc_VisMaterial,
    XCAFDoc_VisMaterialCommon, XCAFDoc_VisMaterialPBR)
from OCP.gp import gp_Trsf, gp_Vec                             # noqa: E402

STEPControl_Controller.Init_s()

SCHEMAS = {"AP214": "AP214IS", "AP242": "AP242DIS"}

RED = Quantity_Color(0.8, 0.1, 0.1, Quantity_TOC_sRGB)
BLUE = Quantity_Color(0.1, 0.2, 0.9, Quantity_TOC_sRGB)
GREEN = Quantity_Color(0.1, 0.7, 0.2, Quantity_TOC_sRGB)


# --------------------------------------------------------------------------
# document construction
# --------------------------------------------------------------------------
def new_doc():
    doc = TDocStd_Document(TCollection_ExtendedString("STEP"))
    app = XCAFApp_Application.GetApplication_s()
    app.NewDocument(TCollection_ExtendedString("MDTV-XCAF"), doc)
    return doc


def tools(doc):
    return (XCAFDoc_DocumentTool.ShapeTool_s(doc.Main()),
            XCAFDoc_DocumentTool.ColorTool_s(doc.Main()),
            XCAFDoc_DocumentTool.VisMaterialTool_s(doc.Main()))


def box(dx=10.0, dy=10.0, dz=10.0):
    return BRepPrimAPI_MakeBox(dx, dy, dz).Shape()


def faces_of(shape):
    out, exp = [], TopExp_Explorer(shape, TopAbs_ShapeEnum.TopAbs_FACE)
    while exp.More():
        out.append(TopoDS.Face_s(exp.Current()))
        exp.Next()
    return out


def moved(shape, dx):
    trsf = gp_Trsf()
    trsf.SetTranslation(gp_Vec(dx, 0.0, 0.0))
    return shape.Moved(TopLoc_Location(trsf))


def vis_material(common=False, pbr=False):
    vm = XCAFDoc_VisMaterial()
    if common:
        c = XCAFDoc_VisMaterialCommon()
        c.IsDefined = True
        c.AmbientColor = Quantity_Color(0.1, 0.1, 0.1, Quantity_TOC_sRGB)
        c.DiffuseColor = RED
        c.SpecularColor = Quantity_Color(0.9, 0.9, 0.9, Quantity_TOC_sRGB)
        c.EmissiveColor = Quantity_Color(0.0, 0.0, 0.0, Quantity_TOC_sRGB)
        c.Shininess = 0.35
        c.Transparency = 0.25
        vm.SetCommonMaterial(c)
    if pbr:
        p = XCAFDoc_VisMaterialPBR()
        p.IsDefined = True
        p.BaseColor = Quantity_ColorRGBA(BLUE, 1.0)
        p.Metallic = 0.9
        p.Roughness = 0.15
        p.RefractionIndex = 1.45
        vm.SetPbrMaterial(p)
    return vm


# --------------------------------------------------------------------------
# the cases
# --------------------------------------------------------------------------
def case_control(doc):
    st, _ct, _vt = tools(doc)
    st.AddShape(box(), False)
    st.AddShape(moved(box(), 20.0), False)
    return "two boxes, no appearance"


def case_solid_colour(doc):
    st, ct, _vt = tools(doc)
    a = st.AddShape(box(), False)
    b = st.AddShape(moved(box(), 20.0), False)
    ct.SetColor(a, RED, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    ct.SetColor(b, BLUE, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    return "colour on each shape label (ColorSurf)"


def case_face_colour(doc):
    st, ct, _vt = tools(doc)
    shape = box()
    lab = st.AddShape(shape, False)
    fs = faces_of(shape)
    ct.SetColor(lab, BLUE, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    for i, f in enumerate(fs[:3]):
        sub = st.AddSubShape(lab, f)
        if sub.IsNull():
            continue
        ct.SetColor(sub, [RED, GREEN, BLUE][i],
                    XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    return f"solid colour + per-face colour on 3 of {len(fs)} faces"


def case_instance_colour(doc):
    """The case that matters: two instances of ONE part, different colours."""
    st, ct, _vt = tools(doc)
    part = st.AddShape(box(), False)          # the referred product
    asm = st.NewShape()                       # the assembly
    t1, t2 = gp_Trsf(), gp_Trsf()
    t2.SetTranslation(gp_Vec(20.0, 0.0, 0.0))
    c1 = st.AddComponent(asm, part, TopLoc_Location(t1))
    c2 = st.AddComponent(asm, part, TopLoc_Location(t2))
    ct.SetColor(part, GREEN, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    ct.SetColor(c1, RED, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    ct.SetColor(c2, BLUE, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    st.UpdateAssemblies()
    return "assembly: green part, instance overrides red/blue"


def case_vis_common(doc):
    st, _ct, vt = tools(doc)
    lab = st.AddShape(box(), False)
    mat = vt.AddMaterial(vis_material(common=True),
                         TCollection_AsciiString("PeakCommon"))
    vt.SetShapeMaterial(lab, mat)
    return "XCAFDoc_VisMaterialCommon only"


def case_vis_pbr(doc):
    st, _ct, vt = tools(doc)
    lab = st.AddShape(box(), False)
    mat = vt.AddMaterial(vis_material(pbr=True),
                         TCollection_AsciiString("PeakPBR"))
    vt.SetShapeMaterial(lab, mat)
    return "XCAFDoc_VisMaterialPBR only (metallic 0.9, roughness 0.15)"


def case_combined(doc):
    st, ct, vt = tools(doc)
    lab = st.AddShape(box(), False)
    ct.SetColor(lab, RED, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    mat = vt.AddMaterial(vis_material(common=True, pbr=True),
                         TCollection_AsciiString("PeakBoth"))
    vt.SetShapeMaterial(lab, mat)
    return "colour + common + pbr on the same shape"


def case_colour_common(doc):
    """Disambiguator: does an explicit ColorTool colour suppress the common
    material's rendering properties?"""
    st, ct, vt = tools(doc)
    lab = st.AddShape(box(), False)
    ct.SetColor(lab, GREEN, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    mat = vt.AddMaterial(vis_material(common=True),
                         TCollection_AsciiString("PeakColourCommon"))
    vt.SetShapeMaterial(lab, mat)
    return "ColorTool colour + VisMaterialCommon (no PBR)"


def case_common_pbr(doc):
    """Disambiguator: does adding PBR suppress the common material's
    rendering properties?"""
    st, _ct, vt = tools(doc)
    lab = st.AddShape(box(), False)
    mat = vt.AddMaterial(vis_material(common=True, pbr=True),
                         TCollection_AsciiString("PeakCommonPBR"))
    vt.SetShapeMaterial(lab, mat)
    return "VisMaterialCommon + VisMaterialPBR (no ColorTool colour)"


CASES = [
    ("control", case_control),
    ("solid_colour", case_solid_colour),
    ("face_colour", case_face_colour),
    ("instance_col", case_instance_colour),
    ("vis_common", case_vis_common),
    ("vis_pbr", case_vis_pbr),
    ("combined", case_combined),
    ("colour_common", case_colour_common),
    ("common_pbr", case_common_pbr),
]


# --------------------------------------------------------------------------
# writing
# --------------------------------------------------------------------------
def write_step(doc, path, schema_token):
    Interface_Static.SetCVal_s("write.step.schema", schema_token)
    got = Interface_Static.CVal_s("write.step.schema")
    if got != schema_token:
        raise RuntimeError(f"schema not applied: asked {schema_token!r}, "
                           f"Interface_Static reports {got!r}")

    w = STEPCAFControl_Writer()
    w.SetColorMode(True)
    w.SetNameMode(True)
    w.SetMaterialMode(True)
    w.SetLayerMode(True)
    w.SetPropsMode(True)

    try:
        w.Transfer(doc, STEPControl_StepModelType.STEPControl_AsIs)
    except TypeError:
        w.Transfer(doc)

    status = w.Write(path)
    if status != IFSelect_ReturnStatus.IFSelect_RetDone:
        raise RuntimeError(f"Write returned {status}")
    return path


def main() -> int:
    results = []
    for case_name, builder in CASES:
        for schema_name, token in SCHEMAS.items():
            rec = {"case": case_name, "ap": schema_name}
            path = os.path.join(OUT, f"{case_name}_{schema_name.lower()}.step")
            try:
                doc = new_doc()
                rec["description"] = builder(doc)
                write_step(doc, path, token)
                rec.update(stepdump.presentation(path))
                rec["ok"] = True
            except Exception as exc:                          # noqa: BLE001
                rec["ok"] = False
                rec["error"] = f"{type(exc).__name__}: {exc}"
                rec["traceback"] = traceback.format_exc(limit=6)
            results.append(rec)
            flag = "ok " if rec.get("ok") else "FAIL"
            pres = rec.get("presentation_counts", {})
            print(f"[{flag}] {case_name:<13} {schema_name}  "
                  f"entities={rec.get('total_entities', '-')}  "
                  f"presentation={pres if pres else '{}'}"
                  + ("" if rec.get("ok") else f"  {rec.get('error')}"))

    matrix = os.path.join(OUT, "probe-matrix.json")
    with open(matrix, "w", encoding="utf-8") as fh:
        json.dump(results, fh, indent=2)
    print(f"\nwrote {matrix}")

    # Every control emits zero presentation entities (checked below), so a
    # case's absolute presentation count IS what the appearance added. The
    # cases do not all share geometry, so a numeric delta against the control
    # would compare unlike files; assert the control is empty instead.
    for r in results:
        if r["case"] == "control" and r.get("presentation_counts"):
            print(f"  WARNING: control ({r['ap']}) emitted presentation "
                  f"entities: {r['presentation_counts']}")

    print("\n--- appearance actually emitted, by case ---")
    for r in results:
        if r["case"] == "control" or not r.get("ok"):
            continue
        pres = r.get("presentation_counts", {})
        rich = {k: v for k, v in pres.items()
                if "RENDERING" in k or "TRANSPARENT" in k or "REFLECTANCE" in k}
        print(f"    {r['case']:<13} {r['ap']:<6} "
              f"styled={pres.get('STYLED_ITEM', 0)}"
              f"+{pres.get('OVER_RIDING_STYLED_ITEM', 0)}ovr"
              f"+{pres.get('CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM', 0)}ctx  "
              f"colours={len(r.get('distinct_colours', []))}  "
              f"rich={rich if rich else 'NONE'}")

    print("\nPROBE DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
