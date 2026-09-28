"""Builds the tower spec (metres) from what was extracted from the three PDFs.

Version 2: the parking levels are a continuous helix of split-level plates and ramps, the level at
+0.000 is gone (it has no name in the drawings), and the typical floor carries its doors, windows
and balcony doors, hosted in walls bridged across their openings.

Everything measured comes from the drawings; everything decided is listed in DECISIONS and written
into the spec file next to the data, so the review can tell which is which.
"""
import json
import sys

from shapely.geometry import LineString, Point, Polygon, box
from shapely.geometry.polygon import orient

# The building's grids, in model metres, are shared by every sheet configuration.
_grids = json.load(open("tipico.json", encoding="utf-8"))["grids"]
LETTER_X, NUMBER_Y = _grids["x"], _grids["y"]

OUT = sys.argv[1]
TOWER_WALL_LEVELS = int(sys.argv[2]) if len(sys.argv) > 2 else 22

DECISIONS = [
    "Elevaciones por la geometría del alzado (coinciden con cotas 104.01 / 83.71 / 20.30 y NPT del podio); "
    "los rótulos NLT de la torre difieren +4.98 m.",
    "Sin nivel +0.000: el plano no le da nombre (pedido del usuario).",
    "AZOTEA = N25 + 3.20.",
    "Estacionamiento en hélice de N03 a SÓTANO 4: mitad B–D en el nivel, mitad G–I 1.50 abajo (NPT 0.00 / −1.50 del "
    "plano), rampa sur B–D→G–I y rampa norte G–I→nivel inferior. En N02 (4.32 m) cada rampa baja la mitad.",
    "N01→SÓTANO 1 (10.39 m) en 3 vueltas de 3.46 m con medias plantas intermedias como losas desfasadas sobre "
    "SÓTANO 1, sin crear niveles.",
    "Sin acceso vehicular modelado de la calle (N04) a N03: N04 es la losa de arranque de la torre.",
    "Vigas del estacionamiento siguiendo las losas: planas en las mitades, inclinadas en las rampas.",
    "Columnas 1.20x1.70 de SÓTANO 4 a N03; en la torre 1.10x1.50 perimetrales y 1.20x1.70 interiores.",
    "Vigas de predimensionamiento: 0.40x0.60 torre, 0.40x0.70 podio y sótanos. Losas 0.15.",
    "Puertas DPB-MD-Abatible h 2.10 con el ancho medido; ventanas DPB-Ventana piso a techo (antepecho 0.10, "
    "alto 2.30, del corte); puertas de balcón DPB-PVC corrediza h 2.10.",
    "Muro de contención 0.30 de SÓTANO 4 a N01.",
    "En SÓTANO 4 columnas, núcleo y contención arrancan 1.50 abajo: la mitad G–I es el fondo de la hélice.",
    "Niveles de plantilla S-N01 a S-N03 eliminados: no existen en el plano.",
]

# ---------------------------------------------------------------- levels
levels = [
    ("SOTANO 4", -13.902), ("SOTANO 3", -10.746), ("SOTANO 2", -7.605), ("SOTANO 1", -4.437),
    ("N01", 5.95), ("N02", 10.27), ("N03", 13.77), ("N04", 16.97),
]
for k in range(5, 26):
    levels.append((f"N{k:02d}", round(16.97 + 3.20 * (k - 4), 3)))
levels.append(("AZOTEA", round(levels[-1][1] + 3.20, 3)))
names = [n for n, _ in levels]
elev = dict(levels)
PARKING = names[:names.index("N04")]            # SOTANO 4 .. N03
TOWER = names[names.index("N04"):names.index("AZOTEA")]
TOWER_WALLS = TOWER[:TOWER_WALL_LEVELS]


def storeys(base_list):
    return {"baseLevel": base_list[0], "repeatOn": base_list[1:]}


BOTTOM_DROP = 1.50   # the east half of SOTANO 4 sits 1.50 below the level (bottom of the helix)


def from_bottom(entry, levels_list):
    """Copies of an element standing on every parking level, written out one by one so the copy on
    SOTANO 4 can start 1.50 lower and reach the bottom of the helix. Ids match what repeatOn gives."""
    out = []
    for i, level in enumerate(levels_list):
        copy = dict(entry, baseLevel=level)
        copy.pop("repeatOn", None)
        if i:
            copy["id"] = entry["id"] + "@" + level
        if level == "SOTANO 4":
            copy["baseOffset"] = -BOTTOM_DROP
        out.append(copy)
    return out


# ---------------------------------------------------------------- grids
grids = []
for name, x in LETTER_X.items():
    grids.append({"id": "EJE " + name, "name": name, "start": [x, -7.0], "end": [x, 31.5]})
for name, y in NUMBER_Y.items():
    grids.append({"id": "EJE " + name, "name": name, "start": [-6.0, y], "end": [68.5, y]})

# ---------------------------------------------------------------- types
types = {
    "walls": [
        {"name": "DPB-ANT MURO NUCLEO 40", "base": "Concrete 300mm", "thickness": 0.40},
        {"name": "DPB-ANT MURO NUCLEO 60", "base": "Concrete 300mm", "thickness": 0.60},
        {"name": "DPB-ANT MURO CONTENCION 30", "base": "Retaining - 300mm Concrete", "thickness": 0.30},
    ],
    "floors": [],
    "columns": [
        {"name": "DPB-ANT C 120x170", "b": 1.20, "h": 1.70},
        {"name": "DPB-ANT C 110x150", "b": 1.10, "h": 1.50},
    ],
    "beams": [
        {"name": "DPB-ANT V 40x60", "b": 0.40, "h": 0.60},
        {"name": "DPB-ANT V 40x70", "b": 0.40, "h": 0.70},
    ],
    "doors": [],
    "windows": [],
}
wall_type_names = {}


def wall_type(layer, t):
    key = (layer, t)
    if key not in wall_type_names:
        label = "TABIQUE" if layer == "interior" or (layer == "nucleo" and t < 0.12) else "MURO"
        name = f"DPB-ANT {label} {int(round(t * 100)):02d}"
        base = "Interior - 97mm Partition (1-hr)" if t < 0.09 else "Generic - 150mm Masonry"
        if all(w["name"] != name for w in types["walls"]):
            types["walls"].append({"name": name, "base": base, "thickness": t})
        wall_type_names[key] = name
    return wall_type_names[key]


def opening_type(kind, width):
    w = round(round(width / 0.05) * 0.05, 2)
    if kind == "door":
        name, fam, h, bucket = f"DPB-ANT P {int(round(w * 100))}x210", "DPB-MD-Abatible", 2.10, "doors"
    elif kind == "window":
        name, fam, h, bucket = f"DPB-ANT V {int(round(w * 100))}x230", "DPB-Ventana", 2.30, "windows"
    else:
        name, fam, h, bucket = f"DPB-ANT PC {int(round(w * 100))}x210", "DPB-PVC-Puerta Corrediza 2 Cuerpos", 2.10, "windows"
    if all(t["name"] != name for t in types[bucket]):
        types[bucket].append({"name": name, "family": fam, "width": w, "height": h})
    return name


# ---------------------------------------------------------------- columns
def at(letter, number):
    return [LETTER_X[letter], NUMBER_Y[number]]


letters, numbers = list(LETTER_X), ["6", "5", "3", "2"]
core = {("E", "5"), ("E", "3"), ("F", "5"), ("F", "3")}
podium_cols = [(l, n) for l in letters for n in numbers]
tower_cols = [(l, n) for l in letters for n in numbers
              if (l, n) not in core and (l, n) not in {("E", "2"), ("F", "2")}]
interior = {(l, n) for l in "CDGH" for n in "53"}

columns = []
for l, n in podium_cols:
    columns += from_bottom({"id": f"C-P {l}{n}", "type": "DPB-ANT C 120x170", "at": at(l, n), "topLevel": "+1"}, PARKING)
for l, n in tower_cols:
    columns.append({"id": f"C-T {l}{n}", "type": "DPB-ANT C 120x170" if (l, n) in interior else "DPB-ANT C 110x150",
                    "at": at(l, n), "topLevel": "+1", **storeys(TOWER)})

# ---------------------------------------------------------------- walls
walls = []
ALL_STOREYS = names[:names.index("AZOTEA")]
core_walls = [
    ("NUCLEO E ALMA", "DPB-ANT MURO NUCLEO 40", [25.79, 8.15], [25.79, 16.15]),
    ("NUCLEO E ALA 5", "DPB-ANT MURO NUCLEO 60", [25.59, 7.845], [28.59, 7.845]),
    ("NUCLEO E ALA 3", "DPB-ANT MURO NUCLEO 60", [25.59, 16.445], [28.73, 16.445]),
    ("NUCLEO F ALMA", "DPB-ANT MURO NUCLEO 40", [34.50, 8.17], [34.50, 16.16]),
    ("NUCLEO F ALA 5", "DPB-ANT MURO NUCLEO 60", [31.39, 7.855], [34.70, 7.855]),
    ("NUCLEO F ALA 3", "DPB-ANT MURO NUCLEO 60", [31.39, 16.45], [34.70, 16.45]),
]
for wid, wtype, a, b in core_walls:
    walls += from_bottom({"id": wid, "type": wtype, "start": a, "end": b, "topLevel": "+1", "structural": True},
                         ALL_STOREYS)

basement_outline = orient(Polygon(json.load(open("out/sotano_outline.json"))["outline"]), 1.0)
ring = list(basement_outline.exterior.coords)
for i, (a, b) in enumerate(zip(ring, ring[1:])):
    walls += from_bottom({"id": f"CONTENCION {i + 1}", "type": "DPB-ANT MURO CONTENCION 30", "start": [a[0], a[1]],
                          "end": [b[0], b[1]], "alignment": "right", "topLevel": "+1", "structural": True},
                         names[:names.index("N01")])

core_zone = Polygon([(25.4, 7.4), (34.9, 7.4), (34.9, 16.9), (25.4, 16.9)])
typical = json.load(open("out/tipico_walls_bridged.json"))
wall_id = {}
kept = 0
for w in typical:
    if w["o"] == "h":
        a, b = [w["lo"], w["c"]], [w["hi"], w["c"]]
    else:
        a, b = [w["c"], w["lo"]], [w["c"], w["hi"]]
    line = LineString([a, b])
    on_core = any(line.distance(LineString([ca, cb])) < 0.35 and
                  (abs(ca[0] - cb[0]) < 1e-6) == (w["o"] == "v") and line.intersection(LineString([ca, cb]).buffer(0.35)).length > 0.5 * line.length
                  for _, _, ca, cb in core_walls)
    if on_core:
        continue
    kept += 1
    wid = f"T-{w['layer'][:3].upper()} {w['index']:03d}"
    wall_id[w["index"]] = wid
    walls.append({"id": wid, "type": wall_type(w["layer"], w["t"]),
                  "start": [round(a[0], 3), round(a[1], 3)], "end": [round(b[0], 3), round(b[1], 3)],
                  "topLevel": "+1", "topOffset": -0.15, **storeys(TOWER_WALLS)})

# ---------------------------------------------------------------- doors and windows (typical floor)
doors, windows = [], []
openings = json.load(open("out/tipico_openings.json"))
for n, o in enumerate(openings):
    host = wall_id.get(o["host"])
    if host is None:
        continue
    pieces = [o["width"]]
    if o["kind"] == "balcony_door":
        pieces = [o["width"] - 0.15]
    if o["kind"] == "balcony_door" and o["width"] > 3.5:
        parts = int(-(-o["width"] // 3.3))
        pieces = [(o["width"] - 0.15 * parts) / parts] * parts
    gap = 0.15 if o["kind"] == "balcony_door" else 0.0
    start = o["at"] - o["width"] / 2 + gap / 2
    for k, width in enumerate(pieces):
        mid = start + (width + gap) * k + width / 2
        point = [round(mid, 3), round(o["c"], 3)] if o["o"] == "h" else [round(o["c"], 3), round(mid, 3)]
        entry = {"id": f"{'P' if o['kind'] == 'door' else 'V'}-{n:03d}{'abc'[k] if len(pieces) > 1 else ''}",
                 "type": opening_type(o["kind"], width), "hostWall": host, "at": point, "level": TOWER_WALLS[0],
                 "repeatOn": TOWER_WALLS[1:]}
        if o["kind"] == "door":
            across = o.get("facing") or 1
            entry["facing"] = [0, across] if o["o"] == "h" else [across, 0]
            # Revit's HandOrientation on these door families points from the latch towards the hinge
            # (checked against the drawing on three doors), so it is the reverse of hinge -> latch.
            along = -1 if o.get("hinge") in (None, "lo") else 1
            entry["hand"] = [along, 0] if o["o"] == "h" else [0, along]
            doors.append(entry)
        else:
            entry["sill"] = 0.10 if o["kind"] == "window" else 0.0
            windows.append(entry)

# ---------------------------------------------------------------- parking helix
X_WEST, X_EAST, Y_SPLIT = LETTER_X["D"], LETTER_X["G"], NUMBER_Y["4"]
RAMP_RUN = X_EAST - X_WEST
stairs = [box(13.83, 12.26, 16.90, 15.32), box(43.40, 12.26, 46.46, 15.32)]
lifts = [box(26.05, 8.25, 28.50, 16.05), box(31.50, 8.90, 34.25, 14.00)]


def zone(x0, y0, x1, y1):
    shape = basement_outline.intersection(box(x0, y0, x1, y1))
    for cut in stairs + lifts:
        shape = shape.difference(cut)
    return max(list(shape.geoms) if hasattr(shape, "geoms") else [shape], key=lambda g: g.area)


def ring_of(poly):
    return [[round(x, 3), round(y, 3)] for x, y in list(orient(poly, 1.0).exterior.coords)[:-1]]


def holes_of(poly):
    return [[[round(x, 3), round(y, 3)] for x, y in list(r.coords)[:-1]] for r in poly.interiors]


ZONES = {
    "OESTE": zone(-10, -10, X_WEST, 40),
    "ESTE": zone(X_EAST, -10, 80, 40),
    "SUR": zone(X_WEST, -10, X_EAST, Y_SPLIT),
    "NORTE": zone(X_WEST, Y_SPLIT, X_EAST, 40),
}

# (turn id, host level, offset of its west plate, rise of the turn); rise None = bottom turn
turns = [("N03", "N03", 0.0, round(elev["N03"] - elev["N02"], 3)),
         ("N02", "N02", 0.0, round(elev["N02"] - elev["N01"], 3)),
         ("N01", "N01", 0.0, None)]
gap = elev["N01"] - elev["SOTANO 1"]
turns[-1] = ("N01", "N01", 0.0, round(gap / 3, 3))
for k in (1, 2):
    turns.append((f"N01-{k}", "SOTANO 1", round(gap * (3 - k) / 3, 3), round(gap / 3, 3)))
turns += [("SOTANO 1", "SOTANO 1", 0.0, round(elev["SOTANO 1"] - elev["SOTANO 2"], 3)),
          ("SOTANO 2", "SOTANO 2", 0.0, round(elev["SOTANO 2"] - elev["SOTANO 3"], 3)),
          ("SOTANO 3", "SOTANO 3", 0.0, round(elev["SOTANO 3"] - elev["SOTANO 4"], 3)),
          ("SOTANO 4", "SOTANO 4", 0.0, None)]

floors, beams = [], []
podium_pts = {c: at(*c) for c in podium_cols}


def plate(fid, zone_name, level, offset, ftype, slope=None):
    poly = ZONES[zone_name]
    f = {"id": fid, "type": ftype, "level": level, "offset": round(offset, 3),
         "boundary": ring_of(poly), "holes": holes_of(poly)}
    if slope:
        f["slopeArrow"] = slope
    floors.append(f)


for tid, level, o, rise in turns:
    drop = 1.50 if (rise is None or rise <= 3.6) else rise / 2
    east = o - drop
    on_grade = tid == "SOTANO 4"
    ftype = "B-CN120" if on_grade else "B-CN150"
    plate(f"HELICE {tid} OESTE", "OESTE", level, o, ftype)
    plate(f"HELICE {tid} ESTE", "ESTE", level, east, ftype)
    plate(f"HELICE {tid} RAMPA SUR", "SUR", level, east, ftype,
          {"from": [X_EAST, 4.0], "to": [X_WEST, 4.0], "percent": round(drop / RAMP_RUN * 100, 3)})
    if rise is None:
        plate(f"HELICE {tid} NORTE", "NORTE", level, east, ftype)
        north = (east, east)
    else:
        low = o - rise
        plate(f"HELICE {tid} RAMPA NORTE", "NORTE", level, low, ftype,
              {"from": [X_WEST, 20.4], "to": [X_EAST, 20.4], "percent": round((rise - drop) / RAMP_RUN * 100, 3)})
        north = (low, east)       # top of slab at X_WEST and at X_EAST
    if on_grade:
        continue                   # slab on grade: no beams

    def plate_or_ramp(x, y, along_ramp, o=o, east=east, north=north):
        """Top of slab under a beam. A beam lying along a ramp (a number line between D and G)
        follows that ramp over its whole length, ends included: on the north side the ramp is a
        full storey below the west plate at D, so taking the plate height there tilted the beam
        through the ramp."""
        t = (x - X_WEST) / RAMP_RUN
        if along_ramp:
            t = min(max(t, 0.0), 1.0)
            return o + (east - o) * t if y < Y_SPLIT else north[0] + (north[1] - north[0]) * t
        if x <= X_WEST + 1e-6:
            return o
        if x >= X_EAST - 1e-6:
            return east
        return o + (east - o) * t if y < Y_SPLIT else north[0] + (north[1] - north[0]) * t

    lines = [(l, sorted([c for c in podium_cols if c[0] == l], key=lambda c: podium_pts[c][1])) for l in letters] + \
            [(n, sorted([c for c in podium_cols if c[1] == n], key=lambda c: podium_pts[c][0])) for n in numbers]
    for name, members in lines:
        for c1, c2 in zip(members, members[1:]):
            p, q = podium_pts[c1], podium_pts[c2]
            mid = Point((p[0] + q[0]) / 2, (p[1] + q[1]) / 2)
            if mid.within(core_zone):
                continue
            if name in LETTER_X and X_WEST < p[0] < X_EAST and (p[1] < Y_SPLIT) != (q[1] < Y_SPLIT):
                continue           # along E or F this would cross the step between the two ramps
            y_mid = (p[1] + q[1]) / 2
            along_ramp = name in NUMBER_Y and X_WEST - 1e-6 <= min(p[0], q[0]) and max(p[0], q[0]) <= X_EAST + 1e-6
            z1, z2 = plate_or_ramp(p[0], y_mid, along_ramp), plate_or_ramp(q[0], y_mid, along_ramp)
            beam = {"id": f"V-H {tid} {c1[0]}{c1[1]}-{c2[0]}{c2[1]}", "type": "DPB-ANT V 40x70",
                    "start": p, "end": q, "level": level, "offset": round(z1, 3)}
            if abs(z2 - z1) > 1e-3:
                beam["endOffset"] = round(z2, 3)
            beams.append(beam)

# podium roof N04: flat, full outline
roof = basement_outline
for cut in stairs + lifts:
    roof = roof.difference(cut)
floors.append({"id": "LOSA N04", "type": "B-CN150", "level": "N04", "boundary": ring_of(roof), "holes": holes_of(roof)})

# tower beams and slabs
tower_outline = Polygon(json.load(open("out/tipico_outline.json"))["outline"])


def grid_beams(cols, prefix, btype, levels_list, outline):
    out = []
    pts = {c: at(*c) for c in cols}
    lines = [(l, [c for c in cols if c[0] == l], 1) for l in letters] + [(n, [c for c in cols if c[1] == n], 0) for n in numbers]
    for name, members, axis in lines:
        members.sort(key=lambda c: pts[c][axis])
        for c1, c2 in zip(members, members[1:]):
            p, q = pts[c1], pts[c2]
            mid = Point((p[0] + q[0]) / 2, (p[1] + q[1]) / 2)
            if abs(q[axis] - p[axis]) > 9.0 or mid.within(core_zone) or not outline.buffer(0.05).contains(mid):
                continue
            out.append({"id": f"{prefix} {c1[0]}{c1[1]}-{c2[0]}{c2[1]}", "type": btype, "start": p, "end": q,
                        "level": levels_list[0], "repeatOn": levels_list[1:]})
    return out


beams += grid_beams(podium_cols, "V-P", "DPB-ANT V 40x70", ["N04"], basement_outline)
beams += grid_beams(tower_cols, "V-T", "DPB-ANT V 40x60", names[names.index("N05"):], tower_outline)

tower_slab = tower_outline
for cut in stairs + lifts:
    tower_slab = tower_slab.difference(cut)
floors.append({"id": "LOSA TORRE", "type": "B-CN150", "level": "N05", "boundary": ring_of(tower_slab),
               "holes": holes_of(tower_slab), "repeatOn": names[names.index("N06"):]})

# balconies: the part of each balcony outside the slab, on every tower floor above N04
balconies = [box(8.83, -1.34, 12.01, -0.33), box(8.83, 24.63, 12.01, 25.66),
             box(48.29, -1.34, 51.47, -0.33), box(48.29, 24.63, 51.47, 25.66)]
for i, b in enumerate(balconies):
    outside = b.difference(tower_outline)
    if outside.area < 0.3:
        continue
    piece = max(list(outside.geoms) if hasattr(outside, "geoms") else [outside], key=lambda g: g.area)
    floors.append({"id": f"BALCON {i + 1}", "type": "B-CN150", "level": "N05", "boundary": ring_of(piece),
                   "repeatOn": names[names.index("N06"):names.index("AZOTEA")]})

spec = {
    "_decisions": DECISIONS,
    "levels": [{"id": n, "name": n, "elevation": e} for n, e in levels],
    "grids": grids, "types": types, "columns": columns, "walls": walls, "beams": beams, "floors": floors,
    "doors": doors, "windows": windows,
}
json.dump(spec, open(OUT, "w", encoding="utf-8"), indent=1, ensure_ascii=False)


def copies(items):
    return sum(1 + len(i.get("repeatOn") or []) for i in items)


print(f"levels {len(levels)}  grids {len(grids)}  columns {copies(columns)}  walls {copies(walls)} "
      f"(typical {kept} x {len(TOWER_WALLS)})  beams {copies(beams)}  floors {copies(floors)}  "
      f"doors {copies(doors)}  windows {copies(windows)}")
print("helix turns:", [(t[0], t[1], t[2], t[3]) for t in turns])
print("door types:", [t["name"] for t in types["doors"]])
print("window types:", [t["name"] for t in types["windows"]])
