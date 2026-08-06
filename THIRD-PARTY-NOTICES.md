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

## Files that this software produces

A STEP file written by this add-in holds your data. Nothing in this licence
applies to it.
