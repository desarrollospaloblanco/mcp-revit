using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Architecture;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Quantification;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>
    /// Builds an area plan with one area per apartment from the rooms of a level. Rooms are grouped
    /// by a parameter (Department by default); rooms of a group that are not touching each other (an
    /// apartment type repeated in two towers) become separate areas. Boundary lines follow the outline
    /// of each group at wall centres, then areas get name, number and type, and optionally a color
    /// scheme, tags and a legend.
    /// </summary>
    public class CreateAreaPlanFromRoomsCommand : DelegateCommand
    {
        private const double FeetToMeters = 0.3048;
        private const double SquareFeetToSquareMeters = 0.09290304;
        private const double GridFeet = 0.002 / FeetToMeters;

        public override string CommandName => "create_area_plan_from_rooms";

        public CreateAreaPlanFromRoomsCommand(UIApplication uiApp) : base(uiApp, "MCP: plano de áreas por apartamento")
        {
        }

        private class Options
        {
            public string DocumentTitle, RoomsLevel, AreaLevel, AreaScheme, ViewName, GroupBy, CommonName, NamePrefix;
            public string TypeParameter, TypePrefix, CommonType, TypeSeparator, ColorScheme;
            public List<string> CommonValues;
            public bool Replace, Tags, Legend, IncludeHoles, HideImports;
            public ToolOptions Tool;
        }

        private class Cluster
        {
            public string Value;
            public bool Common;
            public List<Room> Rooms = new List<Room>();
            public BoundingBoxXYZ Box;
            public string Number;
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var o = new Options
            {
                DocumentTitle = parameters?["documentTitle"]?.Value<string>(),
                Tool = ToolOptions.From(parameters),
                RoomsLevel = parameters?["roomsLevel"]?.Value<string>(),
                AreaLevel = parameters?["areaLevel"]?.Value<string>(),
                AreaScheme = parameters?["areaScheme"]?.Value<string>() ?? "Apartamentos",
                ViewName = parameters?["viewName"]?.Value<string>(),
                GroupBy = parameters?["groupBy"]?.Value<string>() ?? "Department",
                CommonValues = (parameters?["commonValues"] as JArray)?.Select(t => t.Value<string>()).ToList()
                               ?? new List<string> { "COMÚN", "COMUN", "CIRCULACIÓN", "CIRCULACION" },
                CommonName = parameters?["commonName"]?.Value<string>() ?? "CIRCULACIÓN",
                NamePrefix = parameters?["namePrefix"]?.Value<string>() ?? "APTO ",
                TypeParameter = parameters?["typeParameter"]?.Value<string>() ?? "Tipo de Apartamento",
                TypePrefix = parameters?["typePrefix"]?.Value<string>() ?? "TIPO ",
                CommonType = parameters?["commonType"]?.Value<string>() ?? "CIRCULACIÓN COMÚN",
                TypeSeparator = parameters?["typeSeparator"]?.Value<string>() ?? "-",
                ColorScheme = parameters?["colorScheme"]?.Value<string>(),
                Replace = parameters?["replace"]?.Value<bool>() ?? true,
                Tags = parameters?["tags"]?.Value<bool>() ?? true,
                Legend = parameters?["legend"]?.Value<bool>() ?? true,
                IncludeHoles = parameters?["includeHoles"]?.Value<bool>() ?? false,
                HideImports = parameters?["hideImports"]?.Value<bool>() ?? true
            };
            if (string.IsNullOrWhiteSpace(o.RoomsLevel))
                throw new ArgumentException("roomsLevel (name of the level the rooms are on) is required");
            if (string.IsNullOrWhiteSpace(o.AreaLevel)) o.AreaLevel = o.RoomsLevel;
            if (string.IsNullOrWhiteSpace(o.ViewName)) o.ViewName = o.AreaLevel + " - Apartamentos";

            return RunInRevit(app => Build(app, o), 600);
        }

        private static object Build(UIApplication app, Options o)
        {
            Document doc = QuantificationUtils.ResolveDocument(app, o.DocumentTitle, out string error)
                           ?? throw new InvalidOperationException(error);
            Level areaLevel = FindLevel(doc, o.AreaLevel);
            AreaScheme scheme = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>()
                                    .FirstOrDefault(s => string.Equals(s.Name, o.AreaScheme, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException("No area scheme '" + o.AreaScheme + "'. Schemes: " +
                                    string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Select(s => s.Name)));
            FindLevel(doc, o.RoomsLevel);

            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 0 && r.Level?.Name == o.RoomsLevel).ToList();
            var skipped = rooms.Where(r => string.IsNullOrWhiteSpace(GroupValue(r, o.GroupBy))).Select(r => r.Number + " " + r.Name).ToList();
            rooms = rooms.Where(r => !string.IsNullOrWhiteSpace(GroupValue(r, o.GroupBy))).ToList();
            if (rooms.Count == 0)
                throw new InvalidOperationException($"No placed rooms on {o.RoomsLevel} with a value in '{o.GroupBy}'");

            var boundary = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center };
            var loops = rooms.ToDictionary(r => r.Id, r => r.GetBoundarySegments(boundary));

            var builder = new AreaOutlineBuilder(GridFeet);
            int curved = 0;
            foreach (Room room in rooms)
            {
                string group = GroupValue(room, o.GroupBy).Trim();
                var roomLoops = loops[room.Id];
                int outerIndex = OuterLoopIndex(roomLoops);
                for (int i = 0; i < roomLoops.Count; i++)
                {
                    foreach (BoundarySegment segment in roomLoops[i])
                    {
                        Curve curve = segment.GetCurve();
                        IList<XYZ> points = curve is Line ? new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) } : curve.Tessellate();
                        if (!(curve is Line)) curved++;
                        for (int k = 0; k + 1 < points.Count; k++)
                            builder.Add(group, Utils.ElementIdExtensions.GetValue(room.Id), i == outerIndex,
                                points[k].X, points[k].Y, points[k + 1].X, points[k + 1].Y);
                    }
                }
            }
            List<double[]> lines = builder.Build(o.IncludeHoles);
            List<Cluster> clusters = ClusterRooms(rooms, builder, o);

            var created = new List<object>();
            double totalM2 = 0;
            var problems = new List<string>();
            var warnings = new List<string>();
            ViewPlan view;
            using (var t = new Transaction(doc, "MCP: plano de áreas " + o.ViewName))
            {
                t.Start();
                var failureOptions = t.GetFailureHandlingOptions();
                var collector = new WarningCollector(o.Tool.SwallowWarnings);
                failureOptions.SetFailuresPreprocessor(collector);
                t.SetFailureHandlingOptions(failureOptions);

                view = GetOrCreateAreaPlan(doc, scheme, areaLevel, o);

                double z = areaLevel.Elevation;
                SketchPlane plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
                foreach (double[] l in lines)
                {
                    XYZ a = new XYZ(l[0], l[1], z), b = new XYZ(l[2], l[3], z);
                    if (a.DistanceTo(b) < app.Application.ShortCurveTolerance) continue;
                    ModelCurve curve = doc.Create.NewAreaBoundaryLine(plane, Line.CreateBound(a, b), view);
                    RunMarks.Set(curve, o.Tool.RunMark);
                }
                doc.Regenerate();

                if (o.HideImports)
                {
                    var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                        .Where(i => i.CanBeHidden(view)).Select(i => i.Id).ToList();
                    if (imports.Count > 0) view.HideElements(imports);
                }

                var areas = new List<(Area area, Cluster cluster)>();
                foreach (Cluster c in clusters)
                {
                    Room largest = c.Rooms.OrderByDescending(r => r.Area).First();
                    XYZ p = ((LocationPoint)largest.Location).Point;
                    Area area = doc.Create.NewArea(view, new UV(p.X, p.Y));
                    RunMarks.Set(area, o.Tool.RunMark);
                    string name = c.Common ? o.CommonName : o.NamePrefix + c.Value;
                    area.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(name);
                    area.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set(c.Number);
                    string type = c.Common ? o.CommonType : o.TypePrefix + c.Value.Split(new[] { o.TypeSeparator }, StringSplitOptions.None)[0];
                    Parameter typeParameter = area.LookupParameter(o.TypeParameter);
                    if (typeParameter != null && !typeParameter.IsReadOnly && typeParameter.StorageType == StorageType.String)
                        typeParameter.Set(type);
                    area.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(type);
                    areas.Add((area, c));
                }
                doc.Regenerate();

#if REVIT2022_OR_GREATER
                if (!string.IsNullOrWhiteSpace(o.ColorScheme))
                    ApplyColorScheme(doc, view, scheme, o.ColorScheme, problems);
#else
                if (!string.IsNullOrWhiteSpace(o.ColorScheme) || o.Legend)
                    problems.Add("Color schemes and legends need Revit 2022 or later through the API");
#endif

                if (o.Tags)
                {
                    foreach (var (area, _) in areas.Where(x => x.area.Area > 0))
                    {
                        XYZ p = ((LocationPoint)area.Location).Point;
                        AreaTag tag = doc.Create.NewAreaTag(view, area, new UV(p.X, p.Y));
                        if (tag == null) continue;
                        // The head goes on the area point first; Revit refuses a leaderless head outside the area.
                        try
                        {
                            tag.TagHeadPosition = new XYZ(p.X, p.Y, z);
                            tag.HasLeader = false;
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        {
                            problems.Add($"{area.Number}: tag left with its leader ({ex.Message.Split('\n')[0].Trim()})");
                        }
                    }
                }

#if REVIT2022_OR_GREATER
                if (o.Legend && view.GetColorFillSchemeId(new ElementId(BuiltInCategory.OST_Areas)) != ElementId.InvalidElementId)
                {
                    double maxX = clusters.Max(c => c.Box.Max.X), maxY = clusters.Max(c => c.Box.Max.Y);
                    ColorFillLegend.Create(doc, view.Id, new ElementId(BuiltInCategory.OST_Areas), new XYZ(maxX + 2 / FeetToMeters, maxY, z));
                }
#endif

                t.Commit();
                warnings.AddRange(collector.Report());

                foreach (var (area, c) in areas)
                {
                    if (!area.IsValidObject) continue;
                    if (area.Area <= 0) problems.Add($"{c.Number}: the area is not enclosed by the boundary lines");
                    totalM2 += area.Area * SquareFeetToSquareMeters;
                    created.Add(new
                    {
                        id = Utils.ElementIdExtensions.GetValue(area.Id),
                        number = c.Number,
                        name = area.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        rooms = c.Rooms.Count,
                        areaM2 = Math.Round(area.Area * SquareFeetToSquareMeters, 2),
                        roomsM2 = Math.Round(c.Rooms.Sum(r => r.Area) * SquareFeetToSquareMeters, 2)
                    });
                }
            }

            return new
            {
                document = doc.Title,
                view = view.Name,
                viewId = Utils.ElementIdExtensions.GetValue(view.Id),
                areaScheme = scheme.Name,
                boundaryLines = lines.Count,
                curvedSegmentsApproximated = curved,
                roomsWithoutGroup = skipped,
                areas = created,
                totalM2 = Math.Round(totalM2, 2),
                problems,
                warnings
            };
        }

        /// <summary>
        /// Rooms of the same group value that share a stretch of boundary (at wall centres) form one
        /// apartment; separate clusters of a value are numbered value.1, value.2 from west to east.
        /// </summary>
        private static List<Cluster> ClusterRooms(List<Room> rooms, AreaOutlineBuilder builder, Options o)
        {
            var result = new List<Cluster>();
            foreach (var group in rooms.GroupBy(r => GroupValue(r, o.GroupBy).Trim()))
            {
                var list = group.ToList();
                var boxes = list.ToDictionary(r => r.Id, r => r.get_BoundingBox(null));
                long Find(Room r) => builder.Component(Utils.ElementIdExtensions.GetValue(r.Id));

                bool common = o.CommonValues.Any(v => string.Equals(v, group.Key, StringComparison.OrdinalIgnoreCase));
                var clusters = list.GroupBy(Find).Select(g =>
                {
                    var c = new Cluster { Value = group.Key, Common = common, Rooms = g.ToList() };
                    var bs = c.Rooms.Select(r => boxes[r.Id]).Where(b => b != null).ToList();
                    c.Box = new BoundingBoxXYZ
                    {
                        Min = new XYZ(bs.Min(b => b.Min.X), bs.Min(b => b.Min.Y), 0),
                        Max = new XYZ(bs.Max(b => b.Max.X), bs.Max(b => b.Max.Y), 0)
                    };
                    return c;
                }).OrderBy(c => c.Box.Min.X).ThenBy(c => c.Box.Min.Y).ToList();

                for (int k = 0; k < clusters.Count; k++)
                {
                    string baseNumber = common ? "C-" + o.AreaLevel : group.Key;
                    clusters[k].Number = clusters.Count > 1 ? baseNumber + "." + (k + 1) : baseNumber;
                }
                result.AddRange(clusters);
            }
            return result;
        }

        private static ViewPlan GetOrCreateAreaPlan(Document doc, AreaScheme scheme, Level level, Options o)
        {
            ViewPlan view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == o.ViewName);
            if (view != null)
            {
                if (view.ViewType != ViewType.AreaPlan || view.AreaScheme?.Id != scheme.Id || view.GenLevel?.Id != level.Id)
                    throw new InvalidOperationException($"'{o.ViewName}' exists but is not an area plan of {scheme.Name} on {level.Name}");
                if (!o.Replace)
                    throw new InvalidOperationException($"'{o.ViewName}' already exists; pass replace=true to rebuild it");

                // Boundary lines belong to the scheme and the level, so these are the ones of this plan.
                var old = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Areas).ToElementIds()
                    .Concat(new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_AreaSchemeLines).ToElementIds())
#if REVIT2022_OR_GREATER
                    .Concat(new FilteredElementCollector(doc, view.Id).OfClass(typeof(ColorFillLegend)).ToElementIds())
#endif
                    .ToList();
                if (old.Count > 0) doc.Delete(old);
                return view;
            }

            view = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
            view.Name = o.ViewName;
            return view;
        }

#if REVIT2022_OR_GREATER
        private static void ApplyColorScheme(Document doc, ViewPlan view, AreaScheme scheme, string name, List<string> problems)
        {
            var category = new ElementId(BuiltInCategory.OST_Areas);
            ColorFillScheme colors = new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme)).Cast<ColorFillScheme>()
                .FirstOrDefault(c => c.Name == name && c.CategoryId == category && c.AreaSchemeId == scheme.Id);
            if (colors == null)
            {
                problems.Add($"No color scheme '{name}' for areas of {scheme.Name}; the plan has no colors");
                return;
            }
            try
            {
                view.SetColorFillSchemeId(category, colors.Id);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                problems.Add($"Could not set the color scheme on the view ({ex.Message}). If a view template controls it, set it there.");
            }
        }

#endif

        private static int OuterLoopIndex(IList<IList<BoundarySegment>> loops)
        {
            int best = 0;
            double bestArea = -1;
            for (int i = 0; i < loops.Count; i++)
            {
                double area = 0;
                foreach (BoundarySegment s in loops[i])
                {
                    XYZ a = s.GetCurve().GetEndPoint(0), b = s.GetCurve().GetEndPoint(1);
                    area += a.X * b.Y - b.X * a.Y;
                }
                if (Math.Abs(area) > bestArea) { bestArea = Math.Abs(area); best = i; }
            }
            return best;
        }

        private static string GroupValue(Room room, string parameterName)
        {
            Parameter p = string.Equals(parameterName, "Department", StringComparison.OrdinalIgnoreCase)
                ? room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)
                : room.LookupParameter(parameterName);
            return p == null ? null : p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        }

        private static Level FindLevel(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name)
            ?? throw new InvalidOperationException("No level named '" + name + "' in " + doc.Title);
    }
}
