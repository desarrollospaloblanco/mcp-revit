"""Columns on a plan: filled dark-red shapes, mapped to model coordinates and snapped to the grid."""
import collections
import json
import sys

import pymupdf

from sheet import SheetMap

path, cfg_path = sys.argv[1], sys.argv[2]
page = pymupdf.open(path)[0]
cfg = json.load(open(cfg_path))
m = SheetMap(cfg)
LETTER_X, NUMBER_Y = cfg["grids"]["x"], cfg["grids"]["y"]
print(m.describe())

fills = collections.defaultdict(list)
for d in page.get_drawings():
    if d.get("fill"):
        fills[tuple(round(c, 2) for c in d["fill"])].append(d["rect"])

for colour in [tuple(c) for c in cfg.get("column_fills", [[0.5, 0.0, 0.0]])]:
    rects = fills.get(colour, [])
    # Merge touching pieces of one hatch into one footprint.
    merged = []
    for r in sorted(rects, key=lambda r: (r.x0, r.y0)):
        for i, other in enumerate(merged):
            if other.intersects(pymupdf.Rect(r.x0 - 0.3, r.y0 - 0.3, r.x1 + 0.3, r.y1 + 0.3)):
                merged[i] = other | r
                break
        else:
            merged.append(pymupdf.Rect(r))
    print(f"\nfill {colour}: {len(rects)} pieces -> {len(merged)} footprints")
    for r in sorted(merged, key=lambda r: (round(r.y0), r.x0)):
        (x0, y0), (x1, y1) = m.to_model(r.x0, r.y0), m.to_model(r.x1, r.y1)
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        dx, dy = abs(x1 - x0), abs(y1 - y0)
        near_l = min(LETTER_X, key=lambda k: abs(LETTER_X[k] - cx))
        near_n = min(NUMBER_Y, key=lambda k: abs(NUMBER_Y[k] - cy))
        print(f"  centre ({cx:6.2f},{cy:6.2f}) size {dx:4.2f} x {dy:4.2f}  nearest {near_l}{near_n} "
              f"off ({cx - LETTER_X[near_l]:+.2f},{cy - NUMBER_Y[near_n]:+.2f})")
