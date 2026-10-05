# pdf-to-revit

Turns plan sheets printed to PDF into a JSON spec that `build_model_from_spec` builds into Revit:
grids, columns, walls with their doors and windows, slab outlines. It works on the PDF's vectors,
not on an image, so positions come out to the centimetre once the sheet is calibrated on its grid.

```
pip install -r requirements.txt
```

## The pipeline

| Step | Script | Output |
| --- | --- | --- |
| Learn the drawing's layers | `pdf_colors.py plan.pdf` | paths grouped by colour, width and kind |
| Find the grid bubbles | `pdf_grids.py plan.pdf out/bubbles.json` | bubble centres, and a PNG with them numbered |
| Calibrate the sheet | a sheet configuration, see `sheet.py` | residual per axis, should be ≤ 1 cm |
| Walls | `pdf_walls.py plan.pdf sheet.json out/walls_raw.json out/walls_raw.png` | wall pieces as centreline, thickness, layer |
| Wall network | `pdf_walls_refine.py out/walls_raw.json out/walls.json` | duplicates removed, gaps closed, junctions met |
| Doors and windows | `pdf_openings.py plan.pdf sheet.json out/walls.json out/walls_bridged.json out/openings.json out/openings.png` | openings, and walls bridged across them |
| Slab outline | `pdf_footprint.py plan.pdf sheet.json out/walls.json\|- out/outline.json out/outline.png` | orthogonal outline polygon |
| Columns | `pdf_columns.py plan.pdf sheet.json` | column footprints snapped to grid intersections |
| Review | `pdf_review.py plan.pdf sheet.json out/walls_bridged.json out/review` | walls drawn to thickness over the sheet, per zone |

A project generator then assembles the spec: levels from the elevation, which floors repeat, types,
structure, and a `_decisions` list with everything the drawings do not settle.
`examples/torre/build_spec.py` is a complete one (a 30-level tower with a parking helix); run it
with `examples/torre/run.ps1`.

## Sheet configuration

```json
{
  "grids":      {"x": {"B": 0.0, "C": 8.40}, "y": {"6": 0.0, "5": 7.75}},
  "grid_lines": {"x": {"B": 270.96, "C": 338.40}, "y": {"6": 222.84, "5": 285.12}},
  "x_along":    "sheet_y",
  "wall_colours": {"exterior": [[0, 1, 1]], "interior": [[0, 1, 0]]},
  "column_fills": [[0.5, 0, 0]],
  "opening_colours": {"door": [0.87, 0.43, 0], "glass": [0.5, 0.75, 1]},
  "footprint_colours": [],
  "review_zones": {"one_apartment": [-1.5, -1.5, 8.8, 8.8]}
}
```

- `grids` are model metres from the written dimensions, the same for every sheet of a building, so
  sheets align by grid name and not by where the plan sits on the paper.
- `grid_lines` are the sheet coordinates (PDF points) of the grid lines drawn on this sheet.
- `x_along` is `sheet_y` for a plan printed rotated 90 degrees, `sheet_x` otherwise.
- Colours are `[r, g, b]` in 0..1, as `pdf_colors.py` prints them.

## Lessons that are built in

- Walls come as pairs of parallel lines **and** as single heavy lines (the line weight is the
  thickness); both are read.
- Gaps between collinear pieces are closed per line. Grouping on a rounded coordinate instead lets
  a piece at one end of the building swallow pieces at the other.
- An opening is only cut through a wall when the drawing shows why it is open: a door frame on the
  wall line, a door leaf hinged at a jamb, or glazing in the gap.
- Outlines are orthogonalised and start at their lowest-left vertex, so ids numbered along them
  are stable across runs.
