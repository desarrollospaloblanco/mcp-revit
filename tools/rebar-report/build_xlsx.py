"""Excel de despiece y cuantificación de acero a partir del JSON de rebar_takeoff.

Uso: python build_xlsx.py <config.json>

Todos los pesos en qq se muestran redondeados hacia arriba al quintal entero. Cada cifra se redondea
desde su valor exacto (columnas ocultas) y los totales salen del total exacto, no de sumar filas.
"""
import collections
import sys

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter as COL

import report_common as rc

AZ, TX, GR, GR2, LAP = "2C589A", "1A1A2E", "F2F4F7", "E3E8F0", "C2185B"
thin = Side(style="thin", color="D0D5DD")
BOX = Border(bottom=thin)
QQ_FMT = '#,##0" qq"'


def F(**k):
    return Font(name="Arial", size=k.pop("size", 9), color=k.pop("color", TX), **k)


def fill(c):
    return PatternFill("solid", fgColor=c)


def RU(x):
    return f"ROUNDUP({x},0)"


class Book:
    def __init__(self):
        self.wb = Workbook()
        self.wb.remove(self.wb.active)

    def sheet(self, name, widths):
        ws = self.wb.create_sheet(name)
        for i, w in enumerate(widths, 1):
            ws.column_dimensions[COL(i)].width = w
        ws.sheet_view.showGridLines = False
        return ws


def title(ws, t, sub):
    ws["A1"] = t
    ws["A1"].font = F(size=14, bold=True, color=AZ)
    ws["A2"] = sub
    ws["A2"].font = F(size=9, color="5A6270")
    ws.row_dimensions[1].height = 22


def header(ws, r, cols):
    for i, c in enumerate(cols, 1):
        x = ws.cell(r, i, c)
        x.font = F(bold=True, color="FFFFFF")
        x.fill = fill(AZ)
        x.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
    ws.row_dimensions[r].height = 30


def band(ws, r, text, ncol):
    for i in range(1, ncol + 1):
        ws.cell(r, i).fill = fill(GR2)
    ws.cell(r, 1, text).font = F(bold=True, size=10, color=AZ)


def put(ws, r, c, v, fmt=None, **font):
    x = ws.cell(r, c, v)
    x.font = F(**font)
    if fmt:
        x.number_format = fmt
    return x


def build(cfg, data):
    bk = Book()
    sizes = rc.sizes(data)
    zones = data["zones"]
    levels = data["levels"]
    waste = data["summary"]["waste"]
    bars, meshes = rc.bars(data), rc.meshes(data)

    # ------------------------------------------------------------ Parámetros
    wp = bk.sheet("Parámetros", [30, 16, 14, 16, 60])
    title(wp, "Parámetros de cuantificación", "Valores que usan las fórmulas del libro. Celdas en azul: editables.")
    header(wp, 4, ["Parámetro", "Valor", "", "", "Nota"])
    params = [("Quintal (kg)", 45.359237, "0.000000", "1 qq = 100 lb = 45.359237 kg"),
              ("Desperdicio de varilla", waste, "0.0%", "Sobre el peso neto"),
              ("Desperdicio de electromalla", waste, "0.0%", "Sobre el peso con traslapes de plancha"),
              ("Largo comercial de varilla (m)", data["stockLength"], "0.00", "Ninguna pieza del modelo pasa de este largo")]
    for i, (k, v, fmt, n) in enumerate(params, 5):
        put(wp, i, 1, k)
        put(wp, i, 2, v, fmt, color="0000FF")
        put(wp, i, 5, n, color="5A6270")
    QQc, WV, WM = "Parámetros!$B$5", "Parámetros!$B$6", "Parámetros!$B$7"
    header(wp, 11, ["Clave", "Diámetro", "kg/m", f"Traslape {rc.lap_rule(cfg, data, True)} (m)", ""])
    k0 = 12
    for j, s in enumerate(sizes, k0):
        put(wp, j, 1, s)
        put(wp, j, 2, rc.size_name(s))
        put(wp, j, 3, data["kgPerMeter"].get(s), "0.0000", color="0000FF")
        put(wp, j, 4, data["laps"].get(s), "0.00", color="0000FF")
    k1 = k0 + max(1, len(sizes)) - 1
    KEYS, KGM = f"Parámetros!$A${k0}:$A${k1}", f"Parámetros!$C${k0}:$C${k1}"
    put(wp, k1 + 2, 1, "Todos los pesos en qq se muestran redondeados hacia arriba al quintal entero, cada uno desde su valor exacto "
        "(columnas ocultas). Por eso una suma de filas puede ser mayor que su total.", color="5A6270")
    put(wp, k1 + 3, 1, f"Fuente: {data['document']}, cuantificado el {data['date']} con rebar_takeoff (largos de barra de Revit).", color="5A6270")

    # ------------------------------------------------------------ Detalle por nivel y elemento
    zc = [f"{z} (qq)" for z in zones]
    cols = ["Nivel", "Grupo", "Elemento", "Barra", "Pieza", "Diámetro", "Cant.", "Largo de corte (m)", "Long. total (m)",
            "% en el nivel", "Long. en el nivel (m)", "Peso neto (qq)"] + zc + ["Traslape", "Neto exacto"] + [f"{z} exacto" for z in zones]
    wd = bk.sheet("Detalle por elemento", [14, 16, 20, 40, 9, 9, 7, 10, 10, 9, 11, 10] + [10] * len(zones) + [14, 10] + [10] * len(zones))
    title(wd, "Detalle de varilla por nivel y elemento",
          "Cada fila es un grupo de barras iguales. Las barras que pasan de un nivel a otro se reparten por su largo en cada nivel.")
    header(wd, 4, cols)
    cEx = 12 + len(zones) + 2
    r = 5
    det0 = r
    order = {g: i for i, g in enumerate(dict.fromkeys(b["group"] for b in bars))}
    for lv in levels:
        sel = [b for b in bars if b["levels"].get(lv, 0) > 0.001]
        if not sel:
            continue
        band(wd, r, rc.level_label(cfg, lv), len(cols))
        r += 1
        for b in sorted(sel, key=lambda b: (order[b["group"]], b["element"], rc.size_sort(b["size"]), b["description"])):
            frac = b["levels"][lv]
            vals = [lv, b["group"], b["element"], b["description"], b["pieceMark"], b["size"], b["count"], b["cutLength"], b["totalLength"], frac]
            for i, v in enumerate(vals, 1):
                put(wd, r, i, v)
            wd.cell(r, 8).number_format = "0.00"
            wd.cell(r, 9).number_format = "#,##0.00"
            wd.cell(r, 10).number_format = "0%"
            put(wd, r, 11, f"=I{r}*J{r}", "#,##0.00")
            ex = COL(cEx)
            put(wd, r, cEx, f"=K{r}*INDEX({KGM},MATCH(F{r},{KEYS},0))/{QQc}", "0.0000")
            put(wd, r, 12, f"={RU(f'{ex}{r}')}", "0")
            for zi, z in enumerate(zones):
                ce = cEx + 1 + zi
                put(wd, r, ce, f"={ex}{r}*{round(b['zones'].get(z, 0), 6)}", "0.0000")
                put(wd, r, 13 + zi, f"={RU(f'{COL(ce)}{r}')}", "0")
            put(wd, r, 13 + len(zones), b["laps"] or "", color=LAP)
            for i in range(1, 14 + len(zones)):
                wd.cell(r, i).border = BOX
            r += 1
    det1 = max(det0, r - 1)
    wd.freeze_panes = "A5"
    wd.auto_filter.ref = f"A4:{COL(len(cols))}{det1}"
    for c in range(cEx, len(cols) + 1):
        wd.column_dimensions[COL(c)].hidden = True

    def DR(c):
        return f"'Detalle por elemento'!${COL(c)}${det0}:${COL(c)}${det1}"

    # ------------------------------------------------------------ Despiece por pieza
    wz = bk.sheet("Despiece por pieza", [10, 16, 26, 11, 10, 12, 9, 12, 14, 10])
    title(wz, "Despiece por pieza de hierro", "Una marca por diámetro, forma y largo de corte. Cantidad = total de barras iguales en el modelo.")
    header(wz, 4, ["Pieza", "Diámetro", "Forma", "Largo de corte (m)", "Cantidad", "Long. total (m)", "kg/m", "Peso neto (qq)",
                   f"Con desperdicio (qq)", "Neto exacto"])
    pieces = collections.OrderedDict()
    for b in sorted(bars, key=lambda b: (rc.size_sort(b["size"]), b["pieceMark"] or "")):
        p = pieces.setdefault(b["pieceMark"], dict(size=b["size"], shape=b["shape"], L=b["cutLength"], n=0, tl=0.0))
        p["n"] += b["count"]
        p["tl"] += b["totalLength"]
    r = 5
    pz0 = r
    last = None
    for mark, p in pieces.items():
        if p["size"] != last:
            band(wz, r, rc.size_name(p["size"]), 9)
            r += 1
            last = p["size"]
        for i, v in enumerate([mark, rc.size_name(p["size"]), p["shape"], p["L"], p["n"], round(p["tl"], 3)], 1):
            put(wz, r, i, v, bold=(i == 1))
        wz.cell(r, 4).number_format = "0.00"
        wz.cell(r, 6).number_format = "#,##0.00"
        put(wz, r, 7, f"=INDEX({KGM},MATCH(\"{p['size']}\",{KEYS},0))", "0.000")
        put(wz, r, 10, f"=F{r}*G{r}/{QQc}", "0.0000")
        put(wz, r, 8, f"={RU(f'J{r}')}", "0")
        put(wz, r, 9, f"={RU(f'J{r}*(1+{WV})')}", "0")
        for i in range(1, 10):
            wz.cell(r, i).border = BOX
        r += 1
    pz1 = max(pz0, r - 1)
    put(wz, r, 1, "Total", bold=True)
    put(wz, r, 5, f"=SUBTOTAL(9,E{pz0}:E{pz1})", "#,##0", bold=True)
    put(wz, r, 6, f"=SUBTOTAL(9,F{pz0}:F{pz1})", "#,##0.00", bold=True)
    put(wz, r, 8, f"={RU(f'SUBTOTAL(9,J{pz0}:J{pz1})')}", QQ_FMT, bold=True)
    put(wz, r, 9, f"={RU(f'SUBTOTAL(9,J{pz0}:J{pz1})*(1+{WV})')}", QQ_FMT, bold=True, color=AZ)
    wz.column_dimensions["J"].hidden = True
    wz.freeze_panes = "A5"

    # ------------------------------------------------------------ Electromalla
    mcols = ["Elemento", "Grupo", "Malla", "Descripción", "Planchas Revit", "Área neta (m²)", "Área con traslapes (m²)", "Peso neto (qq)"] + zc + ["Neto exacto"] + [f"{z} exacto" for z in zones]
    wm = bk.sheet("Electromalla", [20, 16, 14, 34, 10, 12, 14, 11] + [10] * len(zones) + [10] * (1 + len(zones)))
    title(wm, "Electromalla", "Peso por área neta con el traslape de plancha, o por los alambres que tendió Revit cuando no hay área.")
    header(wm, 4, mcols)
    cMx = 9 + len(zones)
    r = 5
    me0 = r
    for m in sorted(meshes, key=lambda m: (m["size"], m["group"], m["element"])):
        for i, v in enumerate([m["element"], m["group"], m["size"], m["description"], m["count"], m["area"], m["areaWithLaps"]], 1):
            put(wm, r, i, v)
        wm.cell(r, 6).number_format = wm.cell(r, 7).number_format = "#,##0.00"
        put(wm, r, cMx, m["qq"], "0.0000")
        put(wm, r, 8, f"={RU(f'{COL(cMx)}{r}')}", "0")
        for zi, z in enumerate(zones):
            ce = cMx + 1 + zi
            put(wm, r, ce, f"={COL(cMx)}{r}*{round(m['zones'].get(z, 0), 6)}", "0.0000")
            put(wm, r, 9 + zi, f"={RU(f'{COL(ce)}{r}')}", "0")
        for i in range(1, 9 + len(zones)):
            wm.cell(r, i).border = BOX
        r += 1
    me1 = max(me0, r - 1)
    for c in range(cMx, len(mcols) + 1):
        wm.column_dimensions[COL(c)].hidden = True
    wm.freeze_panes = "A5"

    def MR(c):
        return f"Electromalla!${COL(c)}${me0}:${COL(c)}${me1}"

    # ------------------------------------------------------------ Corte comercial y traslapes
    wc = bk.sheet("Corte comercial", [16, 14, 16, 18, 16, 16])
    title(wc, f"Varillas comerciales de {data['stockLength']:g} m", "Corte por primer ajuste decreciente: cada pieza entra en la primera varilla donde cabe.")
    header(wc, 4, ["Diámetro", "Piezas a cortar", "Largo de corte (m)", f"Varillas de {data['stockLength']:g} m", "Desperdicio de corte", "Piezas más largas"])
    r = 5
    for c in sorted(data["cutting"], key=lambda c: rc.size_sort(c["bar"])):
        for i, v in enumerate([rc.size_name(c["bar"]), c["bars"], c["cutLength"], c["stockBars"], c["cuttingWaste"], c["longerThanStock"]], 1):
            put(wc, r, i, v)
        wc.cell(r, 3).number_format = "#,##0.00"
        wc.cell(r, 5).number_format = "0.0%"
        r += 1

    wl = bk.sheet("Traslapes", [10, 24, 40, 12, 12, 9, 24])
    title(wl, "Traslapes", f"Empalmes por traslape de {rc.lap_rule(cfg, data, True)}, fuera de nudos. El id es el del marcador en el modelo y del parámetro Traslape.")
    header(wl, 4, ["Traslape", "Elemento", "Barra", "Diámetro", "Largo (m)", "Barras", "Piezas que une (m)"])
    r = 5
    for lp in data["lapSplices"]:
        vals = [lp["id"], lp["element"], lp["description"], rc.size_name(lp["bar"]), lp["lap"], lp["count"], " + ".join(f"{x:.2f}" for x in lp["pieces"])]
        for i, v in enumerate(vals, 1):
            put(wl, r, i, v, color=LAP if i == 1 else TX)
        wl.cell(r, 5).number_format = "0.00"
        r += 1

    # ------------------------------------------------------------ Resumen
    ws = bk.sheet("Resumen", [34, 14, 14, 16, 12])
    bk.wb.move_sheet("Resumen", offset=-len(bk.wb.sheetnames) + 1)
    title(ws, f"{cfg.get('name', data['document'])} - Acero de refuerzo",
          f"{cfg.get('project', '')} · modelo {cfg.get('model', data['document'])} · qq de 100 lb redondeados hacia arriba al entero")
    header(ws, 4, ["Concepto", "Barras / m²", "Peso neto (qq)", "Con desperdicio (qq)", "Exacto"])
    r = 5
    band(ws, r, "Varilla por diámetro", 4)
    r += 1
    v0 = r
    for s in sizes:
        bs = next((x for x in data["summary"]["bySize"] if x["bar"] == s), None)
        put(ws, r, 1, rc.size_name(s))
        put(ws, r, 2, bs["bars"] if bs else 0, "#,##0")
        put(ws, r, 5, f"=SUMIF({DR(6)},\"{s}\",{DR(cEx)})", "0.0000")
        put(ws, r, 3, f"={RU(f'E{r}')}", "0")
        put(ws, r, 4, f"={RU(f'E{r}*(1+{WV})')}", "0")
        r += 1
    v1 = r - 1
    put(ws, r, 1, "Varilla", bold=True)
    put(ws, r, 5, f"=SUM(E{v0}:E{v1})", "0.0000")
    put(ws, r, 3, f"={RU(f'E{r}')}", QQ_FMT, bold=True)
    put(ws, r, 4, f"={RU(f'E{r}*(1+{WV})')}", QQ_FMT, bold=True)
    rv = r
    r += 2
    band(ws, r, "Electromalla", 4)
    r += 1
    m0 = r
    for mk in sorted({m["size"] for m in meshes}):
        put(ws, r, 1, f"Malla {mk}")
        put(ws, r, 2, f"=SUMIF({MR(3)},\"{mk}\",{MR(7)})", "#,##0.00")
        put(ws, r, 5, f"=SUMIF({MR(3)},\"{mk}\",{MR(cMx)})", "0.0000")
        put(ws, r, 3, f"={RU(f'E{r}')}", "0")
        put(ws, r, 4, f"={RU(f'E{r}*(1+{WM})')}", "0")
        r += 1
    m1 = r - 1
    put(ws, r, 1, "Electromalla", bold=True)
    put(ws, r, 5, f"=SUM(E{m0}:E{m1})" if m1 >= m0 else "=0", "0.0000")
    put(ws, r, 3, f"={RU(f'E{r}')}", QQ_FMT, bold=True)
    put(ws, r, 4, f"={RU(f'E{r}*(1+{WM})')}", QQ_FMT, bold=True)
    rm = r
    r += 2
    put(ws, r, 1, "Acero total", bold=True, size=11, color=AZ)
    put(ws, r, 5, f"=E{rv}+E{rm}", "0.0000")
    put(ws, r, 3, f"={RU(f'E{r}')}", QQ_FMT, bold=True, size=11)
    put(ws, r, 4, f"={RU(f'E{rv}*(1+{WV})+E{rm}*(1+{WM})')}", QQ_FMT, bold=True, size=11, color=AZ)
    r += 2
    if zones:
        band(ws, r, "Reparto", 4)
        r += 1
        for zi, z in enumerate(zones):
            put(ws, r, 1, f"{z} - varilla + malla")
            put(ws, r, 5, f"=SUM({DR(cEx + 1 + zi)})+SUM({MR(cMx + 1 + zi)})", "0.0000")
            put(ws, r, 3, f"={RU(f'E{r}')}", "0")
            put(ws, r, 4, f"={RU(f'SUM({DR(cEx + 1 + zi)})*(1+{WV})+SUM({MR(cMx + 1 + zi)})*(1+{WM})')}", "0")
            r += 1
        r += 1
    band(ws, r, "Varilla por nivel", 4)
    r += 1
    for lv in levels:
        put(ws, r, 1, rc.level_label(cfg, lv))
        put(ws, r, 5, f"=SUMIF({DR(1)},\"{lv}\",{DR(cEx)})", "0.0000")
        put(ws, r, 3, f"={RU(f'E{r}')}", "0")
        put(ws, r, 4, f"={RU(f'E{r}*(1+{WV})')}", "0")
        r += 1
    ws.column_dimensions["E"].hidden = True
    for row in ws.iter_rows(min_row=5, max_row=r, max_col=4):
        for c in row:
            if c.value is not None:
                c.border = BOX
    put(ws, r + 1, 1, f"Varilla comercial de {data['stockLength']:g} m · traslapes de {rc.lap_rule(cfg, data, True)} fuera de nudos · "
        f"desperdicio {waste:.0%}. Las cifras se redondean hacia arriba cada una desde su exacto.", color="5A6270")

    bk.wb.save(cfg["_paths"]["xlsx"])
    return cfg["_paths"]["xlsx"]


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("Uso: python build_xlsx.py <config.json>")
    c, d = rc.load(sys.argv[1])
    print(build(c, d))
