# NEXT-STEP-SW — Findings

Rich-appearance STEP export from SolidWorks. Feasibility study; see `PLAN.md`
for the spike definitions and the go/no-go criteria fixed before any spike ran.

**Status: S1 and S2 (Blender arm) complete. S0, S3–S6 not yet run — they need
the corpus models, which must be authored in SolidWorks by hand (`corpus/CORPUS.md`).**

**House rule: every claim below cites a file in `evidence/`.** Nothing here is
from memory or from vendor documentation alone.

---

## 1. Verdict so far

**Provisional: GO-REDUCED, at rung R0 + R3.**

Per-face, per-solid and per-instance colour can be written into conformant
AP214 *and* AP242 and read back correctly — that is the defect we set out to
fix, and it is reachable. Everything above plain colour degrades sharply:
OCCT emits only transparency as a rendering property, and it does so *only*
when nothing else is styling the shape. Textures, UVs, metallic and roughness
do not exist in STEP at all and are not recoverable by any route.

Two findings change the shape of the production design and are stated up front
because they were not anticipated in the plan:

1. **Colour and reflectance are mutually exclusive per shape** through the
   OCCT/XCAF path (§3.2). Since per-face and per-instance colour is the whole
   point, R1 is unreachable through `STEPCAFControl_Writer` — but it *is*
   reachable by emitting the entities into the file text ourselves, which is
   the S4 arm B (textual splice) route, demonstrated working in §3.4. So arm B
   is now favoured on capability grounds as well as geometry-fidelity grounds,
   before S4 has even run.
2. **STEPper NEXT — our primary consumer — drops per-instance colour on
   import** (§4.2). The overrides are present and correct in the file and on
   OCCT's re-read labels; the importer simply does not apply them. So the
   exporter would be undermined on its most important consumer until STEPper
   NEXT is fixed. It is the same class of defect as the SolidWorks one this
   project exists to fix, at the other end of the pipe.

A third finding, from questions raised after the study opened (§3b): **engineering
material metadata writes identically to AP214 and AP242 and STEPper NEXT already
reads it**, so the exporter can supply the CAD-material feature users have asked
for without any MBD licence. Custom properties are also writable but currently
overwrite that material in every OCCT-based reader, so they need a discriminator
agreed before either ships.

**The premise check has now passed** (§2): SolidWorks does lose component- and
assembly-level overrides, proven at entity level against ground-truth
screenshots, and AP242 is confirmed licence-blocked (status 4 on all 9 models).
It is *not* deficient at per-face export, which it handles correctly — the
defect is specific to assemblies.

**Gate A: passed.** Fusion 360 reads per-face colour correctly and STEPper NEXT
reads per-face and per-solid (§4.1, §4.3), so ≥2 of 3 consumer classes read the
achievable rung and nothing we emit breaks a consumer.

**The architecture is now settled** (§4.5, §4b), and it is simpler than the plan
assumed: keep OCCT's native per-instance encoding, fix STEPper NEXT's importer
(verified mechanism, §4.6), and skip face matching entirely — SolidWorks already
exports per-face colour correctly, so the exporter only has to fix the
occurrence level.

---

## 2. The problem, measured

**Done.** 9 corpus models × 7 export variants = 63 files, all exported without
error, plus the AP242 licence probe. SolidWorks **2026** (revision 34.2.1),
driven over COM automation by `tools/Peak.StepSpike.Harvest`. Evidence:
`evidence/S0/s0-exports.json`, `s0-ap242-probe.json`, `s0-analysis.json`,
`harvest.log`, and the 63 `.step` files.

### 2.1 AP242 is licence-blocked — confirmed

`IModelDocExtension.PublishSTEP242File` returns **4 =
`swPublishStep242_MBDLicenseNotAvailable`** on all 9 models, and writes no file.

> Probe defect worth recording: a first attempt using a `.step` extension
> returned `1 = InvalidPath` on every model. **The API requires `.STP`/`.stp`.**
> Had that not been chased down, "InvalidPath" could easily have been misread as
> the licence gate. The harness now tries several forms and records each
> attempt.

### 2.2 What SolidWorks gets RIGHT

Two things must be said plainly, because the study's value depends on being
honest about the baseline:

- **Per-face appearance export works, and respects precedence.** C2 stacks red
  (part) → orange (body) → yellow (feature) → green (face); the screenshot
  shows yellow top and green side. The export maps exactly:

  | Styled item | Target | Colour |
  |---|---|---|
  | `#69` | `ADVANCED_FACE` (top, PLANE) | yellow |
  | `#121` | `ADVANCED_FACE` (bottom, PLANE) | yellow |
  | `#118` | `ADVANCED_FACE` (side, CYLINDRICAL_SURFACE) | green |
  | `#114` | `MANIFOLD_SOLID_BREP` | orange (base, overridden by all three faces) |

  Orange never renders. **SolidWorks resolves part/body/feature/face precedence
  correctly.** An earlier reading of mine, based on counting distinct colours,
  suggested a defect here; mapping each styled item to its target face showed
  that was wrong.

- **`swStepExportAppearances` genuinely works** (and exists only from SW2024's
  `swconst`, id 787 — it is absent from SW2022's). With it off, every model
  exports exactly one colour, `rgb(0.79, 0.82, 0.93)` — SolidWorks' default
  part grey. With it on, C7 goes from 1 colour/5 styled items to 6 colours/15.
  AP203 exports no appearance at all, as expected.

### 2.3 What SolidWorks gets WRONG — the premise, confirmed

**Component-level overrides are lost.** C4_component_override_1 has two
instances of one red part, overridden orange and yellow (confirmed against
`C4_component_override_1.jpg`). The export contains:

```
solids=1   occurrences=2   shape_reps=1   PRODUCT_DEFINITION=2
#38 STYLED_ITEM -> #10 ADVANCED_BREP_SHAPE_REPRESENTATION   colour=orange
#79 STYLED_ITEM -> #12 MANIFOLD_SOLID_BREP                  colour=red
```

- **Yellow is entirely absent from the file.** One instance is unavoidably the
  wrong colour in every consumer.
- Both occurrences share **one** shape representation, and the single override
  is attached to that shared representation — so it cannot differ per instance
  by construction.
- **Zero `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM`.** SolidWorks never uses
  the STEP entity designed for exactly this case, in any of the 63 files.

**Assembly-level overrides are ignored outright.** C4_component_override_2 adds
a green assembly-level override on top; its export is byte-equivalent in
appearance terms to `_1` — orange and red, no green anywhere.

**Display states / configurations do not help.** C5 (config "1" = orange +
yellow) exports the same two colours, red + orange: the same single-override
flattening.

**Textures and decals are dropped**, as expected. C3's 37 × 37 mm checker
becomes a flat `rgb(1.0,1.0,1.0)`/`rgb(0.89,0.89,0.89)` pair; C8's barcode decal
leaves no trace at all — its export is indistinguishable from plain C1.

**`swStepExportSplitPeriodic` is a real 1:N hazard for S4.** C7 goes from 11
faces to **17** with splitting on (+6), and styled items 15 → 21. Every model
gains one face under `ap214_split`.

### 2.4 An encoding difference that matters for S4 and S2

SolidWorks expresses per-face colour as a plain `STYLED_ITEM` targeting
`ADVANCED_FACE`. **It never emits `OVER_RIDING_STYLED_ITEM`** — whereas OCCT
uses `OVER_RIDING_STYLED_ITEM` for exactly the same intent (§3.1).

Both are legal, but SolidWorks' STEP output is read correctly by every CAD tool
in the industry, which makes its encoding the demonstrably safe one. Since arm B
(textual splice) lets us emit whichever encoding we choose, **it can emit the
SolidWorks-style encoding and inherit that compatibility**, while OCCT's writer
would impose OCCT's. This is a third independent argument for arm B, and it
sharpens what §4.3 needs to test.

### 2.5 Verdict on the premise

**The premise holds.** SolidWorks is not deficient in the way originally
assumed — per-face and per-body appearance export is fine — but it is
deficient in precisely the way that matters for assemblies: it flattens every
component- and assembly-level override onto one shared representation, silently
discarding all but one. The project has a real defect to fix, and §3.1 shows
STEP and OCCT can both represent the case correctly.

---

## 3. The ceiling: what STEP can carry and what OCCT emits

Method: build XCAF documents with known appearance, write each to AP214 and
AP242, then re-read the files **as text** and count entities. Reading them back
through a toolkit would beg the question, so `tools/stepdump.py` is a plain
Part 21 parser with no OCCT dependency (cross-checked against raw `grep`).

Evidence: `evidence/S1/probe-matrix.json`, `evidence/S1/*.step`,
`tools/occt_probe.py`. OCCT 7.9.3.1 via the `cadquery_ocp_novtk` wheel that
STEPper NEXT already vendors; CPython 3.13.9 under Blender 5.1.

### 3.1 What was emitted

| Case | Written to the document | Entities emitted | Colours in file |
|---|---|---|---|
| `control` | nothing | **none** | 0 |
| `solid_colour` | colour on 2 shape labels | 3× `STYLED_ITEM` → `MANIFOLD_SOLID_BREP` | 2 |
| `face_colour` | solid colour + 3 face sub-shape colours | 1× `STYLED_ITEM` → `MANIFOLD_SOLID_BREP`, 3× `OVER_RIDING_STYLED_ITEM` → **`ADVANCED_FACE`** | 3 |
| `instance_col` | part colour + 2 component-label colours | 1× `STYLED_ITEM`, 3× `OVER_RIDING_STYLED_ITEM` + 3× `PRESENTATION_STYLE_BY_CONTEXT`, 2× `NEXT_ASSEMBLY_USAGE_OCCURRENCE` | 3 |
| `vis_common` | `XCAFDoc_VisMaterialCommon` | `SURFACE_STYLE_RENDERING_WITH_PROPERTIES` + `SURFACE_STYLE_TRANSPARENT` | 1 |
| `vis_pbr` | `XCAFDoc_VisMaterialPBR` | plain colour only | 1 |
| `combined` | colour + common + PBR | plain colour only — **rendering properties lost** | 1 |
| `colour_common` | colour + common | plain colour only — **rendering properties lost** | 1 |
| `common_pbr` | common + PBR | plain colour only — **rendering properties lost** | 1 |

**AP214 and AP242 output was identical in every case.** The schema token was
verified applied, not assumed: the probe reads `write.step.schema` back after
setting it and aborts on mismatch, and the resulting files carry
`AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }` and
`AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF {1 0 10303 442 1 1 4 }`
respectively. **So AP242 buys nothing over AP214 for appearance** — which
matters, because AP242 is the licence-gated one.

> Note: `write.step.schema` reads back empty until `STEPControl_Controller::Init`
> has run. Without that call every schema switch silently no-ops and all output
> is AP214 while appearing to honour the request.

### 3.2 The mutual-exclusion finding

`vis_common` alone emits rendering properties:

```
#360 = SURFACE_STYLE_RENDERING_WITH_PROPERTIES(.NORMAL_SHADING., #359, (#361));
#361 = SURFACE_STYLE_TRANSPARENT(0.25);
```

Add *anything else* that styles the same shape — an explicit XCAF colour
(`colour_common`) or a PBR material (`common_pbr`) — and the rendering
properties vanish, leaving a flat colour. Both suppressors were tested
separately to isolate the cause; each is sufficient on its own.

Two consequences:

- Of the 9-double `MaterialPropertyValues` SolidWorks can give us
  (`[R,G,B,Ambient,Diffuse,Specular,Shininess,Transparency,Emission]`), only
  **transparency** and the base colour survive into STEP through OCCT.
  Ambient, diffuse, specular and shininess are dropped even in the best case —
  no `SURFACE_STYLE_REFLECTANCE_AMBIENT` is emitted at all.
- Because per-face and per-instance colour requires the colour channel, **R1 is
  not reachable through `STEPCAFControl_Writer` at the same time as R0.**

### 3.3 PBR and textures

`XCAFDoc_VisMaterialPBR` contributes **nothing** to STEP output. Searching the
emitted file for `METALLIC`, `ROUGH`, `PBR`, the material name, and the literal
values `0.9`/`0.15` returns zero hits (`evidence/S1/vis_pbr_ap214.step`).

The same document written to glTF via `RWGltf_CafWriter` carries all of it
(`evidence/S1/vis_pbr.glb`, `evidence/S1/gltf-control.json`):

```json
{"name": "mat_0",
 "pbrMetallicRoughness": {"baseColorFactor": [0.0100, 0.0331, 0.7874, 1.0],
                          "metallicFactor": 0.9, "roughnessFactor": 0.15},
 "doubleSided": true}
```

Identical source document; the loss is provably in the STEP writer, not in the
document we built. This is the cleanest available demonstration that **R4 is
dead and R3 is the right home for PBR data.**

Corroborating the mechanism: a symbol dump of the OCCT binary shows the
`StepVisual` package contains `StyledItem`, `OverRidingStyledItem`,
`ContextDependentOverRidingStyledItem`, `PresentationStyleAssignment`,
`SurfaceStyleUsage`, `SurfaceSideStyle`, `SurfaceStyleFillArea`,
`FillAreaStyleColour`, `SurfaceStyleRendering(WithProperties)`,
`SurfaceStyleTransparent`, `SurfaceStyleReflectanceAmbient`, `CurveStyle`,
`Invisibility` — and **no texture or UV entity of any kind**. There is no
`...ReflectanceAmbientDiffuse` or `...AmbientDiffuseSpecular` subtype either,
which is consistent with §3.2's observation that only transparency survives.

> **Caveat on scope.** `XCAFDoc_VisMaterialPBR` in the *OCP Python bindings*
> exposes only `BaseColor`, `Metallic`, `Roughness`, `EmissiveFactor`,
> `RefractionIndex` — the `Image_Texture` members are not bound. So this spike
> cannot test texture export to glTF; the C++/CLI production path can, because
> the C++ class does have them. The STEP conclusion is unaffected: STEP has no
> texture entity to write to regardless of binding.

### 3.4 R1 is reachable, but only by emitting the entities ourselves

Since §3.2 rules out getting reflectance from `STEPCAFControl_Writer` alongside
colour, the remaining route is to write the entities into the file text — a
miniature of the S4 arm B (textual splice) approach. Tested in
`tools/r1_splice.py` (evidence: `evidence/S1/r1-splice.json`,
`evidence/S1/face_colour_r1_spliced.step`).

Taking the `face_colour` probe, which already carries three per-face colours,
and splicing `SURFACE_STYLE_RENDERING_WITH_PROPERTIES` +
`SURFACE_STYLE_TRANSPARENT` onto each of its four `SURFACE_SIDE_STYLE`s:

| Check | Result |
|---|---|
| Entities added | +4 `SURFACE_STYLE_RENDERING_WITH_PROPERTIES`, +4 `SURFACE_STYLE_TRANSPARENT` |
| Geometry | faces 6 → 6, solids 1 → 1, **untouched** |
| OCCT re-read | `IFSelect_RetDone`; all three colours identical before and after |
| STEPper NEXT | `FINISHED`, 1 mesh, all 3 per-face materials still correct |

**So R0 and R1 can coexist — through arm B, not through the OCCT writer.**
This is now an independent argument for arm B, on top of its geometry-fidelity
advantage: it is the only route to reflectance at all.

Caveat: this demonstrates the entities can be written and do not break readers.
It does **not** yet demonstrate any consumer *honours* them — STEPper NEXT
imported the file happily but left alpha at 1.0 despite
`SURFACE_STYLE_TRANSPARENT(0.3)` (§4.1). Whether R1 is worth shipping depends
on §4.3, which has not run.

### 3.5 Colour space

Written as sRGB `(0.8, 0.1, 0.1)`; the file stores `0.800000010877, …`;
`Quantity_Color::Red()` on re-read returns `0.604`, i.e. linear. Blender ends up
with `0.6038` on the Principled base colour, which is correct for Blender.
End-to-end colour fidelity holds — but any hand-written entity emission (arm B)
must reproduce this convention deliberately rather than by accident.

---

## 3b. Metadata: engineering material and custom properties

Added after the study opened, from two questions by the STEPper NEXT
maintainer. Evidence: `evidence/S1b/metadata-probe.json`,
`evidence/S1b/*.step`, `tools/metadata_probe.py`.

### 3b.1 AP242 is NOT required for engineering material

A STEPper NEXT user reported wanting CAD material (Aluminium, ABS, Steel) on
import, believing "AP242 is the only STEP format that can store that kind of
metadata". **That is false.** `XCAFDoc_MaterialTool.SetMaterial` writes:

```
#360 = PROPERTY_DEFINITION('material property','material name',#5);
#362 = PROPERTY_DEFINITION('material property','density',#5);
     -> DESCRIPTIVE_REPRESENTATION_ITEM('Aluminium 6061-T6','Aluminium alloy, …')
     -> MEASURE_REPRESENTATION_ITEM('kg/m^3',2.7E+03,#353)
```

and the emission is **byte-identical between AP214 and AP242** (asserted
programmatically, not eyeballed). The same holds for the pre-existing
`ci/baselines/mat_ap214.step` / `mat_ap242.step` in STEPper NEXT.

STEPper NEXT already reads it from **both** schemas — importing our generated
AP214 file yields a Blender material `Aluminium 6061-T6` plus
`STEP_material_desc` and `STEP_material_density = 2700.0`. So the feature the
user asked for already works today on AP214; the only thing missing is that
**SolidWorks does not write it**, which is precisely what this exporter would
fix. No MBD licence is implicated.

> Encoding detail for the exporter: OCCT names the measure item after the
> `DensName` argument. Passing `"kg/m^3"` produced
> `MEASURE_REPRESENTATION_ITEM('kg/m^3',…)`, whereas the conventional encoding
> (and STEPper NEXT's own baselines) uses `('density',…)`. Pass `"density"` as
> `DensName` to match convention; the value parses either way.

### 3b.2 Custom properties are writable — and they break material detection

Splicing four properties (PartNumber, Revision, Supplier, FinishSpec) onto
every `PRODUCT_DEFINITION`, using the same `property_definition` +
`descriptive_representation_item` pattern OCCT itself uses for material:
8 `PROPERTY_DEFINITION`s added, entities 423 → 455, faces unchanged, file still
imports.

**But it corrupts engineering material.** Re-reading that file with
`STEPCAFControl_Reader` yields material labels:

```
name='FinishSpec'  desc='Anodised, matt black'  density=0.0
name='FinishSpec'  desc='Anodised, matt black'  density=0.0
```

OCCT scoops *any* `descriptive_representation_item` hanging off a
`property_definition` into `XCAFDoc_Material`, ignoring the property's role
name — our `'user defined attribute'` was treated exactly like
`'material property'`, and the last one wins. STEPper NEXT then faithfully
creates a Blender material called `FinishSpec`.

**This is OCCT reader behaviour, not a STEPper NEXT bug**, so every OCCT-based
consumer would do the same. Consequences for the design:

- Writing custom properties in the obvious encoding is **actively harmful**
  while a real material is also present — it overwrites it.
- Either use a representation item type OCCT does not scoop, or have STEPper
  NEXT read the raw `PROPERTY_DEFINITION` role (`'material property'` vs
  `'user defined attribute'`) rather than trusting `XCAFDoc_Material`. STEPper
  NEXT already imports the raw `StepRepr_*`/`StepBasic_*` classes, so it has
  the machinery.
- Until one of those is done, **custom properties and engineering material are
  mutually exclusive in practice.** Worth deciding before either ships.

## 3c. The reused-part / per-instance colour problem

The maintainer's long-standing frustration: the same part used in several
assemblies with different assembly-level colour overrides imports into Blender
as instances, so some come in the wrong colour.

**The STEP file is not at fault, and neither is the exporter design.** §3.1 and
§4.2 show a single referred part carrying its own colour while each occurrence
carries its own override, and the whole structure surviving a round trip:
component `0:1:1:1:1` red, component `0:1:1:1:2` blue, referred part green.
STEP models exactly the case that is going wrong.

The loss is entirely in the importer (§4.2). Any fix therefore belongs in
STEPper NEXT, and the exporter should simply keep writing overrides per
occurrence, which it does for free.

**Recommended shape for the importer fix** (STEPper NEXT work, outside this
study — recorded here because the evidence bears directly on it):

- Keep one Blender material per *engineering material* / appearance, shared
  across instances. This preserves instancing and is what makes the material
  name meaningful.
- Carry the per-occurrence colour on the object, not the material: set
  `obj.color` from the component-label override and feed it through an
  **Object Info → Color** node into Base Color. Shared mesh, shared material,
  per-instance colour, no duplication. This matches the maintainer's own
  instinct ("rely on component custom properties/object color").
- Fall back to a per-object material slot (`slot.link = 'OBJECT'`) only when an
  override differs by more than colour — object colour is RGBA only, so a
  differing roughness or texture needs a real material copy.
- Record the resolved override in a custom property (e.g. `STEP_color_override`)
  so it is inspectable and survives a save.

**One thing to verify before committing to that design:** it is clean for the
EMPTIES / linked-duplicate hierarchy mode, where each occurrence is a real
object sharing mesh data. For `COLLECTION_INSTANCES` mode the objects inside
the instanced collection are shared wholesale, and whether Object Info → Color
resolves to the instancer's colour needs checking in Blender 5.1 rather than
assuming. If it does not, collection-instance mode needs the material-copy
fallback.

## 3d. S3 — appearance harvest fidelity

Can we get out of SolidWorks what its exporter throws away? Evidence:
`evidence/S3/s3-harvest.json`, `s3-c6-perf.json`, `s3-c6-full.json`,
`harvest.log`; harvester `tools/Peak.StepSpike.Harvest` verb `harvest`.

### 3d.1 The data is all available — but we must resolve it ourselves

`IComponent2.MaterialPropertyValues` returns a resolved appearance per
occurrence, and for C4_component_override_1 it is correct:

| Component | Harvested | Screenshot |
|---|---|---|
| `C4_part_1-1` | **orange** | orange |
| `C4_part_1-2` | **yellow** | yellow |

**But it is wrong the moment a higher-level override exists.** For
C4_component_override_2 — same assembly plus a green *assembly-level* override
— it still returns orange and yellow, while `C4_component_override_2.JPG` shows
**both cylinders green**. `IComponent2.MaterialPropertyValues` does not account
for overrides applied above the component.

An earlier draft of this section claimed the API resolves precedence for us and
that we would never need to reimplement it. That was wrong, and the correction
matters: **we must implement the ladder ourselves.**

The inputs are all present in the harvest (§3d.2): C4_2's document-level list
carries `green → ModelDoc2Class` (the assembly-level override) alongside
`orange → component` and `yellow → component`.

### 3d.1b The precedence rule to implement

Stated by Oscar and corroborated by both corpus screenshots:

> Top-level assembly → sub-assembly override → sub-assembly → part override →
> part → body → feature → face, **highest level wins**. An appearance set at
> the very top overrides everything beneath it.

Two rules, applied in order:

1. **Across documents, the highest level wins.** Walk the occurrence path from
   the top assembly downwards; the first override found colours the whole
   occurrence, whatever the part says internally. (C4_2: green beats the
   component overrides beats the part's red.)
2. **Within a single part, the most specific wins** — face > feature > body >
   part. (C2: green face and yellow feature render over orange body and red
   part; `C2_stacked_overrides.jpg`.)

The goal is that exported STEP always matches what SolidWorks displays, so
where the two rules could disagree the screenshots are the arbiter, not the
API's resolved value.

### 3d.2 The six-scope map is complete and reliable

`GetRenderMaterials2` attributes every appearance to its scope. C2 resolves
exactly as `corpus/C2_stacked_overrides.jpg` shows:

| Appearance | `entityTypes` |
|---|---|
| red | `["part"]` |
| orange | `["body"]` |
| yellow | `["feature"]` |
| green | `["face"]` |

C7 shows 4 body-level plus 1 face-level; C5 shows 4 component-level
appearances, each reporting its `linkedDisplayStates`. **We can build the
precedence ladder ourselves from this map.**

### 3d.3 …but per-face resolution must be implemented by us

The plan hoped `IGetMaterialPropertyValuesForFace` would resolve per-face
inheritance. Two problems, both now settled:

- **It is not callable from C#.** The interop declares
  `Double IGetMaterialPropertyValuesForFace(Object)` — the C++
  pointer-returning form — with no managed-friendly overload.
- **`IFace2.GetMaterialPropertyValues2` does not walk up the chain.** On C2 the
  green face (which has its own appearance) returns
  `[0, 1, 0, 1, 1, 0.5, 0.3125, 0, 0]`, correctly. The two planes, which
  inherit, return `[-1, -1, -1, -1, -1, -1, -1, -1, -1]` — a "nothing of my
  own" sentinel, not the inherited value.

So face-level precedence is ours to implement, from the §3d.2 scope map. That
is a bounded, well-specified job rather than a risk, but it is real work the
plan had hoped to avoid.

### 3d.4 Textures are fully harvestable

C3 (checker texture, specified at 37 × 37 mm on a cylindrical face):

```
textureFilename = checker.jpg      widthMetres = 0.037   heightMetres = 0.037
mappingType = 3   rotationAngle = 3.14159   entityTypes = ["face"]
```

Real-world texture size comes back **exactly as specified**, with the mapping
frame. Everything the R3 companion glTF needs is available — the constraint on
textures is STEP's schema (§3.3), never the harvest.

### 3d.5 Performance — this decides addin vs automation

C6, the real assembly (`SS65_02_00_00`): **2,437 components, 37,927 faces**.

| Pass | Time | Per face |
|---|---|---|
| Traversal + component appearance only | 146.7 s | 3.87 ms |
| Full harvest, 78,291 property calls | **196.1 s** | 5.17 ms |

The plan estimated 20–100 µs per cross-process COM call; the measured cost is
**~40× worse**, and the dominant cost is face *traversal* (`GetFirstFace` /
`GetNextFace`), not the property reads — traversal alone is 75% of the total.

**Implication:** a ~3¼ minute appearance harvest is tolerable for a one-shot
export but is not interactive. The plan's escalation trigger (>60 s on C6) is
met, so **the production tool should run in-process as an add-in**, where these
become vtable calls rather than cross-process marshalling. The spikes can stay
out-of-process; the product should not.

## 4. Consumers

### 4.1 Blender / STEPper NEXT — the primary consumer

Evidence: `evidence/S2/blender-consumer.json`, `tools/s2_blender.py`.

| Level written | Arrived in Blender |
|---|---|
| per-solid colour | **yes** — 2 objects, 2 materials, correct colours |
| per-face colour | **yes** — one mesh with 3 material slots, all 3 colours correct |
| per-instance colour | **no** — both instances came back with the *part* colour |
| `SURFACE_STYLE_TRANSPARENT` | **not consumed** — a spliced `(0.3)` left the Principled `Alpha` at 1.0 (§3.4) |

> Methodological note: `Material.diffuse_color` is only Blender's viewport
> swatch and reads `(0.8, 0.8, 0.8)` for every imported material. The real
> colour is on the Principled BSDF `Base Color`. `ci/parity_harness.py` dumps
> `diffuse_color`, so its snapshots are fine for parity diffing but must not be
> read as evidence about colour.

### 4.2 The per-instance failure is in the importer, not the file

Localised in `tools/roundtrip_check.py` (evidence:
`evidence/S1/roundtrip-labels.json`). Re-reading our own probe file with
`STEPCAFControl_Reader` and walking the XCAF tree the way `importer.py` does:

```
0:1:1:1      [A  ] Open CASCADE STEP  {}
  0:1:1:1:1  [ CR] =>[0:1:1:1]  {'Surf': (0.604, 0.01, 0.01)} -> 0:1:1:2 {'Surf': (0.01, 0.448, 0.033)}
  0:1:1:1:2  [ CR] =>[0:1:1:1]  {'Surf': (0.01, 0.033, 0.787)} -> 0:1:1:2 {'Surf': (0.01, 0.448, 0.033)}
```

Both component labels carry their override (red, blue); the referred part
carries green. The three-level structure survives the round trip intact.

Yet on import, both objects get one material, `LIMEGREEN` — the part colour.
They are *not* sharing a mesh (two distinct datablocks, `users=1` each, slot
link `DATA`), so this is not a linked-duplicate artifact: the importer resolves
colour from the referred product label and never applies the component-label
override.

**Actionable:** this is a STEPper NEXT bug, independent of this project. It
also means any measurement of "did the appearance arrive" using STEPper NEXT as
the oracle will under-report until it is fixed.

### 4.3 Fusion 360 — and the central problem

Tested by opening the S1 probe files directly (Oscar, Fusion 360):

| File | Expected | Fusion showed | Verdict |
|---|---|---|---|
| `face_colour_ap214.step` | blue solid, one red / one green / one blue face | cube with 4 blue faces, green top, **red bottom** | **correct** |
| `instance_col_ap214.step` | one red cube, one blue cube (part is green) | **2 green cubes** | **override ignored** |

So **per-face colour works in Fusion**, and per-instance colour does not.
Combined with §4.2, **two independent consumers ignore OCCT's per-instance
encoding** (`OVER_RIDING_STYLED_ITEM` + `PRESENTATION_STYLE_BY_CONTEXT`), each
falling back to the referred part's colour. Both render the exact failure the
project exists to fix.

This is the study's central problem, and it is not a SolidWorks problem or an
OCCT bug — it is that the encoding OCCT chooses is not the one consumers read.
§4.4 tests the alternatives.

### 4.4 Three candidate encodings for per-instance colour

Identical geometry and intent in each (two instances of one box, one red, one
blue), differing only in how the override is expressed. Built by
`tools/instance_encodings.py`; files in `evidence/S2/`.

| | Encoding | Entities | OCCT re-read |
|---|---|---|---|
| **A** | OCCT default | 1 styled, 3 over-riding, 0 context-dependent | both component colours recovered |
| **B** | `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM` bound to the NAUO — the entity ISO 10303-46 defines for this case, which **neither OCCT nor SolidWorks emits** | 1 styled, 1 over-riding, **2 context-dependent** | **degrades — only one component colour survives, red is lost from the palette** |
| **C** | De-instanced: each occurrence is its own product with its own solid and a plain `STYLED_ITEM` | 3 styled, 0 over-riding, 2 solids | both colours recovered |

**C cannot fail** — it reduces per-instance colour to per-solid colour, which
every consumer in §4.1–4.3 already reads correctly. The cost is duplicated
geometry and lost instancing, which matters on assemblies like C6 (2,437
components, 37,927 faces).

**B is the standards-correct answer but carries a real risk**: OCCT's own
reader handles it worse than its own encoding, so it could fix Fusion while
breaking Blender. It must be tested in Fusion before being considered.

### 4.5 Decision: keep encoding A, fix the importer

**Priority ruling (Oscar): preserving appearance for STEPper NEXT matters more
than for other CAD packages.** That settles the encoding question in favour of
**A, OCCT's native encoding**, and B and C become unnecessary:

- A already round-trips **correctly** — both component labels carry their
  override after a re-read (§4.2, `evidence/S1/roundtrip-labels.json`). Nothing
  is lost in the file; only STEPper NEXT's importer fails to apply it.
- A preserves instancing. C duplicates geometry per occurrence, which on C6
  (2,437 components, 37,927 faces) is a serious file-size and import-time cost
  paid purely for consumers we have just deprioritised.
- B is standards-correct but degrades OCCT's own readback (§4.4), i.e. it would
  trade our primary consumer for our secondary ones — the wrong direction.
- Fusion and Rhino degrade **gracefully** under A: they show the part colour
  rather than failing to open. Wrong-but-sane beats broken.

**Both encodings ship (decided).** C becomes a documented export option,
"De-instance overridden components", because the two encodings serve two
different Blender workflows:

- **A (instanced, default)** — one shared material, per-object colour. Best when
  you want to apply one engineering material everywhere and iterate in the
  shader editor; colour rides on the object rather than the material.
- **C (de-instanced)** — each overridden occurrence is its own product with its
  own colour. Renders correctly in every CAD package and needs no importer
  support at all. Best when bulk-editing object colours would be painful and
  you would rather have real, separately-coloured objects.

C costs little once the harvest exists, and it is the only encoding proven to
work everywhere. STEPper NEXT should handle A out of the box regardless.

### 4.6 The importer fix is verified, in both hierarchy modes

§3c recommended one shared material driven by per-object colour through an
Object Info → Color node, and flagged that collection-instance mode needed
checking rather than assuming. Checked, in Blender 5.1
(`tools/blender_instance_colour.py`, `evidence/S2/blender-instance-colour.json`):

| Mode | Datablocks | Rendered |
|---|---|---|
| Linked duplicates | **1 mesh, 1 material** | red-dominant left, blue-dominant right |
| Collection instances | shared collection | red-dominant left, blue-dominant right |

**Both work.** One material renders two colours, with no mesh or material
duplication, in each of the hierarchy modes STEPper NEXT offers. The
recommended fix is sound as specified.

> Method note: a first run reported failure because the comparison used an
> absolute 0.05 tolerance against a very dim render whose entire dynamic range
> was ~0.008. The hues were correct all along. The check now compares which
> channel dominates and lifts exposure.

---

## 4b. S4 may be largely unnecessary — a structural simplification

S4 was scoped as the study's biggest technical risk: matching SolidWorks
`IFace2`s to `TopoDS_Face`s at ≥99.5% with zero silent mismatches, complicated
by the split-periodic 1:N trap (§2.3 measured 11 → 17 faces on C7).

**That risk exists only to re-attach per-face colour. S0 proved SolidWorks
already gets per-face colour right** (§2.2: C2's faces map exactly to the
screenshot, and Fusion renders `face_colour` correctly per §4.3). The defect is
confined to component- and assembly-level overrides.

So the exporter does not have to touch faces at all. It needs to:

1. let SolidWorks export AP214 with appearances — per-face and per-body colour
   already correct, geometry untouched;
2. harvest the per-occurrence resolved appearance, which §3d.1 shows is
   available and correct;
3. attach it **per occurrence** — matching `NEXT_ASSEMBLY_USAGE_OCCURRENCE`
   entities to component paths by transform and referred-product name.

Occurrence matching is a far smaller problem than face matching: C6 has 2,437
components against 37,927 faces, the candidates are disambiguated by placement
transform *and* product name, and a mismatch is visible rather than silent.
**The split-periodic trap disappears entirely**, because face counts stop
mattering.

This also removes the argument for arm A (OCCT round-trip): if we are only
adding presentation entities bound to occurrences, arm B (textual splice) does
it while leaving the geometry section byte-identical — no OCCT re-tolerancing,
no lost PMI, and the SolidWorks-authored per-face styling is preserved exactly
as SolidWorks wrote it.

**Open question this creates, which the corpus cannot currently answer.**
Does a component-level override outrank a *face-level* appearance inside the
part? `C4_part_1` carries only a part-level colour, so C4 cannot distinguish
"component override replaces everything" from "component override replaces only
the part-level default". The answer decides whether step 3 above may simply add
occurrence styling (if face colours survive an override) or must also suppress
the part's own face styling for that occurrence.

**Suggested C9:** a part with one face-level colour, instanced twice in an
assembly with two different component-level overrides. A screenshot of what
SolidWorks displays settles it in one model.

## 5. Rung status

| Rung | Status | Evidence |
|---|---|---|
| **R0** colour hierarchy | **Write: proven** at solid/face/instance, AP214 and AP242. **Read: 2 of 3** — instance level blocked by §4.2. | §3.1, §4.1, §4.2 |
| **R1** reflectance | **Write: reachable via arm B only** (§3.4) — not via `STEPCAFControl_Writer` alongside R0 (§3.2), and limited to transparency + shading method; ambient/diffuse/specular/shininess have no emitted representation. **Read: no consumer yet shown to honour it.** | §3.2, §3.4 |
| **R2** named appearances | Untested. `styled_item.name` is written (`'color'`, `'overriding color'`) so the slot exists; putting the SolidWorks appearance name there is untested but low-risk. | — |
| **R3** companion glTF | **Viable and demonstrated** for metallic/roughness/base colour. Texture export untestable from Python bindings (§3.3 caveat). | §3.3 |
| **R4** textures in STEP | **Dead.** No texture entity exists in the schema as implemented. | §3.3 |
| **M0** engineering material | **Proven both ways** — writes identically to AP214 and AP242, and STEPper NEXT already reads it. Free to add; SolidWorks not writing it is the actual gap. | §3b.1 |
| **M1** custom properties | Writable, but currently **destroys M0** in every OCCT-based reader. Needs a discriminator agreed between writer and reader first. | §3b.2 |

---

## 6. What we could not determine

- Whether Rhino/Fusion/KeyShot honour `OVER_RIDING_STYLED_ITEM` on
  `ADVANCED_FACE`, or `PRESENTATION_STYLE_BY_CONTEXT` for instances. OCCT
  chose these encodings; third-party support for them is unverified and is the
  single biggest open risk to R0.
- Whether textures can reach glTF from a C++ XCAF document (Python bindings
  cannot express it).
- Everything in S3–S6.

---

## 7. Evidence index

| File | Proves |
|---|---|
| `evidence/S1/probe-matrix.json` | Full case × schema × emitted-entity grid |
| `evidence/S1/*.step` (18 files) | The actual bytes every claim in §3 is read from |
| `evidence/S1/vis_pbr.glb`, `gltf-control.json` | PBR survives to glTF, not to STEP |
| `evidence/S1/roundtrip-labels.json` | Instance overrides survive the STEP round trip |
| `evidence/S1/r1-splice.json`, `face_colour_r1_spliced.step` | Reflectance can be spliced in alongside per-face colour without disturbing geometry or readers |
| `evidence/S2/blender-consumer.json` | What reached Blender, per case |
| `evidence/S1b/metadata-probe.json`, `eng_material_ap*.step` | Engineering material is identical in AP214 and AP242, and STEPper NEXT reads both |
| `evidence/S1b/custom_props_ap214.step` | Custom properties write cleanly but are misread as engineering material by OCCT |
| `evidence/S0/s0-ap242-probe.json` | `PublishSTEP242File` = 4 (MBD licence) on all 9 models |
| `evidence/S0/*.step` (63 files), `s0-exports.json`, `s0-analysis.json` | The full SolidWorks export matrix §2 is read from |
| `evidence/S0/harvest.log` | SW revision, per-export status, preference availability |
| `corpus/C2_*.jpg`, `C4_*.jpg` | Ground truth the export is judged against |
| `tools/stepdump.py` | The Part 21 parser every count comes from |
| `tools/occt_probe.py`, `gltf_control.py`, `s2_blender.py`, `roundtrip_check.py`, `occt_env.py` | Reproduce all of the above |
