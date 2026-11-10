"""Reporte HTML autocontenido (imágenes en base64) del acero de un modelo, a partir del JSON de rebar_takeoff.

Uso: python build_html.py <config.json>
"""
import base64
import html
import os
import sys

import report_common as rc

CSS = """
:root{--az:#2C589A;--azo:#1E3F73;--rojo:#CC1F37;--tx:#1A1A2E;--g6:#52525B;--g5:#71717A;--g3:#D1D1D6;--g1:#F0F0F0;--lap:#C2185B;--bg:#FAFAFA}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--tx);font:15px/1.55 "Open Sans","Segoe UI",Arial,sans-serif}
main{max-width:1080px;margin:0 auto;padding:40px 20px 64px}
.k{font:12px Consolas,monospace;letter-spacing:.12em;color:var(--az);text-transform:uppercase;margin:0 0 6px}
h1{font-size:34px;line-height:1.15;margin:0 0 10px}h2{font-size:22px;margin:44px 0 6px}
.lede{color:var(--g6);max-width:760px;margin:0 0 22px}.rule{width:44px;height:3px;background:var(--az);margin:10px 0 18px}
.ledger{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:0;border-top:1px solid var(--g3);border-bottom:1px solid var(--g3);margin:8px 0 6px}
.ledger div{padding:14px 18px 14px 0}.ledger b{display:block;font-size:30px;line-height:1.1;color:var(--tx)}.ledger b.main{color:var(--az)}
.ledger span{font:11px Consolas,monospace;letter-spacing:.1em;color:var(--g5);text-transform:uppercase}.ledger small{display:block;color:var(--g6);font-size:13px}
table{width:100%;border-collapse:collapse;margin:12px 0;background:#fff;border-radius:6px;overflow:hidden}
th{background:var(--az);color:#fff;font-weight:600;font-size:13px;text-align:left;padding:8px 10px}td{padding:7px 10px;border-bottom:1px solid var(--g1);font-size:14px}
td.n,th.n{text-align:right;font-variant-numeric:tabular-nums}tr.tot td{font-weight:700;border-top:1px solid var(--g3)}tr.band td{background:#E3E8F0;color:var(--az);font-weight:600}
.bar{display:inline-block;height:9px;background:var(--az);border-radius:2px;vertical-align:middle}
.lapid{color:var(--lap);font-family:Consolas,monospace}.note{color:var(--g5);font-size:13px}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:24px}.grid2>div{min-width:0}
figure{margin:12px 0}figure img{width:100%;border:1px solid var(--g3);border-radius:6px;background:#fff}figcaption{color:var(--g5);font-size:13px}
.crit{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px}.crit div{background:#fff;border:1px solid var(--g3);border-radius:6px;padding:14px 16px}
.crit b{display:block;margin-bottom:4px}footer{margin-top:48px;color:var(--g5);font-size:12px;border-top:1px solid var(--g3);padding-top:12px}
@media(max-width:760px){.grid2{grid-template-columns:1fr}h1{font-size:28px}table{font-size:13px}main{padding:28px 16px}}
"""


def e(x):
    return html.escape(str(x))


def n0(x):
    return f"{x:,.0f}"


def qq(x):
    return f"{rc.up(x):,}"


def img(path):
    if not path or not os.path.exists(path):
        return None
    ext = os.path.splitext(path)[1].lower().lstrip(".") or "png"
    mime = "jpeg" if ext in ("jpg", "jpeg") else ext
    return f"data:image/{mime};base64," + base64.b64encode(open(path, "rb").read()).decode()


def table(head, rows, numeric=(), cls=None):
    th = "".join(f'<th class="{"n" if i in numeric else ""}">{e(h)}</th>' for i, h in enumerate(head))
    body = []
    for r in rows:
        if isinstance(r, tuple) and r and r[0] == "__band__":
            body.append(f'<tr class="band"><td colspan="{len(head)}">{e(r[1])}</td></tr>')
            continue
        tr_cls = ""
        if isinstance(r, tuple) and r and r[0] == "__tot__":
            tr_cls, r = ' class="tot"', r[1]
        tds = "".join(f'<td class="{"n" if i in numeric else ""}">{c}</td>' for i, c in enumerate(r))
        body.append(f"<tr{tr_cls}>{tds}</tr>")
    return f'<table{f" class={cls}" if cls else ""}><thead><tr>{th}</tr></thead><tbody>{"".join(body)}</tbody></table>'


def build(cfg, data):
    S = data["summary"]
    W = S["waste"]
    name = cfg.get("name", data["document"])
    bars, meshes = rc.bars(data), rc.meshes(data)
    zones, levels = data["zones"], data["levels"]
    stock = data["stockLength"]
    bar_q, mesh_q = S["bar"]["net"], S["mesh"]["net"]
    parts = []
    parts.append(f'<p class="k">{e(cfg.get("project", ""))} · cuantificación de acero</p>')
    parts.append(f"<h1>Acero de refuerzo de la {e(name)}</h1><div class=rule></div>")
    parts.append(f'<p class="lede">{e(cfg.get("subtitle", ""))} Pesos en quintales de 100 lb (qq), cada cifra redondeada hacia arriba '
                 f'al quintal entero desde su valor exacto; con desperdicio de {W:.0%}. Varilla comercial de {stock:g} m, traslapes de '
                 f'{e(rc.lap_rule(cfg, data))} fuera de nudos.</p>')
    zone_txt = " y ".join(f"{z} {qq(S['byZone'][z]['bar'] + S['byZone'][z]['mesh'])} qq netos" for z in zones)
    parts.append('<div class="ledger">'
                 f'<div><span>Acero total con {W:.0%}</span><b class="main">{n0(S["total"]["withWasteRoundedUp"])} qq</b><small>{n0(S["total"]["netRoundedUp"])} qq netos{(" · " + e(zone_txt)) if zones else ""}</small></div>'
                 f'<div><span>Varilla</span><b>{n0(S["bar"]["withWasteRoundedUp"])} qq</b><small>{qq(bar_q)} qq netos en {S["bars"]:,} barras</small></div>'
                 f'<div><span>Electromalla</span><b>{n0(S["mesh"]["withWasteRoundedUp"])} qq</b><small>{qq(mesh_q)} qq netos en {S["meshAreas"]} áreas</small></div>'
                 f'<div><span>Traslapes</span><b>{len(data["lapSplices"])}</b><small>pieza más larga {S["longestBar"]:.2f} m</small></div>'
                 "</div>")

    full, laps = img(cfg["images"].get("full")), img(cfg["images"].get("laps"))
    if full or laps:
        figs = []
        if full:
            figs.append(f'<figure><img alt="Acero completo en 3D" src="{full}"><figcaption>Vista ACERO - 3D completo del modelo.</figcaption></figure>')
        if laps:
            figs.append(f'<figure><img alt="Zonas de traslape" src="{laps}"><figcaption>Vista ACERO - Zonas de traslape: piezas empalmadas en magenta.</figcaption></figure>')
        parts.append(f'<div class="grid2">{"".join(figs)}</div>')

    # por diámetro
    parts.append("<h2>Varilla por diámetro</h2>")
    mx = max([x["qq"] for x in S["bySize"]] or [1])
    rows = []
    for x in sorted(S["bySize"], key=lambda x: rc.size_sort(x["bar"])):
        w = 160 * x["qq"] / mx
        rows.append([e(rc.size_name(x["bar"])), n0(x["bars"]), f'{x["length"]:,.1f}', qq(x["qq"]), qq(x["qq"] * (1 + W)), f'<span class="bar" style="width:{w:.0f}px"></span>'])
    rows.append(("__tot__", ["Total", n0(S["bars"]), f'{sum(x["length"] for x in S["bySize"]):,.1f}', qq(bar_q), qq(bar_q * (1 + W)), ""]))
    parts.append(table(["Diámetro", "Barras", "Longitud (m)", "Neto (qq)", f"Con {W:.0%} (qq)", ""], rows, numeric=(1, 2, 3, 4)))

    # reparto
    blocks = []
    if zones:
        zr = [[e(z), qq(S["byZone"][z]["bar"]), qq(S["byZone"][z]["mesh"]), qq(S["byZone"][z]["bar"] + S["byZone"][z]["mesh"])] for z in zones]
        blocks.append("<div><h2>Reparto</h2><p class=note>Lo que está sobre la línea divisoria se reparte mitad y mitad; lo que la cruza, según su largo en cada lado.</p>"
                      + table(["Parte", "Varilla (qq)", "Malla (qq)", "Total neto (qq)"], zr, numeric=(1, 2, 3)) + "</div>")
    lr = [[e(rc.level_label(cfg, l)), qq(S["byLevel"][l]["bar"]), qq(S["byLevel"][l]["mesh"])] for l in levels if S["byLevel"][l]["bar"] + S["byLevel"][l]["mesh"] > 0]
    blocks.append("<div><h2>Por nivel</h2><p class=note>Las barras que pasan de un nivel a otro se reparten por su largo en cada nivel.</p>"
                  + table(["Nivel", "Varilla (qq)", "Malla (qq)"], lr, numeric=(1, 2)) + "</div>")
    parts.append(f'<div class="grid2">{"".join(blocks)}</div>')

    # por elemento
    parts.append("<h2>Varilla por nivel y elemento</h2>")
    rows = []
    for l in levels:
        groups = {}
        for b in bars:
            f = b["levels"].get(l, 0)
            if f > 0:
                groups[b["group"]] = groups.get(b["group"], 0) + b["qq"] * f
        if not groups:
            continue
        rows.append(("__band__", rc.level_label(cfg, l)))
        for g, v in groups.items():
            rows.append([e(g), qq(v)])
    parts.append(table(["Elemento", "Neto (qq)"], rows, numeric=(1,)))

    # corte comercial
    parts.append(f"<h2>Varillas comerciales de {stock:g} m</h2>")
    rows = [[e(rc.size_name(c["bar"])), n0(c["bars"]), f'{c["cutLength"]:,.1f}', n0(c["stockBars"]), f'{c["cuttingWaste"]:.1%}']
            for c in sorted(data["cutting"], key=lambda c: rc.size_sort(c["bar"]))]
    parts.append(table(["Diámetro", "Piezas", "Largo de corte (m)", f"Varillas de {stock:g} m", "Desperdicio de corte"], rows, numeric=(1, 2, 3, 4)))
    parts.append('<p class="note">Corte por primer ajuste decreciente: cada pieza entra en la primera varilla donde cabe. El desperdicio de cuantificación '
                 f'({W:.0%}) es aparte.</p>')

    # electromalla
    if meshes:
        parts.append("<h2>Electromalla</h2>")
        rows = [[e(x["mesh"]), f'{x["area"]:,.1f}', f'{x["areaWithLaps"]:,.1f}', qq(x["qq"]), qq(x["qq"] * (1 + W))] for x in S["byMesh"]]
        parts.append(table(["Malla", "Área neta (m²)", "Con traslapes (m²)", "Neto (qq)", f"Con {W:.0%} (qq)"], rows, numeric=(1, 2, 3, 4)))

    # traslapes
    if data["lapSplices"]:
        parts.append("<h2>Traslapes</h2>")
        laps_txt = ", ".join(f"{rc.size_name(k)} {v:.2f} m" for k, v in sorted(data["laps"].items(), key=lambda kv: rc.size_sort(kv[0])))
        parts.append(f'<p class="note">Largo de traslape por diámetro: {e(laps_txt)}. El id es el del marcador en el modelo y del parámetro Traslape.</p>')
        rows = [[f'<span class="lapid">{e(lp["id"])}</span>', e(lp["element"]), e(lp["description"]), f'{lp["lap"]:.2f}', n0(lp["count"])]
                for lp in data["lapSplices"][:300]]
        parts.append(table(["Traslape", "Elemento", "Barra", "Largo (m)", "Barras"], rows, numeric=(3, 4)))

    # criterios y RFI
    crit = [(f"Varilla de {stock:g} m", f"Ninguna pieza pasa del largo comercial; la más larga mide {S['longestBar']:.2f} m."),
            (f"Traslapes de {rc.lap_rule(cfg, data)}", "Largo en que dos barras se enciman para pasarse la fuerza, redondeado a 0.05 m."),
            ("Empalmes alternados", "Los grupos de barras de un mismo elemento empalman desde extremos alternos para no cortar una misma sección.")]
    crit += [tuple(c) for c in cfg["criteria"]]
    parts.append("<h2>Criterios</h2><div class=crit>" + "".join(f"<div><b>{e(a)}</b>{e(b)}</div>" for a, b in crit) + "</div>")
    if cfg["rfi"]:
        parts.append("<h2>Consultas abiertas</h2><div class=crit>" + "".join(f"<div><b>{e(a)}</b>{e(b)}</div>" for a, b in cfg["rfi"]) + "</div>")
    parts.append(f'<footer>Fuente: {e(data["document"])}, cuantificado el {e(data["date"])} con rebar_takeoff del MCP de Revit. '
                 f'Modelo {e(cfg.get("model", ""))} · diseño {e(cfg.get("design", ""))} · elaboró {e(cfg.get("author", ""))}.</footer>')

    doc = (f'<!doctype html><html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
           f"<title>{e(name)} - Acero de refuerzo</title><style>{CSS}</style></head><body><main>{''.join(parts)}</main></body></html>")
    open(cfg["_paths"]["html"], "w", encoding="utf-8").write(doc)
    return cfg["_paths"]["html"]


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("Uso: python build_html.py <config.json>")
    c, d = rc.load(sys.argv[1])
    print(build(c, d))
