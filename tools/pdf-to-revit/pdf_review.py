"""Draws extracted walls as outlined rectangles of their real thickness over the PDF, for review by zone.

    python pdf_review.py plano.pdf hoja.json muros.json out/revision

Blue = exterior layer, magenta = the rest. Look at every zone at a resolution where a 10 cm wall
is several pixels wide before building anything in Revit.
"""
import json
import sys

import pymupdf

from sheet import SheetMap

plan, cfg_path, walls_json, out_prefix = sys.argv[1:5]
cfg = json.load(open(cfg_path))
sheet = SheetMap(cfg)
walls = json.load(open(walls_json))


def sheet_point(X, Y):
    return pymupdf.Point(*sheet.to_sheet(X, Y))


# Review zones in model metres, per sheet: "review_zones": {"name": [X0, Y0, X1, Y1], ...}.
# Without them the whole sheet is rendered once.
zones = cfg.get("review_zones") or {"hoja": None}
for name, box in zones.items():
    doc = pymupdf.open(plan)
    page = doc[0]
    shape = page.new_shape()
    for w in walls:
        h = w["t"] / 2
        if w["o"] == "h":
            a, b = sheet_point(w["lo"], w["c"] - h), sheet_point(w["hi"], w["c"] + h)
        else:
            a, b = sheet_point(w["c"] - h, w["lo"]), sheet_point(w["c"] + h, w["hi"])
        shape.draw_rect(pymupdf.Rect(a, b).normalize())
        shape.finish(color=(0, 0, 1) if w["layer"] == "exterior" else (1, 0, 1), width=0.35, fill=None)
    shape.commit()
    clip = page.rect if box is None else pymupdf.Rect(sheet_point(box[0], box[1]), sheet_point(box[2], box[3])).normalize()
    page.get_pixmap(dpi=int(cfg.get("review_dpi", 300)), clip=clip).save(f"{out_prefix}_{name}.png")
    print(name, clip)
