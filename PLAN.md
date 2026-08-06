# NEXT-STEP-SW — Feasibility Study: rich-appearance STEP export from SolidWorks

## Context

SolidWorks' native STEP export is deficient in ways that block our pipeline:

- **AP242 is licence-gated.** The API route is `IModelDocExtension.PublishSTEP242File` with `swPublishStepOpts_e`; `swStep242Error_e` includes `swPublishStep242_MBDLicenseNotAvailable`. Without the paid MBD addon there is no AP242.
- **AP214 "appearance export" underdelivers.** `swStepExportAppearances` claims appearance support but emits flat solid colour, ignores component/assembly-level appearance overrides (does not respect SolidWorks' appearance hierarchy), and carries nothing of textures, UV mapping, roughness or specular — and even the colour it does emit is unreliable.

We want to know whether an in-house addin can do materially better, and exactly how much better. **This study delivers time-boxed spikes and a findings report ending in a go/no-go — not a shipped addin.**

`c:\PeakDesign\NEXT-STEP-SW` is currently empty. Three existing in-house SolidWorks COM addins supply the patterns: **Peak-Release** (newest skeleton; its `Export\Converters\StepConverter.cs` already does the save/restore-`swStepAP` + `SaveAs3` dance), **Peak-Custom-Properties** (the `ISwAddin` + `[ComRegisterFunction]` template, and 19,200 extracted SolidWorks API help pages), and **SW-Timelapse**.

### Decisions taken (do not re-open)

| | Decision |
|---|---|
| Deliverable | Spikes + `FINDINGS.md` + go/no-go. No production addin. |
| Consumers | STEPper NEXT (Blender), third-party CAD (Rhino/CATIA/NX/Fusion), renderers (KeyShot/Visualize/Twinmotion). **Output must stay schema-conformant** — no proprietary hacks inside the `.step`. |
| Geometry | Post-process SolidWorks' own STEP export. SW writes the B-rep; we re-attach the correct appearance hierarchy and rewrite. |
| Writer tech | C++/CLI OCCT wrapper called from the C# addin. (S5 measures two control arms — see the note there.) |

### Evidence already gathered (verified first-hand, not assumed)

**The harvest side is healthy — the writer side is the constraint.**

- `IRenderMaterial` exposes everything we need: `PrimaryColor`, `Diffuse`, `Specular`, `SpecularColor`, `SpecularSpread`, `Roughness`, `Reflectivity`, `BlurryReflection`, `Transparency`, `Luminous`, `BumpMap`, `BumpAmplitude`, `MappingType`, `FileName` (the `.p2m` path), `Entities/EntitiesCount`, at all six `swAppearanceTargetType_e` scopes (Part/Component/Body/Feature/Face/AppearanceFilter). Display-state variants come via `GetRenderMaterialsCount2(swAllDisplayState, names)` / `AddDisplayStateSpecificRenderMaterial` (confirmed in `SW-API\extracted\sldworksapi\Get_Appearance_Filename_Example_CSharp.htm`).
- 631 `.p2m` appearance files at `SOLIDWORKS 2024\data\graphics\Materials\` are quoted key/value **text** (not XML), carrying `col1/col2/num_cols`, `diffuse_factor`, `specular_color/factor`, `roughness`, `reflectivity`, `transparency`, `mtl_ior`, `bumpTexture/bumpStrength`, `color_texname`, and `initTextureWidth/initTextureHeight` — the real-world texture extent **in metres**, i.e. our UV scale.

**The ceiling is lower than hoped — this reshapes the whole study.** I dumped the symbol table of the OCCT 7.9.3 binary STEPper NEXT already vendors (`OCP\OCP.cp313-win_amd64.pyd`):

- `StepVisual_*` presentation entities present: `StyledItem`, `OverRidingStyledItem`, `ContextDependentOverRidingStyledItem`, `PresentationStyleAssignment`, `SurfaceStyleUsage`, `SurfaceSideStyle`, `SurfaceStyleFillArea`, `FillAreaStyleColour`, `SurfaceStyleRendering`, `SurfaceStyleRenderingWithProperties`, `SurfaceStyleTransparent`, `SurfaceStyleReflectanceAmbient`, `CurveStyle`, `Invisibility`.
- **There is no texture entity of any kind.** A grep for `Step*_*Texture*` / UV-coordinate entities across the whole binary returns **zero** hits. Textures and UVs are not representable through OCCT's STEP writer.
- Reflectance is limited to `SurfaceStyleReflectanceAmbient` — the `...AmbientDiffuse` / `...AmbientDiffuseSpecular` subtypes are absent, so diffuse/specular cannot be written through OCCT's typed API without hand-built entities.
- `XCAFDoc_VisMaterial`, `VisMaterialCommon`, `VisMaterialPBR`, `VisMaterialTool` and `STEPCAFControl_Writer` all exist — but PBR having a class does not mean STEP output carries it. **`RWGltf_CafWriter` is present**, and glTF genuinely does carry base-colour texture + metallic-roughness.

So the headline goal — textures/UVs/roughness inside conformant STEP — is **probably unreachable**, and proving that cheaply is the study's first job. The realistic target is *correct colour hierarchy + reflectance + named appearances in STEP, with a companion glTF carrying the PBR/texture data*. The study exists to establish that with evidence rather than assertion.

---

## Fallback ladder

Fix this on day 1; record the rung each spike reaches. The report states the highest rung that is **proven, consumable and conformant**.

| Rung | Content | Consumers |
|---|---|---|
| **R0** | Correct colour hierarchy — per-face, per-solid, **per-instance** RGB + transparency, respecting SW precedence, display state and configuration | All three classes. STEPper NEXT's `query_color` already reads exactly these three levels. |
| **R1** | R0 + reflectance (`surface_style_rendering_with_properties`: ambient/diffuse/specular/shininess from the 9-double `MaterialPropertyValues`) | Renderers. Likely needs hand-built entities — OCCT only has the ambient subtype. |
| **R2** | R1 + `styled_item.name` = the SolidWorks appearance name (`IFace2.MaterialUserName`, `IRenderMaterial.FileName` basename), so a consumer can look the `.p2m` up. Schema-legal, silently ignorable. | STEPper NEXT via its existing `MaterialDB\material_database.blend` mapping. |
| **R3** | R2 + a **companion glTF** beside the `.step` from the same harvested data, carrying texture path, mapping frame, roughness, metallic, bump. Zero schema risk. | Blender/KeyShot/Twinmotion read glTF natively; everything else ignores it. |
| **R4** | Textures/UVs inside conformant STEP | Ruled out by the symbol dump above unless S1 overturns it. |

**R2 + R3 is the expected honest outcome** and should be presented as the designed answer, not a concession.

---

## Spikes

Ordered so the cheapest disqualifying question runs first. **Nothing before S5 needs C++**, and S1/S2 need no SolidWorks at all — the whole capability question is answerable in Python against the OCCT build we already own.

### S0 — Baseline: what does SolidWorks actually emit? · 0.5 d
**Question.** Entity-for-entity, what does native AP214 + `swStepExportAppearances` produce, and is `PublishSTEP242File` genuinely licence-blocked here?

- Export the full matrix per corpus model (AP203; AP214 appearances off/on; + `swStepExportFaceEdgeProps`; `swStepExportSplitPeriodic` on/off; `swStepExportConfigurationData`). Save/restore the prefs — reuse the discipline in [StepConverter.cs](c:/PeakDesign/Peak-Release/Export/Converters/StepConverter.cs).
- Call `PublishSTEP242File` on a part and an assembly, with `swPublishStepOpts_FaceEdgeSTEP242` and `_SplitFacesSTEP242`. **Capture the literal return int** decoded against `swStep242Error_e` — this is the citation the project rests on.
- Histogram every output: counts of `STYLED_ITEM`, `PRESENTATION_STYLE_ASSIGNMENT`, `SURFACE_STYLE_USAGE`, `SURFACE_STYLE_RENDERING*`, `COLOUR_RGB`, and for each `STYLED_ITEM` what it targets (an `ADVANCED_FACE`, a `MANIFOLD_SOLID_BREP`, or an NAUO via `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM`).

**Must answer:** does SW emit per-face styled items at all, or only per-solid? Any `SURFACE_STYLE_RENDERING`? Does it *ever* emit a context-dependent overriding styled item for a component override (corpus C4)?

**Premise check — this spike can end the study.** If C4 round-trips correctly through native AP214, SolidWorks does respect the hierarchy and the premise is wrong. Unlikely given the observed behaviour, but it costs half a day to be sure and it is not honest to skip it.

### S1 — The ceiling: what can STEP carry, what will OCCT emit? · 1.5 d · no SW, no C++
Run under Blender's Python with STEPper NEXT on `sys.path`, exactly as `ci\parity_harness.py` does.

- **Schema (0.3 d).** Read AP214 and AP242 EXPRESS long-forms for `presentation_appearance`; enumerate the legal `surface_style_element_select` members and `rendering_properties_select` subtypes. Establish what the *standard* permits, independent of OCCT.
- **Writer probe (0.7 d).** Two boxes in a `TDocStd_Document`; systematically probe `STEPCAFControl_Writer` for: per-solid colour (`XCAFDoc_ColorSurf`), per-face colour on a sub-shape label, per-instance colour on the component label, `XCAFDoc_VisMaterialCommon`, `XCAFDoc_VisMaterialPBR` with textures — each written to **AP214** and **AP242** (`Interface_Static.SetCVal("write.step.schema", ...)`). Diff against a no-appearance control. The measurement is *which set values survive into file bytes, and as what entities*.
- **Cross-write control (0.2 d).** Write the same PBR-laden XCAF doc to glTF via `RWGltf_CafWriter`. If glTF carries texture + metallic-roughness and STEP silently drops them, that is the quotable demonstration of where loss occurs — and it validates R3 in the same stroke.
- **Raw-entity feasibility (0.3 d).** Can `STEPConstruct_Styles` + direct `StepVisual_*` construction produce reflectance beyond ambient? If not, R1 requires textual emission (which S4 arm B makes trivial).

**Pass:** per-face + per-solid + per-instance colour into both schemas, plus transparency/rendering ⇒ green. Colour only ⇒ amber (R0/R2 + glTF). Cannot write per-face or per-instance colour to AP214 ⇒ **red**: post-processing has no advantage over the built-in.

### S2 — Consumers: does the achievable rung actually arrive? · 1.0 d
Take S1's tiny unambiguous probe files plus S0's baselines into every consumer class.

- **Blender/STEPper NEXT:** automate via `ci\parity_harness.py` and diff the JSON scene snapshot. The importer reads `ColorSurf > ColorGen > ColorCurv` at three levels (instance label, product label, per-face sub-shape via `GetSubShapes_s`) — this directly confirms our three write levels land on its three read levels. Tightest feedback loop available; build it first.
- **Rhino + Fusion:** opens cleanly? per-face colour honoured? instance overrides honoured? warnings?
- **KeyShot / SW Visualize:** which material channels populate?
- **Regression guard:** open the richest file we can produce in the dumbest reader available. Anything that makes a consumer error or refuse the file is not conformant in practice, whatever the EXPRESS says, and gets dropped.

**Pass:** a rung counts only if ≥2 of 3 consumer classes read it **and STEPper NEXT is one of them**, and nothing we emit breaks any consumer.

> **Gate A (~day 3.5).** S0+S1+S2 answer *"is there a worthwhile, reachable, consumable target?"* A no here ends the study for ~3.5 days spent. This ordering is the point.

### S3 — Appearance harvest fidelity · 2.0 d
- **Resolved channel (0.5 d).** For every face of C2/C4: `IComponent2.IGetMaterialPropertyValuesForFace(face)` and `IFace2.GetMaterialPropertyValues2` (the 9-double `[R,G,B,Ambient,Diffuse,Specular,Shininess,Transparency,Emission]`), plus `HasMaterialPropertyValues`. **Test the hypothesis that `IGetMaterialPropertyValuesForFace` already implements SolidWorks' own precedence** by comparing against a screen-sampled flat-shaded screenshot. If it holds, we never reimplement the six-scope rules — a large de-risking.
- **Rich channel (0.5 d).** Enumerate `GetRenderMaterials2` on the doc and on each `IComponent2`; dump every `IRenderMaterial` property plus `GetEntities()` and the linked display states; build the scope map.
- **Precedence reconciliation (0.3 d).** Record every disagreement between the two channels; produce an evidence-backed rule set — or the finding that the resolved channel makes it unnecessary.
- **`.p2m` reconciliation (0.3 d).** Parse each harvested `FileName`; diff library values against live `IRenderMaterial` values. Expected: `IRenderMaterial` is instance state and wins; the `.p2m` is needed only for fields the API doesn't expose (`sw_shader`, `displacementDistance`). Prove it by editing a roughness in SW and re-reading both.
- **Textures/mapping/decals (0.2 d).** Texture filename (resolve relative paths), real-world `Width`/`Height` vs `.p2m` `initTextureWidth/Height`, `MappingType`, rotation/translation, mirrors; decal properties. **Note the hard constraint:** `IFace2.GetTessTextures()` returns UVs per tessellation triangle — UVs exist on the mesh, not parametrically on the B-rep face. Another independent reason R4 is the wrong target and glTF is the right one.
- **Perf (0.2 d).** Time the full harvest on C6 (>50k faces); record faces/second. **This single number decides addin-vs-automation for the production design.**

**Pass:** resolved colour matches the SW screenshot on ≥99% of sampled faces in C2/C4; display-state and configuration variants distinguishable; every field either returns a plausible value or is documented unavailable.

### S4 — Face and instance matching: the biggest technical risk · 2.5 d
**Question.** Can a SolidWorks `IFace2` be matched to the corresponding `TopoDS_Face` in SolidWorks' own STEP output reliably — and **detectably** when it fails? And can the assembly instance tree be matched to the STEP product-occurrence tree?

Keys — SW side: `GetArea`, `GetBox`, `Normal`, `GetUVBounds`, edge/loop counts, `GetSurface()` → `IsPlane/IsCylinder/...` + `CylinderParams` etc. OCCT side: `BRepGProp::SurfaceProperties` (area + centre of mass), `BRepBndLib`, `BRepAdaptor_Surface::GetType()` + analytic params, `BRepTools::UVBounds`.

Algorithm to test, **per body in body-local coordinates**, never globally: bucket on surface type + quantised area + bbox diagonal → nearest centroid → verify with outward normal at UV midpoint and analytic axis/radius → resolve residual N-to-N buckets by assignment → **report every unmatched or ambiguous face**.

- **Split-periodic trap.** `swStepExportSplitPeriodic` (and `_SplitFacesSTEP242`) turn one SW cylinder into N STEP faces. Mitigation to test: match by **point containment** (sample points via `EvaluateAtPoint`, classify against OCCT faces) rather than area equality — then 1:N resolves naturally and splitting stops being a threat.
- **Instance mapping.** SW component-occurrence tree (`IComponent2` path + referenced configuration/display state) against the STEP `NEXT_ASSEMBLY_USAGE_OCCURRENCE` tree via the same XCAF walk `importer.py` performs (`IsAssembly_s`/`GetComponents_s`/`IsReference_s`/`GetReferredShape_s`/`GetLocation_s`). This is what makes the C4 component-override case work — not an afterthought.
- **Arm A — OCCT round-trip.** Reader → XCAF → colours on sub-shape labels → `STEPCAFControl_Writer`. **Measure the collateral damage:** face count, total area, volume, entity count and validation properties before vs after, since OCCT rewrites the whole file and may heal/re-tolerance the B-rep. Check whether SW's own styled items survive and duplicate ours.
- **Arm B — textual splice.** Read with OCCT purely as an interpreter; recover each matched face's source `#nnn ADVANCED_FACE` via `XSControl_TransferReader::EntityFromShapeResult`; rewrite **only the presentation section** of SolidWorks' own bytes. Geometry stays byte-identical — no healing, no lost PMI — and it sidesteps OCCT's missing reflectance subtypes entirely. Compare head-to-head with arm A.
- **De-risking alternative (0.3 d).** Export part-by-part and compose the assembly in XCAF ourselves: collapses matching to tens of faces per file and makes instance overrides a pure label operation. Cost: in-context features lost, file count explodes. Record the trade honestly.

**Pass:** ≥99.5% match on C1–C4/C7, ≥99% on C6, and **zero silent mismatches** — verified by deliberately injecting near-duplicate faces (mirrored part, linear pattern) and confirming the algorithm reports ambiguity rather than guessing.

**98–99.5% with loud failures** ⇒ viable with per-body fallback. **<98% or silent mismatches** ⇒ do not ship per-face colour; recommend per-body/per-instance only, which needs no face matching and still fixes the component-override defect. That reduced product is genuinely useful and should be presented as such.

> **Gate B (~day 7.5).** S3+S4 answer *"can we attach the right appearance to the right thing?"* A no here skips S5/S6 (~4 days saved).

### S5 — Hosting and toolchain · 1.5 d · deferred deliberately until after Gate B
- **Acquire OCCT 7.9.3 for MSVC (0.4 d).** Prefer official VC14 x64 binaries; else vcpkg; else CMake with the VS 17 generator (**no ninja/conda installed** — STEPper NEXT's CI uses conda, which is not available here). Module set: `TKernel TKMath TKG2d TKG3d TKGeomBase TKGeomAlgo TKBRep TKTopAlgo TKShHealing TKMesh TKXSBase TKDE TKDESTEP TKLCAF TKCAF TKXCAF` (+ `TKDEGLTF` for R3). **Build with TBB off.**
- **Collision audit (0.3 d).** SolidWorks 2024 ships `tbb12.dll` and `tbbmalloc.dll` in its own directory and Windows resolves by module name — an in-process OCCT built against a different TBB would silently bind to SolidWorks' loaded copy. This is a named, concrete risk. Enumerate `SLDWORKS.exe`'s loaded modules and intersect against the OCCT dependency set (expect `tbb12`, `zlib`, `freetype`, `FreeImage`).
- **Load test (0.4 d).** From a minimal addin stub — the only place in this study an addin is needed — load OCCT from the addin folder and run one STEP read+write. SolidWorks stays stable across repeated load/unload; `SetDllDirectory`/`AddDllDirectory` finds DLLs beside the addin.
- **Control arms (0.4 d).** Time the same work through (a) a flat `extern "C"` DLL + `[DllImport]` and (b) an out-of-process `worker.exe`. **Why measure these despite the C++/CLI decision:** C++/CLI is confirmed available (MSVC 14.44.35207 with `msvcmrt.lib`, `vcclr.h`, `msclr\`), so it is not blocked — but if S4 selects arm B, OCCT is only an *interpreter* returning face keys and entity ids, which is a natural C ABI and wants no mixed-mode loader. STEPper NEXT's `native\stepper_native.cpp` already proves the "only bytes cross the boundary" pattern in this codebase family. For a one-shot export the out-of-process overhead will be small, and a crashed worker cannot take an unsaved assembly with it. The decision stands unless the numbers say otherwise; the report presents them either way.

### S6 — End-to-end vertical slice · 2.0 d
Wire the winning choices into one pass on C4, then C6: native export → harvest → match → rewrite presentation → companion glTF if R3 is in play → run S2's consumer matrix against the result.

**Pass:** C4's component override renders correctly from our file and incorrectly from SolidWorks' own, in Blender, same importer settings. C6 end-to-end under ~2 minutes. Produce the side-by-side image (SW viewport / native STEP in Blender / our STEP in Blender) — that single image is the report's headline evidence.

### S7 — Report and go/no-go · 0.5 d

---

## Scaffolding

```
c:\PeakDesign\NEXT-STEP-SW\
├── PLAN.md / FINDINGS.md          FINDINGS skeleton written day 1, filled as we go
├── corpus\CORPUS.md               why each model is in the set
├── tools\Peak.StepSpike.Harvest\  net48 x64 console, SW COM automation
│   ├── Program.cs                 verbs: baseline | harvest | facekeys | slice
│   ├── SwSession.cs  AppearanceHarvest.cs  FaceKeyDump.cs  Log.cs
├── tools\stepdump.py  occt_probe.py  occt_match.py
├── spikes\S0..S7\README.md        question / method / result / verdict
└── evidence\                      every file cited by FINDINGS.md
```

**Spikes run from a standalone console exe, not an addin.** Every API needed (`GetRenderMaterials2`, `IGetMaterialPropertyValuesForFace`, `GetMaterialPropertyValues2`, `GetTessTextures`, `GetDisplayStateSetting`, `PublishSTEP242File`, `SetUserPreferenceIntegerValue`) is plain COM automation on `ISldWorks`; none is addin-only. The only cost is marshalling (~20–100 µs/call), which S3 measures. Escalate to an addin only if that measurement is bad or an API fails out-of-process — and record either outcome as a finding.

Copy the interop reference block verbatim from [Peak.Release.csproj](c:/PeakDesign/Peak-Release/Peak.Release.csproj): SW2022 interops from `api\redist`, `Private=True`, `SpecificVersion=False`, `Microsoft.NETFramework.ReferenceAssemblies`, `AppendTargetFrameworkToOutputPath=false`. **`<PlatformTarget>x64</PlatformTarget>`.** The `swpublished` binding hazard that csproj warns about does not apply to a standalone exe — it never loads `ISwAddin`, so private copies suffice and no `AssemblyResolve` hook is needed.

**Reuse rather than rebuild:** `ci\parity_harness.py` (deterministic JSON scene snapshot — the automated consumer oracle for S2/S6), `analyzer.py` / `stepanalyzer.py` (STEP inspection), `ci\baselines\mat_ap214.step` and `mat_ap242.step` (existing OCCT-7.9-written reference files), `native\CMakeLists.txt` (the OCCT-against-MSVC build precedent), and `AddIn.Log()` from Peak-Custom-Properties.

**Corpus — build day 1; every later spike blocks on it.** C1 single part, one appearance · C2 stacked face+feature+body overrides · C3 textured appearance with real-world size · C4 **assembly where a component override masks the part's own appearance — the case native SW gets wrong, and the study's whole justification** · C5 ≥2 display states and ≥2 configurations · C6 large real Peak assembly >50k faces · C7 cylinders/tori/sphere/seamed periodic face · C8 decal.

---

## Verification

Each spike is verified by its own pass/fail criterion above; the study as a whole is verified by:

1. **Automated:** `ci\parity_harness.py` diffing Blender scene snapshots for every probe and slice output — run it on S1's files, S0's baselines and S6's result, and diff against each other rather than against prose.
2. **Manual, and unavoidable:** opening the S6 output in Rhino/Fusion and KeyShot/Visualize, and the side-by-side visual against the SolidWorks viewport.
3. **Hard rule: every claim in `FINDINGS.md` cites a file in `evidence\`.** No claim from memory or from vendor documentation alone. The report's value is entirely in its artifacts.

### `FINDINGS.md` structure
Verdict (up front, one paragraph) · the problem measured (S0 table + the literal `PublishSTEP242File` return) · the ceiling (S1 probe matrix + the `StepVisual` symbol dump) · **capability matrix: SW source field × rung × emitted entity × each consumer, one row per appearance property, every cell citing evidence** · matching reliability (S4 numbers per model and surface type, arm A vs B) · recommended architecture with measured overheads · costed production plan if GO · risks carried forward · **what we could not determine** (explicit, not omitted) · evidence index.

### Go / no-go criteria — fixed now, before any spike runs

**GO** requires all of: S0 shows a concrete entity-level deficiency, specifically that C4 is wrong in native output · S1 reaches ≥R0 conformantly in AP214 · S2 shows ≥2 of 3 consumer classes read it, STEPper NEXT among them, breaking nothing · S4 hits ≥99.5%/≥99% with zero silent mismatches · S5 finds at least one hosting model that doesn't destabilise SolidWorks (out-of-process counts) · S6 visibly beats native on C4.

**GO-REDUCED:** S4 at 98–99.5% ⇒ per-instance and per-body colour, per-face only where confident. S1 caps at R0 ⇒ colour-hierarchy fix + R3 companion file, drop rendering properties. In-process OCCT unsafe ⇒ out-of-process worker, feature set unchanged.

**NO-GO** if: S0 shows SolidWorks already handles C4 correctly (premise wrong) · S1 shows OCCT cannot write per-face or per-instance colour to AP214 (no advantage over built-in) · S2 shows no consumer reads what we can write · S4 produces undetectable silent mismatches (**mis-coloured faces are worse than no colour, because they are wrong without warning**).

If NO-GO on the STEP path specifically, the study will by then have proven the alternative in S1's cross-write control: **export glTF from the same harvested data** — where textures, UVs, roughness and metallic are all first-class — and keep SolidWorks' native STEP for geometry.

---

## Effort

| Spike | Days | Gate | SW | C++ |
|---|---|---|---|---|
| Scaffolding + corpus + FINDINGS skeleton | 0.5 | | ✓ | |
| S0 baseline | 0.5 | | ✓ | |
| S1 ceiling | 1.5 | **A** | | |
| S2 consumers | 1.0 | **A** | | |
| S3 harvest fidelity | 2.0 | | ✓ | |
| S4 face + instance matching | 2.5 | **B** | ✓ | |
| S5 hosting and toolchain | 1.5 | | ✓ | ✓ |
| S6 vertical slice | 2.0 | | ✓ | maybe |
| S7 report | 0.5 | | | |
| **Total** | **12.0** | | | |

Plus ~25% contingency ⇒ **3 working weeks**. Gate A at ~day 3.5 can end it for ~3.5 days spent; Gate B at ~day 7.5.
