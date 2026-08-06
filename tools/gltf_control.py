"""S1 cross-write control + colour-precedence disambiguation.

Two questions:

1. In the colour_common case both an XCAFDoc colour (green) and a
   VisMaterialCommon (red diffuse) were set. Which one reached the file?
   That tells us which channel wins when they compete.

2. Write the SAME PBR-laden XCAF document to glTF via RWGltf_CafWriter.
   If metallic/roughness reach the glTF and are absent from the STEP, the
   loss is demonstrably in the STEP writer, not in our document.

Run:  blender.exe -b --factory-startup --python tools\gltf_control.py
"""
from __future__ import annotations

import json
import os
import struct
import sys

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

import stepdump                                                # noqa: E402
import occt_probe as probe                                     # noqa: E402

from OCP.BRepMesh import BRepMesh_IncrementalMesh              # noqa: E402
from OCP.Message import Message_ProgressRange                  # noqa: E402
from OCP.RWGltf import RWGltf_CafWriter                        # noqa: E402
from OCP.TColStd import TColStd_IndexedDataMapOfStringString   # noqa: E402
from OCP.TCollection import TCollection_AsciiString            # noqa: E402
from OCP.XCAFDoc import XCAFDoc_DocumentTool                   # noqa: E402

print("=== 1. colour precedence: XCAFDoc colour vs VisMaterialCommon ===")
path = os.path.join(OUT, "colour_common_ap214.step")
s = stepdump.parse(path)
GREEN = (0.1, 0.7, 0.2)   # set via ColorTool
RED = (0.8, 0.1, 0.1)     # set as VisMaterialCommon.DiffuseColor
for eid in s.by_type("COLOUR_RGB"):
    nums = [float(x) for x in
            __import__("re").findall(r"-?\d+\.?\d*(?:E[-+]?\d+)?",
                                     s.entities[eid][1].split("'", 2)[-1])]
    got = tuple(round(v, 2) for v in nums[:3])
    which = ("ColorTool GREEN" if abs(got[1] - 0.7) < 0.05 else
             "VisMaterialCommon RED" if abs(got[0] - 0.8) < 0.05 else
             "unrecognised")
    print(f"  #{eid} COLOUR_RGB {got} -> {which}")

print("\n=== 2. same PBR document -> glTF ===")
doc = probe.new_doc()
desc = probe.case_vis_pbr(doc)
print(f"  document: {desc}")

# glTF needs a triangulation; STEP did not.
st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
from OCP.TDF import TDF_LabelSequence                          # noqa: E402
labs = TDF_LabelSequence()
st.GetFreeShapes(labs)
for i in range(1, labs.Length() + 1):
    BRepMesh_IncrementalMesh(st.GetShape_s(labs.Value(i)), 0.5, False, 0.5, True)

glb = os.path.join(OUT, "vis_pbr.glb")
writer = RWGltf_CafWriter(TCollection_AsciiString(glb), True)
ok = writer.Perform(doc, TColStd_IndexedDataMapOfStringString(),
                    Message_ProgressRange())
print(f"  RWGltf_CafWriter.Perform -> {ok}; wrote {os.path.basename(glb)} "
      f"({os.path.getsize(glb)} bytes)")

# Pull the JSON chunk out of the .glb and report the material block.
with open(glb, "rb") as fh:
    data = fh.read()
magic, version, _length = struct.unpack_from("<III", data, 0)
assert magic == 0x46546C67, "not a glb"
clen, ctype = struct.unpack_from("<II", data, 12)
chunk = data[20:20 + clen]
gltf = json.loads(chunk.decode("utf-8"))
mats = gltf.get("materials", [])
print(f"  glTF version {version}, materials: {len(mats)}")
print("  material block:")
print("   ", json.dumps(mats, indent=2).replace("\n", "\n    "))

print("\n=== 3. verdict ===")
step_txt = open(os.path.join(OUT, "vis_pbr_ap214.step"),
                encoding="utf-8", errors="replace").read().upper()
step_has = any(n in step_txt for n in ("METALLIC", "ROUGH"))
pbr_block = mats[0].get("pbrMetallicRoughness", {}) if mats else {}
gltf_has = ("metallicFactor" in pbr_block or "roughnessFactor" in pbr_block
            or "baseColorFactor" in pbr_block)
print(f"  metallic/roughness present in STEP: {step_has}")
print(f"  metallic/roughness present in glTF: {gltf_has}  {pbr_block}")
print("  => loss is in the STEP writer, not the document"
      if gltf_has and not step_has else
      "  => inconclusive, inspect manually")

with open(os.path.join(OUT, "gltf-control.json"), "w", encoding="utf-8") as fh:
    json.dump({"gltf_materials": mats, "step_has_pbr": step_has,
               "gltf_has_pbr": bool(gltf_has)}, fh, indent=2)
print("\nGLTF CONTROL DONE")
