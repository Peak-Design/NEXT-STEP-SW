# README images

## `comparison.png` — currently a placeholder, replace it

Three panels, left to right, all showing the **same assembly**:

1. **SolidWorks viewport** — the appearances as authored, including the
   component and assembly level overrides.
2. **Native STEP export** — SolidWorks' own AP214 export with appearances
   switched on, imported into Blender. The overrides are gone: re-used parts
   come in sharing one colour.
3. **NEXT-STEP export** — the same assembly through Export STEP+, imported the
   same way. Matches panel 1.

Panels 2 and 3 must use **identical importer settings and identical camera**,
or the comparison proves nothing. The only variable is which exporter wrote the
file.

`C4_component_override_1` in `corpus/` is the minimal case (two instances of one
part, overridden orange and yellow, so native export gives orange twice), but a
real assembly makes the point better.

Keep the same path and roughly 3:1 aspect so the README layout holds.

## `icon.png`

Generated — do not edit by hand. It is a copy of
`src/Peak.NextStep/icons/NextStep_128.png`. Regenerate both with:

```
python tools/make_icons.py
copy src\Peak.NextStep\icons\NextStep_128.png docs\images\icon.png
```
