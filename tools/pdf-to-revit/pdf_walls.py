"""Walls on a plan: pairs of parallel lines of a layer colour, reduced to centrelines with a thickness."""
import json
import sys

import pymupdf

from sheet import SheetMap


def segments_of(page, colours, sheet):
    """Orthogonal segments (in model metres) of every path stroked or filled with one of the colours."""
    segs = []
    for d in page.get_drawings():
        stroke = tuple(round(c, 2) for c in d["color"]) if d.get("color") else None
        fill = tuple(round(c, 2) for c in d["fill"]) if d.get("fill") else None
        if stroke not in colours and fill not in colours:
            continue
        for item in d["items"]:
            if item[0] == "l":
                pts = [item[1], item[2]]
            elif item[0] == "re":
                r = item[1]
                pts = [r.tl, r.tr, r.br, r.bl, r.tl]
            elif item[0] == "qu":
                q = item[1]
                pts = [q.ul, q.ur, q.lr, q.ll, q.ul]
            else:
                continue
            for p, q in zip(pts, pts[1:]):
                (x0, y0), (x1, y1) = sheet.to_model(p.x, p.y), sheet.to_model(q.x, q.y)
                if abs(y0 - y1) < 0.02 and abs(x1 - x0) >= 0.15:
                    segs.append(("h", (y0 + y1) / 2, min(x0, x1), max(x0, x1)))
                elif abs(x0 - x1) < 0.02 and abs(y1 - y0) >= 0.15:
                    segs.append(("v", (x0 + x1) / 2, min(y0, y1), max(y0, y1)))
    return segs


def heavy_strokes(page, colours, sheet, min_width_pt=0.7):
    """Walls drawn as a single heavy line: the line weight is the wall thickness."""
    out = []
    for d in page.get_drawings():
        stroke = tuple(round(c, 2) for c in d["color"]) if d.get("color") else None
        width = d.get("width") or 0
        if stroke not in colours or width < min_width_pt or d.get("fill"):
            continue
        t = width * sheet.length(1.0)
        for item in d["items"]:
            if item[0] != "l":
                continue
            (x0, y0), (x1, y1) = sheet.to_model(item[1].x, item[1].y), sheet.to_model(item[2].x, item[2].y)
            if abs(y0 - y1) < 0.02 and abs(x1 - x0) >= 0.3:
                out.append({"o": "h", "c": (y0 + y1) / 2, "t": t, "lo": min(x0, x1), "hi": max(x0, x1)})
            elif abs(x0 - x1) < 0.02 and abs(y1 - y0) >= 0.3:
                out.append({"o": "v", "c": (x0 + x1) / 2, "t": t, "lo": min(y0, y1), "hi": max(y0, y1)})
    return out


def pair_walls(segs, tmin=0.07, tmax=0.32, min_overlap=0.25):
    """Each segment paired with its nearest overlapping parallel partner; returns wall pieces."""
    pieces = []
    for orient in ("h", "v"):
        lines = sorted([s for s in segs if s[0] == orient], key=lambda s: s[1])
        for i, a in enumerate(lines):
            best = None
            for b in lines[i + 1:]:
                gap = b[1] - a[1]
                if gap > tmax:
                    break
                if gap < tmin:
                    continue
                lo, hi = max(a[2], b[2]), min(a[3], b[3])
                if hi - lo >= min_overlap and (best is None or gap < best[0]):
                    best = (gap, lo, hi, b)
            if best:
                gap, lo, hi, b = best
                pieces.append({"o": orient, "c": (a[1] + b[1]) / 2, "t": gap, "lo": lo, "hi": hi})
    return pieces


def merge(pieces, pos_tol=0.03, t_tol=0.03, gap_tol=0.08):
    """Joins collinear pieces of the same thickness that touch or overlap."""
    out = []
    for p in sorted(pieces, key=lambda p: (p["o"], round(p["c"], 2), p["lo"])):
        for w in out:
            if (w["o"] == p["o"] and abs(w["c"] - p["c"]) < pos_tol and abs(w["t"] - p["t"]) < t_tol
                    and p["lo"] <= w["hi"] + gap_tol and p["hi"] >= w["lo"] - gap_tol):
                w["lo"], w["hi"] = min(w["lo"], p["lo"]), max(w["hi"], p["hi"])
                break
        else:
            out.append(dict(p))
    return out


def snap_thickness(t):
    for std in (0.10, 0.12, 0.15, 0.20, 0.25, 0.30):
        if abs(t - std) <= 0.025:
            return std
    return round(t, 2)


if __name__ == "__main__":
    path, cfg_path, out_json, out_png = sys.argv[1:5]
    cfg = json.load(open(cfg_path))
    page = pymupdf.open(path)[0]
    sheet = SheetMap(cfg)

    layers = {name: [tuple(c) for c in cols] for name, cols in cfg["wall_colours"].items()}
    walls = []
    for name, colours in layers.items():
        segs = segments_of(page, colours, sheet)
        pieces = merge(pair_walls(segs, cfg.get("tmin", 0.07), cfg.get("tmax", 0.32)) + heavy_strokes(page, colours, sheet))
        pieces = [p for p in pieces if p["hi"] - p["lo"] >= cfg.get("min_len", 0.5)]
        for p in pieces:
            p["layer"] = name
            p["t"] = snap_thickness(p["t"])
        print(f"{name}: {len(segs)} segments -> {len(pieces)} walls, "
              f"{sum(p['hi'] - p['lo'] for p in pieces):.0f} m")
        walls += pieces
    json.dump(walls, open(out_json, "w"), indent=1)

    # Overlay: draw the extracted centrelines back on the sheet, in sheet coordinates.
    shape = page.new_shape()
    palette = {"exterior": (0, 0, 1), "interior": (1, 0, 1), "core": (1, 0.5, 0)}
    for w in walls:
        if w["o"] == "h":   # constant model Y -> constant sheet x
            p, q = pymupdf.Point(*sheet.to_sheet(w["lo"], w["c"])), pymupdf.Point(*sheet.to_sheet(w["hi"], w["c"]))
        else:
            p, q = pymupdf.Point(*sheet.to_sheet(w["c"], w["lo"])), pymupdf.Point(*sheet.to_sheet(w["c"], w["hi"]))
        shape.draw_line(p, q)
        shape.finish(color=palette.get(w["layer"], (0, 0, 0)), width=1.2)
    shape.commit()
    page.get_pixmap(dpi=int(cfg.get("dpi", 220)), clip=pymupdf.Rect(*cfg["clip"])).save(out_png)
    print("overlay", out_png)
