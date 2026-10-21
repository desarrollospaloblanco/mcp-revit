using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Commands.Views;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    /// <summary>
    /// Moves the texts of horizontal and vertical dimension chains so they do not overlap each other
    /// or the dimension lines. Each text keeps its place when it is free; otherwise it tries sliding
    /// along its line, the other side of the line and a second tier, and takes the first free spot.
    /// TextPosition is the bottom centre of the text: above horizontal lines, left of vertical ones.
    /// </summary>
    public class ArrangeDimensionTextCommand : DelegateCommand
    {
        public override string CommandName => "arrange_dimension_text";

        public ArrangeDimensionTextCommand(UIApplication uiApp) : base(uiApp, "MCP: acomodar textos de cotas")
        {
        }

        private class Item
        {
            public Dimension Dim;
            public DimensionSegment Segment;
            public bool Horizontal;
            public double Along, Across;   // origin of the segment on the dimension line
            public double Width, Height;
            public int Side;               // +1 above / left, -1 below / right
            public double Value;
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var viewNames = (parameters?["views"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (viewNames == null || viewNames.Count == 0) throw new ArgumentException("views is required");
            double gapMm = parameters?["gapMm"]?.Value<double>() ?? 0.75;
            double padMm = parameters?["clearanceMm"]?.Value<double>() ?? 0.5;
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var report = new List<object>();
                ToolTransaction.Run(doc, "MCP: acomodar textos de cotas", options, warnings, () =>
                {
                    foreach (string name in viewNames)
                    {
                        View view = ExportViewsCommand.FindView(doc, name) ?? throw new InvalidOperationException("No view '" + name + "'");
                        report.Add(Arrange(doc, view, gapMm, padMm, dryRun));
                    }
                });
                if (dryRun) warnings.Add("dryRun: nothing was moved");
                return new { document = doc.Title, views = report, warnings };
            }, 600);
        }

        private static object Arrange(Document doc, View view, double gapMm, double padMm, bool dryRun)
        {
            double scale = view.Scale, gap = gapMm / 304.8 * scale, pad = padMm / 304.8 * scale;
            var items = new List<Item>();
            var obstacles = new List<double[]>();

            foreach (Dimension dim in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                if (dim is SpotDimension || !(dim.Curve is Line line)) continue;
                XYZ u = line.Direction.Normalize();
                bool horizontal = Math.Abs(u.Y) < 1e-6, vertical = Math.Abs(u.X) < 1e-6;
                if (!horizontal && !vertical) continue;
                var type = doc.GetElement(dim.GetTypeId()) as DimensionType;
                double h = (type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 2.5 / 304.8) * scale;
                double wf = type?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1;

                double lo = double.MaxValue, hi = double.MinValue;
                void Add(DimensionSegment seg, XYZ origin, XYZ textPos, double? value, string text)
                {
                    if (value == null) return;
                    double along = horizontal ? origin.X : origin.Y, across = horizontal ? origin.Y : origin.X;
                    double tAcross = horizontal ? textPos.Y : textPos.X;
                    int side = horizontal ? (tAcross >= across ? 1 : -1) : (tAcross <= across ? 1 : -1);
                    items.Add(new Item
                    {
                        Dim = dim, Segment = seg, Horizontal = horizontal, Along = along, Across = across,
                        Height = h, Width = TextWidth(text, h, wf), Side = side, Value = value.Value
                    });
                    lo = Math.Min(lo, along - value.Value / 2);
                    hi = Math.Max(hi, along + value.Value / 2);
                }
                if (dim.NumberOfSegments == 0) Add(null, dim.Origin, dim.TextPosition, dim.Value, dim.ValueString);
                else foreach (DimensionSegment s in dim.Segments) Add(s, s.Origin, s.TextPosition, s.Value, s.ValueString);
                if (lo == double.MaxValue) continue;

                double across0 = horizontal ? line.Origin.Y : line.Origin.X, thin = 0.1 / 304.8 * scale;
                obstacles.Add(horizontal ? new[] { lo, across0 - thin, hi, across0 + thin } : new[] { across0 - thin, lo, across0 + thin, hi });
            }

            BoundingBoxXYZ crop = view.CropBoxActive ? view.CropBox : null;
            var placed = new List<double[]>();
            int kept = 0, moved = 0, unresolved = 0;
            // Wide segments first: they have the fewest alternatives worth taking.
            foreach (Item it in items.OrderByDescending(i => i.Value))
            {
                var candidates = new List<(double shift, int side, int tier)> { (0, it.Side, 0) };
                double step = it.Width / 2 + it.Value / 2 + gap;
                for (int k = 1; k <= 3; k++)
                {
                    candidates.Add((step * k, it.Side, 0));
                    candidates.Add((-step * k, it.Side, 0));
                }
                candidates.Add((0, -it.Side, 0));
                candidates.Add((0, it.Side, 1));
                candidates.Add((step, -it.Side, 0));
                candidates.Add((-step, -it.Side, 0));

                bool done = false;
                foreach (var c in candidates)
                {
                    var (pos, box) = Place(it, c.shift, c.side, c.tier, gap, pad);
                    if (Hits(box, placed, obstacles, crop)) continue;
                    placed.Add(box);
                    if (c.shift == 0 && c.side == it.Side && c.tier == 0) kept++;
                    else
                    {
                        moved++;
                        if (!dryRun) SetTextPosition(it, pos);
                    }
                    done = true;
                    break;
                }
                if (!done)
                {
                    unresolved++;
                    placed.Add(Place(it, 0, it.Side, 0, gap, pad).box);
                }
            }
            return new { view = view.Name, texts = items.Count, kept, moved, unresolved };
        }

        /// <summary>Approximate text width from character classes, like the default Arial-like fonts.</summary>
        private static double TextWidth(string text, double height, double widthFactor)
        {
            double em = height / 0.716, w = 0;
            foreach (char ch in text ?? "") w += ch == '.' || ch == ',' ? 0.278 : 0.556;
            return w * em * widthFactor;
        }

        private static (XYZ pos, double[] box) Place(Item it, double shift, int side, int tier, double gap, double pad)
        {
            double off = gap + tier * (it.Height * 1.4);
            double u = it.Along + shift, c = it.Across;
            XYZ pos;
            double[] b;
            if (it.Horizontal)
            {
                double y = side > 0 ? c + off : c - off - it.Height;
                pos = new XYZ(u, y, 0);
                b = new[] { u - it.Width / 2, y, u + it.Width / 2, y + it.Height };
            }
            else
            {
                double x = side > 0 ? c - off : c + off + it.Height;
                pos = new XYZ(x, u, 0);
                b = new[] { x - it.Height, u - it.Width / 2, x, u + it.Width / 2 };
            }
            return (pos, new[] { b[0] - pad, b[1] - pad, b[2] + pad, b[3] + pad });
        }

        private static bool Hits(double[] b, List<double[]> placed, List<double[]> obstacles, BoundingBoxXYZ crop)
        {
            if (crop != null && (b[0] < crop.Min.X || b[1] < crop.Min.Y || b[2] > crop.Max.X || b[3] > crop.Max.Y)) return true;
            foreach (double[] q in placed.Concat(obstacles))
                if (b[0] < q[2] && q[0] < b[2] && b[1] < q[3] && q[1] < b[3]) return true;
            return false;
        }

        private static void SetTextPosition(Item it, XYZ pos)
        {
            if (it.Segment != null)
            {
                if (!it.Segment.IsTextPositionAdjustable()) return;
                it.Segment.TextPosition = new XYZ(pos.X, pos.Y, it.Segment.TextPosition.Z);
            }
            else if (it.Dim.IsTextPositionAdjustable())
            {
                it.Dim.TextPosition = new XYZ(pos.X, pos.Y, it.Dim.TextPosition.Z);
            }
        }
    }
}
