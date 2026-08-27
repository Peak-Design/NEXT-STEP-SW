# Changelog

The notable changes to NEXT-STEP. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The version numbers
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-08-27

### Added

- **Export only the selected components** option, off by default. The option
  becomes available when you select one or more components.
  - A selected assembly brings every component inside it.
  - Each part stays in its place in the assembly tree.
  - A face or an edge that you select counts as its component.
- The export report gives the number of components that the selection left out.

### Fixed

- The add-in puts back the visibility of every component that it changed for
  the export.

## [0.2.0] - 2026-08-06

### Added

- **Include hidden components** option, off by default. Suppressed components
  are never exported.

### Fixed

- **Nested assemblies now work.** The first release matched components as a
  flat list, so on a multi-level assembly almost every component failed to
  match and kept the wrong colour. The matcher now walks the assembly tree,
  level by level, the way the STEP file stores it.
- An override on a sub-assembly now reaches every part below it, at any depth.
  Overrides made inside a sub-assembly document are found too.
- A component below a hidden or suppressed component is now excluded with its
  parent, instead of producing a false "unmatched" warning.
- Multibody parts: every body of an overridden part takes the colour, not just
  the first.
- Two uses of one shared sub-assembly can now show different colours inside.
  The export copies the sub-assembly structure for the uses that differ. The
  geometry stays shared, and uses that look the same stay on one definition.
- The export report no longer warns that hidden and suppressed components could
  not be matched. They are absent from the file by design.

## [0.1.0] - 2026-08-06

The first release.

### Added

- **Appearance overrides.** An override on a component or an assembly survives
  the export. SolidWorks drops them and writes the colour of the part.
- **De-instance mode**, on by default. Instances that need different colours
  become separate parts. Instances that share a colour stay instances.
- **Instanced mode**, with de-instance off. Every instance stays shared, for
  readers that support per-instance colour.
- **Engineering material export**, off by default. Writes the material name and
  density. With de-instancing on, the names are numbered per colour.
- Registers for SolidWorks 2022, 2024, 2025 and 2026.
- An installer and a matching uninstaller.

### Notes

- Geometry is untouched. The geometry in the file is the SolidWorks geometry,
  byte for byte.
- Textures, UVs, roughness and metallic values are not exported. STEP has no
  entity for them.
- AP242 is not used. Everything here works in AP214, which needs no extra
  licence.
