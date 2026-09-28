---
name: pdf-a-revit
description: Modelar en Revit un proyecto a partir de planos en PDF (plantas, elevación o corte) a nivel anteproyecto — niveles, ejes, columnas, muros, vigas, losas, rampas, puertas y ventanas — con el extractor de tools/pdf-to-revit y el comando build_model_from_spec. Usar cuando el usuario entrega PDF de planos y pide modelarlos, o pide corregir o refinar un modelo hecho así.
---

# De planos PDF a un modelo Revit

Flujo probado en una torre de 30 niveles (4 sótanos con rampas en hélice, planta típica de 13
departamentos): 8,700 muros, 2,000 puertas y 1,000 ventanas, 0 elementos rechazados por Revit.
El ejemplo completo está en `tools/pdf-to-revit/examples/torre/`.

El principio: **lo medido sale del PDF; lo decidido se escribe como decisión**. Todo lo que el
plano no resuelve (secciones de viga, espesores, niveles ambiguos) va en la lista `_decisions`
del JSON y se le reporta al usuario; nunca se inventa en silencio.

## 1. Diagnosticar los PDF antes de prometer nada

- `python tools/pdf-to-revit/pdf_colors.py plano.pdf`: agrupa los trazos por color, grosor y
  tipo. Así se identifican las capas por color, porque "Print to PDF" pierde las capas y la escala
  pero conserva vectores y colores.
- Leer la hoja como imagen (Read del PDF) y hacer ampliaciones con PyMuPDF
  (`page.get_pixmap(dpi=500, clip=...)`) para leer los nombres de ejes y las cotas: casi siempre
  vienen como trazos (SHX), no como texto.
- Los rótulos de nivel (NLT/NPT) sí suelen ser texto: extraerlos **con su posición** y emparejar
  nombre y cota por cercanía, no por el orden del texto.

## 2. Calibrar cada hoja con la grilla

1. `python tools/pdf-to-revit/pdf_grids.py plano.pdf out/burbujas.json` numera las burbujas.
2. Leer en la imagen el nombre de cada burbuja y las cotas entre ejes.
3. Escribir la configuración de la hoja (ver `sheet.py`): `grids` (metros, desde las cotas;
   iguales para todas las hojas del edificio), `grid_lines` (puntos de la hoja) y `x_along`
   (`sheet_y` si la planta está impresa girada 90°).
4. **Verificar el residuo**: `SheetMap(cfg).describe()` debe dar ≤ 1 cm. Si no, un nombre o una
   cota está mal leído.

Cada hoja tiene su propio origen: las hojas se alinean **por nombre de eje**, nunca por su
posición en el papel.

## 3. Extraer, siempre con revisión visual antes de tocar Revit

```
python pdf_walls.py        plano.pdf hoja.json out/muros_crudos.json out/muros_crudos.png
python pdf_walls_refine.py out/muros_crudos.json out/muros.json
python pdf_openings.py     plano.pdf hoja.json out/muros.json out/muros_unidos.json out/aberturas.json out/aberturas.png
python pdf_footprint.py    plano.pdf hoja.json out/muros.json out/contorno.json out/contorno.png
python pdf_columns.py      plano.pdf hoja.json
python pdf_review.py       plano.pdf hoja.json out/muros_unidos.json out/revision
```

Mirar **cada** PNG de superposición. Revisar a fondo al menos un departamento completo y el núcleo
con ampliaciones (≥ 450 dpi). Contrastar con los datos del plano (el área de planta rotulada
contra el contorno, el número de bloques de puerta contra las puertas detectadas).

Lo que suele fallar y cómo se detecta:

- **Muros de otro color.** Los departamentos centrales, las bodegas y los vestíbulos pueden venir
  en colores distintos a los tabiques. Correr `pdf_colors.py`, dibujar los pares detectados por
  color sobre el núcleo y agregar a `wall_colours` solo los que son muros (no los muebles).
- **Muros de una sola línea gruesa** (≈0.84 pt ≈ 0.10 m): `pdf_walls.py` ya los toma.
- **Encuentros.** `pdf_walls_refine.py` quita duplicados, cierra huecos colineales **por línea**
  y lleva los extremos al eje del muro perpendicular.
- **Puertas.** El marco sobre la línea del muro da el vano; la hoja perpendicular da el lado de
  apertura. Hay puertas en otro color (rojo) y con la hoja girada en ángulo. Contar los bloques
  de puerta del plano y compararlos con las puertas detectadas.
- **Ventanas.** Son vidrio (líneas finas) dentro de un vano de fachada. Las puertas de balcón son
  otra capa y suelen ir retranqueadas.

## 4. Generar el JSON y construir

- El generador del proyecto (ver `examples/torre/build_spec.py`) arma el spec en metros:
  `repeatOn` para la planta típica, niveles relativos `"+1"`, tipos por espesor o sección y
  `_decisions` con todo lo supuesto.
- Siempre primero `build_model_from_spec` con `dryRun: true` y `deleteMissing: true`: muestra qué
  se crearía, cambiaría o borraría, sin dejar nada.
- Para edificios grandes, pasar `specPath` y llamar con `node scripts/revit-call.mjs` (timeout
  1800): el cliente MCP normal puede quedarse corto.
- Documentos nuevos: crearlos con `Application.NewProjectDocument(plantilla)` y `SaveAs`, y
  apuntar siempre por `documentTitle`. **Nunca escribir en un documento que el usuario no
  autorizó.**

## 5. Verificar lo construido: nunca entregar sin mirar

- `export_view_image` de plantas de detalle (duplicar la planta y recortar a un departamento),
  cortes a lo largo de los ejes de vigas y 3D con caja de sección. Compararlos con el PDF.
- Aberturas: comprobar que el muro anfitrión las lista en `FindInserts` (y su volumen baja). Si no
  cortan, tocar el antepecho 1 mm y regenerar; el constructor ya lo hace y lo reporta.
- Vigas en rampas: en un corte por el eje, cada viga debe ser paralela a su losa.
- Puertas: verificar dos o tres bisagras contra el PDF antes de repetir la planta en 20 pisos.

## Criterios que el usuario ya fijó (aplicarlos sin volver a preguntar)

- No crear niveles que no existan en el plano, ni conservar los niveles de la plantilla que el
  plano no tiene.
- Preguntar siempre en opción múltiple, y solo cuando las lecturas posibles cambian el trabajo.
- Si el usuario plantea algo como prueba de capacidad, decidir de forma autónoma y explicar el
  porqué.

## Rampas de estacionamiento en medios niveles

Mitad oeste en el nivel y mitad este 1.50 abajo, unidas por una rampa sur (media planta) y una
rampa norte (hasta el nivel inferior): una hélice continua. Entrepisos altos se reparten en varias
vueltas con losas desfasadas, sin niveles nuevos. En el fondo de la hélice, columnas, núcleo y
contención arrancan 1.50 m más abajo. Las vigas a lo largo de una rampa siguen la rampa de punta
a punta, incluso en los ejes de borde.

## Cuando algo falla en Revit

- Timeouts: casi siempre hay un diálogo modal. Enumerar ventanas del proceso (`#32770`) y medir
  la CPU antes de reintentar.
- El diario (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <año>\Journals\journal.*.txt`)
  registra cada `ExternalEventExecution` y los avisos que el gestor de fallas descartó (por
  ejemplo "Can't cut instance out of Wall").
- Cambios grandes de muros sobre un modelo ya construido: es más seguro borrar los marcados por el
  spec y recrearlos que actualizarlos en su lugar (las uniones entre muros bloquean la
  actualización).
