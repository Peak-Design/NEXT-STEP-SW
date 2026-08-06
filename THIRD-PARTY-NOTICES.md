# Third-party notices

## SolidWorks interop assemblies

    SolidWorks.Interop.sldworks.dll
    SolidWorks.Interop.swconst.dll
    SolidWorks.Interop.swpublished.dll

These are the property of Dassault Systèmes SolidWorks Corporation. They are
**not** covered by this project's MIT licence and no rights to them are granted
here.

They are not committed to this repository. The build reads them from the
SolidWorks installation on the build machine
(`…\SOLIDWORKS <year>\SOLIDWORKS\api\redist\`), and copies of them are included
in release archives so the add-in can load on machines whose SolidWorks version
differs from the one it was compiled against. That directory is named `redist`
because Dassault provides these assemblies specifically for redistribution
alongside add-ins that use the SolidWorks API. Anyone building from source
needs a SolidWorks installation, which anyone running a SolidWorks add-in has
by definition.

## Why the MIT licence, and why not a copyleft one

A SolidWorks add-in is a COM server loaded into `SLDWORKS.exe` and linked
against the proprietary interop assemblies above. Dassault places no condition
on how you license your own add-in code, so the choice is ours — but it is not
unconstrained in practice.

A strong copyleft licence (GPL) requires that the complete corresponding source
be available for the whole combined work, and would oblige every recipient to
be able to relink it. Neither is possible here: the interop assemblies are
proprietary and cannot be relicensed, and the add-in has no meaning outside a
proprietary host. Claiming GPL terms over something that cannot satisfy them
would create real uncertainty for anyone using this in a commercial CAD
workflow, which is where a SolidWorks add-in lives.

MIT avoids all of that. It is short, it imposes nothing on the host or on the
user's own designs, and it is the conventional choice for SolidWorks add-ins.
Apache-2.0 would be an equally defensible alternative if an explicit patent
grant is ever wanted; the practical difference for a project this size is small.

## Files produced by this software

STEP files written by this add-in are your own data. Nothing in this licence
applies to them.
