"""Generate the SolidWorks command icons for NEXT-STEP.

SolidWorks wants PNG icon *strips* at six sizes (20, 32, 40, 64, 96, 128).
A strip is one image holding every command in the group side by side, so a
one-command group is simply a square. The add-in points CommandGroup.IconList
and .MainIconList at these files by absolute path at runtime.

The artwork is generated rather than committed as opaque binaries so it can be
changed in one place and re-rendered at every size consistently. Run:

    python tools/make_icons.py

Everything is drawn at 8x and downsampled, because PIL's polygon fill has no
antialiasing of its own and a hard-edged isometric cube looks broken at 20px.
"""

from __future__ import annotations

import os
from PIL import Image, ImageDraw

SIZES = (20, 32, 40, 64, 96, 128)
SS = 8  # supersampling factor

# Three faces, three colours: the icon says what the add-in does, which is
# carry a distinct appearance onto each face and each occurrence rather than
# flattening everything to one colour.
TOP = (245, 189, 60, 255)
LEFT = (47, 128, 237, 255)
RIGHT = (39, 174, 96, 255)
EDGE = (26, 34, 46, 255)
ARROW = (26, 34, 46, 255)
HALO = (255, 255, 255, 255)


def _cube(draw: ImageDraw.ImageDraw, cx: float, cy: float, r: float) -> None:
    """Isometric cube centred on (cx, cy) with circumradius r."""
    dx = r * 0.866  # cos(30)
    dy = r * 0.5

    top = [(cx, cy - r), (cx + dx, cy - dy), (cx, cy), (cx - dx, cy - dy)]
    left = [(cx - dx, cy - dy), (cx, cy), (cx, cy + r), (cx - dx, cy + dy)]
    right = [(cx + dx, cy - dy), (cx, cy), (cx, cy + r), (cx + dx, cy + dy)]

    w = max(1, int(r * 0.075))
    for poly, fill in ((left, LEFT), (right, RIGHT), (top, TOP)):
        draw.polygon(poly, fill=fill, outline=EDGE, width=w)


def _arrow(draw: ImageDraw.ImageDraw, x: float, y: float, h: float) -> None:
    """Right-pointing export arrow with a white halo, so it stays legible
    wherever it overlaps the cube."""
    shaft_h = h * 0.38
    shaft_w = h * 0.50
    head_w = h * 0.60

    body = [
        (x, y - shaft_h / 2),
        (x + shaft_w, y - shaft_h / 2),
        (x + shaft_w, y - h / 2),
        (x + shaft_w + head_w, y),
        (x + shaft_w, y + h / 2),
        (x + shaft_w, y + shaft_h / 2),
        (x, y + shaft_h / 2),
    ]
    draw.polygon(body, fill=ARROW, outline=HALO, width=max(1, int(h * 0.11)))


def render(size: int) -> Image.Image:
    px = size * SS
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # The cube sits up and left so the arrow clears its lower-right edge
    # completely. They must not touch: the arrow's white halo cutting into a
    # cube face reads as damage rather than as separation, and at 20px the
    # notch is the first thing the eye finds. The numbers are proportions of
    # the canvas, so every size lands in the same place.
    _cube(draw, cx=px * 0.38, cy=px * 0.40, r=px * 0.35)
    _arrow(draw, x=px * 0.60, y=px * 0.82, h=px * 0.32)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(here, "src", "Peak.NextStep", "icons")
    os.makedirs(out, exist_ok=True)

    for size in SIZES:
        img = render(size)
        # One command in the group, so the strip is a single square. Both
        # lists get the same artwork: the tab icon and the button icon are
        # the same product mark.
        img.save(os.path.join(out, f"NextStep_{size}.png"))
        img.save(os.path.join(out, f"NextStepMain_{size}.png"))
        print(f"  {size:>3}px -> NextStep_{size}.png, NextStepMain_{size}.png")

    print(f"wrote {len(SIZES) * 2} files to {out}")


if __name__ == "__main__":
    main()
