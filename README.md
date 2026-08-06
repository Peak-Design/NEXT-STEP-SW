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
come out the same colour. A top-level override that paints a whole assembly is
dropped entirely. NEXT-STEP puts them back.

<!-- Three-panel comparison: SolidWorks viewport / native STEP export / NEXT-STEP export -->
![SolidWorks viewport, native STEP export, and NEXT-STEP export compared](docs/images/comparison.png)

## What it fixes

**Appearance hierarchy.** SolidWorks resolves appearance top down — assembly
override beats sub-assembly, which beats component override, which beats part,
body, feature and face. Its STEP export does not: everything above the part is
discarded. NEXT-STEP walks the real hierarchy and writes the colour SolidWorks
actually displays.

**Re-used parts.** A part used more than once shares one shape representation
carrying one styled item, so every occurrence past the first is wrong. This is
the defect that makes a re-used part come into a renderer in somebody else's
colour.

**Engineering material.** Material name and density, which SolidWorks does not
export to STEP in any form. Optional.

**Geometry is never touched.** SolidWorks writes the B-rep and NEXT-STEP only
adds and corrects presentation entities, so everything before `ENDSEC;` stays
byte-identical. No re-tolerancing, no healing, no lost PMI. If you trust
SolidWorks' STEP geometry today, you still can.

## Install

1. Download the latest release archive and extract it anywhere.
2. Close SolidWorks.
3. Run **Install.bat**. It asks for administrator rights, because registering
   a COM add-in writes to `HKEY_LOCAL_MACHINE`.
4. Start SolidWorks. Enable **NEXT-STEP** under *Tools → Add-Ins* if it is not
   already ticked.

**Export STEP+** appears on the NEXT-STEP tab for parts and assemblies.

Run **Uninstall.bat** to remove it.

Windows SmartScreen may warn about the download, because the archive is not
code-signed. Choose *More info → Run anyway*, or check the files first.

### Supported versions

SolidWorks 2022 through 2026, 64-bit. Requires .NET Framework 4.8, which ships
with Windows 10 and 11.

## The two export modes

**De-instance (default on).** Occurrences are grouped by the colour they
resolve to, and each group gets its own copy of the part carrying a plain
`STYLED_ITEM` — the encoding every reader understands. Occurrences that resolve
to the *same* colour stay genuine instances of one product, so a top-level
override that paints an assembly one colour duplicates nothing at all. Only
genuinely different-looking occurrences cost extra geometry.

**De-instance off.** Geometry stays fully instanced and each occurrence's
colour is written as `CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM`, the ISO
10303-46 entity for exactly this. Compact and correct by the standard, but
readers that do not implement occurrence styling fall back to the part's own
colour — which today is most of them.

**Engineering material (default off).** Writes material name and density. With
de-instancing on, each distinct appearance gets its own numbered variant of the
material name (`Plain Carbon Steel`, `Plain Carbon Steel.001`), so a reader that
builds one material per material name keeps the colours apart instead of merging
every copy of a part into one. That is a display-side workaround with a cost:
STEP has no association between a material and an appearance, so a file exported
this way reports a numbered name to anything reading the material properly. It
is off by default for that reason.

## Known limits

- Textures, UV mapping, roughness and metallic cannot be represented in STEP at
  all. No entity exists for them in AP214 or AP242. A companion glTF is the
  planned route — see [FINDINGS.md](FINDINGS.md) §3.3.
- AP242 export through `PublishSTEP242File` requires a SolidWorks MBD licence
  and is not used. Everything here works in AP214, which needs no extra licence.

## Building from source

Requires SolidWorks (for the interop assemblies) and the .NET SDK.

```
dotnet build src\Peak.NextStep\Peak.NextStep.csproj -c Release
src\Peak.NextStep\Register-Addin.bat
```

Regenerate the icons after editing the generator:

```
python tools\make_icons.py
```

To cut a release, see [RELEASING.md](RELEASING.md).

## How this was arrived at

[FINDINGS.md](FINDINGS.md) is the measurement record: what SolidWorks actually
emits entity by entity, what STEP and OCCT can carry, what each consumer reads,
and what was ruled out and why. Every claim in it cites a file in `evidence/`.
`PLAN.md` is the original study plan.

## Licence

MIT — see [LICENSE](LICENSE). The SolidWorks interop assemblies are Dassault
Systèmes' property and are not covered by it; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which also explains why a
copyleft licence is a poor fit for a SolidWorks add-in.
