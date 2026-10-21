using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Wall clean-up operations that kept coming back as scripts: align to a line by axis or face,
    /// snap ends to a module, extend or trim ends to the nearest wall, merge collinear pieces, delete
    /// duplicates, control end joins, change the location line without moving the wall, and flip.
    /// </summary>
    public class EditWallsCommand : DelegateCommand
    {
        private const double ParallelTolerance = 0.0175; // about one degree

        public override string CommandName => "edit_walls";

        public EditWallsCommand(UIApplication uiApp) : base(uiApp, "MCP: editar muros")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string op = (parameters?["operation"]?.Value<string>() ?? "").ToLowerInvariant();
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var walls = ElementSelection.Resolve(doc, parameters, BuiltInCategory.OST_Walls).OfType<Wall>()
                    .Where(w => w.WallType.Kind != WallKind.Curtain || op == "joins" || op == "flip").ToList();
                var warnings = new List<string>();
                var changes = new List<object>();
                var skipped = new List<object>();

                ToolTransaction.Run(doc, "MCP: muros " + op, options, warnings, () =>
                {
                    switch (op)
                    {
                        case "align": Align(walls, parameters, changes, skipped); break;
                        case "snap": Snap(walls, Metric.Ft(parameters?["moduleMeters"]?.Value<double>() ?? 0.005), changes); break;
                        case "extend": Extend(doc, walls, Metric.Ft(parameters?["toleranceMeters"]?.Value<double>() ?? 0.3), changes); break;
                        case "merge": Merge(doc, walls, Metric.Ft(parameters?["toleranceMeters"]?.Value<double>() ?? 0.01), changes, skipped); break;
                        case "dedupe": Dedupe(doc, walls, Metric.Ft(parameters?["toleranceMeters"]?.Value<double>() ?? 0.02), changes, skipped); break;
                        case "joins": Joins(walls, parameters, changes); break;
                        case "locationline": LocationLine(doc, walls, parameters?["locationLine"]?.Value<string>(), changes); break;
                        case "flip": foreach (Wall w in walls) { w.Flip(); changes.Add(new { id = ElementSelection.Id(w), flipped = w.Flipped }); } break;
                        default: throw new ArgumentException("operation must be align, snap, extend, merge, dedupe, joins, locationLine or flip");
                    }
                }, dryRun);
                return new { document = doc.Title, operation = op, dryRun, selected = walls.Count, changed = changes.Count, changes, skipped, warnings };
            }, 900);
        }

        private static Line Axis(Wall w) => (w.Location as LocationCurve)?.Curve as Line;

        private static XYZ Flat(XYZ v) => new XYZ(v.X, v.Y, 0);

        /// <summary>Signed position of the wall's axis, exterior or interior face across a direction.</summary>
        private static double? Position(Wall w, XYZ normal, string reference)
        {
            Line axis = Axis(w);
            if (axis == null) return null;
            // LocationCurve may sit on a face or the core, so positions come from the side faces themselves.
            double? ext = FacePosition(w, ShellLayerType.Exterior, normal), inn = FacePosition(w, ShellLayerType.Interior, normal);
            if (ext == null || inn == null) return null;
            switch (reference)
            {
                case "exterior": return ext;
                case "interior": return inn;
                default: return (ext + inn) / 2;
            }
        }

        private static double? FacePosition(Wall w, ShellLayerType side, XYZ normal)
        {
            foreach (Reference r in HostObjectUtils.GetSideFaces(w, side))
                if (w.GetGeometryObjectFromReference(r) is PlanarFace f)
                    return Flat(f.Origin).DotProduct(normal);
            return null;
        }

        private static void Align(List<Wall> walls, JObject p, List<object> changes, List<object> skipped)
        {
            JObject target = p?["target"] as JObject ?? throw new ArgumentException("align needs target: {axis:'x'|'y', value} or {from, to}");
            string reference = (p?["reference"]?.Value<string>() ?? "axis").ToLowerInvariant();
            XYZ origin, dir;
            if (target["axis"] != null)
            {
                double v = Metric.Ft(target["value"].Value<double>());
                bool x = target["axis"].Value<string>().ToLowerInvariant() == "x";
                // axis 'x' = the line x = value (vertical in plan); 'y' = the line y = value
                origin = x ? new XYZ(v, 0, 0) : new XYZ(0, v, 0);
                dir = x ? XYZ.BasisY : XYZ.BasisX;
            }
            else
            {
                XYZ a = Flat(Metric.Point(target["from"])), b = Flat(Metric.Point(target["to"]));
                origin = a;
                dir = (b - a).Normalize();
            }
            XYZ normal = new XYZ(-dir.Y, dir.X, 0);
            double goal = origin.DotProduct(normal);

            foreach (Wall w in walls)
            {
                Line axis = Axis(w);
                if (axis == null || Math.Abs(Flat(axis.Direction).Normalize().CrossProduct(dir).Z) > ParallelTolerance)
                {
                    skipped.Add(new { id = ElementSelection.Id(w), reason = "not straight or not parallel to the target" });
                    continue;
                }
                double? now = Position(w, normal, reference);
                if (now == null) { skipped.Add(new { id = ElementSelection.Id(w), reason = "no side faces" }); continue; }
                double delta = goal - now.Value;
                if (Math.Abs(delta) < 1e-6) continue;
                ElementTransformUtils.MoveElement(w.Document, w.Id, normal * delta);
                changes.Add(new { id = ElementSelection.Id(w), movedMeters = Metric.M(delta) });
            }
        }

        private static void Snap(List<Wall> walls, double module, List<object> changes)
        {
            if (module <= 0) throw new ArgumentException("moduleMeters must be positive");
            double R(double v) => Math.Round(v / module) * module;
            foreach (Wall w in walls)
            {
                Line axis = Axis(w);
                if (axis == null) continue;
                XYZ a = axis.GetEndPoint(0), b = axis.GetEndPoint(1);
                XYZ na = new XYZ(R(a.X), R(a.Y), a.Z), nb = new XYZ(R(b.X), R(b.Y), b.Z);
                if (na.IsAlmostEqualTo(a) && nb.IsAlmostEqualTo(b)) continue;
                ((LocationCurve)w.Location).Curve = Line.CreateBound(na, nb);
                changes.Add(new { id = ElementSelection.Id(w), from = new[] { Metric.Out2(a), Metric.Out2(b) }, to = new[] { Metric.Out2(na), Metric.Out2(nb) } });
            }
        }

        /// <summary>
        /// Each end moves to where its axis meets the axis of another wall, when that point is within
        /// the tolerance of the end (closing gaps and trimming overshoots) and on the other wall.
        /// </summary>
        private static void Extend(Document doc, List<Wall> walls, double tolerance, List<object> changes)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => Axis(w) != null).ToList();
            foreach (Wall w in walls)
            {
                Line axis = Axis(w);
                if (axis == null) continue;
                XYZ[] ends = { axis.GetEndPoint(0), axis.GetEndPoint(1) };
                XYZ d = Flat(axis.Direction).Normalize();
                bool moved = false;
                for (int i = 0; i < 2; i++)
                {
                    XYZ best = null;
                    double bestDist = tolerance;
                    foreach (Wall o in all)
                    {
                        if (o.Id == w.Id || !OverlapInHeight(o, w)) continue;
                        Line oa = Axis(o);
                        XYZ od = Flat(oa.Direction).Normalize();
                        double cross = d.X * od.Y - d.Y * od.X;
                        if (Math.Abs(cross) < ParallelTolerance) continue;
                        XYZ q = Flat(oa.GetEndPoint(0)) - Flat(ends[0]);
                        double t = (q.X * od.Y - q.Y * od.X) / cross;
                        double s = (q.X * d.Y - q.Y * d.X) / cross;
                        XYZ hit = Flat(ends[0]) + d * t;
                        double onOther = s;
                        if (onOther < -tolerance || onOther > oa.Length + tolerance) continue;
                        double dist = hit.DistanceTo(Flat(ends[i]));
                        if (dist < 1e-4 || dist > bestDist) continue;
                        bestDist = dist;
                        best = hit;
                    }
                    if (best != null) { ends[i] = new XYZ(best.X, best.Y, ends[i].Z); moved = true; }
                }
                if (!moved || ends[0].DistanceTo(ends[1]) < 0.1) continue;
                ((LocationCurve)w.Location).Curve = Line.CreateBound(ends[0], ends[1]);
                changes.Add(new { id = ElementSelection.Id(w), to = new[] { Metric.Out2(ends[0]), Metric.Out2(ends[1]) } });
            }
        }

        /// <summary>Walls on different floors can share an axis in plan; they only interact when their heights overlap.</summary>
        private static bool OverlapInHeight(Wall a, Wall b)
        {
            BoundingBoxXYZ ba = a.get_BoundingBox(null), bb = b.get_BoundingBox(null);
            if (ba == null || bb == null) return false;
            double overlap = Math.Min(ba.Max.Z, bb.Max.Z) - Math.Max(ba.Min.Z, bb.Min.Z);
            return overlap > 0.5 * Math.Min(ba.Max.Z - ba.Min.Z, bb.Max.Z - bb.Min.Z);
        }

        private static bool Collinear(Line a, Line b, double tolerance)
        {
            XYZ da = Flat(a.Direction).Normalize(), db = Flat(b.Direction).Normalize();
            if (Math.Abs(da.X * db.Y - da.Y * db.X) > ParallelTolerance) return false;
            XYZ n = new XYZ(-da.Y, da.X, 0);
            return Math.Abs((Flat(b.GetEndPoint(0)) - Flat(a.GetEndPoint(0))).DotProduct(n)) <= tolerance;
        }

        private static (double, double) Span(Line l, XYZ origin, XYZ d)
        {
            double s0 = (Flat(l.GetEndPoint(0)) - origin).DotProduct(d), s1 = (Flat(l.GetEndPoint(1)) - origin).DotProduct(d);
            return (Math.Min(s0, s1), Math.Max(s0, s1));
        }

        private static bool HasInserts(Wall w) => w.FindInserts(true, true, true, true).Count > 0;

        /// <summary>Collinear walls of the same type that touch become one; a wall with inserts is kept, never deleted.</summary>
        private static void Merge(Document doc, List<Wall> walls, double tolerance, List<object> changes, List<object> skipped)
        {
            var pool = walls.Where(w => Axis(w) != null).ToList();
            bool again = true;
            while (again)
            {
                again = false;
                for (int i = 0; i < pool.Count && !again; i++)
                    for (int j = i + 1; j < pool.Count && !again; j++)
                    {
                        Wall a = pool[i], b = pool[j];
                        if (a.GetTypeId() != b.GetTypeId() || a.LevelId != b.LevelId || !OverlapInHeight(a, b)) continue;
                        Line la = Axis(a), lb = Axis(b);
                        if (!Collinear(la, lb, tolerance)) continue;
                        XYZ o = Flat(la.GetEndPoint(0)), d = Flat(la.Direction).Normalize();
                        var (a0, a1) = Span(la, o, d);
                        var (b0, b1) = Span(lb, o, d);
                        if (b0 > a1 + tolerance || a0 > b1 + tolerance) continue;
                        Wall keep = HasInserts(b) && !HasInserts(a) ? b : a, drop = keep == a ? b : a;
                        if (HasInserts(drop)) { skipped.Add(new { ids = new[] { ElementSelection.Id(a), ElementSelection.Id(b) }, reason = "both have inserts" }); continue; }
                        double s0 = Math.Min(a0, b0), s1 = Math.Max(a1, b1);
                        Line lk = Axis(keep);
                        double z = lk.GetEndPoint(0).Z;
                        XYZ start = o + d * s0, end = o + d * s1;
                        // keep the kept wall's direction so its exterior side does not flip
                        bool forward = Flat(lk.Direction).Normalize().DotProduct(d) > 0;
                        long dropId = ElementSelection.Id(drop);
                        pool.Remove(drop);
                        doc.Delete(drop.Id);
                        ((LocationCurve)keep.Location).Curve = forward
                            ? Line.CreateBound(new XYZ(start.X, start.Y, z), new XYZ(end.X, end.Y, z))
                            : Line.CreateBound(new XYZ(end.X, end.Y, z), new XYZ(start.X, start.Y, z));
                        changes.Add(new { kept = ElementSelection.Id(keep), deleted = dropId });
                        again = true;
                    }
            }
        }

        /// <summary>Two parallel walls on the same axis overlapping by half of the shorter: the one without inserts goes.</summary>
        private static void Dedupe(Document doc, List<Wall> walls, double tolerance, List<object> changes, List<object> skipped)
        {
            var pool = walls.Where(w => Axis(w) != null).ToList();
            for (int i = 0; i < pool.Count; i++)
                for (int j = i + 1; j < pool.Count; j++)
                {
                    Wall a = pool[i], b = pool[j];
                    if (!a.IsValidObject || !b.IsValidObject) continue;
                    if (ElementSelection.LevelOf(a) != ElementSelection.LevelOf(b) || !OverlapInHeight(a, b)) continue;
                    Line la = Axis(a), lb = Axis(b);
                    if (!Collinear(la, lb, Math.Max(tolerance, (a.Width + b.Width) / 4))) continue;
                    XYZ o = Flat(la.GetEndPoint(0)), d = Flat(la.Direction).Normalize();
                    var (a0, a1) = Span(la, o, d);
                    var (b0, b1) = Span(lb, o, d);
                    double overlap = Math.Min(a1, b1) - Math.Max(a0, b0), shorter = Math.Min(a1 - a0, b1 - b0);
                    if (overlap < shorter * 0.5) continue;
                    bool ia = HasInserts(a), ib = HasInserts(b);
                    if (ia && ib) { skipped.Add(new { ids = new[] { ElementSelection.Id(a), ElementSelection.Id(b) }, reason = "both have inserts" }); continue; }
                    Wall drop = ia ? b : ib ? a : (a1 - a0 < b1 - b0 ? a : b);
                    Wall keep = drop == a ? b : a;
                    ElementId dropId = drop.Id;
                    changes.Add(new { kept = ElementSelection.Id(keep), deleted = ElementSelection.Id(drop), overlapMeters = Metric.M(overlap) });
                    doc.Delete(dropId);
                }
        }

        private static void Joins(List<Wall> walls, JObject p, List<object> changes)
        {
            bool allow = (p?["joins"]?.Value<string>() ?? "allow").ToLowerInvariant() == "allow";
            string ends = (p?["ends"]?.Value<string>() ?? "both").ToLowerInvariant();
            foreach (Wall w in walls)
            {
                foreach (int end in ends == "start" ? new[] { 0 } : ends == "end" ? new[] { 1 } : new[] { 0, 1 })
                {
                    if (allow) WallUtils.AllowWallJoinAtEnd(w, end);
                    else WallUtils.DisallowWallJoinAtEnd(w, end);
                }
                changes.Add(new { id = ElementSelection.Id(w), joins = allow ? "allowed" : "disallowed", ends });
            }
        }

        /// <summary>
        /// Changing the location line keeps the location curve and moves the wall body, so the body
        /// is measured before and moved back afterwards: the wall stays where it was.
        /// </summary>
        private static void LocationLine(Document doc, List<Wall> walls, string value, List<object> changes)
        {
            var map = new Dictionary<string, WallLocationLine>(StringComparer.OrdinalIgnoreCase)
            {
                { "centerline", WallLocationLine.WallCenterline }, { "coreCenterline", WallLocationLine.CoreCenterline },
                { "finishExterior", WallLocationLine.FinishFaceExterior }, { "finishInterior", WallLocationLine.FinishFaceInterior },
                { "coreExterior", WallLocationLine.CoreExterior }, { "coreInterior", WallLocationLine.CoreInterior }
            };
            if (value == null || !map.TryGetValue(value, out WallLocationLine target))
                throw new ArgumentException("locationLine must be centerline, coreCenterline, finishExterior, finishInterior, coreExterior or coreInterior");
            foreach (Wall w in walls)
            {
                Parameter prm = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (prm == null || prm.IsReadOnly || prm.AsInteger() == (int)target) continue;
                XYZ normal = Flat(w.Orientation).Normalize();
                double? before = Position(w, normal, "exterior");
                prm.Set((int)target);
                doc.Regenerate();
                double? after = Position(w, normal, "exterior");
                if (before != null && after != null && Math.Abs(before.Value - after.Value) > 1e-6)
                    ElementTransformUtils.MoveElement(doc, w.Id, normal * (before.Value - after.Value));
                changes.Add(new { id = ElementSelection.Id(w), locationLine = value });
            }
        }
    }
}
