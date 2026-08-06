# Changelog

The notable changes to NEXT-STEP. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The version numbers
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Include hidden components** option, off by default. SolidWorks keeps hidden
  components out of a STEP file and has no preference for this. It asks the user,
  and the silent export that the add-in uses answers that question. With this
  option on, the add-in shows the hidden components for the export and hides
  them again afterwards. It still never exports suppressed components, because
  SolidWorks must rebuild the assembly to resolve one.

### Fixed

- The add-in no longer reports that hidden and suppressed components fail to
  match the STEP file. They are absent from the file by design, so the warning
  was a false alarm on every export of an assembly with a hidden component.

## [0.1.0] - 2026-08-06

The first release.

### Added

- **Appearance hierarchy repair.** The add-in resolves the appearance that
  SolidWorks displays for each component occurrence, and writes it into the STEP
  file. An assembly override beats a sub-assembly. A sub-assembly beats a
  component override. A component override beats the part, body, feature and
  face. The SolidWorks export discards everything above the part.
- **De-instance mode**, on by default. The add-in groups the occurrences by
  resolved colour. Each group gets one copy of the part with a plain
  `STYLED_ITEM`. Occurrences with the same colour stay instances. Nothing is
  therefore duplicated without a visible reason.
- **Instanced mode**, with de-instance off. The add-in writes one
  `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM` for each occurrence. This is the
  ISO 10303-46 form, for readers that support it.
- **Engineering material export**, off by default. This writes the material
  name, description and density, which SolidWorks writes to STEP in no form.
  With de-instancing on, each different appearance gets a numbered material
  name. A reader that keys a material by its name then keeps the colours apart.
- Registers for SolidWorks 2022, 2024, 2025 and 2026.
- An installer that asks for admin rights, and a matching uninstaller.

### Notes

- The add-in does not change geometry. Every byte before `ENDSEC;` in the
  SolidWorks output stays the same.
- The add-in does not export textures, UVs, roughness or metallic values,
  because STEP has no entity for them. See FINDINGS.md §3.3.
- The add-in does not use AP242, which needs a SolidWorks MBD licence.
  Everything here works in AP214.
