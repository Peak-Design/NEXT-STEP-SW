"""Localise the per-instance colour loss.

S2 showed instance overrides do not reach Blender. Three candidate causes:
    (a) OCCT's STEP writer never encoded them usefully,
    (b) OCCT's STEP reader does not map them back onto component labels,
    (c) STEPper NEXT's importer does not look where they landed.

This re-reads the probe file with STEPCAFControl_Reader and walks the XCAF
tree the same way importer.py does (query_color: ColorSurf > ColorGen >
ColorCurv, checked on component labels, product labels and sub-shapes). If the
colours are absent from the reader's labels, the cause is (a) or (b) and no
importer change could recover them.

Run:  blender.exe -b --factory-startup --python tools\roundtrip_check.py
"""
from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "evidence", "S1")

ADDON = os.path.join(
    os.environ["APPDATA"], "Blender Foundation", "Blender", "5.1",
    "scripts", "addons", "STEPper_NEXT")
for p in (ADDON, HERE):
    if p not in sys.path:
        sys.path.append(p)

from OCP.Quantity import Quantity_Color                        # noqa: E402
from OCP.STEPCAFControl import STEPCAFControl_Reader           # noqa: E402
from OCP.TCollection import TCollection_ExtendedString         # noqa: E402
from OCP.TDataStd import TDataStd_Name                         # noqa: E402
from OCP.TDF import TDF_LabelSequence                          # noqa: E402
from OCP.TDocStd import TDocStd_Document                       # noqa: E402
from OCP.XCAFApp import XCAFApp_Application                    # noqa: E402
from OCP.XCAFDoc import (                                      # noqa: E402
    XCAFDoc_ColorTool, XCAFDoc_ColorType, XCAFDoc_DocumentTool,
    XCAFDoc_ShapeTool)

COLOR_TYPES = [
    ("Surf", XCAFDoc_ColorType.XCAFDoc_ColorSurf),
    ("Gen", XCAFDoc_ColorType.XCAFDoc_ColorGen),
    ("Curv", XCAFDoc_ColorType.XCAFDoc_ColorCurv),
]


def entry(label):
    from OCP.TDF import TDF_Tool
    from OCP.TCollection import TCollection_AsciiString
    s = TCollection_AsciiString()
    TDF_Tool.Entry_s(label, s)
    return s.ToCString()


def name_of(label):
    from OCP.TDataStd import TDataStd_Name as N
    if label.IsAttribute(N.GetID_s()):
        attr = N()
        if label.FindAttribute(N.GetID_s(), attr):
            return attr.Get().ToExtString()
    return ""


def colours_on(color_tool, label):
    """importer.py's discipline: a separate Quantity_Color per query, because
    GetColor_s mutates the colour it is handed."""
    out = {}
    for tag, ctype in COLOR_TYPES:
        c = Quantity_Color()
        # The instance overload takes a TopoDS_Shape; for labels it is the
        # static GetColor_s -- the same call importer.py makes.
        if XCAFDoc_ColorTool.GetColor_s(label, ctype, c):
            out[tag] = (round(c.Red(), 3), round(c.Green(), 3), round(c.Blue(), 3))
    return out


def walk(shape_tool, color_tool, label, depth, rows, path="?"):
    rec = {
        "entry": entry(label),
        "depth": depth,
        "name": str(name_of(label)),
        "is_assembly": shape_tool.IsAssembly_s(label),
        "is_component": shape_tool.IsComponent_s(label),
        "is_reference": shape_tool.IsReference_s(label),
        "colours": colours_on(color_tool, label),
    }
    if rec["is_reference"]:
        from OCP.TDF import TDF_Label
        ref = TDF_Label()
        if shape_tool.GetReferredShape_s(label, ref):
            rec["refers_to"] = entry(ref)
            rec["referred_colours"] = colours_on(color_tool, ref)
    rows.append(rec)

    comps = TDF_LabelSequence()
    if shape_tool.IsAssembly_s(label):
        shape_tool.GetComponents_s(label, comps)
        for i in range(1, comps.Length() + 1):
            walk(shape_tool, color_tool, comps.Value(i), depth + 1, rows)

    subs = TDF_LabelSequence()
    XCAFDoc_ShapeTool.GetSubShapes_s(label, subs)
    for i in range(1, subs.Length() + 1):
        sub = subs.Value(i)
        cols = colours_on(color_tool, sub)
        if cols:
            rows.append({"entry": entry(sub), "depth": depth + 1,
                         "name": "<subshape>", "is_assembly": False,
                         "is_component": False, "is_reference": False,
                         "colours": cols})


def check(path):
    doc = TDocStd_Document(TCollection_ExtendedString("STEP"))
    app = XCAFApp_Application.GetApplication_s()
    app.NewDocument(TCollection_ExtendedString("MDTV-XCAF"), doc)

    reader = STEPCAFControl_Reader()
    reader.SetColorMode(True)
    reader.SetNameMode(True)
    reader.SetMatMode(True)
    reader.SetLayerMode(True)
    reader.ReadFile(path)
    reader.Transfer(doc)

    st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    ct = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())

    roots = TDF_LabelSequence()
    st.GetFreeShapes(roots)
    rows = []
    for i in range(1, roots.Length() + 1):
        walk(st, ct, roots.Value(i), 0, rows)

    # Every colour the document knows about, regardless of where it hangs.
    all_cols = TDF_LabelSequence()
    ct.GetColors(all_cols)
    palette = []
    for i in range(1, all_cols.Length() + 1):
        c = Quantity_Color()
        # A colour label holds the colour attribute directly.
        if XCAFDoc_ColorTool.GetColor_s(all_cols.Value(i), c):
            palette.append((round(c.Red(), 3), round(c.Green(), 3),
                            round(c.Blue(), 3)))
    return rows, palette


def main() -> int:
    report = {}
    for case in ("instance_col", "solid_colour", "face_colour"):
        path = os.path.join(OUT, f"{case}_ap214.step")
        rows, palette = check(path)
        report[case] = {"labels": rows, "palette": palette}

        print(f"\n=== {case} ===")
        print(f"  colour palette in document: {palette}")
        for r in rows:
            tags = "".join([
                "A" if r["is_assembly"] else " ",
                "C" if r["is_component"] else " ",
                "R" if r["is_reference"] else " ",
            ])
            extra = ""
            if "refers_to" in r:
                extra = f" -> {r['refers_to']} {r['referred_colours']}"
            print(f"  {'  ' * r['depth']}{r['entry']:<12} [{tags}] "
                  f"{r['name'][:18]:<18} {r['colours']}{extra}")

    with open(os.path.join(OUT, "roundtrip-labels.json"), "w",
              encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)

    inst = report["instance_col"]
    comp_cols = [r["colours"] for r in inst["labels"] if r["is_component"]]
    print("\n=== verdict: per-instance colour ===")
    print(f"  distinct colours in palette: {len(set(inst['palette']))}")
    print(f"  colours found on component labels: {comp_cols}")
    if any(c for c in comp_cols):
        print("  => reader DOES place instance colour on component labels;"
              " the gap is in the importer (cause c)")
    elif len(set(inst["palette"])) >= 3:
        print("  => colours survive in the file but not on component labels;"
              " OCCT's reader loses the instance association (cause b)")
    else:
        print("  => the instance colours are not in the re-read document at"
              " all; OCCT's writer did not encode them recoverably (cause a)")
    print("\nROUNDTRIP DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
