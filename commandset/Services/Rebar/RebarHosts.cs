using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// The concrete of the model as boxes in metres: finds the host of a bar when none is given and
    /// the joints a bar crosses (columns, walls and beams across a horizontal bar; slabs and beams
    /// across a vertical one), which become the faces its splices must keep away from.
    /// </summary>
    public class RebarHosts
    {
        private class Box
        {
            public Element Element;
            public BuiltInCategory Category;
            public XYZ Min, Max;
            public double Volume => (Max.X - Min.X) * (Max.Y - Min.Y) * (Max.Z - Min.Z);
        }

        private static readonly BuiltInCategory[] HostCategories =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Stairs
        };

        private readonly List<Box> _boxes = new List<Box>();

        public RebarHosts(Document doc)
        {
            foreach (BuiltInCategory bic in HostCategories)
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null) continue;
                    _boxes.Add(new Box { Element = e, Category = bic, Min = b.Min * 0.3048, Max = b.Max * 0.3048 });
                }
        }

        /// <summary>The smallest valid rebar host whose box holds the middle of the bar's longest segment.</summary>
        public Element HostFor(BarSpec spec)
        {
            XYZ mid = Middle(spec.Points);
            return _boxes.Where(b => Contains(b, mid, 0.03) && IsHost(b.Element))
                .OrderBy(b => b.Volume).Select(b => b.Element).FirstOrDefault();
        }

        public Element HostAt(XYZ point) =>
            _boxes.Where(b => Contains(b, point, 0.03) && IsHost(b.Element)).OrderBy(b => b.Volume).Select(b => b.Element).FirstOrDefault();

        public static bool IsHost(Element e)
        {
            try { return RebarHostData.IsValidHost(e); }
            catch (Exception) { return false; }
        }

        /// <summary>Splice zones of a bar from the joints its longest segment crosses.</summary>
        public List<double[]> Zones(BarSpec spec, Element host)
        {
            var pts = spec.Points;
            int k = 0;
            double best = -1;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double l = pts[i].DistanceTo(pts[i + 1]);
                if (l > best) { best = l; k = i; }
            }
            XYZ a = pts[k], b = pts[k + 1];
            double length = a.DistanceTo(b);
            bool vertical = Math.Abs((b - a).Normalize().Z) > 0.7;
            var joints = new List<double[]>();
            foreach (Box box in _boxes)
            {
                if (host != null && box.Element.Id == host.Id) continue;
                bool support = vertical
                    ? box.Category == BuiltInCategory.OST_Floors || box.Category == BuiltInCategory.OST_StructuralFraming
                      || box.Category == BuiltInCategory.OST_StructuralFoundation
                    : box.Category != BuiltInCategory.OST_Floors && box.Category != BuiltInCategory.OST_Stairs;
                if (!support || !Clip(box, a, b, out double t0, out double t1)) continue;
                double span = (t1 - t0) * length;
                if (span < 0.02 || span > 0.8 * length) continue;   // a box along the whole bar is a host-like neighbour, not a joint
                joints.Add(new[] { t0 * length, t1 * length });
            }
            double depth = 0;
            if (!vertical && host != null && host.Category?.Id.GetValue() == (long)BuiltInCategory.OST_StructuralFraming)
            {
                Box hb = _boxes.FirstOrDefault(x => x.Element.Id == host.Id);
                if (hb != null) depth = hb.Max.Z - hb.Min.Z;
            }
            return RebarSplicer.ZonesBetween(joints, length, depth);
        }

        private static XYZ Middle(List<XYZ> pts)
        {
            int k = 0;
            double best = -1;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double l = pts[i].DistanceTo(pts[i + 1]);
                if (l > best) { best = l; k = i; }
            }
            return pts.Count == 1 ? pts[0] : (pts[k] + pts[k + 1]) / 2;
        }

        private static bool Contains(Box b, XYZ p, double tol) =>
            p.X >= b.Min.X - tol && p.X <= b.Max.X + tol && p.Y >= b.Min.Y - tol && p.Y <= b.Max.Y + tol && p.Z >= b.Min.Z - tol && p.Z <= b.Max.Z + tol;

        /// <summary>Segment a-b against the box (slab method); t0..t1 as fractions of the segment.</summary>
        private static bool Clip(Box box, XYZ a, XYZ b, out double t0, out double t1)
        {
            t0 = 0; t1 = 1;
            double[] p = { a.X, a.Y, a.Z }, d = { b.X - a.X, b.Y - a.Y, b.Z - a.Z };
            double[] lo = { box.Min.X, box.Min.Y, box.Min.Z }, hi = { box.Max.X, box.Max.Y, box.Max.Z };
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-9)
                {
                    if (p[i] < lo[i] || p[i] > hi[i]) return false;
                    continue;
                }
                double u0 = (lo[i] - p[i]) / d[i], u1 = (hi[i] - p[i]) / d[i];
                if (u0 > u1) { double s = u0; u0 = u1; u1 = s; }
                t0 = Math.Max(t0, u0);
                t1 = Math.Min(t1, u1);
                if (t0 >= t1) return false;
            }
            return true;
        }
    }
}
