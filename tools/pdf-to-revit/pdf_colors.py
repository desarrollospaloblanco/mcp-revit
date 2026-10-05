"""Group every vector path of a PDF page by stroke/fill colour, width and dash, to learn its layers."""
import collections
import sys

import pymupdf

path = sys.argv[1]
page = pymupdf.open(path)[0]
print("page", page.rect, "rotation", page.rotation)

groups = collections.defaultdict(lambda: {"n": 0, "items": 0, "len": 0.0, "bbox": None})
for d in page.get_drawings():
    color = tuple(round(c, 2) for c in d["color"]) if d.get("color") else None
    fill = tuple(round(c, 2) for c in d["fill"]) if d.get("fill") else None
    key = (d["type"], color, fill, round(d.get("width") or 0, 2), d.get("dashes") or "")
    g = groups[key]
    g["n"] += 1
    g["items"] += len(d["items"])
    for item in d["items"]:
        if item[0] == "l":
            g["len"] += abs(item[1] - item[2])
    r = d["rect"]
    g["bbox"] = r if g["bbox"] is None else g["bbox"] | r

rows = sorted(groups.items(), key=lambda kv: -kv[1]["items"])
for key, g in rows[:45]:
    kind, color, fill, width, dashes = key
    b = g["bbox"]
    print(f"{kind:3} stroke={color} fill={fill} w={width} dash={dashes!r:18} paths={g['n']:5} segs={g['items']:6} "
          f"len={g['len']:8.0f} bbox=({b.x0:.0f},{b.y0:.0f})-({b.x1:.0f},{b.y1:.0f})")
