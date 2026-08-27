# Changelog

The notable changes to NEXT-STEP. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The version numbers
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-08-27

### Added

- **Export only the selected components** option, off by default and offered
  only when something is selected — an export that silently wrote an empty
  file would be the worst outcome here. The set is wider than what was
  clicked, in both directions, and both are needed: everything INSIDE a
  selected assembly comes with it, and every assembly ABOVE the selection
  stays visible, because SolidWorks omits the whole branch below a hidden
  node and trimming an ancestor would take the selection with it. A face or
  edge picked in the graphics area counts as its component.

  The appearance ladder is told the same set, so it no longer tries to repair
  occurrences that are not in the file and report them as unmatched.

  This is also the answer for **Isolate**, which the add-in cannot follow on
  its own: `swIsolateVisibility_e` defaults to WIREFRAME, so isolated-out
  components remain `swComponentVisible` and SolidWorks exports them, and
  `IAssemblyDoc` has no query for whether Isolate is even active — it exposes
  only Isolate, ExitIsolate, SaveIsolate and SetIsolateVisibility. The dialog
  now says so where the choice is made.

### Changed

- Visibility for an export is now decided in ONE pass and restored from what
  actually changed. Revealing hidden components and hiding unselected ones act
  on the same property, and a component that is both was touched by two passes
  with two undo lists — the order the restores ran in decided whether the user
  got their assembly back.

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
