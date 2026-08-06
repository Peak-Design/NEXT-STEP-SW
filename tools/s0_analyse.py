"""S0 analysis: what did SolidWorks actually emit?

Reads the export matrix produced by Peak.StepSpike.Harvest and answers the
questions the study turns on:

  * Does SolidWorks emit per-face styled items, or only per-solid?
  * Does swStepExportAppearances change anything?
  * **C4: do the two component-level overrides survive?**  This is the premise
    check. If they do, SolidWorks respects the appearance hierarchy and the
    project is unnecessary.
  * C7: what does swStepExportSplitPeriodic do to face counts?

Run:  python tools\s0_analyse.py
"""
from __future__ import annotations

import json
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
S0 = os.path.join(ROOT, "evidence", "S0")
sys.path.insert(0, HERE)

import stepdump  # noqa: E402

# Ground truth from corpus/CORPUS.md, as sRGB-ish triples. SolidWorks writes
# COLOUR_RGB in 0..1; these are approximate anchors for naming what we find.
NAMED = {
    "red": (0.8, 0.0, 0.0),
    "orange": (1.0, 0.5, 0.0),
    "yellow": (1.0, 1.0, 0.0),
    "green": (0.0, 0.8, 0.0),
    "cyan": (0.0, 1.0, 1.0),
}


def name_colour(c):
    best, dist = None, 1e9
    for label, ref in NAMED.items():
        d = sum((a - b) ** 2 for a, b in zip(c, ref))
        if d < dist:
            best, dist = label, d
    return best if dist < 0.25 else f"rgb{tuple(round(v, 2) for v in c)}"


def load():
    rows = []
    for fn in sorted(os.listdir(S0)):
        if not fn.endswith(".step") or "__" not in fn:
            continue
        model, variant = fn[:-5].split("__", 1)
        try:
            pres = stepdump.presentation(os.path.join(S0, fn))
        except Exception as exc:                               # noqa: BLE001
            rows.append({"model": model, "variant": variant, "error": str(exc)})
            continue
        rows.append({"model": model, "variant": variant, **pres})
    return rows


def main() -> int:
    rows = load()
    by_model = defaultdict(dict)
    for r in rows:
        by_model[r["model"]][r["variant"]] = r

    print("=== per-model, per-variant: what appearance is in the file? ===")
    hdr = f"{'model':<26}{'variant':<17}{'faces':>6}{'styled':>7}{'ovr':>5}{'ctx':>5}{'cols':>6}  colours"
    print(hdr)
    print("-" * len(hdr))
    for model in sorted(by_model):
        for variant in ("ap203", "ap214_noappear", "ap214_appear",
                        "ap214_appear_fe", "ap214_nosplit", "ap214_split",
                        "ap214_cfg"):
            r = by_model[model].get(variant)
            if not r or "error" in r:
                continue
            pc = r["presentation_counts"]
            cols = [name_colour(c) for c in r["distinct_colours"]]
            print(f"{model:<26}{variant:<17}{r['faces']:>6}"
                  f"{pc.get('STYLED_ITEM', 0):>7}"
                  f"{pc.get('OVER_RIDING_STYLED_ITEM', 0):>5}"
                  f"{pc.get('CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM', 0):>5}"
                  f"{len(cols):>6}  {','.join(cols)}")
        print()

    print("=== styled_item targets (ap214_appear) ===")
    for model in sorted(by_model):
        r = by_model[model].get("ap214_appear")
        if r and r.get("styled_item_targets"):
            print(f"  {model:<26} {r['styled_item_targets']}")

    print("\n=== PREMISE CHECK: C4 component overrides ===")
    print("  corpus ground truth: C4_part_1 is red at part level;")
    print("  _1 overrides its two instances to orange and yellow;")
    print("  _2 additionally has a green assembly-level override.\n")
    for model in ("C4_part_1", "C4_component_override_1", "C4_component_override_2"):
        r = by_model.get(model, {}).get("ap214_appear")
        if not r:
            print(f"  {model}: no ap214_appear export")
            continue
        cols = sorted({name_colour(c) for c in r["distinct_colours"]})
        pc = r["presentation_counts"]
        print(f"  {model:<26} colours={cols}  "
              f"styled={pc.get('STYLED_ITEM', 0)} "
              f"ovr={pc.get('OVER_RIDING_STYLED_ITEM', 0)} "
              f"ctx={pc.get('CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM', 0)} "
              f"occurrences={r['occurrences']}")

    c4 = by_model.get("C4_component_override_1", {}).get("ap214_appear")
    if c4:
        names = {name_colour(c) for c in c4["distinct_colours"]}
        got_both = "orange" in names and "yellow" in names
        print()
        if got_both:
            print("  => SolidWorks DOES export per-instance overrides."
                  "  PREMISE FAILS -- re-examine before proceeding.")
        else:
            print(f"  => per-instance overrides NOT both present (found {sorted(names)})."
                  "  Premise holds: SolidWorks flattens the appearance hierarchy.")

    print("\n=== C7: split-periodic effect on face count ===")
    for variant in ("ap214_nosplit", "ap214_split"):
        r = by_model.get("C7_periodic", {}).get(variant)
        if r:
            print(f"  {variant:<16} faces={r['faces']:<5} "
                  f"styled={r['presentation_counts'].get('STYLED_ITEM', 0)} "
                  f"ovr={r['presentation_counts'].get('OVER_RIDING_STYLED_ITEM', 0)}")

    print("\n=== does swStepExportAppearances change anything? ===")
    for model in sorted(by_model):
        off = by_model[model].get("ap214_noappear")
        on = by_model[model].get("ap214_appear")
        if not (off and on):
            continue
        d_styled = (on["presentation_counts"].get("STYLED_ITEM", 0)
                    - off["presentation_counts"].get("STYLED_ITEM", 0))
        d_cols = len(on["distinct_colours"]) - len(off["distinct_colours"])
        print(f"  {model:<26} styled {off['presentation_counts'].get('STYLED_ITEM', 0)}"
              f" -> {on['presentation_counts'].get('STYLED_ITEM', 0)} ({d_styled:+d})"
              f"   colours {len(off['distinct_colours'])} -> "
              f"{len(on['distinct_colours'])} ({d_cols:+d})")

    with open(os.path.join(S0, "s0-analysis.json"), "w", encoding="utf-8") as fh:
        json.dump(rows, fh, indent=2)
    print(f"\nwrote {os.path.join(S0, 's0-analysis.json')}")
    print("S0 ANALYSIS DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
