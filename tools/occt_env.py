"""S1 pre-flight: confirm the vendored OCCT is importable and dump the exact
signatures the writer probe depends on.

Run:  blender.exe -b --factory-startup --python tools\occt_env.py
(Blender only supplies a CPython 3.13 matching the OCP wheel; no Blender API is
used beyond that.)
"""
import os
import sys

ADDON = os.path.join(
    os.environ["APPDATA"], "Blender Foundation", "Blender", "5.1",
    "scripts", "addons", "STEPper_NEXT")
if ADDON not in sys.path:
    sys.path.append(ADDON)

import OCP  # noqa: E402

print("OCP_VERSION:", OCP.__version__)
print("PYTHON:", sys.version.split()[0])


def members(obj, *needles):
    out = []
    for name in dir(obj):
        low = name.lower()
        if any(n in low for n in needles):
            out.append(name)
    return sorted(out)


def show(label, obj, *needles):
    print(f"\n--- {label} ---")
    for name in members(obj, *needles):
        print("   ", name)


from OCP.STEPCAFControl import STEPCAFControl_Writer          # noqa: E402
from OCP.XCAFDoc import (                                     # noqa: E402
    XCAFDoc_DocumentTool, XCAFDoc_ColorTool, XCAFDoc_ShapeTool,
    XCAFDoc_VisMaterialTool, XCAFDoc_VisMaterial,
    XCAFDoc_VisMaterialCommon, XCAFDoc_VisMaterialPBR)
from OCP.Interface import Interface_Static                    # noqa: E402

show("STEPCAFControl_Writer", STEPCAFControl_Writer,
     "transfer", "write", "mode", "material", "setc", "perform")
show("XCAFDoc_ShapeTool", XCAFDoc_ShapeTool,
     "add", "subshape", "component", "assembly")
show("XCAFDoc_ColorTool", XCAFDoc_ColorTool, "setcolor", "addcolor")
show("XCAFDoc_VisMaterialTool", XCAFDoc_VisMaterialTool,
     "add", "set", "link")
show("XCAFDoc_VisMaterial", XCAFDoc_VisMaterial,
     "pbr", "common", "set", "alpha")
show("XCAFDoc_VisMaterialPBR", XCAFDoc_VisMaterialPBR, "")
show("XCAFDoc_VisMaterialCommon", XCAFDoc_VisMaterialCommon, "")

# Which schema tokens does write.step.schema accept?
print("\n--- Interface_Static write.step.* ---")
for key in ("write.step.schema", "write.step.product.name",
            "write.step.assembly", "write.step.unit",
            "write.surfacecurve.mode", "write.step.vertex.mode"):
    try:
        print(f"    {key} = {Interface_Static.CVal_s(key)!r}")
    except Exception as exc:                                   # noqa: BLE001
        print(f"    {key}: <{type(exc).__name__}: {exc}>")

# Does the glTF writer exist for the R3 control arm?
print("\n--- glTF writer ---")
try:
    from OCP.RWGltf import RWGltf_CafWriter
    print("    RWGltf_CafWriter OK:",
          [m for m in dir(RWGltf_CafWriter) if not m.startswith("_")][:12])
except Exception as exc:                                       # noqa: BLE001
    print(f"    RWGltf_CafWriter MISSING: {type(exc).__name__}: {exc}")

# Raw presentation entities for the R1 feasibility question.
print("\n--- StepVisual reflectance/rendering classes importable? ---")
import OCP.StepVisual as SV                                    # noqa: E402
for name in ("StepVisual_SurfaceStyleRendering",
             "StepVisual_SurfaceStyleRenderingWithProperties",
             "StepVisual_SurfaceStyleReflectanceAmbient",
             "StepVisual_SurfaceStyleTransparent",
             "StepVisual_StyledItem",
             "StepVisual_ContextDependentOverRidingStyledItem",
             "StepVisual_ShadingSurfaceMethod",
             "StepVisual_RenderingPropertiesSelect"):
    print(f"    {name}: {'yes' if hasattr(SV, name) else 'NO'}")

print("\n--- STEPConstruct_Styles ---")
try:
    from OCP.STEPConstruct import STEPConstruct_Styles
    print("   ", [m for m in dir(STEPConstruct_Styles)
                  if not m.startswith("_")])
except Exception as exc:                                       # noqa: BLE001
    print(f"    MISSING: {type(exc).__name__}: {exc}")

print("\n--- XSControl_TransferReader (S4 arm B) ---")
try:
    from OCP.XSControl import XSControl_TransferReader
    print("   ", [m for m in dir(XSControl_TransferReader)
                  if "ntity" in m or "hape" in m])
except Exception as exc:                                       # noqa: BLE001
    print(f"    MISSING: {type(exc).__name__}: {exc}")

print("\nENV PROBE DONE")
