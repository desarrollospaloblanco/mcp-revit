# Reportes de acero

Generadores del Excel de despiece, el reporte HTML autocontenido y la presentación DPB a partir del
JSON que exporta la herramienta `rebar_takeoff` del MCP de Revit. Es el mismo flujo con el que se
cuantificaron las casas Sherman, Lincoln y Hamilton de Persea de Arrazola.

## Flujo completo

1. **Armado por tipo** con `rebar_by_type`: columnas, vigas, vigas de amarre, zapatas, cimientos
   corridos y mallas de muros y losas, por marca de tipo.
2. **Armado de los planos** con `place_rebar`: lo que solo dicen los planos (barras dibujadas en
   elevaciones, bastones de losa, pasadores, gradas), como polilíneas en metros.
3. Las dos herramientas parten las barras más largas que la varilla comercial (`stockLength` de 6, 9
   o 12 m) en piezas con traslape de 40 db, nunca en un nudo, alternan los empalmes y dibujan un
   marcador magenta en cada traslape.
4. **Cuantificación** con `rebar_takeoff`: pesos en qq de 100 lb, reparto por una línea divisoria
   (casa 1 y casa 2) y por nivel, marcas de pieza, tablas ACERO 01 a 05, vistas 3D y exportación
   JSON + CSV.
5. **Reportes** con estos scripts, a partir del JSON exportado.

## Uso

Copia `report_config.example.json`, ajusta los datos y corre:

```bash
python build_xlsx.py mi_config.json
```

```bash
python build_html.py mi_config.json
```

```bash
node build_deck.js mi_config.json
```

- Las rutas relativas del archivo de configuración se leen desde la carpeta del archivo.
- `images.full` e `images.laps` son las vistas "ACERO - 3D completo" y "ACERO - Zonas de traslape"
  exportadas a PNG. Son opcionales.
- La presentación necesita `pptxgenjs` (`npm i pptxgenjs@4`, o `NODE_PATH` hacia una instalación) y
  `DPB_SKILL_DIR` apuntando a la skill `dpb-presentaciones`, de donde sale el isotipo. Sin esa
  variable la presentación sale sin isotipo; nunca se redibuja.
- El Excel queda con fórmulas. Ábrelo en Excel una vez (o recalcula) para que muestre los valores.

## Criterio de redondeo

Todas las cifras en qq se muestran redondeadas hacia arriba al quintal entero. Cada una se redondea
desde su valor exacto (en el Excel van en columnas ocultas) y los totales salen del total exacto, no
de sumar filas redondeadas, así que una suma de filas puede quedar un quintal arriba de su total.

## Qué trae el JSON de `rebar_takeoff`

- `summary`: varilla, malla y total (neto, con desperdicio y redondeados hacia arriba), por
  diámetro, por malla, por parte (`byZone`) y por nivel (`byLevel`).
- `cutting`: piezas por diámetro y varillas comerciales necesarias con corte por primer ajuste
  decreciente.
- `lapSplices`: cada traslape con su id (el del marcador y del parámetro Traslape).
- `rows`: un renglón por grupo de barras o área de malla, con fracciones por parte y por nivel.
