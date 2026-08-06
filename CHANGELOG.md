# Changelog

The notable changes to NEXT-STEP. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The version numbers
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Include hidden components** option, off by default. Suppressed components
  are never exported.

### Fixed

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
  entity for them. See FINDINGS.md §3.3.
- AP242 is not used. Everything here works in AP214, which needs no extra
  licence.
