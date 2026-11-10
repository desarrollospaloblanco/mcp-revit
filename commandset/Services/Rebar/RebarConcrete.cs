using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// The concrete of the model as solids (walls, slabs, beams, columns, foundations and stairs), to measure
    /// how much of a bar line runs inside it: where a beam bar can anchor past the end of the beam, how long a
    /// hook leg fits, and how much of each bar is left outside the concrete. Points are in metres; solids are
    /// read the first time a line comes near them.
    /// </summary>
    public class RebarConcrete
    {
        public static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Stairs
        };

        /// <summary>What can receive a beam bar past the end of the beam: a wall, a column or another beam.</summary>
        public static readonly BuiltInCategory[] Supports =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming
        };

        private const double Gap = 0.003;   // joined faces may leave hairline gaps between two solids

        private class Item
        {
            public Element Element;
            public BuiltInCategory Category;
            public XYZ Min, Max;
            public List<Solid> Solids;
        }

        private readonly List<Item> _items = new List<Item>();

        /// <summary>Solids Revit could not intersect with a line; they count as no concrete.</summary>
        public int FailedSolids { get; private set; }

        public RebarConcrete(Document doc)
        {
            foreach (BuiltInCategory bic in Categories)
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null) continue;
                    _items.Add(new Item { Element = e, Category = bic, Min = b.Min * 0.3048, Max = b.Max * 0.3048 });
                }
        }

        /// <summary>Length in metres of the segment a-b inside the concrete of those categories (all when null).</summary>
        public double InsideLength(XYZ a, XYZ b, ICollection<BuiltInCategory> categories = null)
        {
            double length = a.DistanceTo(b);
            if (length < 1e-6) return 0;
            return Merge(Intervals(a, b, categories), 0).Sum(i => i.t1 - i.t0) * length;
        }

        /// <summary>
        /// How far the concrete of those categories runs from p along the direction without a break, up to max
        /// metres: 0 when p is not in (or on the face of) that concrete.
        /// </summary>
        public double ContinuousFrom(XYZ p, XYZ direction, double max, ICollection<BuiltInCategory> categories = null)
        {
            XYZ q = p + direction.Normalize() * max;
            var merged = Merge(Intervals(p, q, categories), Gap / max);
            if (merged.Count == 0 || merged[0].t0 * max > Gap) return 0;
            return merged[0].t1 * max;
        }

        private List<(double t0, double t1)> Intervals(XYZ a, XYZ b, ICollection<BuiltInCategory> categories)
        {
            var result = new List<(double, double)>();
            double length = a.DistanceTo(b);
            XYZ lo = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
            XYZ hi = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
            Line line = null;
            foreach (Item item in _items)
            {
                if (categories != null && !categories.Contains(item.Category)) continue;
                if (item.Max.X < lo.X - 0.01 || item.Min.X > hi.X + 0.01 || item.Max.Y < lo.Y - 0.01 || item.Min.Y > hi.Y + 0.01
                    || item.Max.Z < lo.Z - 0.01 || item.Min.Z > hi.Z + 0.01) continue;
                if (item.Solids == null) item.Solids = RebarFrame.Solids(item.Element);
                if (line == null) line = Line.CreateBound(a * Metric.FeetPerMeter, b * Metric.FeetPerMeter);
                XYZ dir = (b - a) / length;
                foreach (Solid s in item.Solids)
                {
                    SolidCurveIntersection hit;
                    try
                    {
                        hit = s.IntersectWithCurve(line, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                    {
                        FailedSolids++;
                        continue;
                    }
                    for (int i = 0; i < hit.SegmentCount; i++)
                    {
                        Curve seg = hit.GetCurveSegment(i);
                        double t0 = (seg.GetEndPoint(0) * 0.3048 - a).DotProduct(dir) / length;
                        double t1 = (seg.GetEndPoint(1) * 0.3048 - a).DotProduct(dir) / length;
                        if (t0 > t1) { double t = t0; t0 = t1; t1 = t; }
                        result.Add((Math.Max(0, t0), Math.Min(1, t1)));
                    }
                }
            }
            return result;
        }

        /// <summary>Union of the intervals, closing gaps up to gap (a fraction of the segment).</summary>
        private static List<(double t0, double t1)> Merge(List<(double t0, double t1)> intervals, double gap)
        {
            var merged = new List<(double t0, double t1)>();
            foreach (var i in intervals.Where(i => i.t1 > i.t0).OrderBy(i => i.t0))
            {
                if (merged.Count > 0 && i.t0 <= merged[merged.Count - 1].t1 + gap + 1e-9)
                    merged[merged.Count - 1] = (merged[merged.Count - 1].t0, Math.Max(merged[merged.Count - 1].t1, i.t1));
                else
                    merged.Add(i);
            }
            return merged;
        }
    }
}
