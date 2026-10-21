using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Quantification;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>
    /// Read-only review of doors and windows against their host walls: openings without a host or
    /// hosted by something other than a wall, openings that do not cut their wall, openings running
    /// past the end of the wall, and walls crossing or doubling the wall inside a door opening.
    /// </summary>
    public class CheckHostedOpeningsCommand : DelegateCommand
    {
        private const double FeetToMeters = 0.3048;

        public override string CommandName => "check_hosted_openings";

        public CheckHostedOpeningsCommand(UIApplication uiApp) : base(uiApp, "MCP: revisar puertas y ventanas")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            string title = parameters?["documentTitle"]?.Value<string>();
            var levels = (parameters?["levels"] as JArray)?.Select(t => t.Value<string>()).ToList();
            var categories = (parameters?["categories"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                             ?? new List<string> { "doors", "windows" };
            double endTolerance = (parameters?["endToleranceMeters"]?.Value<double>() ?? 0.05) / FeetToMeters;
            int limit = Math.Max(1, parameters?["limit"]?.Value<int>() ?? 300);

            return RunInRevit(app =>
            {
                Document doc = QuantificationUtils.ResolveDocument(app, title, out string error)
                               ?? throw new InvalidOperationException(error);

                var bics = new List<BuiltInCategory>();
                if (categories.Contains("doors")) bics.Add(BuiltInCategory.OST_Doors);
                if (categories.Contains("windows")) bics.Add(BuiltInCategory.OST_Windows);
                if (bics.Count == 0) throw new ArgumentException("categories must include 'doors' and/or 'windows'");

                var openings = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .WherePasses(new ElementMulticategoryFilter(bics)).Cast<FamilyInstance>()
                    .Where(fi => levels == null || levels.Count == 0 || levels.Contains(LevelName(doc, fi)))
                    .ToList();

                var issues = new List<object>();
                var counts = new Dictionary<string, int>();
                foreach (FamilyInstance fi in openings)
                {
                    foreach (var (code, message) in Review(doc, fi, endTolerance))
                    {
                        counts[code] = counts.TryGetValue(code, out int n) ? n + 1 : 1;
                        if (issues.Count >= limit) continue;
                        XYZ p = (fi.Location as LocationPoint)?.Point;
                        issues.Add(new
                        {
                            id = Utils.ElementIdExtensions.GetValue(fi.Id),
                            category = fi.Category?.Name,
                            type = fi.Symbol?.FamilyName + " : " + fi.Name,
                            level = LevelName(doc, fi),
                            hostId = fi.Host == null ? (long?)null : Utils.ElementIdExtensions.GetValue(fi.Host.Id),
                            issue = code,
                            message,
                            point = p == null ? null : new[] { Math.Round(p.X * FeetToMeters, 3), Math.Round(p.Y * FeetToMeters, 3), Math.Round(p.Z * FeetToMeters, 3) }
                        });
                    }
                }

                return new
                {
                    document = doc.Title,
                    checkedOpenings = openings.Count,
                    issueCounts = counts,
                    issues,
                    truncated = counts.Values.Sum() > issues.Count
                };
            }, 600);
        }

        private static IEnumerable<(string code, string message)> Review(Document doc, FamilyInstance fi, double endTolerance)
        {
            if (fi.Host == null)
            {
                yield return ("unhosted", "No host: the opening does not belong to any wall");
                yield break;
            }
            if (!(fi.Host is Wall wall))
            {
                yield return ("hostNotWall", "Hosted by " + (fi.Host.Category?.Name ?? fi.Host.GetType().Name));
                yield break;
            }
            if (wall.WallType.Kind == WallKind.Curtain)
                yield break; // curtain panels are swapped, not cut

            XYZ point = (fi.Location as LocationPoint)?.Point;
            Line axis = (wall.Location as LocationCurve)?.Curve as Line;
            if (point == null || axis == null)
                yield break; // arc walls and odd families: geometry checks below assume a straight wall

            BoundingBoxXYZ box = fi.get_BoundingBox(null);
            double centerZ = box != null ? (box.Min.Z + box.Max.Z) / 2 : point.Z + 1;
            XYZ center = new XYZ(point.X, point.Y, centerZ);
            XYZ normal = wall.Orientation.Normalize();
            XYZ along = (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();

            if (!CutsWall(wall, fi, center, normal))
                yield return ("notCutting", "The opening does not cut its wall: the wall is solid through the opening centre");

            double width = OpeningWidth(fi, box, along);
            double t = (center - axis.GetEndPoint(0)).DotProduct(along);
            double toEnd = Math.Min(t, axis.Length - t);
            if (width > 0 && toEnd < width / 2 + endTolerance)
                yield return ("nearWallEnd", $"The opening reaches the end of its wall ({Math.Round((toEnd - width / 2) * FeetToMeters, 3)} m left)");

            if (fi.Category != null && Utils.ElementIdExtensions.GetValue(fi.Category.Id) == (long)BuiltInCategory.OST_Doors && width > 0)
            {
                foreach (var issue in OtherWallsInOpening(doc, wall, fi, center, along, width))
                    yield return issue;
            }
        }

        /// <summary>A line through the wall thickness at the opening centre must not hit the wall solid.</summary>
        private static bool CutsWall(Wall wall, FamilyInstance fi, XYZ center, XYZ normal)
        {
            if (!wall.FindInserts(true, false, true, true).Contains(fi.Id))
                return false;

            double half = wall.Width / 2 + 0.3;
            Line probe = Line.CreateBound(center - normal * half, center + normal * half);
            double solidLength = 0;
            foreach (Solid solid in QuantificationUtils.GetSolids(wall))
            {
                SolidCurveIntersection hit = solid.IntersectWithCurve(probe, new SolidCurveIntersectionOptions());
                for (int i = 0; i < hit.SegmentCount; i++)
                    solidLength += hit.GetCurveSegment(i).Length;
            }
            return solidLength < wall.Width * 0.5;
        }

        private static double OpeningWidth(FamilyInstance fi, BoundingBoxXYZ box, XYZ along)
        {
            foreach (var bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                double w = fi.get_Parameter(bip)?.AsDouble() ?? fi.Symbol?.get_Parameter(bip)?.AsDouble() ?? 0;
                if (w > 0) return w;
            }
            if (box == null) return 0;
            XYZ d = box.Max - box.Min;
            return Math.Abs(d.X * along.X) + Math.Abs(d.Y * along.Y);
        }

        /// <summary>
        /// Walls whose axis crosses the door opening, or that run parallel inside the host thickness
        /// over the opening (a duplicated wall the door does not cut).
        /// </summary>
        private static IEnumerable<(string code, string message)> OtherWallsInOpening(Document doc, Wall host, FamilyInstance fi,
            XYZ center, XYZ along, double width)
        {
            XYZ a = center - along * (width / 2), b = center + along * (width / 2);
            double reach = host.Width + 1;
            var outline = new Outline(
                new XYZ(Math.Min(a.X, b.X) - reach, Math.Min(a.Y, b.Y) - reach, center.Z - 1),
                new XYZ(Math.Max(a.X, b.X) + reach, Math.Max(a.Y, b.Y) + reach, center.Z + 1));

            var nearby = new FilteredElementCollector(doc).OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(outline)).Cast<Wall>()
                .Where(w => w.Id != host.Id && w.WallType.Kind != WallKind.Curtain);

            foreach (Wall other in nearby)
            {
                if (!((other.Location as LocationCurve)?.Curve is Line line)) continue;
                BoundingBoxXYZ ob = other.get_BoundingBox(null);
                if (ob == null || ob.Max.Z < center.Z || ob.Min.Z > center.Z) continue;

                XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                XYZ dir = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0).Normalize();
                double cross = along.X * dir.Y - along.Y * dir.X;
                long otherId = Utils.ElementIdExtensions.GetValue(other.Id);

                if (Math.Abs(cross) > 0.05)
                {
                    if (CrossesOpening(a, along, width, p0, dir, line.Length, host.Width / 2 + 0.05, cross))
                        yield return ("wallCrossesOpening", $"Wall {otherId} crosses the door opening");
                }
                else
                {
                    double offset = Math.Abs((center.X - p0.X) * dir.Y - (center.Y - p0.Y) * dir.X);
                    double s0 = (a - p0).DotProduct(dir), s1 = (b - p0).DotProduct(dir);
                    double length = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0).GetLength();
                    double overlap = Math.Min(Math.Max(s0, s1), length) - Math.Max(Math.Min(s0, s1), 0);
                    if (offset < (host.Width + other.Width) / 2 && overlap > width * 0.25)
                        yield return ("overlappingWall", $"Wall {otherId} runs inside the host wall over the opening (duplicated wall, not cut)");
                }
            }
        }

        /// <summary>
        /// The other wall's axis meets the door line strictly inside the opening, and the wall reaches
        /// it (a wall ending against the host, a T-junction, counts).
        /// </summary>
        private static bool CrossesOpening(XYZ a, XYZ along, double width, XYZ p0, XYZ dir, double length, double reach, double cross)
        {
            double Cross(double x1, double y1, double x2, double y2) => x1 * y2 - y1 * x2;
            double dx = p0.X - a.X, dy = p0.Y - a.Y;
            double s = Cross(dx, dy, dir.X, dir.Y) / cross;
            double u = Cross(dx, dy, along.X, along.Y) / cross;
            const double inset = 0.05;
            return s > inset && s < width - inset && u > -reach && u < length + reach;
        }

        private static string LevelName(Document doc, FamilyInstance fi)
        {
            ElementId id = fi.LevelId != ElementId.InvalidElementId ? fi.LevelId : fi.Host?.LevelId ?? ElementId.InvalidElementId;
            return (doc.GetElement(id) as Level)?.Name;
        }
    }
}
