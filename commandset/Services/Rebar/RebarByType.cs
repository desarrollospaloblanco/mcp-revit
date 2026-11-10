using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// Builds bar sets and meshes from rules keyed by type mark or type name, reading each element's
    /// solid: columns (longitudinal bars with hooks into the footing, ties and cross-ties), beams and
    /// tie beams (top, middle and bottom bars hooked at the ends, closed stirrups or links), isolated
    /// footings (two bottom mats), strip footings (longitudinal and transverse U bars) and welded
    /// mesh on walls and slabs. The result goes through RebarPlacer like hand-made sets.
    /// </summary>
    public class RebarByType
    {
        private readonly Document _doc;
        private readonly RebarCatalog _cat;
        private readonly List<Element> _foundations;
        private RebarConcrete _concrete;

        /// <summary>The concrete of the model, read on first use; the commands reuse it for the inside-concrete check.</summary>
        public RebarConcrete Concrete => _concrete ?? (_concrete = new RebarConcrete(_doc));

        public List<BarSpec> Bars { get; } = new List<BarSpec>();
        public List<MeshSpec> Meshes { get; } = new List<MeshSpec>();
        public List<object> Matched { get; } = new List<object>();
        public List<string> Notes { get; } = new List<string>();
        public Dictionary<ElementId, double> HostCovers { get; } = new Dictionary<ElementId, double>();

        public RebarByType(Document doc, RebarCatalog catalog)
        {
            _doc = doc;
            _cat = catalog;
            _foundations = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType().ToList();
        }

        public static BuiltInCategory[] DefaultCategories(string kind)
        {
            switch (kind)
            {
                case "column": return new[] { BuiltInCategory.OST_StructuralColumns };
                case "beam": return new[] { BuiltInCategory.OST_StructuralFraming };
                case "footing": return new[] { BuiltInCategory.OST_StructuralFoundation };
                case "stripfooting": return new[] { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralFraming };
                case "wallmesh": return new[] { BuiltInCategory.OST_Walls };
                case "slabmesh": return new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFoundation };
                default: throw new ArgumentException("rule kind must be column, beam, footing, stripFooting, wallMesh or slabMesh");
            }
        }

        public static bool Matches(Element e, string match)
        {
            if (string.IsNullOrWhiteSpace(match)) return true;
            Element type = e.Document.GetElement(e.GetTypeId());
            string typeMark = type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? "";
            string typeName = type?.Name ?? "";
            string family = (type as ElementType)?.FamilyName ?? "";
            return string.Equals(typeMark, match, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(typeName, match, StringComparison.OrdinalIgnoreCase)
                   || typeName.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0
                   || family.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void Build(Element e, JObject rule)
        {
            string kind = (rule["kind"]?.Value<string>() ?? "").ToLowerInvariant();
            string match = rule["match"]?.Value<string>() ?? "";
            string mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            string label = (rule["label"]?.Value<string>() ?? DefaultLabel(kind)) + " " + (string.IsNullOrWhiteSpace(mark) ? e.Id.GetValue().ToString() : mark);
            string group = rule["group"]?.Value<string>() ?? DefaultLabel(kind);
            int before = Bars.Count, meshesBefore = Meshes.Count;
            var ctx = new Ctx { Element = e, Label = label, Group = group, Tag = match, Rule = rule };
            switch (kind)
            {
                case "column": Column(ctx); break;
                case "beam": Beam(ctx); break;
                case "footing": Footing(ctx); break;
                case "stripfooting": StripFooting(ctx); break;
                case "wallmesh": WallMesh(ctx); break;
                case "slabmesh": SlabMesh(ctx); break;
                default: throw new ArgumentException("unknown rule kind " + kind);
            }
            if (rule["hostCover"] != null) HostCovers[e.Id] = rule["hostCover"].Value<double>();
            Matched.Add(new { id = e.Id.GetValue(), element = label, rule = match, kind, sets = Bars.Count - before, meshes = Meshes.Count - meshesBefore });
        }

        private static string DefaultLabel(string kind)
        {
            switch (kind)
            {
                case "column": return "Columna";
                case "beam": return "Viga";
                case "footing": return "Zapata";
                case "stripfooting": return "Cimiento corrido";
                case "wallmesh": return "Muro";
                default: return "Losa";
            }
        }

        private class Ctx
        {
            public Element Element;
            public string Label, Group, Tag;
            public JObject Rule;
        }

        private class Cover
        {
            public double Top, Bottom, Side, Ends;

            public static Cover From(JToken t, double top, double bottom, double side, double ends)
            {
                if (t == null) return new Cover { Top = top, Bottom = bottom, Side = side, Ends = ends };
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                {
                    double c = t.Value<double>();
                    return new Cover { Top = c, Bottom = c, Side = c, Ends = c };
                }
                return new Cover
                {
                    Top = t["top"]?.Value<double>() ?? top,
                    Bottom = t["bottom"]?.Value<double>() ?? bottom,
                    Side = t["side"]?.Value<double>() ?? side,
                    Ends = t["ends"]?.Value<double>() ?? ends
                };
            }
        }

        private static double Db(string size) => RebarCatalog.DiameterMm(size) / 1000;

        private void Add(Ctx c, string description, string size, List<XYZ> pts, XYZ normal, int count, double spacing, bool stirrup = false)
        {
            var spec = new BarSpec
            {
                Element = c.Label,
                Group = c.Group,
                Description = description,
                Size = size,
                Points = pts,
                Normal = normal.Normalize(),
                Count = Math.Max(1, count),
                Spacing = count > 1 ? spacing : 0,
                Stirrup = stirrup,
                HostId = c.Element.Id
            };
            spec.Simplify();
            if (spec.Points.Count >= 2 && spec.Length > 0.02) Bars.Add(spec);
        }

        private static (string size, int count) BarCount(JToken t, string what)
        {
            if (t == null) throw new ArgumentException(what + " is required");
            return (RebarCatalog.Key(t["bar"]?.Value<string>() ?? throw new ArgumentException(what + ".bar is required")), t["count"]?.Value<int>() ?? 2);
        }

        private static (string size, double spacing) BarSpacing(JToken t, string what, double defaultSpacing)
        {
            if (t == null) throw new ArgumentException(what + " is required");
            return (RebarCatalog.Key(t["bar"]?.Value<string>() ?? throw new ArgumentException(what + ".bar is required")), t["spacing"]?.Value<double>() ?? defaultSpacing);
        }

        private static int CountFor(double length, double spacing) => Math.Max(1, (int)Math.Floor(length / spacing + 1e-6) + 1);

        // ------------------------------------------------------------------ columns

        private void Column(Ctx c)
        {
            RebarFrame f = RebarFrame.Of(c.Element);
            JObject r = c.Rule;
            var (size, count) = BarCount(r["longitudinal"], "longitudinal");
            var (tie, tieSpacing) = BarSpacing(r["ties"], "ties", 0.10);
            Cover cov = Cover.From(r["cover"], 0.025, 0.025, 0.025, 0.025);
            int perX = r["longitudinal"]["perFaceX"]?.Value<int>() ?? 0, perY = r["longitudinal"]["perFaceY"]?.Value<int>() ?? 0;
            if (perX < 2 || perY < 2)
            {
                if (count < 4 || count % 2 == 1) throw new ArgumentException($"{c.Label}: column bars must be an even count of 4 or more, or give perFaceX/perFaceY");
                int extra = (count - 4) / 2;
                int ex = (int)Math.Round(extra * f.Length / (f.Length + f.Width), MidpointRounding.AwayFromZero);
                perX = 2 + ex;
                perY = 2 + extra - ex;
            }
            double ax = f.Length / 2 - cov.Side - Db(tie) - Db(size) / 2, ay = f.Width / 2 - cov.Side - Db(tie) - Db(size) / 2;
            var positions = new List<(double x, double y)>();
            for (int i = 0; i < perX; i++)
            {
                double x = -ax + 2 * ax * i / (perX - 1);
                positions.Add((x, -ay));
                positions.Add((x, ay));
            }
            for (int j = 1; j < perY - 1; j++)
            {
                double y = -ay + 2 * ay * j / (perY - 1);
                positions.Add((-ax, y));
                positions.Add((ax, y));
            }

            double zb = BottomOfColumnBars(c, f);
            double zt = f.Top - cov.Top;
            string hooks = (r["hooks"]?.Value<string>() ?? "both").ToLowerInvariant();
            double hook = r["hook"]?.Value<double>() ?? _cat.Hook(size);
            int total = positions.Count;
            foreach (var (x, y) in positions)
            {
                // hook legs point to the centre, along the longer offset
                bool alongX = Math.Abs(x) / Math.Max(ax, 1e-6) >= Math.Abs(y) / Math.Max(ay, 1e-6);
                double hx = alongX ? -Math.Sign(x) * hook : 0, hy = alongX ? 0 : -Math.Sign(y) * hook;
                var pts = new List<XYZ>();
                if (hooks == "both" || hooks == "bottom") pts.Add(f.P(f.Length / 2 + x + hx, y + hy, zb));
                pts.Add(f.P(f.Length / 2 + x, y, zb));
                pts.Add(f.P(f.Length / 2 + x, y, zt));
                if (hooks == "both" || hooks == "top") pts.Add(f.P(f.Length / 2 + x + hx, y + hy, zt));
                XYZ normal = alongX ? f.V : f.U;
                Add(c, $"{total}{size} longitudinal ({c.Tag})", size, pts, normal, 1, 0);
            }

            double start = f.Bottom + (r["ties"]["start"]?.Value<double>() ?? 0.05);
            double end = f.Top - (r["ties"]["start"]?.Value<double>() ?? 0.05);
            int n = CountFor(end - start, tieSpacing);
            double ex2 = f.Length / 2 - cov.Side, ey2 = f.Width / 2 - cov.Side;
            var loop = new List<XYZ>
            {
                f.P(f.Length / 2 - ex2, -ey2, start), f.P(f.Length / 2 + ex2, -ey2, start), f.P(f.Length / 2 + ex2, ey2, start),
                f.P(f.Length / 2 - ex2, ey2, start), f.P(f.Length / 2 - ex2, -ey2, start)
            };
            Add(c, $"Estribo {tie}@{tieSpacing:0.00} ({c.Tag})", tie, loop, XYZ.BasisZ, n, n > 1 ? (end - start) / (n - 1) : 0, true);
            bool cross = r["ties"]["crossTies"]?.Value<bool>() ?? (perX >= 3 || perY >= 3);
            if (cross)
            {
                if (perY >= 3)
                    Add(c, $"Eslabón {tie}@{tieSpacing:0.00} ({c.Tag})", tie,
                        new List<XYZ> { f.P(f.Length / 2 - ex2, 0, start + 0.02), f.P(f.Length / 2 + ex2, 0, start + 0.02) }, XYZ.BasisZ, n, (end - start) / Math.Max(1, n - 1), true);
                if (perX >= 3)
                    Add(c, $"Eslabón {tie}@{tieSpacing:0.00} ({c.Tag})", tie,
                        new List<XYZ> { f.P(f.Length / 2, -ey2, start + 0.03), f.P(f.Length / 2, ey2, start + 0.03) }, XYZ.BasisZ, n, (end - start) / Math.Max(1, n - 1), true);
            }
        }

        /// <summary>Into the footing below (on top of its mats) when there is one, else just above the base.</summary>
        private double BottomOfColumnBars(Ctx c, RebarFrame f)
        {
            JToken below = c.Rule["extendBelow"];
            if (below != null && below.Type != JTokenType.Null) return f.Bottom - below.Value<double>();
            XYZ centre = f.Centre(f.Bottom - 0.05) * Metric.FeetPerMeter;
            foreach (Element fd in _foundations)
            {
                BoundingBoxXYZ b = fd.get_BoundingBox(null);
                if (b == null) continue;
                if (centre.X < b.Min.X || centre.X > b.Max.X || centre.Y < b.Min.Y || centre.Y > b.Max.Y) continue;
                if (centre.Z < b.Min.Z - 0.01 || centre.Z > b.Max.Z + Metric.Ft(0.10)) continue;
                return b.Min.Z * 0.3048 + 0.075 + 0.03;
            }
            return f.Bottom + 0.05;
        }

        // ------------------------------------------------------------------ beams and tie beams

        private void Beam(Ctx c)
        {
            RebarFrame f = RebarFrame.Of(c.Element);
            JObject r = c.Rule;
            JToken st = r["stirrups"];
            string stirrup = st != null ? RebarCatalog.Key(st["bar"]?.Value<string>() ?? "#3") : null;
            double sp = st?["spacing"]?.Value<double>() ?? 0.10;
            Cover cov = Cover.From(r["cover"], 0.025, 0.025, 0.025, 0.025);
            double ext = r["extend"]?.Value<double>() ?? 0.15;
            bool endHooks = r["endHooks"]?.Value<bool>() ?? true;
            double dbS = stirrup != null ? Db(stirrup) : 0;
            foreach (string layer in new[] { "top", "middle", "bottom" })
            {
                if (!(r[layer] is JArray bars)) continue;
                foreach (JToken b in bars)
                {
                    var (size, count) = BarCount(b, layer);
                    double z = layer == "top" ? f.Top - cov.Top - dbS - Db(size) / 2
                             : layer == "bottom" ? f.Bottom + cov.Bottom + dbS + Db(size) / 2
                             : (f.Top + f.Bottom) / 2;
                    double off = count > 1 ? f.Width / 2 - cov.Side - dbS - Db(size) / 2 : 0;
                    double up = layer == "bottom" ? 1 : -1;
                    double hook = endHooks ? b["hook"]?.Value<double>() ?? _cat.Hook(size) : 0;
                    double hookCover = up > 0 ? cov.Top : cov.Bottom;
                    var (start, h0) = BeamEnd(c, f, false, off, z, ext, cov.Ends, hook, up, hookCover, size);
                    var (end, h1) = BeamEnd(c, f, true, off, z, ext, cov.Ends, hook, up, hookCover, size);
                    var pts = new List<XYZ>();
                    if (h0 > 0) pts.Add(f.P(start, -off, z + up * h0));
                    pts.Add(f.P(start, -off, z));
                    pts.Add(f.P(end, -off, z));
                    if (h1 > 0) pts.Add(f.P(end, -off, z + up * h1));
                    string name = layer == "top" ? "superior" : layer == "bottom" ? "inferior" : "intermedia";
                    Add(c, $"{count}{size} {name} ({c.Tag})", size, pts, f.V, count, count > 1 ? 2 * off / (count - 1) : 0);
                }
            }
            if (stirrup == null) return;
            double s0 = st["start"]?.Value<double>() ?? 0.05;
            int n = CountFor(f.Length - 2 * s0, sp);
            double spacing = n > 1 ? (f.Length - 2 * s0) / (n - 1) : 0;
            double hb = f.Width / 2 - cov.Side, zb = f.Bottom + cov.Bottom, zt = f.Top - cov.Top;
            string shape = (st["shape"]?.Value<string>() ?? "closed").ToLowerInvariant();
            if (shape == "link")
                Add(c, $"Eslabón {stirrup}@{sp:0.00} ({c.Tag})", stirrup, new List<XYZ> { f.P(s0, 0, zb), f.P(s0, 0, zt) }, f.U, n, spacing, true);
            else
                Add(c, $"Estribo {stirrup}@{sp:0.00} ({c.Tag})", stirrup,
                    new List<XYZ> { f.P(s0, -hb, zb), f.P(s0, hb, zb), f.P(s0, hb, zt), f.P(s0, -hb, zt), f.P(s0, -hb, zb) }, f.U, n, spacing, true);
        }

        /// <summary>
        /// Where a beam bar ends (s along the beam) and the hook leg it can take there. The bar runs on past the
        /// end of the beam, up to ext, only as far as a wall, column or beam receives it, keeping the end cover
        /// from the far face; with no support it stops inside the beam at the end cover. The hook leg is as long
        /// as the concrete under (or over) the bar allows, keeping the cover, and is dropped below 4 db.
        /// </summary>
        private (double s, double hook) BeamEnd(Ctx c, RebarFrame f, bool atEnd, double off, double z, double ext, double ends,
            double hook, double up, double hookCover, string size)
        {
            XYZ outward = atEnd ? f.U : -f.U;
            double s = atEnd ? f.Length : 0;
            var offsets = off > 0 ? new[] { -off, off } : new[] { 0.0 };
            double support = offsets.Min(o => Concrete.ContinuousFrom(f.P(s, o, z), outward, ext + ends + 0.05, RebarConcrete.Supports));
            double past = Math.Min(ext, support - ends);
            string side = atEnd ? "end" : "start";
            if (support <= 0) Notes.Add($"{c.Label}: no wall, column or beam at the {side}; the bars stop inside the beam");
            else if (past < ext - 1e-6) Notes.Add($"{c.Label}: the support at the {side} takes {Math.Max(0, past):0.00} m of the {ext:0.00} m anchorage");
            s += atEnd ? past : -past;
            if (hook <= 0) return (s, 0);
            double room = offsets.Min(o => Concrete.ContinuousFrom(f.P(s, o, z), up * XYZ.BasisZ, hook + hookCover + 0.05)) - hookCover;
            double leg = Math.Min(hook, room);
            if (leg < 4 * Db(size))
            {
                Notes.Add($"{c.Label}: no room for the {size} hook at the {side}; straight bar end");
                return (s, 0);
            }
            if (leg < hook - 1e-6) Notes.Add($"{c.Label}: {size} hook at the {side} shortened to {leg:0.00} m to stay in the concrete (12 db = {12 * Db(size):0.00} m)");
            return (s, leg);
        }

        // ------------------------------------------------------------------ footings

        private void Footing(Ctx c)
        {
            RebarFrame f = RebarFrame.Of(c.Element);
            JObject r = c.Rule;
            Cover cov = Cover.From(r["cover"], 0.05, 0.075, 0.075, 0.075);
            double up = r["hookUp"]?.Value<double>() ?? 0.15;
            var (sx, spx) = BarSpacing(r["bottomX"] ?? r["bottom"], "bottomX", 0.15);
            var (sy, spy) = BarSpacing(r["bottomY"] ?? r["bottom"] ?? r["bottomX"], "bottomY", 0.15);
            double au = f.Length / 2 - cov.Side, av = f.Width / 2 - cov.Side;
            double z1 = f.Bottom + cov.Bottom + Db(sx) / 2, z2 = z1 + Db(sx) / 2 + Db(sy) / 2;
            double m = f.Length / 2;
            int nx = CountFor(2 * av, spx), ny = CountFor(2 * au, spy);
            var px = new List<XYZ>();
            if (up > 0) px.Add(f.P(m - au, -av, z1 + up));
            px.Add(f.P(m - au, -av, z1)); px.Add(f.P(m + au, -av, z1));
            if (up > 0) px.Add(f.P(m + au, -av, z1 + up));
            Add(c, $"{sx}@{spx:0.00} sentido largo ({c.Tag})", sx, px, f.V, nx, nx > 1 ? 2 * av / (nx - 1) : 0);
            var py = new List<XYZ>();
            if (up > 0) py.Add(f.P(m - au, -av, z2 + up));
            py.Add(f.P(m - au, -av, z2)); py.Add(f.P(m - au, av, z2));
            if (up > 0) py.Add(f.P(m - au, av, z2 + up));
            Add(c, $"{sy}@{spy:0.00} sentido corto ({c.Tag})", sy, py, f.U, ny, ny > 1 ? 2 * au / (ny - 1) : 0);
        }

        private void StripFooting(Ctx c)
        {
            RebarFrame f = RebarFrame.Of(c.Element);
            JObject r = c.Rule;
            Cover cov = Cover.From(r["cover"], 0.05, 0.075, 0.05, 0.05);
            var (ls, lc) = BarCount(r["longitudinal"], "longitudinal");
            JToken tr = r["transverse"];
            double zt = f.Bottom + cov.Bottom;
            if (tr != null)
            {
                var (ts, tsp) = BarSpacing(tr, "transverse", 0.30);
                double up = tr["hookUp"]?.Value<double>() ?? 0.10;
                double ot = f.Width / 2 - cov.Side, L = f.Length - 2 * cov.Ends;
                int n = CountFor(L, tsp);
                var pts = new List<XYZ>();
                if (up > 0) pts.Add(f.P(cov.Ends, -ot, zt + Db(ts) / 2 + up));
                pts.Add(f.P(cov.Ends, -ot, zt + Db(ts) / 2)); pts.Add(f.P(cov.Ends, ot, zt + Db(ts) / 2));
                if (up > 0) pts.Add(f.P(cov.Ends, ot, zt + Db(ts) / 2 + up));
                Add(c, $"{ts}@{tsp:0.00} transversal ({c.Tag})", ts, pts, f.U, n, n > 1 ? L / (n - 1) : 0);
                zt += Db(ts);
            }
            double off = lc > 1 ? f.Width / 2 - cov.Side - Db(ls) / 2 : 0;
            double z = zt + Db(ls) / 2;
            Add(c, $"{lc}{ls} longitudinal ({c.Tag})", ls,
                new List<XYZ> { f.P(cov.Ends, -off, z), f.P(f.Length - cov.Ends, -off, z) }, f.V, lc, lc > 1 ? 2 * off / (lc - 1) : 0);
        }

        // ------------------------------------------------------------------ welded mesh

        private void WallMesh(Ctx c)
        {
            if (!(c.Element is Wall wall)) throw new ArgumentException(c.Label + " is not a wall");
            SheetSpec sheet = SheetSpec.From(c.Rule["sheet"]);
            int layers = c.Rule["layers"]?.Value<int>() ?? 1;
            XYZ n = wall.Orientation;
            PlanarFace face = RebarFrame.FaceToward(wall, n);
            if (face == null) { Notes.Add(c.Label + ": no planar side face, mesh skipped"); return; }
            double half = wall.Width / 2;
            var shift = Transform.CreateTranslation(-n * half);
            XYZ along = new XYZ(-n.Y, n.X, 0).Normalize();
            bool vertical = string.Equals(c.Rule["majorDirection"]?.Value<string>(), "vertical", StringComparison.OrdinalIgnoreCase);
            var parts = Parts(face.GetEdgesAsCurveLoops().Select(l => Points(l, shift)).ToList(), along, XYZ.BasisZ);
            for (int part = 0; part < parts.Count; part++)
            {
                var loops = parts[part];
                XYZ origin = loops[0].OrderBy(p => p.Z).ThenBy(p => p.X * along.X + p.Y * along.Y).First();
                for (int k = 0; k < Math.Max(1, Math.Min(2, layers)); k++)
                    Meshes.Add(new MeshSpec
                    {
                        Element = c.Label,
                        Group = c.Group,
                        Description = $"E.M. {sheet.Name}" + (layers > 1 ? (k == 0 ? " exterior" : " interior") : "") + $" ({c.Tag})" + (parts.Count > 1 ? $" parte {part + 1}" : ""),
                        Loops = loops,
                        MajorDirection = vertical ? XYZ.BasisZ : along,
                        Origin = origin,
                        Top = k == 0,
                        Lap = c.Rule["lap"]?.Value<double>() ?? 0.20,
                        Sheet = sheet,
                        HostId = wall.Id
                    });
            }
        }

        private void SlabMesh(Ctx c)
        {
            SheetSpec sheet = SheetSpec.From(c.Rule["sheet"]);
            int layers = c.Rule["layers"]?.Value<int>() ?? 1;
            PlanarFace face = RebarFrame.FaceToward(c.Element, XYZ.BasisZ);
            if (face == null) { Notes.Add(c.Label + ": no flat top face, mesh skipped"); return; }
            string dir = (c.Rule["majorDirection"]?.Value<string>() ?? "x").ToLowerInvariant();
            XYZ major = dir == "y" ? XYZ.BasisY : XYZ.BasisX;
            string location = (c.Rule["location"]?.Value<string>() ?? "top").ToLowerInvariant();
            var parts = Parts(face.GetEdgesAsCurveLoops().Select(l => Points(l, Transform.Identity)).ToList(), XYZ.BasisX, XYZ.BasisY);
            for (int part = 0; part < parts.Count; part++)
            for (int k = 0; k < Math.Max(1, Math.Min(2, layers)); k++)
            {
                var loops = parts[part];
                XYZ origin = loops[0].OrderBy(p => p.Y).ThenBy(p => p.X).First();
                bool top = layers > 1 ? k == 0 : location != "bottom";
                Meshes.Add(new MeshSpec
                {
                    Element = c.Label,
                    Group = c.Group,
                    Description = $"E.M. {sheet.Name}" + (layers > 1 ? (top ? " superior" : " inferior") : "") + $" ({c.Tag})" + (parts.Count > 1 ? $" parte {part + 1}" : ""),
                    Loops = loops,
                    MajorDirection = major,
                    Origin = origin,
                    Top = top,
                    Lap = c.Rule["lap"]?.Value<double>() ?? 0.20,
                    Sheet = sheet,
                    HostId = c.Element.Id
                });
            }
        }

        /// <summary>
        /// Loop vertices in metres (arcs tessellated), moved by the transform. Vertices closer than 3 mm are
        /// merged: joins leave edges below Revit's short-curve tolerance, which fabric areas reject.
        /// </summary>
        private static List<XYZ> Points(CurveLoop loop, Transform shift)
        {
            var pts = new List<XYZ>();
            foreach (Curve curve in loop)
            {
                var t = curve.Tessellate();
                for (int i = 0; i < t.Count - 1; i++)
                {
                    XYZ p = shift.OfPoint(t[i]) * 0.3048;
                    if (pts.Count == 0 || p.DistanceTo(pts[pts.Count - 1]) > 0.003) pts.Add(p);
                }
            }
            while (pts.Count > 2 && pts[0].DistanceTo(pts[pts.Count - 1]) <= 0.003) pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        /// <summary>
        /// Splits a face's loops into connected pieces, each an outline followed by its holes: a fabric area
        /// must be one connected region, and a slab or wall face can hold several (separate patios, a wall cut
        /// in two). a and b span the plane of the face.
        /// </summary>
        private static List<List<List<XYZ>>> Parts(List<List<XYZ>> loops, XYZ a, XYZ b)
        {
            loops = loops.Where(l => l.Count >= 3).ToList();
            var flat = loops.Select(l => l.Select(p => (x: p.DotProduct(a), y: p.DotProduct(b))).ToList()).ToList();
            double Area(int i)
            {
                var l = flat[i];
                double s = 0;
                for (int k = 0; k < l.Count; k++) { var p = l[k]; var q = l[(k + 1) % l.Count]; s += p.x * q.y - q.x * p.y; }
                return Math.Abs(s) / 2;
            }
            bool Inside(int point, int outline)
            {
                var p = flat[point][0]; var l = flat[outline];
                bool inside = false;
                for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                    if ((l[i].y > p.y) != (l[j].y > p.y) && p.x < (l[j].x - l[i].x) * (p.y - l[i].y) / (l[j].y - l[i].y) + l[i].x) inside = !inside;
                return inside;
            }
            var outlines = new List<int>();
            var members = new Dictionary<int, List<List<XYZ>>>();
            foreach (int i in Enumerable.Range(0, loops.Count).OrderByDescending(Area))
            {
                // a loop inside an outline is a hole of the smallest outline holding it
                int holder = outlines.Where(o => Inside(i, o)).OrderBy(Area).DefaultIfEmpty(-1).First();
                if (holder >= 0) members[holder].Add(loops[i]);
                else { outlines.Add(i); members[i] = new List<List<XYZ>> { loops[i] }; }
            }
            return outlines.Select(o => members[o]).ToList();
        }

        // ------------------------------------------------------------------ host covers

        /// <summary>Gives the hosts their cover (thin slabs reject meshes with the default); inside a transaction.</summary>
        public static int ApplyCovers(Document doc, Dictionary<ElementId, double> covers, List<string> notes)
        {
            int done = 0;
            var types = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>().ToList();
            foreach (var kv in covers)
            {
                double ft = Metric.Ft(kv.Value);
                RebarCoverType type = types.FirstOrDefault(t => Math.Abs(t.CoverDistance - ft) < 1e-4);
                if (type == null)
                {
                    type = RebarCoverType.Create(doc, $"Recubrimiento {kv.Value * 1000:0} mm", ft);
                    types.Add(type);
                    notes.Add("created cover type " + type.Name);
                }
                RebarHostData data = RebarHostData.GetRebarHostData(doc.GetElement(kv.Key));
                if (data == null) continue;
                data.SetCommonCoverType(type);
                done++;
            }
            return done;
        }
    }
}
