<h1 align="center">
  <img src="docs/images/icon.png" width="96" alt=""><br>
  NEXT-STEP
</h1>

<p align="center">
  <strong>STEP export for SolidWorks that keeps your appearances.</strong><br>
  <a href="../../releases/latest">Download</a> ·
  <a href="#install">Install</a> ·
  <a href="#what-it-fixes">What it fixes</a> ·
  <a href="https://ko-fi.com/oskarasspalvys">Tip jar</a>
</p>

---

SolidWorks flattens component and assembly level appearance overrides when it
writes STEP. Two instances of the same part, overridden to different colours,
come out the same colour. A top level override that paints a whole assembly
disappears. NEXT-STEP puts these colours back.

<!-- Three-panel comparison: SolidWorks viewport / native STEP export / NEXT-STEP export -->
![SolidWorks viewport, native STEP export, and NEXT-STEP export compared](docs/images/comparison.png)

<p align="center"><sub>STEP files imported into Blender with
<a href="https://github.com/Peak-Design/STEPper_NEXT">STEPper NEXT</a>.</sub></p>

## What it fixes

**Appearance overrides.** An override on a component or an assembly survives the
export. SolidWorks drops them and writes the colour of the part instead.

**Re-used parts.** Each instance of a part keeps its own colour. In a SolidWorks
export they all share one colour, so every instance after the first is wrong.

**Engineering material.** The material name and density go into the file.
SolidWorks writes neither.

**Geometry is untouched.** NEXT-STEP changes only colour and material. The
geometry in the file is the SolidWorks geometry, byte for byte. NEXT-STEP does
not re-tolerance the geometry, does not heal it, and does not remove PMI.

## Install

1. Download the latest release archive and extract it.
2. Close SolidWorks.
3. Run **Install.bat**, and accept the prompt for administrator rights.
4. Start SolidWorks.
5. If **NEXT-STEP** is not already ticked, tick it in *Tools → Add-Ins*.

**Export STEP+** is on the NEXT-STEP tab for parts and assemblies.

To remove the add-in, run **Uninstall.bat**.

Windows SmartScreen can warn about the download, because the archive is not
signed. Select *More info → Run anyway*.

### Supported versions

SolidWorks 2022 to 2026, 64-bit. NEXT-STEP needs .NET Framework 4.8. Windows
10 and Windows 11 include it.

## The export options

**De-instance (default on).** Instances that need different colours become
separate parts. Instances that share a colour stay instances, and use no extra
geometry.

Turn de-instancing off to keep every instance shared. The file is then smaller.
Only readers that support per-instance colour show the correct colours. Most
readers do not, including Fusion 360.

**Include hidden components (default off).** NEXT-STEP never exports suppressed
components.

**Export only the selected components (default off).** The option becomes
available when you select one or more components. A selected assembly brings
every component inside it. Each part stays in its place in the assembly tree.
A face or an edge that you select counts as its component.

Isolate does not limit the export. To export a part of the assembly, select the
components and use this option.

**Engineering material (default off).** Writes the material name and density.

With de-instancing on, NEXT-STEP numbers the material names for each colour,
such as `Plain Carbon Steel` and `Plain Carbon Steel.001`. To keep the exact
material names, turn de-instancing off.

## Known limits

- STEP cannot carry textures, UV mapping, roughness or metallic values. No
  entity for them exists in AP214 or AP242. A companion glTF file is the planned
  answer.
- AP242 export through `PublishSTEP242File` needs a SolidWorks MBD licence, so
  NEXT-STEP does not use it. Everything here works in AP214, which needs no
  extra licence.

## Build from source

You need SolidWorks, for the interop assemblies, and the .NET SDK.

```
dotnet build src\Peak.NextStep\Peak.NextStep.csproj -c Release
src\Peak.NextStep\Register-Addin.bat
```

## Support

[Peak Design](https://github.com/Peak-Design) is the current maintainer.
Tips are welcome:

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/oskarasspalvys)

## Licence

MIT. See [LICENSE](LICENSE). The MIT licence does not cover the SolidWorks
interop assemblies, which are the property of Dassault Systèmes. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
