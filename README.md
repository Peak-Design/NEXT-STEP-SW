<h1 align="center">
  <img src="docs/images/icon.png" width="96" alt=""><br>
  NEXT-STEP
</h1>

<p align="center">
  <strong>STEP export for SolidWorks that keeps your appearances.</strong><br>
  <a href="../../releases/latest">Download</a> ·
  <a href="#install">Install</a> ·
  <a href="#what-it-fixes">What it fixes</a> ·
  <a href="FINDINGS.md">How it was measured</a>
</p>

---

SolidWorks flattens component and assembly level appearance overrides when it
writes STEP. Two instances of the same part, overridden to different colours,
come out the same colour. A top level override that paints a whole assembly
disappears. NEXT-STEP puts these colours back.

<!-- Three-panel comparison: SolidWorks viewport / native STEP export / NEXT-STEP export -->
![SolidWorks viewport, native STEP export, and NEXT-STEP export compared](docs/images/comparison.png)

## What it fixes

**Appearance hierarchy.** SolidWorks resolves appearance from the top down. An
assembly override beats a sub-assembly. A sub-assembly beats a component
override. A component override beats the part, body, feature and face. The
SolidWorks STEP export discards everything above the part. NEXT-STEP walks the
real hierarchy and writes the colour that SolidWorks displays.

**Re-used parts.** A part used more than once shares one shape representation
with one styled item. Every occurrence after the first one gets the wrong
colour. This is the defect that gives a re-used part somebody else's colour in
a renderer.

**Engineering material.** NEXT-STEP writes the material name and density.
SolidWorks does not export them to STEP in any form. This option is off by
default.

**NEXT-STEP does not change geometry.** SolidWorks writes the B-rep. NEXT-STEP
only adds and corrects presentation entities, so every byte before `ENDSEC;`
stays the same. There is no re-tolerancing, no healing and no loss of PMI. If
you trust the SolidWorks STEP geometry today, you can still trust it.

## Install

1. Download the latest release archive and extract it.
2. Close SolidWorks.
3. Run **Install.bat**. It asks for administrator rights, because a COM add-in
   registers itself in `HKEY_LOCAL_MACHINE`.
4. Start SolidWorks.
5. If **NEXT-STEP** is not already ticked, tick it in *Tools → Add-Ins*.

**Export STEP+** is on the NEXT-STEP tab for parts and assemblies.

To remove the add-in, run **Uninstall.bat**.

Windows SmartScreen can show a warning for the download, because the archive
has no code signature. Select *More info → Run anyway*, or examine the files
first.

### Supported versions

SolidWorks 2022 to 2026, 64-bit. NEXT-STEP needs .NET Framework 4.8. Windows
10 and Windows 11 include it.

## The export options

**De-instance (default on).** NEXT-STEP groups the occurrences by the colour
they resolve to. Each group gets its own copy of the part with a plain
`STYLED_ITEM`, which every reader understands. Occurrences that resolve to the
*same* colour stay true instances of one product. A top level override that
paints an assembly one colour therefore duplicates nothing. Only occurrences
that look different cost extra geometry.

**De-instance off.** The geometry stays fully instanced. NEXT-STEP writes the
colour of each occurrence as `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM`, the
ISO 10303-46 entity for this purpose. This output is compact and correct, but
most readers do not implement occurrence styling. Those readers show the colour
of the part instead.

**Include hidden components (default off).** SolidWorks keeps hidden components
out of a STEP file and has no setting for this. It asks you interactively, and a
silent export answers that question. When this option is on, NEXT-STEP shows the
hidden components for the export and hides them again afterwards. Neither
setting exports suppressed components, because SolidWorks must rebuild the
assembly to resolve one.

**Engineering material (default off).** NEXT-STEP writes the material name and
density. With de-instancing on, each different appearance also gets its own
numbered name. The names are then `Plain Carbon Steel`, `Plain Carbon
Steel.001`, and so on. A reader that builds one material for each material name
then keeps the colours apart. Without the numbers it merges every copy of the
part into one material.

This is a workaround with a cost. STEP has no relation between a material and an
appearance. A file exported this way therefore reports a numbered name to any
tool that reads the material correctly. This is why the option is off by
default.

## Known limits

- STEP cannot carry textures, UV mapping, roughness or metallic values. No
  entity for them exists in AP214 or AP242. A companion glTF file is the planned
  answer. See [FINDINGS.md](FINDINGS.md) §3.3.
- AP242 export through `PublishSTEP242File` needs a SolidWorks MBD licence, so
  NEXT-STEP does not use it. Everything here works in AP214, which needs no
  extra licence.

## Build from source

You need SolidWorks, for the interop assemblies, and the .NET SDK.

```
dotnet build src\Peak.NextStep\Peak.NextStep.csproj -c Release
src\Peak.NextStep\Register-Addin.bat
```

To make a release, see [RELEASING.md](RELEASING.md).

## How this was measured

[FINDINGS.md](FINDINGS.md) is the measurement record. It shows what SolidWorks
writes entity by entity, what STEP and OCCT can carry, and what each consumer
reads. It also records what was ruled out, and why. Every claim in it cites a
file in `evidence/`. `PLAN.md` is the original study plan.

## Licence

MIT. See [LICENSE](LICENSE). The MIT licence does not cover the SolidWorks
interop assemblies, which are the property of Dassault Systèmes. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). That file also explains why a
copyleft licence is a poor fit for a SolidWorks add-in.
