"""Doors and windows on the typical plan, and the walls they need to be hosted in.

Windows: the facade alternates wall pieces with glazed spans (thin light-blue lines). Each glazed
span on an exterior wall line is a window as wide as the span; the wall pieces either side are
merged through it so a single wall can host it.

Doors: each door is drawn open, as an orange leaf perpendicular to the wall, hinged on the wall
line at one jamb. The opening runs from the hinge along the wall, towards the side where the wall
does not continue, for the length of the leaf. The wall piece ending at the hinge is extended
across the opening (merged with the piece beyond it, if there is one).

Every opening records the wall it belongs to, so the builder can host it by spec id.
"""
import json
import sys
from collections import defaultdict

import pymupdf

from sheet import SheetMap

plan, cfg_path, walls_in, walls_out, openings_out, overlay_png = sys.argv[1:7]
cfg = json.load(open(cfg_path))
sheet = SheetMap(cfg)
page = pymupdf.open(plan)[0]
walls = [dict(w, index=i) for i, w in enumerate(json.load(open(walls_in)))]
by_index = {w["index"]: w for w in walls}

# Colours of the drawing's door, glass, balcony and sliding-door layers; override per project in
# the sheet configuration under "opening_colours".
_oc = {"door": [0.87, 0.43, 0.0], "glass": [0.5, 0.75, 1.0], "balcony": [1.0, 1.0, 0.0],
       "sliding": [1.0, 0.75, 0.5], "red_door": [1.0, 0.0, 0.0]}
_oc.update(cfg.get("opening_colours", {}))
ORANGE, GLASS, YELLOW, SLIDING, RED = (tuple(_oc[k]) for k in ("door", "glass", "balcony", "sliding", "red_door"))


def model_rect(r):
    (x0, y0), (x1, y1) = sheet.to_model(r.x0, r.y0), sheet.to_model(r.x1, r.y1)
    return min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)


leaves, glass, balcony, arc_points = [], [], [], []
for d in page.get_drawings():
    stroke = tuple(round(c, 2) for c in (d.get("color") or ()))
    fill = tuple(round(c, 2) for c in (d.get("fill") or ()))
    x0, y0, x1, y1 = model_rect(d["rect"])
    w, h = x1 - x0, y1 - y0
    kinds = [i[0] for i in d["items"]]
    if stroke == RED and 0.3 <= (d.get("width") or 0) <= 0.8 and 0.55 <= max(w, h) <= 1.05:
        # The core apartments' doors are drawn in red: the frame as one or two thin lines on the
        # wall line, the leaf as a rotated quadrilateral opened at an angle.
        if set(kinds) == {"l"} and len(kinds) <= 3 and min(w, h) <= 0.03:
            leaves.append((x0, y0, x1, y1, True))
        elif kinds == ["qu"] and min(w, h) <= 0.35:
            leaves.append((x0, y0, x1, y1, False))
    elif stroke == ORANGE and 0.55 <= max(w, h) <= 1.05 and min(w, h) <= 0.2:
        # The frame is a thin rectangle (about 3 cm); the leaf is thicker, or a heavy line.
        is_frame = [i[0] for i in d["items"]] == ["qu"] and min(w, h) <= 0.06
        leaves.append((x0, y0, x1, y1, is_frame))
    elif stroke in (GLASS, SLIDING) and max(w, h) >= 0.15 and min(w, h) <= 0.12:
        glass.append(("h" if w >= h else "v", (y0 + y1) / 2 if w >= h else (x0 + x1) / 2,
                      x0 if w >= h else y0, x1 if w >= h else y1, stroke == SLIDING))
    elif (stroke == YELLOW or fill == YELLOW) and max(w, h) >= 1.0:
        balcony.append((x0, y0, x1, y1))
    if stroke == RED and round(d.get("width") or 0, 2) in (0.38, 0.48) and max(w, h) <= 1.1:
        for item in d["items"]:
            if item[0] == "l":
                arc_points.append(sheet.to_model(item[1].x, item[1].y))


def pieces_on(orient, c, tol, layers=None):
    return [w for w in walls if w["o"] == orient and abs(w["c"] - c) <= tol and "gone" not in w
            and (layers is None or w["layer"] in layers)]


def root(w):
    """The piece that finally absorbed w (w itself when it survives)."""
    while "gone" in w:
        w = by_index[w["gone"]]
    return w


def absorb(host, lo, hi):
    """Extends host over [lo, hi] and swallows collinear pieces it now overlaps."""
    host = root(host)
    host["lo"], host["hi"] = min(host["lo"], lo), max(host["hi"], hi)
    for other in pieces_on(host["o"], host["c"], 0.04):
        if other is host or abs(other["t"] - host["t"]) > 0.03:
            continue
        if other["lo"] <= host["hi"] + 0.05 and other["hi"] >= host["lo"] - 0.05:
            host["lo"], host["hi"] = min(host["lo"], other["lo"]), max(host["hi"], other["hi"])
            if other is not host:
                other["gone"] = host["index"]


openings = []

# ---------------------------------------------------------------- windows
spans = defaultdict(list)
for orient, c, lo, hi, sliding in glass:
    spans[(orient, round(c / 0.1), sliding)].append([lo, hi, c])
for (orient, _, sliding), items in spans.items():
    items.sort()
    merged = []
    for lo, hi, c in items:
        if merged and lo <= merged[-1][1] + 0.25:
            merged[-1][1] = max(merged[-1][1], hi)
        else:
            merged.append([lo, hi, c])
    for lo, hi, c in merged:
        width = hi - lo
        if width < 0.4 or (sliding and width < 1.2):
            continue
        mid = (lo + hi) / 2
        px, py = (mid, c) if orient == "h" else (c, mid)
        at_balcony = any(x0 - 1.5 <= px <= x1 + 1.5 and y0 - 1.5 <= py <= y1 + 1.5 for x0, y0, x1, y1 in balcony)
        if sliding and not at_balcony:
            continue
        hosts = pieces_on(orient, c, 0.2, {"exterior"})
        near = [w for w in hosts if abs(w["hi"] - lo) <= 0.4 or abs(w["lo"] - hi) <= 0.4
                or (w["lo"] <= lo and w["hi"] >= hi)]
        if near:
            host = root(min(near, key=lambda w: abs(w["c"] - c)))
        elif sliding:
            # The sliding door to a balcony is set back from the facade: it gets its own wall piece.
            host = {"o": orient, "c": round(c, 3), "t": 0.12, "lo": lo, "hi": hi, "layer": "exterior",
                    "index": len(walls)}
            walls.append(host)
            by_index[host["index"]] = host
        else:
            continue
        absorb(host, lo, hi)
        kind = "balcony_door" if sliding or (width >= 1.4 and at_balcony) else "window"
        openings.append({"kind": kind, "host": host["index"], "o": orient, "c": host["c"], "at": round(mid, 3),
                         "width": round(width, 3)})

# ---------------------------------------------------------------- doors
# Each door is drawn with two orange pieces: the frame, lying on the wall line across the opening,
# and the leaf, perpendicular to it. The frame gives the opening exactly; the leaf only which side
# the door swings to. A leaf without a frame falls back to the hinge rule below.
frames, swing = [], []
for x0, y0, x1, y1, is_frame in leaves:
    horizontal = (x1 - x0) >= (y1 - y0)
    orient = "h" if horizontal else "v"
    across = (y0 + y1) / 2 if horizontal else (x0 + x1) / 2
    lo, hi = (x0, x1) if horizontal else (y0, y1)
    on_wall = [w for w in pieces_on(orient, across, 0.2)
               if abs(w["hi"] - lo) <= 0.4 or abs(w["lo"] - hi) <= 0.4 or (w["lo"] <= lo and w["hi"] >= hi)]
    (frames if on_wall and is_frame else swing).append((x0, y0, x1, y1, orient, across, lo, hi, on_wall))

seen, used_leaves = [], set()
for x0, y0, x1, y1, orient, across, lo, hi, on_wall in frames:
    host = root(min(on_wall, key=lambda w: abs(w["c"] - across)))
    mid = (lo + hi) / 2
    key = (orient, round(host["c"], 1), round(mid, 1))
    if key in seen:
        continue
    seen.append(key)
    facing, hinge = None, None
    for n, (sx0, sy0, sx1, sy1, *_) in enumerate(swing):
        # the leaf stands at one end of the frame, perpendicular to it
        pos = (sx0 + sx1) / 2 if orient == "h" else (sy0 + sy1) / 2
        span = (sy0, sy1) if orient == "h" else (sx0, sx1)
        if min(abs(pos - lo), abs(pos - hi)) <= 0.25 and min(abs(span[0] - host["c"]), abs(span[1] - host["c"])) <= 0.25:
            far = span[1] if abs(span[0] - host["c"]) < abs(span[1] - host["c"]) else span[0]
            facing = 1 if far > host["c"] else -1
            hinge = "lo" if abs(pos - lo) < abs(pos - hi) else "hi"
            used_leaves.add(n)
            break
    absorb(host, lo, hi)
    openings.append({"kind": "door", "host": host["index"], "o": orient, "c": host["c"], "at": round(mid, 3),
                     "width": round(hi - lo, 3), "facing": facing, "hinge": hinge, "from": "frame"})

def door_jambs():
    for o in openings:
        if o["kind"] != "door":
            continue
        for end in (o["at"] - o["width"] / 2, o["at"] + o["width"] / 2):
            yield (end, o["c"]) if o["o"] == "h" else (o["c"], end)


for n, (x0, y0, x1, y1, *_) in enumerate(swing):
    if n in used_leaves:
        continue
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    # every drawn piece of a leaf that already has its door sits within half a leaf of a jamb
    if any(abs(cx - jx) <= 0.6 and abs(cy - jy) <= 0.6 for jx, jy in door_jambs()):
        continue
    vertical_leaf = (y1 - y0) > (x1 - x0)
    wall_orient = "h" if vertical_leaf else "v"
    length = max(x1 - x0, y1 - y0)
    along = (x0 + x1) / 2 if vertical_leaf else (y0 + y1) / 2          # leaf position along the wall
    ends = (y0, y1) if vertical_leaf else (x0, x1)                      # leaf ends across the wall
    best = None
    for end in ends:
        for w in pieces_on(wall_orient, end, 0.22):
            d_end = min(abs(w["hi"] - along), abs(w["lo"] - along))
            if w["lo"] - 0.3 <= along <= w["hi"] + 0.3:
                d_end = min(d_end, 0.3)
            if d_end <= 0.3 and (best is None or abs(w["c"] - end) + d_end < best[0]):
                best = (abs(w["c"] - end) + d_end, w, end)
    if best is None:
        # A leaf drawn along the wall line, continuing a wall piece: the leaf itself spans the opening.
        orient = "v" if vertical_leaf else "h"
        across = (x0 + x1) / 2 if vertical_leaf else (y0 + y1) / 2
        lo, hi = ends
        on_line = [w for w in pieces_on(orient, across, 0.1) if abs(w["hi"] - lo) <= 0.3 or abs(w["lo"] - hi) <= 0.3]
        if not on_line:
            continue
        host = root(on_line[0])
        mid = (lo + hi) / 2
        absorb(host, lo, hi)
        openings.append({"kind": "door", "host": host["index"], "o": orient, "c": host["c"], "at": round(mid, 3),
                         "width": round(hi - lo, 3), "facing": 1, "hinge": "lo", "from": "leaf-along"})
        continue
    _, host, hinge_end = best
    host = root(host)
    far_end = ends[1] if hinge_end == ends[0] else ends[0]
    if host["lo"] + 0.3 < along < host["hi"] - 0.3:
        # Hinge in the middle of a continuous piece: the swing arc says which way the opening runs.
        hx, hy = (along, host["c"]) if wall_orient == "h" else (host["c"], along)
        near = [(px, py) for px, py in arc_points if abs(px - hx) <= length + 0.1 and abs(py - hy) <= length + 0.1]
        if not near:
            continue
        side = sum((px - hx) if wall_orient == "h" else (py - hy) for px, py in near)
        lo, hi, hinge = (along, along + length, "lo") if side > 0 else (along - length, along, "hi")
    elif abs(host["hi"] - along) <= abs(host["lo"] - along):
        lo, hi, hinge = host["hi"], host["hi"] + length, "lo"   # opening continues past the piece's end
    else:
        lo, hi, hinge = host["lo"] - length, host["lo"], "hi"
    mid = (lo + hi) / 2
    key = (wall_orient, round(host["c"], 1), round(mid, 1))
    # a leaf already paired with a frame is skipped: its door is in the list
    if key in seen or any(o["kind"] == "door" and o["o"] == wall_orient and abs(o["c"] - host["c"]) < 0.25
                          and abs(o["at"] - mid) < 0.6 for o in openings):
        continue
    seen.append(key)
    absorb(host, lo, hi)
    openings.append({"kind": "door", "host": host["index"], "o": wall_orient, "c": host["c"], "at": round(mid, 3),
                     "width": round(length, 3), "facing": 1 if far_end > host["c"] else -1, "hinge": hinge,
                     "from": "leaf"})

# Every door in this drawing carries a heavy orange leaf line; a door without one nearby is a false
# match on some other orange linework, and two doors overlapping on one line are the same door.
heavy = []
red_leaf_centres = []
for d in page.get_drawings():
    if tuple(round(c, 2) for c in (d.get("color") or ())) == RED and [i[0] for i in d["items"]] == ["qu"]:
        x0, y0, x1, y1 = model_rect(d["rect"])
        if 0.55 <= max(x1 - x0, y1 - y0) <= 1.05 and min(x1 - x0, y1 - y0) <= 0.35:
            red_leaf_centres.append(((x0 + x1) / 2, (y0 + y1) / 2))
for d in page.get_drawings():
    if tuple(round(c, 2) for c in (d.get("color") or ())) == ORANGE and round(d.get("width") or 0, 2) == 2.28:
        x0, y0, x1, y1 = model_rect(d["rect"])
        if 0.55 <= max(x1 - x0, y1 - y0) <= 1.05:
            heavy.append(((x0 + x1) / 2, (y0 + y1) / 2))


def jamb_points(o):
    return [((e, o["c"]) if o["o"] == "h" else (o["c"], e)) for e in (o["at"] - o["width"] / 2, o["at"] + o["width"] / 2)]


filtered = []
for o in openings:
    if o["kind"] == "door":
        if not any(abs(hx - x) <= 0.75 and abs(hy - y) <= 0.75 for x, y in jamb_points(o) for hx, hy in heavy + red_leaf_centres):
            continue
        if any(f["kind"] == "door" and f["o"] == o["o"] and abs(f["c"] - o["c"]) < 0.2
               and abs(f["at"] - o["at"]) < (f["width"] + o["width"]) / 2 - 0.05 for f in filtered):
            continue
    filtered.append(o)
openings = filtered

# Openings hosted on a piece that was later swallowed move to the piece that swallowed it.
for o in openings:
    o["host"] = root(by_index[o["host"]])["index"]

kept = [w for w in walls if "gone" not in w]
json.dump(kept, open(walls_out, "w"), indent=1)
json.dump(openings, open(openings_out, "w"), indent=1)

counts = defaultdict(int)
for o in openings:
    counts[o["kind"]] += 1
print(f"leaves {len(leaves)}  glass pieces {len(glass)}  balcony shapes {len(balcony)}")
print(f"walls {len(walls)} -> {len(kept)} after bridging; openings {dict(counts)}")

inv = lambda X, Y: pymupdf.Point(*sheet.to_sheet(X, Y))
shape = page.new_shape()
colours = {"door": (1, 0, 1), "window": (0, 0, 1), "balcony_door": (0, 0.6, 0)}
for o in openings:
    half = o["width"] / 2
    if o["o"] == "h":
        p, q = inv(o["at"] - half, o["c"]), inv(o["at"] + half, o["c"])
    else:
        p, q = inv(o["c"], o["at"] - half), inv(o["c"], o["at"] + half)
    shape.draw_line(p, q)
    shape.finish(color=colours[o["kind"]], width=2.2)
shape.commit()
page.get_pixmap(dpi=int(cfg.get("dpi", 220)), clip=pymupdf.Rect(*cfg["clip"])).save(overlay_png)
page.get_pixmap(dpi=520, clip=pymupdf.Rect(205, 255, 300, 345)).save(overlay_png.replace(".png", "_zoom.png"))
print("overlay", overlay_png)
