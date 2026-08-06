# Third-party notices

## SolidWorks interop assemblies

    SolidWorks.Interop.sldworks.dll
    SolidWorks.Interop.swconst.dll
    SolidWorks.Interop.swpublished.dll

These files are the property of Dassault Systèmes SolidWorks Corporation. The
MIT licence of this project does **not** cover them. This project grants no
rights to them.

The repository does not hold these files. The build reads them from the
SolidWorks installation on the build machine, in
`…\SOLIDWORKS <year>\SOLIDWORKS\api\redist\`. A release archive includes copies
of them. The add-in can then load on a machine whose SolidWorks version differs
from the one that compiled it. Dassault names that directory `redist` because it
supplies these assemblies for this purpose. To build from source you need a
SolidWorks installation. Anyone who runs a SolidWorks add-in has one.

## Why the MIT licence, and not a copyleft one

A SolidWorks add-in is a COM server. SolidWorks loads it into `SLDWORKS.exe`,
and it links against the interop assemblies above. Dassault sets no condition on
the licence of your own add-in code, so the choice is ours. In practice, however,
the choice is not free.

A strong copyleft licence such as the GPL asks for the complete source of the
whole combined work. It also asks that every recipient can link that work again.
Neither is possible here. The interop assemblies are closed, and nobody can
relicense them. The add-in also has no purpose outside a closed host. To claim
GPL terms over a work that cannot meet them creates real doubt. That doubt falls
on anyone who uses this in a commercial CAD workflow, and that is where a
SolidWorks add-in lives.

The MIT licence avoids all of this. It is short. It asks nothing of the host, or
of the designs of the user. It is also the usual choice for a SolidWorks add-in.

Apache-2.0 is an equally sound alternative, if this project ever wants an
explicit patent grant. For a project of this size the practical difference is
small.

## Files that this software produces

A STEP file written by this add-in holds your data. Nothing in this licence
applies to it.
