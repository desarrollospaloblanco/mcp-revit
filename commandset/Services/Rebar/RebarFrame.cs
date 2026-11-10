namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// A horizontal frame on a concrete element, in metres: U along it (beam axis, column or footing
    /// X), V across it, Z up, and the element's solid measured in that frame. Bars are laid out with
    /// P(s, off, z): s along U from the start of the solid, off across from its centre line, z absolute.
    /// </summary>
    public class RebarFrame
    {
        public XYZ Origin { get; private set; }
        public XYZ U { get; private set; }
        public XYZ V { get; private set; }
        public double Length { get; private set; }
        public double Width { get; private set; }
        public double Bottom { get; private set; }
        public double Top { get; private set; }
        public double Height => Top - Bottom;

        public XYZ P(double s, double off, double z) => new XYZ(Origin.X + U.X * s + V.X * off, Origin.Y + U.Y * s + V.Y * off, z);

        /// <summary>The middle of the solid in plan, at z.</summary>
        public XYZ Centre(double z) => P(Length / 2, 0, z);

        /// <summary>Frame of an element with a straight location line (beam, strip footing, wall) or a location point (column, footing).</summary>
        public static RebarFrame Of(Element e)
        {
            XYZ u;
            if (e.Location is LocationCurve lc && lc.Curve is Line line)
                u = new XYZ(line.Direction.X, line.Direction.Y, 0);
            else if (e is WallFoundation wf && e.Document.GetElement(wf.WallId)?.Location is LocationCurve wl && wl.Curve is Line wline)
                u = new XYZ(wline.Direction.X, wline.Direction.Y, 0);
            else if (e is FamilyInstance fi)
                u = new XYZ(fi.GetTransform().BasisX.X, fi.GetTransform().BasisX.Y, 0);
            else
                u = XYZ.BasisX;
            if (u.GetLength() < 1e-6) u = XYZ.BasisX;
            u = u.Normalize();
            XYZ v = XYZ.BasisZ.CrossProduct(u).Normalize();

            var points = Vertices(e);
            if (points.Count == 0) throw new InvalidOperationException("element " + e.Id + " has no solid geometry");
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (XYZ p in points)
            {
                double a = p.X * u.X + p.Y * u.Y, b = p.X * v.X + p.Y * v.Y;
                minU = Math.Min(minU, a); maxU = Math.Max(maxU, a);
                minV = Math.Min(minV, b); maxV = Math.Max(maxV, b);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            double midV = (minV + maxV) / 2;
            return new RebarFrame
            {
                Origin = new XYZ(u.X * minU + v.X * midV, u.Y * minU + v.Y * midV, 0),
                U = u,
                V = v,
                Length = maxU - minU,
                Width = maxV - minV,
                Bottom = minZ,
                Top = maxZ
            };
        }

        /// <summary>
        /// Vertices of the element's solids in metres, instance geometry included. Family instances add their
        /// geometry from before joins and cuts: a lintel inside a wall or a beam under a slab can lose most of
        /// its solid to the join, and the frame must still measure the whole section.
        /// </summary>
        public static List<XYZ> Vertices(Element e)
        {
            var points = new List<XYZ>();
            foreach (Solid s in Solids(e))
                AddVertices(s, Transform.Identity, points);
            if (e is FamilyInstance fi)
            {
                GeometryElement original = fi.GetOriginalGeometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (original != null) CollectOriginal(original, fi.GetTotalTransform(), points);
            }
            return points;
        }

        private static void CollectOriginal(GeometryElement geom, Transform transform, List<XYZ> points)
        {
            foreach (GeometryObject o in geom)
            {
                if (o is Solid s && s.Volume > 1e-6) AddVertices(s, transform, points);
                else if (o is GeometryInstance gi) CollectOriginal(gi.GetSymbolGeometry(), transform.Multiply(gi.Transform), points);
            }
        }

        private static void AddVertices(Solid s, Transform transform, List<XYZ> points)
        {
            foreach (Edge edge in s.Edges)
                foreach (XYZ p in edge.Tessellate())
                    points.Add(transform.OfPoint(p) * 0.3048);
        }

        public static List<Solid> Solids(Element e)
        {
            var result = new List<Solid>();
            GeometryElement geom = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });
            if (geom == null) return result;
            Collect(geom, result);
            return result;
        }

        private static void Collect(GeometryElement geom, List<Solid> result)
        {
            foreach (GeometryObject o in geom)
            {
                if (o is Solid s && s.Volume > 1e-6) result.Add(s);
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), result);
            }
        }

        /// <summary>The planar face whose normal is closest to the direction (largest one on ties).</summary>
        public static PlanarFace FaceToward(Element e, XYZ direction)
        {
            PlanarFace best = null;
            double bestDot = 0.99, bestArea = 0;
            foreach (Solid s in Solids(e))
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    double dot = pf.FaceNormal.DotProduct(direction);
                    if (dot > bestDot + 1e-6 || (Math.Abs(dot - bestDot) <= 1e-6 && pf.Area > bestArea))
                    {
                        best = pf; bestDot = dot; bestArea = pf.Area;
                    }
                }
            return best;
        }

        /// <summary>Area in square metres of planar loops (outline minus holes), Newell's method.</summary>
        public static double LoopArea(List<List<XYZ>> loops)
        {
            double total = 0;
            for (int k = 0; k < loops.Count; k++)
            {
                var l = loops[k];
                XYZ n = XYZ.Zero;
                for (int i = 0; i < l.Count; i++)
                {
                    XYZ a = l[i], b = l[(i + 1) % l.Count];
                    n += new XYZ((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                }
                double area = n.GetLength() / 2;
                total += k == 0 ? area : -area;
            }
            return Math.Max(0, total);
        }
    }
}
