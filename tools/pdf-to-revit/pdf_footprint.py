"""Slab outline of a floor: the closed envelope of its facade walls, columns and other layers.

    python pdf_footprint.py plan.pdf sheet.json walls.json|- outline.json overlay.png

Shapes taken into the envelope:
  - exterior walls from walls.json (pass "-" to skip, for a sheet without extracted walls);
  - every filled shape in "column_fills" (columns and core);
  - the bounding box of every shape stroked or filled in "footprint_colours" (for example a
    basement's retaining-wall hatch and its parking stalls).

The shapes are closed across window gaps ("close", default 0.9 m), filled, then bridged across
corridors ("bridge", default 2.5 m) and filled again. The result is orthogonalised: edges within
6 cm of horizontal or vertical are made exact, because a nearly-orthogonal sketch line makes
Revit warn "slightly off axis" on every floor built from it.
"""
import json
import sys

import pymupdf
from shapely.geometry import Polygon, box
from shapely.ops import unary_union

from sheet import SheetMap

plan, cfg_path, walls_json, out_json, out_png = sys.argv[1:6]
cfg = json.load(open(cfg_path))
sheet = SheetMap(cfg)
COLUMN_FILLS = [tuple(c) for c in cfg.get("column_fills", [[0.5, 0.0, 0.0]])]
EXTRA = [tuple(c) for c in cfg.get("footprint_colours", [])]
page = pymupdf.open(plan)[0]
walls = [] if walls_json == "-" else json.load(open(walls_json))


def model_box(r):
    (x0, y0), (x1, y1) = sheet.to_model(r.x0, r.y0), sheet.to_model(r.x1, r.y1)
    return box(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1))


shapes = []
for w in walls:
    if w["layer"] != "exterior":
        continue
    h = w["t"] / 2
    if w["o"] == "h":
        shapes.append(box(w["lo"], w["c"] - h, w["hi"], w["c"] + h))
    else:
        shapes.append(box(w["c"] - h, w["lo"], w["c"] + h, w["hi"]))

for d in page.get_drawings():
    fill = tuple(round(c, 2) for c in d["fill"]) if d.get("fill") else None
    stroke = tuple(round(c, 2) for c in d["color"]) if d.get("color") else None
    if fill in COLUMN_FILLS or fill in EXTRA or stroke in EXTRA:
        shapes.append(model_box(d["rect"]))


def filled(geom):
    items = list(geom.geoms) if hasattr(geom, "geoms") else [geom]
    return unary_union([Polygon(g.exterior) for g in items])


def orthogonalise(ring, tol=0.06):
    """Makes nearly horizontal or vertical edges exact and drops the vertices that leaves collinear."""
    r = [list(p) for p in ring]
    n = len(r)
    for _ in range(3):
        for i in range(n):
            a, b = r[i], r[(i + 1) % n]
            if abs(a[0] - b[0]) < tol <= abs(a[1] - b[1]):
                a[0] = b[0] = round((a[0] + b[0]) / 2, 3)
            elif abs(a[1] - b[1]) < tol <= abs(a[0] - b[0]):
                a[1] = b[1] = round((a[1] + b[1]) / 2, 3)
    out = []
    for p in r:
        if not out or abs(p[0] - out[-1][0]) + abs(p[1] - out[-1][1]) > 1e-3:
            out.append(p)
    kept = []
    for i, p in enumerate(out):
        a, b = out[i - 1], out[(i + 1) % len(out)]
        if (abs(a[0] - p[0]) < 1e-6 and abs(p[0] - b[0]) < 1e-6) or (abs(a[1] - p[1]) < 1e-6 and abs(p[1] - b[1]) < 1e-6):
            continue
        kept.append(p)
    return kept


close = float(cfg.get("close", 0.9))
merged = filled(unary_union(shapes).buffer(close, join_style=2).buffer(-close, join_style=2))
bridge = float(cfg.get("bridge", 2.5))
if bridge > 0:
    merged = filled(merged.buffer(bridge, join_style=2).buffer(-bridge, join_style=2))
parts = list(merged.geoms) if hasattr(merged, "geoms") else [merged]
parts.sort(key=lambda p: -p.area)
ring = orthogonalise(list(Polygon(parts[0].exterior).simplify(0.05).exterior.coords)[:-1])
# Start at the lowest-left vertex, so ids numbered along the outline (retaining walls, for one)
# come out the same on every run and every machine.
start = min(range(len(ring)), key=lambda i: (round(ring[i][0], 2), round(ring[i][1], 2)))
ring = ring[start:] + ring[:start]
outline = Polygon(ring)
print(f"pieces {len(parts)}, outline area {outline.area:.1f} m2, {len(ring)} vertices")
for p in parts[1:6]:
    print("  other piece area", round(p.area, 2), [round(v, 2) for v in p.bounds])

json.dump({"outline": [[round(x, 3), round(y, 3)] for x, y in ring]}, open(out_json, "w"), indent=1)

shape = page.new_shape()
shape.draw_polyline([pymupdf.Point(*sheet.to_sheet(x, y)) for x, y in ring + ring[:1]])
shape.finish(color=(0, 0, 1), width=1.5)
shape.commit()
clip = pymupdf.Rect(*cfg["clip"]) if "clip" in cfg else page.rect
page.get_pixmap(dpi=150, clip=clip).save(out_png)
print("overlay", out_png)
