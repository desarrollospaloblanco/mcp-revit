"""Cleans the raw wall pieces so they read as a wall network rather than loose strokes.

1. Duplicates: two pieces on overlapping bands of the same line (a facade drawn twice, a window
   frame over the facade) keep only the more important one.
2. Collinear gaps shorter than 0.25 m between pieces of the same thickness are closed.
3. Junctions: a wall end within reach of a perpendicular wall is extended or trimmed to that
   wall's centreline, so corners and T-junctions meet instead of stopping short.
"""
import json
import sys

walls = json.load(open(sys.argv[1]))
out_path = sys.argv[2]
PRIORITY = {"exterior": 3, "nucleo": 2, "interior": 1}
REACH = 0.35

for i, w in enumerate(walls):
    w.setdefault("index", i)


def overlap(a, b):
    return min(a["hi"], b["hi"]) - max(a["lo"], b["lo"])


# 1. duplicates
walls.sort(key=lambda w: (-PRIORITY.get(w["layer"], 0), -w["t"], -(w["hi"] - w["lo"])))
kept = []
for w in walls:
    dup = False
    for k in kept:
        if k["o"] != w["o"]:
            continue
        if abs(k["c"] - w["c"]) < (k["t"] + w["t"]) / 2 - 0.01:
            shorter = min(k["hi"] - k["lo"], w["hi"] - w["lo"])
            if overlap(k, w) > 0.6 * shorter:
                dup = True
                if w["lo"] < k["lo"] - 0.05 or w["hi"] > k["hi"] + 0.05:
                    # the dropped piece reaches further: the survivor takes over its extent
                    if abs(k["c"] - w["c"]) < 0.03:
                        k["lo"], k["hi"] = min(k["lo"], w["lo"]), max(k["hi"], w["hi"])
                break
    if not dup:
        kept.append(w)
removed_dups = len(walls) - len(kept)

# 2. collinear gaps: group pieces by line first, then close gaps along each line. Sorting on a
#    rounded coordinate instead interleaves two nearly-aligned lines and lets a piece at one end of
#    the building swallow pieces at the other.
lines = []
for w in sorted(kept, key=lambda w: (w["o"], w["c"])):
    if lines and lines[-1][0]["o"] == w["o"] and abs(lines[-1][-1]["c"] - w["c"]) < 0.03:
        lines[-1].append(w)
    else:
        lines.append([w])
merged = []
for line in lines:
    open_pieces = []
    for w in sorted(line, key=lambda w: w["lo"]):
        target = next((m for m in open_pieces if abs(m["t"] - w["t"]) < 0.03 and w["lo"] - m["hi"] < 0.25), None)
        if target:
            target["hi"] = max(target["hi"], w["hi"])
        else:
            open_pieces.append(w)
    merged += open_pieces
closed_gaps = len(kept) - len(merged)

# 3. junctions
snapped = 0
for w in merged:
    for end in ("lo", "hi"):
        pos = w[end]
        best = None
        for p in merged:
            if p is w or p["o"] == w["o"]:
                continue
            # p is perpendicular: its centreline is at along-coordinate p["c"]; w's line must fall in p's span
            if not (p["lo"] - 0.1 <= w["c"] <= p["hi"] + 0.1):
                continue
            d = abs(p["c"] - pos)
            if d <= max(REACH, p["t"] / 2 + 0.2) and (best is None or d < best[0]):
                best = (d, p)
        if best is None or best[0] < 0.005:
            continue
        new = best[1]["c"]
        if (end == "lo" and new < w["hi"] - 0.2) or (end == "hi" and new > w["lo"] + 0.2):
            w[end] = new
            snapped += 1

result = [w for w in merged if w["hi"] - w["lo"] >= 0.3]
json.dump(result, open(out_path, "w"), indent=1)
print(f"{len(walls)} pieces: {removed_dups} duplicates removed, {closed_gaps} gaps closed, "
      f"{snapped} ends snapped to a junction -> {len(result)} walls")
