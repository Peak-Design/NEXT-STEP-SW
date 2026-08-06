# Releasing

The procedure is four commands. Read the reasons below only if something goes
wrong.

## Versioning

There is one version number, in one place. It is `<Version>` in
[src/Peak.NextStep/Peak.NextStep.csproj](src/Peak.NextStep/Peak.NextStep.csproj).

The add-in reads it from its own assembly. The installer reads it from the built
DLL. The release script makes the git tag from it. Nothing else stores a
version, so nothing else can disagree.

Change it as `MAJOR.MINOR.PATCH`:

| Change | New number |
|---|---|
| A repair, with no change that the user must know about | PATCH: `0.1.0` to `0.1.1` |
| A new option, or new export behaviour | MINOR: `0.1.0` to `0.2.0` |
| Output changes, and an existing workflow stops working | MAJOR |

Below `1.0.0`, a MINOR release can break things. After the export format
settles, release `1.0.0` and then follow the table above.

## Make a release

```powershell
# 1. Change <Version> in the csproj. Add the entry to CHANGELOG.md.

# 2. Commit those two edits.
git add -A
git commit -m "Release 0.2.0"

# 3. Close SolidWorks. Then build, package and tag.
.\tools\Make-Release.ps1 -Tag

# 4. Push the commit and the tag.
git push
git push --tags
```

Then open GitHub. Select **Releases**, then **Draft a new release**. Select the
tag that you pushed. Paste the CHANGELOG entry as the description. Attach
`dist\NEXT-STEP-<version>.zip`.

`Make-Release.ps1` refuses to tag a tree with uncommitted changes. You therefore
cannot skip step 2 by accident.

## What the release archive holds

```
NEXT-STEP-<version>/
  Install.bat              double-click this to install
  Uninstall.bat
  Install-NEXT-STEP.ps1    the installer itself. It asks for admin rights.
  LICENSE
  THIRD-PARTY-NOTICES.md
  README.md
  app/
    Peak.NextStep.dll
    SolidWorks.Interop.*.dll
    icons/*.png
```

The archive holds the interop assemblies on purpose. They have strong names, no
copies exist in the GAC, and SolidWorks publishes no binding redirects. On a
SolidWorks newer than the one that built the add-in, only a copy next to the DLL
can satisfy the reference. Without these files, the add-in loads on one
SolidWorks version and fails on all the others.

## Why there is no CI build

A build needs `SolidWorks.Interop.*.dll` from a SolidWorks installation. The
hosted runners of GitHub have no SolidWorks, and cannot have one. A workflow
that cannot compile the project gives only a false sense of cover. Releases are
therefore built on a machine with SolidWorks, which is the same machine that
tests the add-in.

If this project ever needs CI, there are two options. The first is a self-hosted
runner on a licensed machine. The second is to commit the three interop
assemblies to the repository. The `api\redist` directory of Dassault exists for
that purpose, but it puts their binaries in a public repository. Make that
choice on purpose, not by accident.

## Before you tag

- [ ] The version in the csproj is correct
- [ ] The CHANGELOG entry is written
- [ ] You installed from the built archive, not only from `bin\Release`
- [ ] You exported an assembly with a component override and checked the colours
- [ ] Uninstall.bat left no registry keys

## If a release is wrong

Do not move a tag after you push it. Anyone who already fetched the tag keeps
the old commit, and the two then disagree with no message. Change the PATCH
number and release again. You can delete the wrong GitHub release, but leave the
tag.
