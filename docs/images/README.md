# README images

## `comparison.png`

Three panels, from left to right. Each panel shows the same assembly:

1. **SolidWorks viewport.** The appearances as the user made them, with the
   overrides at component level and assembly level.
2. **Native STEP export.** The AP214 export of SolidWorks, with appearances on,
   imported into Blender. The overrides are gone. Every re-used part has one
   colour.
3. **NEXT-STEP export.** The same assembly through Export STEP+, imported the
   same way. It matches panel 1.

Panel 2 and panel 3 must use the same importer settings and the same camera.
The only difference must be the program that wrote the file.

Keep the same path and an aspect of about 3:1, so that the README layout holds.

## `icon.png`

This file is a copy of `src/Peak.NextStep/icons/NextStep_128.png`. To change it,
copy that file again:

```
copy src\Peak.NextStep\icons\NextStep_128.png docs\images\icon.png
```
