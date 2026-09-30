"""Finds the grid bubbles of a plan sheet and the grid line through each one.

    python pdf_grids.py plan.pdf out.json [--colour 1,0,0] [--width 1.44] [--page 0]

Grid names are usually drawn as text converted to strokes, so they cannot be read from the PDF's
text layer. This writes the bubble centres to out.json and an image next to it with every bubble
numbered in blue, so the names can be read off the drawing and paired with the dimensions to
fill in "grids" and "grid_lines" of the sheet configuration (see sheet.py).

A bubble whose line runs vertically on the sheet marks a sheet-x grid position; one whose line
runs horizontally marks a sheet-y position.
"""
import argparse
import json

import pymupdf

parser = argparse.ArgumentParser()
parser.add_argument("pdf")
parser.add_argument("out")
parser.add_argument("--colour", default="1,0,0", help="bubble stroke colour, r,g,b in 0..1")
parser.add_argument("--width", type=float, default=1.44, help="bubble stroke width in points")
parser.add_argument("--page", type=int, default=0)
args = parser.parse_args()

colour = tuple(round(float(c), 2) for c in args.colour.split(","))
page = pymupdf.open(args.pdf)[args.page]
drawings = page.get_drawings()

bubbles = []
for d in drawings:
    if d.get("color") and tuple(round(c, 2) for c in d["color"]) == colour and abs((d.get("width") or 0) - args.width) < 0.01:
        r = d["rect"]
        if 4 < r.width < 40 and abs(r.width - r.height) < 2:
            bubbles.append({"cx": round((r.x0 + r.x1) / 2, 3), "cy": round((r.y0 + r.y1) / 2, 3), "d": round(r.width, 3)})

# Thin strokes of the same colour are the grid lines (dash-dot lines come exploded into segments).
segments = []
for d in drawings:
    if d.get("color") and tuple(round(c, 2) for c in d["color"]) == colour and (d.get("width") or 0) < args.width:
        for item in d["items"]:
            if item[0] == "l":
                p, q = item[1], item[2]
                segments.append((p.x, p.y, q.x, q.y))


def line_through(b, vertical):
    n, lo, hi = 0, None, None
    for x0, y0, x1, y1 in segments:
        if vertical and abs(x0 - x1) < 0.05 and abs(x0 - b["cx"]) < 0.3:
            a, c = sorted((y0, y1))
        elif not vertical and abs(y0 - y1) < 0.05 and abs(y0 - b["cy"]) < 0.3:
            a, c = sorted((x0, x1))
        else:
            continue
        n += 1
        lo = a if lo is None else min(lo, a)
        hi = c if hi is None else max(hi, c)
    return n, lo, hi


result = []
for i, b in enumerate(sorted(bubbles, key=lambda b: (round(b["cy"]), b["cx"]))):
    nv, vlo, vhi = line_through(b, True)
    nh, hlo, hhi = line_through(b, False)
    runs = "vertical" if (vhi or 0) - (vlo or 0) >= (hhi or 0) - (hlo or 0) else "horizontal"
    result.append({"n": i, **b, "line": runs,
                   "sheet_coordinate": b["cx"] if runs == "vertical" else b["cy"],
                   "grid_line_axis": "sheet_x" if runs == "vertical" else "sheet_y"})

json.dump(result, open(args.out, "w"), indent=1)
for b in result:
    print(f"bubble {b['n']:2d} at ({b['cx']:.2f}, {b['cy']:.2f}): line runs {b['line']}, "
          f"{b['grid_line_axis']} = {b['sheet_coordinate']:.2f}")

for b in result:
    page.insert_text(pymupdf.Point(b["cx"] + 5, b["cy"] - 5), str(b["n"]), fontsize=6, color=(0, 0, 1))
png = args.out.rsplit(".", 1)[0] + ".png"
page.get_pixmap(dpi=200).save(png)
print("numbered sheet:", png)
