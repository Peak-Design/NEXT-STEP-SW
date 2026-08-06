# Corpus

Eight SolidWorks models the spikes run against, as specified by Oscar
(authoritative — this replaces the earlier draft spec).

`.sldprt`/`.sldasm` files are gitignored. **C4 is the model the study is
justified by**; S0's premise check runs on it.

| # | Model | Contents | Spikes |
|---|---|---|---|
| **C1** | `C1_single_part` | Red material only, part level. | S0, S4 |
| **C2** | `C2_stacked_overrides` | Red at part level, orange at body, yellow at feature, green on a face. **Only yellow and green are visible** — yellow overrides the earlier two in precedence order. | S0, S3, S4 |
| **C3** | `C3_textured` | Checker texture on a cylindrical face, 37 × 37 mm real-world size. No part-level material. | S3 |
| **C4** | `C4_component_override_1.sldasm` | 2 instances of `C4_part_1` (red at part level, same as C1). One instance overridden orange, the other yellow. | S0, S3, S4, S6 |
| | `C4_component_override_2.sldasm` | As above, **plus a green assembly-level override** on top. | S0, S3, S4, S6 |
| **C5** | `C5_display_states` | 2 configurations with linked display states. Part-level overrides only, no assembly appearance. Config "1": one instance orange, one yellow. Config "2": one green, one cyan. | S0, S3 |
| **C6** | `SS65_02_00_00.SLDASM` | `C:\Dropbox\Projects\01 - Projects\Ezystak\01 - SS80 Redesign\04 - 3D Model\2022 Redesign\Solid Model\02-FEEDER CONVEYOR\00-FINAL ASSEMBLY\` | S3, S4, S6 |
| **C7** | `C7_periodic` | 5 bodies: red cylinder **with** a split cylindrical face, orange cylinder **without** a split periodic face, yellow cone, green torus, cyan sphere. | S0, S4 |
| **C8** | `C8_decal` | As C1, plus a barcode decal on the cylindrical face. | S3 |

## Why these shapes are the right ones

- **C2's stated ground truth is the test.** "Only yellow and green visible" is
  exactly what S3 checks the API against: if
  `IGetMaterialPropertyValuesForFace` returns yellow and green (and never red
  or orange) then SolidWorks resolves precedence for us and we do not
  reimplement it. Writing the expected answer down *before* running the spike
  is what makes it a test rather than an observation.
- **C4's two variants separate two different failure modes.** `_1` tests
  component-level overrides; `_2` adds an assembly-level override on top, which
  is the case where a naive exporter picks the wrong winner. Having both means
  a failure localises immediately.
- **C7 having a split and an unsplit cylinder in one part** is better than
  exporting one cylinder twice: both live in the same file under the same
  export settings, so any difference is the geometry's doing and not the
  translator's.
- **C6 is off-repo and large.** Referenced by path, never copied in.

## Still needed for S3

**C2 and C4 need a written ground truth.** Save a flat-shaded screenshot beside
each model recording which appearance SolidWorks actually displays on which
face. S3 compares the API's resolved colour against that screenshot; with no
screenshot there is nothing to compare against. For C2 the expectation is
already stated above (yellow body/feature, green face) — the screenshot just
makes it checkable.
