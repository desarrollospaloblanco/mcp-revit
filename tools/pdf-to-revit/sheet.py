"""Sheet-to-model mapping for a plan printed to PDF, calibrated on its grid lines.

A plan printed to PDF (for example with "Microsoft Print to PDF") keeps its vectors but loses its
scale and layers. The scale comes back from the grid: each grid line's position on the sheet is
paired with its model coordinate taken from the written dimensions, and a least-squares fit per
axis gives the transform and its residual. A residual of a centimetre or less means the grid
names and dimensions were read correctly.

Each sheet has its own configuration file:

    {
      "grids":      {"x": {"B": 0.0, "C": 8.40, ...}, "y": {"6": 0.0, "5": 7.75, ...}},
      "grid_lines": {"x": {"B": 270.96, "C": 338.40, ...}, "y": {"6": 222.84, ...}},
      "x_along":    "sheet_y"
    }

"grids" are model coordinates in metres (the same for every sheet of a building, so sheets align
by grid name, not by where the plan sits on the paper). "grid_lines" are the sheet coordinates of
the grid lines drawn on this sheet, in PDF points. "x_along" says which sheet axis the model X
runs along: "sheet_x" for a plan printed upright, "sheet_y" for one printed rotated 90 degrees.

Older configurations with "letter_y" and "number_x" are read as x_along = sheet_y.
"""
import numpy as np


def fit(sheet_values, model_values):
    """Least-squares a * s + b, with the largest residual in metres."""
    a, b = np.polyfit(sheet_values, model_values, 1)
    residual = np.array(model_values) - (a * np.array(sheet_values) + b)
    return a, b, float(np.abs(residual).max())


class SheetMap:
    def __init__(self, cfg):
        if "grid_lines" in cfg:
            lines_x, lines_y = cfg["grid_lines"]["x"], cfg["grid_lines"]["y"]
            self.x_along = cfg.get("x_along", "sheet_x")
        else:  # older format: lettered grids along sheet y, numbered along sheet x
            lines_x, lines_y = cfg["letter_y"], cfg["number_x"]
            self.x_along = "sheet_y"
        grids_x, grids_y = cfg["grids"]["x"], cfg["grids"]["y"]
        kx = [k for k in grids_x if k in lines_x]
        ky = [k for k in grids_y if k in lines_y]
        if len(kx) < 2 or len(ky) < 2:
            raise ValueError("each axis needs at least two grid lines found on the sheet")
        self.ax, self.bx, self.rx = fit([lines_x[k] for k in kx], [grids_x[k] for k in kx])
        self.ay, self.by, self.ry = fit([lines_y[k] for k in ky], [grids_y[k] for k in ky])

    def to_model(self, px, py):
        """Sheet point (PDF points) to model (X, Y) in metres."""
        sx, sy = (py, px) if self.x_along == "sheet_y" else (px, py)
        return self.ax * sx + self.bx, self.ay * sy + self.by

    def to_sheet(self, X, Y):
        """Model (X, Y) in metres back to a sheet point, for drawing overlays."""
        sx, sy = (X - self.bx) / self.ax, (Y - self.by) / self.ay
        return (sy, sx) if self.x_along == "sheet_y" else (sx, sy)

    def length(self, pt):
        """A sheet length in points, in metres."""
        return abs(self.ax) * pt

    def describe(self):
        return (f"X residual {self.rx * 100:.1f} cm, Y residual {self.ry * 100:.1f} cm, "
                f"{1 / abs(self.ax):.3f} pt/m, print scale 1:{abs(self.ax) / 0.3528e-3:.0f}")
