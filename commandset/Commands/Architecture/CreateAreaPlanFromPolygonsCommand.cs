using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Architecture;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>
    /// Area plan from given polygons instead of rooms (covered areas under slabs, lots, zones): the
    /// polygon edges become boundary lines, shared edges drawn once, and each polygon gets an area
    /// with name, number and parameters, placed at a point inside it.
    /// </summary>
    public class CreateAreaPlanFromPolygonsCommand : DelegateCommand
    {
        private const double GridFeet = 0.002 / 0.3048;

        public override string CommandName => "create_area_plan_from_polygons";

        public CreateAreaPlanFromPolygonsCommand(UIApplication uiApp) : base(uiApp, "MCP: áreas desde polígonos")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string levelName = parameters?["level"]?.Value<string>() ?? throw new ArgumentException("level is required");
            string schemeName = parameters?["areaScheme"]?.Value<string>() ?? throw new ArgumentException("areaScheme is required");
            string viewName = parameters?["viewName"]?.Value<string>() ?? levelName + " - " + schemeName;
            var specs = (parameters?["areas"] as JArray)?.OfType<JObject>().ToList();
            if (specs == null || specs.Count == 0) throw new ArgumentException("areas [{loop, name, number}] is required");
            bool tags = parameters?["tags"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                Level level = Modeling.CreateFloorsCommand.FindLevel(doc, levelName);
                AreaScheme scheme = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>()
                                        .FirstOrDefault(s => s.Name == schemeName)
                                    ?? throw new InvalidOperationException("No area scheme '" + schemeName + "'");

                var builder = new AreaOutlineBuilder(GridFeet);
                var polygons = new List<List<XYZ>>();
                var holesOf = new List<List<List<XYZ>>>();
                for (int i = 0; i < specs.Count; i++)
                {
                    var loops = new List<JArray>();
                    if (specs[i]["loop"] is JArray single) loops.Add(single);
                    loops.AddRange((specs[i]["holes"] as JArray)?.OfType<JArray>() ?? Enumerable.Empty<JArray>());
                    if (loops.Count == 0) throw new ArgumentException($"area {i} needs loop");
                    for (int k = 0; k < loops.Count; k++)
                    {
                        var pts = loops[k].Select(p => Metric.Point(p)).ToList();
                        if (k == 0) { polygons.Add(pts); holesOf.Add(new List<List<XYZ>>()); }
                        else holesOf[holesOf.Count - 1].Add(pts);
                        for (int j = 0; j < pts.Count; j++)
                        {
                            XYZ a = pts[j], b = pts[(j + 1) % pts.Count];
                            // each polygon is its own group, so every edge stays; shared edges collapse in the merge
                            builder.Add("p" + i, i, true, a.X, a.Y, b.X, b.Y);
                        }
                    }
                }
                List<double[]> lines = builder.Build(includeHoles: true);

                var warnings = new List<string>();
                var problems = new List<string>();
                var created = new List<object>();
                ViewPlan view = null;
                ToolTransaction.Run(doc, "MCP: áreas desde polígonos", options, warnings, () =>
                {
                    view = ViewSetup.FindView<ViewPlan>(doc, viewName);
                    if (view == null)
                    {
                        view = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
                        view.Name = viewName;
                    }
                    else
                    {
                        if (view.ViewType != ViewType.AreaPlan || view.AreaScheme?.Id != scheme.Id || view.GenLevel?.Id != level.Id)
                            throw new InvalidOperationException($"'{viewName}' exists but is not an area plan of {schemeName} on {levelName}");
                        var old = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Areas).ToElementIds()
                            .Concat(new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_AreaSchemeLines).ToElementIds()).ToList();
                        if (old.Count > 0) doc.Delete(old);
                    }
                    RunMarks.Set(view, options.RunMark);

                    double z = level.Elevation;
                    SketchPlane plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
                    foreach (double[] l in lines)
                    {
                        XYZ a = new XYZ(l[0], l[1], z), b = new XYZ(l[2], l[3], z);
                        if (a.DistanceTo(b) < app.Application.ShortCurveTolerance) continue;
                        RunMarks.Set(doc.Create.NewAreaBoundaryLine(plane, Line.CreateBound(a, b), view), options.RunMark);
                    }
                    doc.Regenerate();

                    var areas = new List<(Area area, JObject spec)>();
                    for (int i = 0; i < specs.Count; i++)
                    {
                        XYZ inside = InsidePoint(polygons[i], holesOf[i]);
                        Area area = doc.Create.NewArea(view, new UV(inside.X, inside.Y));
                        if (specs[i]["name"] != null) area.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(specs[i]["name"].Value<string>());
                        if (specs[i]["number"] != null) area.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set(specs[i]["number"].Value<string>());
                        foreach (var prop in (specs[i]["parameters"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                            if (!Metric.Set(area.LookupParameter(prop.Name), prop.Value)) problems.Add($"area {i}: parameter '{prop.Name}' not set");
                        RunMarks.Set(area, options.RunMark);
                        areas.Add((area, specs[i]));
                    }
                    doc.Regenerate();
                    if (tags)
                    {
                        foreach (var (area, _) in areas.Where(a => a.area.Area > 0))
                        {
                            XYZ p = ((LocationPoint)area.Location).Point;
                            AreaTag tag = doc.Create.NewAreaTag(view, area, new UV(p.X, p.Y));
                            if (tag == null) continue;
                            try { tag.TagHeadPosition = new XYZ(p.X, p.Y, z); tag.HasLeader = false; }
                            catch (Autodesk.Revit.Exceptions.ArgumentException) { problems.Add($"{area.Number}: tag kept its leader"); }
                            RunMarks.Set(tag, options.RunMark);
                        }
                    }
                    foreach (var (area, spec) in areas)
                    {
                        if (area.Area <= 0) problems.Add($"{area.Number}: not enclosed by the boundary lines");
                        created.Add(new
                        {
                            id = ElementSelection.Id(area),
                            number = area.Number,
                            name = area.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                            areaM2 = Math.Round(area.Area * Metric.SquareMetersPerSquareFoot, 2)
                        });
                    }
                });
                return new { document = doc.Title, view = view.Name, boundaryLines = lines.Count, areas = created, problems, warnings };
            }, 900);
        }

        /// <summary>
        /// The point of a grid over the polygon that is inside it, outside its holes and farthest from
        /// every edge, so the area lands in the polygon and not in a hole or a thin corner.
        /// </summary>
        private static XYZ InsidePoint(List<XYZ> poly, List<List<XYZ>> holes)
        {
            double x0 = poly.Min(p => p.X), x1 = poly.Max(p => p.X), y0 = poly.Min(p => p.Y), y1 = poly.Max(p => p.Y);
            var edges = new[] { poly }.Concat(holes).ToList();
            XYZ best = null;
            double bestClearance = -1;
            const int n = 24;
            for (int i = 1; i < n; i++)
                for (int j = 1; j < n; j++)
                {
                    double x = x0 + (x1 - x0) * i / n, y = y0 + (y1 - y0) * j / n;
                    if (!Inside(poly, x, y) || holes.Any(h => Inside(h, x, y))) continue;
                    double clearance = edges.Min(loop => Clearance(loop, x, y));
                    if (clearance > bestClearance) { bestClearance = clearance; best = new XYZ(x, y, 0); }
                }
            return best ?? new XYZ(poly.Average(p => p.X), poly.Average(p => p.Y), 0);
        }

        private static double Clearance(List<XYZ> loop, double x, double y)
        {
            double min = double.MaxValue;
            for (int k = 0; k < loop.Count; k++)
            {
                XYZ a = loop[k], b = loop[(k + 1) % loop.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / len2));
                double px = a.X + t * dx - x, py = a.Y + t * dy - y;
                min = Math.Min(min, Math.Sqrt(px * px + py * py));
            }
            return min;
        }

        private static bool Inside(List<XYZ> poly, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].Y > y) != (poly[j].Y > y) &&
                    x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                    inside = !inside;
            }
            return inside;
        }
    }
}
