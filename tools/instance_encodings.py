"""The decisive experiment: which per-instance colour encoding do consumers honour?

Fusion 360 and STEPper NEXT both ignore OCCT's default encoding for
per-instance colour (OVER_RIDING_STYLED_ITEM + PRESENTATION_STYLE_BY_CONTEXT),
rendering every occurrence in the referred part's colour. Since per-instance
colour is the project's headline feature, we need an encoding that survives.

Three candidates, written as three files with identical geometry and the same
intent -- two instances of one box, one red and one blue:

  A  occt_default    what STEPCAFControl_Writer produces (known to fail)
  B  context_dep     CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM bound to the
                     NEXT_ASSEMBLY_USAGE_OCCURRENCE. This is the entity ISO
                     10303 defines for exactly this case, and neither OCCT nor
                     SolidWorks emits it.
  C  deinstanced     each occurrence is its own product with its own solid and
                     a plain STYLED_ITEM. Cannot fail -- it is just per-solid
                     colour, which every consumer reads -- at the cost of
                     duplicated geometry and lost instancing.

Run:  blender.exe -b --factory-startup --python tools\instance_encodings.py
Out:  evidence\S2\instance_*.step  (open these in Fusion / Rhino / Blender)
"""
from __future__ import annotations

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "evidence", "S2")
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
from OCP.TopLoc import TopLoc_Location                         # noqa: E402
from OCP.XCAFDoc import XCAFDoc_ColorType, XCAFDoc_DocumentTool  # noqa: E402
from OCP.gp import gp_Trsf, gp_Vec                             # noqa: E402

RED = probe.RED
BLUE = probe.BLUE
GREEN = probe.GREEN


def write(doc, path):
    Interface_Static.SetCVal_s("write.step.schema", "AP214IS")
    w = STEPCAFControl_Writer()
    w.SetColorMode(True)
    w.SetNameMode(True)
    w.Transfer(doc, STEPControl_StepModelType.STEPControl_AsIs)
    if w.Write(path) != IFSelect_ReturnStatus.IFSelect_RetDone:
        raise RuntimeError("write failed")
    return path


# ---------------------------------------------------------------- A ------
def make_occt_default(path):
    """Exactly the instance_col probe: the encoding known to fail."""
    doc = probe.new_doc()
    probe.case_instance_colour(doc)
    return write(doc, path)


# ---------------------------------------------------------------- C ------
def make_deinstanced(path):
    """Two independent products, each with its own solid and colour.

    No shared referred product, so there is nothing to override and nothing a
    consumer can get wrong. This is the safe fallback.
    """
    doc = probe.new_doc()
    st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    ct = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())

    a = st.AddShape(probe.box(), False)
    b = st.AddShape(probe.moved(probe.box(), 20.0), False)
    ct.SetColor(a, RED, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    ct.SetColor(b, BLUE, XCAFDoc_ColorType.XCAFDoc_ColorSurf)
    return write(doc, path)


# ---------------------------------------------------------------- B ------
def make_context_dependent(src, dst):
    """Rewrite OCCT's OVER_RIDING_STYLED_ITEMs as
    CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEMs bound to the NAUOs.

    EXPRESS (ISO 10303-46):
        context_dependent_over_riding_styled_item
            SUBTYPE OF (over_riding_styled_item);
            style_context : LIST [1:?] OF product_definition_relationship;

    so the serialised form is
        CDORSI(name, styles, item, over_ridden_style, (nauo));
    """
    text = open(src, encoding="utf-8", errors="replace").read()
    d = stepdump.parse(src)

    nauos = d.by_type("NEXT_ASSEMBLY_USAGE_OCCURRENCE")
    overriding = d.by_type("OVER_RIDING_STYLED_ITEM")
    if not nauos:
        raise RuntimeError("no NAUO in source")
    if not overriding:
        raise RuntimeError("no OVER_RIDING_STYLED_ITEM in source")

    # Pair each overriding styled item with an occurrence. OCCT emitted one
    # per instance plus a spare; bind in order and drop any surplus.
    pairs = list(zip(overriding, nauos))
    print(f"    {len(overriding)} overriding styled items, {len(nauos)} occurrences"
          f" -> binding {len(pairs)}")

    out = text
    for eid, nauo in pairs:
        m = re.search(rf"^#{eid}\s*=\s*OVER_RIDING_STYLED_ITEM(.*?);",
                      out, re.MULTILINE | re.DOTALL)
        if not m:
            continue
        args = m.group(1).strip()
        # args looks like: ('overriding color',(#401),#37,#392)
        assert args.startswith("(") and args.endswith(")"), args[:60]
        new = (f"#{eid}=CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM"
               f"{args[:-1]},(#{nauo}));")
        out = out[:m.start()] + new + out[m.end():]

    # Any overriding styled item we could not bind is left as-is; report it.
    with open(dst, "w", encoding="utf-8") as fh:
        fh.write(out)
    return dst


def main() -> int:
    a = make_occt_default(os.path.join(OUT, "instanceA_occt_default.step"))
    print(f"A occt_default   -> {os.path.basename(a)}")

    c = make_deinstanced(os.path.join(OUT, "instanceC_deinstanced.step"))
    print(f"C deinstanced    -> {os.path.basename(c)}")

    b = make_context_dependent(a, os.path.join(OUT, "instanceB_context_dep.step"))
    print(f"B context_dep    -> {os.path.basename(b)}")

    print("\n--- what is in each file ---")
    for label, path in (("A occt_default", a), ("B context_dep", b),
                        ("C deinstanced", c)):
        p = stepdump.presentation(path)
        pc = p["presentation_counts"]
        print(f"  {label:<16} styled={pc.get('STYLED_ITEM', 0)} "
              f"ovr={pc.get('OVER_RIDING_STYLED_ITEM', 0)} "
              f"ctx={pc.get('CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM', 0)} "
              f"colours={len(p['distinct_colours'])} "
              f"occurrences={p['occurrences']} solids={p['solids']}")

    print("\n--- does OCCT still read each one? ---")
    sys.path.insert(0, HERE)
    import roundtrip_check as rt
    for label, path in (("A occt_default", a), ("B context_dep", b),
                        ("C deinstanced", c)):
        try:
            rows, palette = rt.check(path)
            comp = [r["colours"] for r in rows if r["is_component"] and r["colours"]]
            print(f"  {label:<16} palette={palette} component-colours={comp}")
        except Exception as exc:                               # noqa: BLE001
            print(f"  {label:<16} FAILED: {type(exc).__name__}: {exc}")

    print("\nOpen all three in Fusion 360. Expected if the encoding works:")
    print("  ONE RED CUBE AND ONE BLUE CUBE.  Two green cubes = override ignored.")
    print("\nINSTANCE ENCODINGS DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
