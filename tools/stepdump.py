"""ISO-10303-21 (STEP Part 21) inspector.

Used by S0 (what SolidWorks emits) and S1 (what OCCT emits). Deliberately a
plain text parser with no OCCT dependency: the question both spikes ask is
"what is literally in the file", and reading it through a toolkit that
normalises entities would beg that question.

CLI:
    python stepdump.py <file.step> [--json out.json] [--quiet]

API:
    parse(path)        -> Step (entities, header)
    histogram(path)    -> {ENTITY_NAME: count}
    presentation(path) -> presentation-graph summary dict
"""
from __future__ import annotations

import json
import os
import re
import sys
from collections import Counter, defaultdict

# Entities that carry appearance. Counted and reported separately because the
# whole study is about whether these appear and what they point at.
PRESENTATION = (
    "STYLED_ITEM",
    "OVER_RIDING_STYLED_ITEM",
    "CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM",
    "PRESENTATION_STYLE_ASSIGNMENT",
    "PRESENTATION_STYLE_BY_CONTEXT",
    "SURFACE_STYLE_USAGE",
    "SURFACE_SIDE_STYLE",
    "SURFACE_STYLE_FILL_AREA",
    "SURFACE_STYLE_RENDERING",
    "SURFACE_STYLE_RENDERING_WITH_PROPERTIES",
    "SURFACE_STYLE_REFLECTANCE_AMBIENT",
    "SURFACE_STYLE_TRANSPARENT",
    "FILL_AREA_STYLE",
    "FILL_AREA_STYLE_COLOUR",
    "COLOUR_RGB",
    "DRAUGHTING_PRE_DEFINED_COLOUR",
    "CURVE_STYLE",
    "MECHANICAL_DESIGN_GEOMETRIC_PRESENTATION_REPRESENTATION",
    "DRAUGHTING_MODEL",
    "INVISIBILITY",
)

# What a styled_item may target, in the order we care about when classifying.
STYLE_TARGETS = (
    "ADVANCED_FACE",
    "FACE_SURFACE",
    "MANIFOLD_SOLID_BREP",
    "SHELL_BASED_SURFACE_MODEL",
    "BREP_WITH_VOIDS",
    "NEXT_ASSEMBLY_USAGE_OCCURRENCE",
    "PRODUCT_DEFINITION",
    "SHAPE_REPRESENTATION",
    "ADVANCED_BREP_SHAPE_REPRESENTATION",
    "MANIFOLD_SURFACE_SHAPE_REPRESENTATION",
)

_ENTITY_RE = re.compile(r"^#(\d+)\s*=\s*([A-Z0-9_]+)?\s*\(", re.IGNORECASE)
_REF_RE = re.compile(r"#(\d+)")


class Step:
    """A parsed Part 21 file: id -> (type, argument text)."""

    def __init__(self, path: str):
        self.path = path
        self.header: list[str] = []
        self.entities: dict[int, tuple[str, str]] = {}
        self.schema: str = ""
        self._parse()

    # -- parsing ---------------------------------------------------------
    def _parse(self) -> None:
        with open(self.path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()

        head_start = text.find("HEADER;")
        data_start = text.find("DATA;")
        if data_start < 0:
            raise ValueError(f"{self.path}: no DATA section")

        if head_start >= 0:
            head = text[head_start:data_start]
            self.header = [s.strip() for s in _split_statements(head) if s.strip()]
            for stmt in self.header:
                if stmt.upper().startswith("FILE_SCHEMA"):
                    m = re.search(r"'([^']+)'", stmt)
                    if m:
                        self.schema = m.group(1)

        body = text[data_start + len("DATA;"):]
        end = body.rfind("ENDSEC;")
        if end >= 0:
            body = body[:end]

        for stmt in _split_statements(body):
            stmt = stmt.strip()
            if not stmt.startswith("#"):
                continue
            m = _ENTITY_RE.match(stmt)
            if not m:
                continue
            eid = int(m.group(1))
            name = (m.group(2) or "").upper()
            args = stmt[m.end() - 1:]
            if not name:
                # Complex instance:  #12=(A(..)B(..));  record the first type.
                inner = re.search(r"\(\s*([A-Z0-9_]+)\s*\(", stmt)
                name = "COMPLEX:" + (inner.group(1).upper() if inner else "?")
            self.entities[eid] = (name, args)

    # -- queries ---------------------------------------------------------
    def histogram(self) -> dict[str, int]:
        return dict(Counter(t for t, _ in self.entities.values()))

    def refs(self, eid: int) -> list[int]:
        _t, args = self.entities[eid]
        return [int(x) for x in _REF_RE.findall(args)]

    def type_of(self, eid: int) -> str:
        e = self.entities.get(eid)
        return e[0] if e else "<dangling>"

    def by_type(self, name: str) -> list[int]:
        name = name.upper()
        return sorted(k for k, (t, _) in self.entities.items() if t == name)

    def presentation(self) -> dict:
        """What appearance is in this file, and what does it attach to?"""
        hist = self.histogram()
        counts = {n: hist.get(n, 0) for n in PRESENTATION if hist.get(n)}

        # For every styled_item flavour, classify the target entity type.
        targets: dict[str, Counter] = defaultdict(Counter)
        for flavour in ("STYLED_ITEM", "OVER_RIDING_STYLED_ITEM",
                        "CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM"):
            for eid in self.by_type(flavour):
                for ref in self.refs(eid):
                    t = self.type_of(ref)
                    if t in STYLE_TARGETS:
                        targets[flavour][t] += 1

        colours = []
        for eid in self.by_type("COLOUR_RGB"):
            nums = re.findall(r"-?\d+\.?\d*(?:[eE][-+]?\d+)?",
                              self.entities[eid][1].split("'", 2)[-1])
            if len(nums) >= 3:
                colours.append(tuple(round(float(v), 4) for v in nums[:3]))

        return {
            "file": os.path.basename(self.path),
            "schema": self.schema,
            "total_entities": len(self.entities),
            "presentation_counts": counts,
            "styled_item_targets": {k: dict(v) for k, v in targets.items()},
            "distinct_colours": sorted(set(colours)),
            "faces": hist.get("ADVANCED_FACE", 0),
            "solids": hist.get("MANIFOLD_SOLID_BREP", 0),
            "occurrences": hist.get("NEXT_ASSEMBLY_USAGE_OCCURRENCE", 0),
        }


def _split_statements(text: str) -> list[str]:
    """Split on ';' while respecting Part 21 single-quoted strings ('' escape)."""
    out, buf, in_str, i = [], [], False, 0
    n = len(text)
    while i < n:
        ch = text[i]
        if in_str:
            if ch == "'":
                if i + 1 < n and text[i + 1] == "'":
                    buf.append("''")
                    i += 2
                    continue
                in_str = False
            buf.append(ch)
        elif ch == "'":
            in_str = True
            buf.append(ch)
        elif ch == ";":
            out.append("".join(buf))
            buf = []
        else:
            buf.append(ch)
        i += 1
    if buf:
        out.append("".join(buf))
    return out


def parse(path: str) -> Step:
    return Step(path)


def histogram(path: str) -> dict[str, int]:
    return Step(path).histogram()


def presentation(path: str) -> dict:
    return Step(path).presentation()


def main(argv: list[str]) -> int:
    args = [a for a in argv if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    quiet = "--quiet" in argv
    out_json = None
    if "--json" in argv:
        out_json = argv[argv.index("--json") + 1]

    reports = []
    for path in args:
        rep = Step(path).presentation()
        reports.append(rep)
        if not quiet:
            print(json.dumps(rep, indent=2))

    if out_json:
        with open(out_json, "w", encoding="utf-8") as fh:
            json.dump(reports if len(reports) > 1 else reports[0], fh, indent=2)
        print(f"wrote {out_json}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
