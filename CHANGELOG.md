# Changelog

Notable changes to NEXT-STEP. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Include hidden components** option (default off). SolidWorks omits hidden
  components from a STEP file and exposes no preference for it — it asks
  interactively, and the silent export the add-in uses answers that prompt.
  Switching this on shows hidden components for the duration of the export and
  hides them again afterwards. Suppressed components are still never exported:
  resolving one would rebuild the assembly.

### Fixed

- Hidden and suppressed components are no longer reported as failing to match
  the STEP file. They are absent from it by design, so the warning was a false
  alarm on every export of an assembly with anything hidden.

## [0.1.0] - 2026-08-06

First release.

### Added

- **Appearance hierarchy repair.** Resolves the appearance SolidWorks actually
  displays for each component occurrence — assembly override beats
  sub-assembly, which beats component override, which beats part, body, feature
  and face — and writes it into the STEP file. SolidWorks' own export discards
  everything above the part.
- **De-instance mode** (default on). Occurrences are grouped by resolved
  colour; each group gets one copy of the part carrying a plain `STYLED_ITEM`.
  Occurrences that resolve to the same colour stay instanced, so nothing is
  duplicated without a visible reason.
- **Instanced mode** (de-instance off). Writes
  `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM` per occurrence, the ISO 10303-46
  encoding, for readers that implement it.
- **Engineering material export** (default off). Material name, description and
  density, which SolidWorks does not write to STEP in any form. With
  de-instancing on, each distinct appearance gets a numbered variant of the
  material name so readers that key materials by name keep the colours apart.
- Registers for SolidWorks 2022, 2024, 2025 and 2026.
- Installer with self-elevation, and a matching uninstaller.

### Notes

- Geometry is never modified. Everything before `ENDSEC;` in SolidWorks'
  output is left byte-identical.
- Textures, UVs, roughness and metallic are not exported, because STEP has no
  entity for them. See FINDINGS.md §3.3.
- AP242 is not used; it requires a SolidWorks MBD licence. Everything here
  works in AP214.
