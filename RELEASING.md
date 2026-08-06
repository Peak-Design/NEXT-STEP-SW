# Releasing

The whole procedure is four commands. Read the "why" sections only if
something goes wrong.

## Versioning

One number, in one place: `<Version>` in
[src/Peak.NextStep/Peak.NextStep.csproj](src/Peak.NextStep/Peak.NextStep.csproj).

The add-in reads it back off its own assembly, the installer reads it off the
built DLL, and the release script derives the git tag from it. Nothing else
stores a version, so nothing else can disagree.

Bump it as `MAJOR.MINOR.PATCH`:

| Change | Bump |
|---|---|
| Bug fix, no behaviour change users must know about | PATCH — `0.1.0` → `0.1.1` |
| New option, new export behaviour | MINOR — `0.1.0` → `0.2.0` |
| Output changes in a way that breaks an existing workflow | MAJOR |

Below `1.0.0` the convention is that MINOR may break things. Once the export
format is settled, release `1.0.0` and hold to the table above.

## Cutting a release

```powershell
# 1. Bump <Version> in the csproj, and add the entry to CHANGELOG.md.

# 2. Commit those two edits.
git add -A
git commit -m "Release 0.2.0"

# 3. Build, package and tag. Close SolidWorks first.
.\tools\Make-Release.ps1 -Tag

# 4. Push the commit and the tag.
git push
git push --tags
```

Then on GitHub: **Releases → Draft a new release**, choose the tag you just
pushed, paste the CHANGELOG entry as the description, and attach
`dist\NEXT-STEP-<version>.zip`.

`Make-Release.ps1` refuses to tag a dirty working tree, so step 2 cannot be
skipped by accident.

## What the release archive contains

```
NEXT-STEP-<version>/
  Install.bat              double-click to install
  Uninstall.bat
  Install-NEXT-STEP.ps1    the actual installer; self-elevates
  LICENSE
  THIRD-PARTY-NOTICES.md
  README.md
  app/
    Peak.NextStep.dll
    SolidWorks.Interop.*.dll
    icons/*.png
```

The interop assemblies ship with the archive deliberately. They are
strong-named, there are no copies in the GAC, and SolidWorks publishes no
binding redirects — so on a SolidWorks version newer than the one the add-in
was compiled against, the only thing that can satisfy the reference is a copy
sitting next to the DLL. Without them the add-in fails to load on every
version except the one it was built for.

## Why there is no CI build

Building needs `SolidWorks.Interop.*.dll` from a SolidWorks installation.
GitHub's hosted runners do not have SolidWorks and cannot have it. A workflow
that could not compile the project would only give a false sense of coverage,
so releases are built on a machine that has SolidWorks — which is the same
machine the add-in is tested on.

If this ever needs CI, the options are a self-hosted runner on a licensed
machine, or committing the three interop assemblies to the repository. The
second is what Dassault's `api\redist` directory is for, but it puts their
binaries in a public repo, which is a decision worth making deliberately
rather than by drift.

## Before tagging

- [ ] Version bumped in the csproj
- [ ] CHANGELOG entry written
- [ ] Icons regenerated if the generator changed (`python tools\make_icons.py`)
- [ ] Installed from the built archive on a clean-ish machine, not just
      registered from `bin\Release`
- [ ] Exported an assembly with a component override and confirmed the colours
- [ ] Uninstall.bat leaves no registry keys behind

## If a release is wrong

Do not move a tag that has been pushed — anyone who already fetched it keeps
the old commit and the two silently disagree. Bump the PATCH version and
release again. Delete the bad GitHub release if it is misleading, but leave
the tag.
