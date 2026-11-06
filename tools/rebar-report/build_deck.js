// Presentación DPB (variante B, arquitectónica técnica) del acero de un modelo, a partir del JSON de rebar_takeoff.
// Uso: node build_deck.js <config.json>
// Requiere pptxgenjs (npm i pptxgenjs@4) y DPB_SKILL_DIR apuntando a la skill dpb-presentaciones, de donde sale el isotipo.
const pptxgen = require("pptxgenjs");
const path = require("path");
const fs = require("fs");

const cfgPath = process.argv[2];
if (!cfgPath) { console.error("Uso: node build_deck.js <config.json>"); process.exit(1); }
const C = JSON.parse(fs.readFileSync(cfgPath, "utf8"));
const base = path.dirname(path.resolve(cfgPath));
const resolve = (p) => (!p || path.isAbsolute(p) ? p : path.join(base, p));   // rutas relativas al archivo de configuración
const tkPath = resolve(C.takeoff);
const D = JSON.parse(fs.readFileSync(tkPath, "utf8"));
const S = D.summary;
const WASTE = S.waste;
const out = C.output || {};
const NAME = C.name || D.document;
const outFile = path.join(resolve(out.folder) || path.dirname(tkPath), (out.basename || `${NAME} - Acero de refuerzo`) + ".pptx");
const levelLabel = (l) => (C.levelNames || {})[l] || l;
const images = Object.fromEntries(Object.entries(C.images || {}).map(([k, v]) => [k, resolve(v)]));

const DPB = {
  bg: "FAFAFA", white: "FFFFFF", dark: "1A1A2E", blue: "2C589A", red: "CC1F37",
  g300: "D1D1D6", g400: "A1A1AA", g500: "71717A", g600: "52525B", grid: "EEEFF2", lap: "C2185B",
};
const FONT = "Open Sans", MONO = "Consolas";
const ISO = process.env.DPB_SKILL_DIR ? path.join(process.env.DPB_SKILL_DIR, "assets", "isotipo_transparent_hd.png") : null;
const hasIso = ISO && fs.existsSync(ISO);
if (!hasIso) console.warn("Sin isotipo: define DPB_SKILL_DIR con la ruta de la skill dpb-presentaciones.");

const up = (x) => Math.ceil(x - 1e-9);
const q = (x) => up(x).toLocaleString("es-GT");
const n0 = (x) => Math.round(x).toLocaleString("es-GT");
const pct = (x, d = 0) => (x * 100).toFixed(d) + " %";
const sizeName = (k) => (k.startsWith("#") ? `No. ${k.slice(1)}` : `Ø ${k.slice(1)} mm`);
const sizeSort = (a, b) => {
  const ka = a.startsWith("#") ? [0, -parseInt(a.slice(1))] : [1, -parseFloat(a.slice(1))];
  const kb = b.startsWith("#") ? [0, -parseInt(b.slice(1))] : [1, -parseFloat(b.slice(1))];
  return ka[0] - kb[0] || ka[1] - kb[1];
};
const bars = D.rows.filter((r) => r.kind === "bar");
const meshes = D.rows.filter((r) => r.kind === "mesh");
const hasImg = (p) => p && fs.existsSync(p);

const slides = ["portada", "resultado", "criterios", "diametro", "nivel", "corte", "traslapes"];
if (meshes.length) slides.push("malla");
if ((C.rfi || []).length) slides.push("rfi");
slides.push("cierre");
const N = slides.length;
const CODE = `DPB-${(C.code || NAME).toUpperCase()}-ACERO`;

const pres = new pptxgen();
pres.layout = "LAYOUT_WIDE";
pres.title = `${NAME} - Acero de refuerzo`;
pres.author = "Desarrollos Palo Blanco";
const W = 13.3, H = 7.5;

const grid = (s) => {
  s.background = { color: DPB.bg };
  for (let x = 0.3; x <= W; x += 0.3) s.addShape(pres.shapes.LINE, { x, y: 0, w: 0, h: H, line: { color: DPB.grid, width: 0.25 } });
  for (let y = 0.3; y <= H; y += 0.3) s.addShape(pres.shapes.LINE, { x: 0, y, w: W, h: 0, line: { color: DPB.grid, width: 0.25 } });
};
const rule = (s, x, y) => s.addShape(pres.shapes.RECTANGLE, { x, y, w: 0.5, h: 0.04, fill: { color: DPB.blue }, line: { width: 0 } });
const foot = (s, n) => {
  s.addShape(pres.shapes.LINE, { x: 0.5, y: H - 0.5, w: W - 1, h: 0, line: { color: DPB.g300, width: 0.5 } });
  s.addText(CODE, { x: 0.5, y: H - 0.4, w: 6, h: 0.25, fontSize: 8, fontFace: MONO, color: DPB.g500, margin: 0 });
  s.addText(`LÁMINA ${String(n).padStart(2, "0")}/${N}`, { x: W - 3, y: H - 0.4, w: 2.5, h: 0.25, fontSize: 8, fontFace: MONO, color: DPB.g500, align: "right", margin: 0 });
};
const head = (s, key, kicker, title, sub) => {
  const n = slides.indexOf(key) + 1;
  grid(s);
  s.addText(`${String(n - 1).padStart(2, "0")} · ${kicker}`, { x: 0.7, y: 0.6, w: 8, h: 0.25, fontSize: 9, fontFace: MONO, color: DPB.blue, charSpacing: 2, margin: 0 });
  s.addText(title, { x: 0.7, y: 0.85, w: 10.5, h: 0.8, fontSize: 26, fontFace: FONT, bold: true, color: DPB.dark, margin: 0 });
  rule(s, 0.7, 1.62);
  if (sub) s.addText(sub, { x: 0.7, y: 1.75, w: 11.5, h: 0.5, fontSize: 12, fontFace: FONT, color: DPB.g600, margin: 0 });
  if (hasIso) s.addImage({ path: ISO, x: W - 1.0, y: 0.5, w: 0.55, h: 0.6 });
  foot(s, n);
};
const cellH = (t) => ({ text: t, options: { bold: true, color: DPB.white, fill: { color: DPB.blue }, fontFace: FONT, fontSize: 10, align: "center", valign: "middle" } });
const cell = (t, o = {}) => ({ text: String(t), options: Object.assign({ fontFace: FONT, fontSize: 10, color: DPB.dark, valign: "middle" }, o) });
const num = (t, o = {}) => cell(t, Object.assign({ align: "right" }, o));
const tableOpts = (x, y, w, colW) => ({ x, y, w, colW, rowH: 0.34, border: { type: "solid", color: DPB.g300, pt: 0.5 }, fill: { color: DPB.white } });
const picture = (s, p, x, y, w, h) => s.addImage({ path: p, x, y, w, h, sizing: { type: "contain", w, h } });

// ===== PORTADA =====
{
  const s = pres.addSlide();
  grid(s);
  if (hasImg(images.full)) picture(s, images.full, 7.3, 1.2, 5.5, 5.2);
  if (hasIso) s.addImage({ path: ISO, x: 0.8, y: 0.8, w: 0.75, h: 0.8 });
  s.addText(C.date || D.date.slice(0, 10), { x: W - 2.5, y: 0.6, w: 2, h: 0.25, fontSize: 9, fontFace: MONO, color: DPB.g600, align: "right", margin: 0 });
  s.addText(`DOC-${(C.code || NAME).toUpperCase()}-ACERO · CUANTIFICACIÓN`, { x: 0.8, y: 2.6, w: 7, h: 0.3, fontSize: 10, fontFace: MONO, color: DPB.red, margin: 0 });
  s.addText(`Acero de refuerzo\nde la ${NAME}`, { x: 0.8, y: 3.0, w: 6.5, h: 1.8, fontSize: 42, fontFace: FONT, bold: true, color: DPB.dark, margin: 0 });
  rule(s, 0.8, 4.85);
  s.addText(C.subtitle || `Despiece y cuantificación con varilla de ${D.stockLength} m y traslapes fuera de nudos.`,
    { x: 0.8, y: 5.05, w: 6.2, h: 0.9, fontSize: 13, fontFace: FONT, color: DPB.g600, margin: 0 });
  s.addShape(pres.shapes.LINE, { x: 0.8, y: 6.7, w: W - 1.6, h: 0, line: { color: DPB.g400, width: 0.5 } });
  [["PROYECTO", C.project || ""], ["MODELO", C.model || D.document], ["DISEÑO", C.design || ""], ["ELABORÓ", C.author || ""]].forEach((c, i) => {
    const x = 0.8 + i * 3.0;
    s.addText(c[0], { x, y: 6.8, w: 3, h: 0.25, fontSize: 8, fontFace: MONO, color: DPB.g400, margin: 0 });
    s.addText(c[1], { x, y: 7.05, w: 2.9, h: 0.3, fontSize: 10.5, fontFace: FONT, bold: true, color: DPB.dark, margin: 0, fit: "shrink" });
  });
}

// ===== RESULTADO =====
{
  const s = pres.addSlide();
  head(s, "resultado", "RESULTADO", "Cuánto acero lleva el modelo",
    `Pesos en quintales de 100 lb (qq), redondeados hacia arriba al quintal entero. La cifra grande incluye ${pct(WASTE)} de desperdicio y abajo va el neto.`);
  const zones = D.zones.map((z) => `${z} ${q(S.byZone[z].bar + S.byZone[z].mesh)} qq netos`).join(" y ");
  const rows = [
    ["Varilla", S.bar.withWasteRoundedUp, `${q(S.bar.net)} qq netos en ${n0(S.bars)} barras`],
    ["Electromalla", S.mesh.withWasteRoundedUp, `${q(S.mesh.net)} qq netos en ${S.meshAreas} áreas de malla`],
    ["Acero total", S.total.withWasteRoundedUp, zones || `${q(S.total.net)} qq netos`],
  ];
  rows.forEach((r, i) => {
    const y = 2.55 + i * 1.3;
    s.addShape(pres.shapes.LINE, { x: 0.7, y, w: 11.9, h: 0, line: { color: i === 2 ? DPB.blue : DPB.g300, width: i === 2 ? 1.25 : 0.5 } });
    s.addText(r[0].toUpperCase(), { x: 0.7, y: y + 0.25, w: 3, h: 0.3, fontSize: 10, fontFace: MONO, color: DPB.g500, charSpacing: 2, margin: 0 });
    s.addText(`${n0(r[1])} qq`, { x: 3.4, y: y + 0.1, w: 4.2, h: 0.75, fontSize: i === 2 ? 40 : 34, fontFace: FONT, bold: true, color: i === 2 ? DPB.blue : DPB.dark, margin: 0 });
    s.addText(r[2], { x: 7.8, y: y + 0.25, w: 4.8, h: 0.5, fontSize: 12, fontFace: FONT, color: DPB.g600, margin: 0 });
  });
  s.addText("Cada cifra se redondea desde su valor exacto, así que una suma de filas puede quedar un quintal arriba de su total. Las tablas del modelo y el Excel salen del mismo despiece.",
    { x: 0.7, y: 6.35, w: 11.9, h: 0.4, fontSize: 11, fontFace: FONT, color: DPB.g500, italic: true, margin: 0 });
}

// ===== CRITERIOS =====
{
  const s = pres.addSlide();
  head(s, "criterios", "CRITERIOS", "Cómo se armó y dónde se empalma", "Reglas del modelo y del despiece.");
  const laps = Object.keys(D.laps).sort(sizeSort).map((k) => `${sizeName(k)} ${D.laps[k].toFixed(2)}`).join(", ");
  const items = [
    [`Varilla de ${D.stockLength} m`, `Ninguna barra pasa del largo comercial. Las que lo exceden se parten en piezas con traslape; la más larga mide ${S.longestBar.toFixed(2)} m.`],
    [`Traslape de ${D.lapFactor} diámetros`, `Largo en que dos barras se enciman para pasarse la fuerza. ${laps} m.`],
    ["Sin traslapes en nudos", "El empalme queda completo en la mitad central del claro libre y a más de 2h de la cara del apoyo (ACI 318-19)."],
    ["Empalmes alternados", "Los grupos de barras de un mismo elemento empalman desde extremos alternos, para no debilitar una misma sección."],
  ].concat((C.criteria || []).map((c) => [c[0], c[1]])).slice(0, 6);
  items.forEach((it, i) => {
    const col = i % 3, row = Math.floor(i / 3);
    const x = 0.7 + col * 4.05, y = 2.5 + row * 2.0;
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, { x, y, w: 3.8, h: 1.8, fill: { color: DPB.white }, line: { color: DPB.g300, width: 0.5 }, rectRadius: 0.06 });
    s.addText(String(i + 1).padStart(2, "0"), { x: x + 0.2, y: y + 0.18, w: 0.6, h: 0.3, fontSize: 11, fontFace: MONO, bold: true, color: DPB.blue, margin: 0 });
    s.addText(it[0], { x: x + 0.75, y: y + 0.15, w: 2.9, h: 0.35, fontSize: 13, fontFace: FONT, bold: true, color: DPB.dark, margin: 0 });
    s.addText(it[1], { x: x + 0.2, y: y + 0.6, w: 3.4, h: 1.1, fontSize: 10, fontFace: FONT, color: DPB.g600, margin: 0, valign: "top" });
  });
}

// ===== POR DIÁMETRO =====
{
  const s = pres.addSlide();
  const by = [...S.bySize].sort((a, b) => sizeSort(a.bar, b.bar));
  const top2 = [...by].sort((a, b) => b.qq - a.qq).slice(0, 2);
  const share = top2.reduce((t, x) => t + x.qq, 0) / Math.max(S.bar.net, 1e-9);
  head(s, "diametro", "VARILLA POR DIÁMETRO", `${top2.map((x) => sizeName(x.bar)).join(" y ")} son el ${pct(share)} de la varilla`, "Peso neto por diámetro en qq.");
  s.addChart(pres.charts.BAR, [{ name: "Peso neto (qq)", labels: by.map((x) => sizeName(x.bar)), values: by.map((x) => up(x.qq)) }], {
    x: 0.6, y: 2.4, w: 6.2, h: 4.4, barDir: "bar", chartColors: [DPB.blue], showValue: true, dataLabelFontSize: 9, dataLabelFormatCode: "0",
    catAxisLabelFontSize: 10, catAxisLabelFontFace: FONT, valAxisHidden: true, valGridLine: { style: "none" }, catAxisOrientation: "maxMin", barGapWidthPct: 45, showLegend: false,
  });
  const t = [[cellH("Diámetro"), cellH("Barras"), cellH("Neto (qq)"), cellH(`Con ${pct(WASTE)}`)]];
  by.forEach((x) => t.push([cell(sizeName(x.bar), { bold: true }), num(n0(x.bars)), num(q(x.qq)), num(q(x.qq * (1 + WASTE)))]));
  t.push([cell("Total", { bold: true }), num(n0(S.bars), { bold: true }), num(q(S.bar.net), { bold: true }), num(q(S.bar.net * (1 + WASTE)), { bold: true, color: DPB.blue })]);
  s.addTable(t, tableOpts(7.2, 2.45, 5.4, [1.6, 1.2, 1.3, 1.3]));
}

// ===== POR NIVEL Y ELEMENTO =====
{
  const s = pres.addSlide();
  const groups = [...new Set(bars.map((b) => b.group))];
  const lv = D.levels.filter((l) => S.byLevel[l].bar > 0);
  head(s, "nivel", "VARILLA POR NIVEL", "Dónde va la varilla", "Peso neto en qq por nivel y elemento. Las barras que pasan de un nivel a otro se reparten por su largo.");
  const t = [[cellH("Elemento")].concat(lv.map((l) => cellH(levelLabel(l)))).concat([cellH("Total")])];
  groups.forEach((g) => {
    const vals = lv.map((l) => bars.filter((b) => b.group === g).reduce((a, b) => a + b.qq * (b.levels[l] || 0), 0));
    t.push([cell(g, { bold: true })].concat(vals.map((v) => num(v > 0 ? q(v) : "-"))).concat([num(q(vals.reduce((a, b) => a + b, 0)), { bold: true })]));
  });
  t.push([cell("Total", { bold: true })].concat(lv.map((l) => num(q(S.byLevel[l].bar), { bold: true }))).concat([num(q(S.bar.net), { bold: true, color: DPB.blue })]));
  const w = Math.min(11.9, 2.4 + 1.5 * (lv.length + 1));
  s.addTable(t, tableOpts(0.7, 2.45, w, [2.4].concat(Array(lv.length + 1).fill((w - 2.4) / (lv.length + 1)))));
}

// ===== CORTE COMERCIAL =====
{
  const s = pres.addSlide();
  const cut = [...D.cutting].sort((a, b) => sizeSort(a.bar, b.bar));
  const stockBars = cut.reduce((a, c) => a + c.stockBars, 0);
  head(s, "corte", "CORTE COMERCIAL", `${n0(stockBars)} varillas de ${D.stockLength} m`,
    "Cada pieza entra en la primera varilla donde cabe, de la más larga a la más corta. El desperdicio de corte es aparte del de cuantificación.");
  const t = [[cellH("Diámetro"), cellH("Piezas"), cellH("Largo de corte (m)"), cellH(`Varillas de ${D.stockLength} m`), cellH("Desperdicio de corte")]];
  cut.forEach((c) => t.push([cell(sizeName(c.bar), { bold: true }), num(n0(c.bars)), num(c.cutLength.toLocaleString("es-GT", { maximumFractionDigits: 1 })), num(n0(c.stockBars)), num(pct(c.cuttingWaste, 1))]));
  s.addTable(t, tableOpts(0.7, 2.45, 9.5, [1.9, 1.6, 2.2, 2.0, 1.8]));
}

// ===== TRASLAPES =====
{
  const s = pres.addSlide();
  const laps = D.lapSplices;
  head(s, "traslapes", "TRASLAPES", `${laps.length} traslapes, ninguno en un nudo`,
    "Cada traslape lleva un marcador magenta en el modelo y su id en el parámetro Traslape de las dos piezas que une.");
  let x0 = 0.7;
  if (hasImg(images.laps)) { picture(s, images.laps, 0.6, 2.3, 5.6, 4.5); x0 = 6.6; }
  const t = [[cellH("Id"), cellH("Elemento"), cellH("Diámetro"), cellH("Largo (m)")]];
  laps.slice(0, 11).forEach((l) => t.push([cell(l.id, { color: DPB.lap, fontFace: MONO }), cell(l.element), cell(sizeName(l.bar)), num(l.lap.toFixed(2))]));
  if (laps.length > 11) t.push([cell(`y ${laps.length - 11} más en el Excel`, { italic: true, color: DPB.g500, colspan: 4 })]);
  s.addTable(t, tableOpts(x0, 2.35, 12.6 - x0, [0.9, (12.6 - x0) - 3.4, 1.3, 1.2]));
}

// ===== ELECTROMALLA =====
if (meshes.length) {
  const s = pres.addSlide();
  head(s, "malla", "ELECTROMALLA", `${q(S.mesh.withWasteRoundedUp)} qq de malla con desperdicio`,
    "Peso por área neta con el traslape de plancha. En Revit cada recorte cuenta como plancha; las de compra salen del área.");
  const t = [[cellH("Malla"), cellH("Área neta (m²)"), cellH("Con traslapes (m²)"), cellH("Neto (qq)"), cellH(`Con ${pct(WASTE)} (qq)`)]];
  S.byMesh.forEach((m) => t.push([cell(m.mesh, { bold: true }), num(m.area.toFixed(1)), num(m.areaWithLaps.toFixed(1)), num(q(m.qq)), num(q(m.qq * (1 + WASTE)))]));
  s.addTable(t, tableOpts(0.7, 2.45, 9.5, [2.3, 1.8, 2.0, 1.6, 1.8]));
}

// ===== CONSULTAS =====
if ((C.rfi || []).length) {
  const s = pres.addSlide();
  const items = C.rfi;
  head(s, "rfi", "CONSULTAS ABIERTAS", "Lo que falta confirmar con el diseño", "Se cuantificó con el criterio indicado mientras llega la respuesta.");
  const dy = items.length > 4 ? 0.76 : 0.95;
  items.slice(0, 6).forEach((it, i) => {
    const y = 2.4 + i * dy;
    s.addShape(pres.shapes.LINE, { x: 0.7, y, w: 11.9, h: 0, line: { color: DPB.g300, width: 0.5 } });
    s.addText(String(i + 1).padStart(2, "0"), { x: 0.7, y: y + 0.18, w: 0.6, h: 0.35, fontSize: 14, fontFace: MONO, bold: true, color: DPB.blue, margin: 0 });
    s.addText(it[0], { x: 1.4, y: y + 0.15, w: 3.3, h: 0.4, fontSize: 13, fontFace: FONT, bold: true, color: DPB.dark, margin: 0 });
    s.addText(it[1], { x: 4.8, y: y + 0.12, w: 7.8, h: dy - 0.15, fontSize: 11, fontFace: FONT, color: DPB.g600, margin: 0, valign: "top" });
  });
}

// ===== CIERRE =====
{
  const s = pres.addSlide();
  head(s, "cierre", "ENTREGABLES", "Qué se entrega", "Todo sale del mismo despiece del modelo.");
  [
    ["Modelo de Revit", "Varillas y mallas nativas con parámetros MCP Acero, tablas ACERO 01 a 05 y vistas 3D de acero y de zonas de traslape."],
    ["Excel de despiece", "Detalle por nivel y elemento, despiece por pieza, electromalla, corte comercial y traslapes, con fórmulas."],
    ["Reporte HTML", "Resumen autocontenido para compartir, con imágenes del modelo."],
  ].forEach((it, i) => {
    const y = 2.5 + i * 1.2;
    s.addShape(pres.shapes.LINE, { x: 0.7, y, w: 11.9, h: 0, line: { color: DPB.g300, width: 0.5 } });
    s.addText(it[0], { x: 0.7, y: y + 0.2, w: 3.5, h: 0.4, fontSize: 14, fontFace: FONT, bold: true, color: DPB.dark, margin: 0 });
    s.addText(it[1], { x: 4.4, y: y + 0.2, w: 8.2, h: 0.8, fontSize: 12, fontFace: FONT, color: DPB.g600, margin: 0, valign: "top" });
  });
}

pres.writeFile({ fileName: outFile }).then((f) => console.log(f));
