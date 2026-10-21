using System.IO;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Commands.Views;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Execution;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    /// Read-only dump of the plan geometry of a level in metres, for analysis outside Revit: wall
    /// axes and faces, doors and windows with hand and facing, floor outlines, room boundaries at
    /// wall centres, columns, CAD curves by layer and boxes of any other category. Written to a JSON
    /// file (the summary comes back), or returned inline when small.
    /// </summary>
    public class ExportPlanGeometryCommand : DelegateCommand
    {
        private static readonly string[] AllParts = { "walls", "openings", "floors", "rooms", "columns", "cad" };

        public override string CommandName => "export_plan_geometry";

        public ExportPlanGeometryCommand(UIApplication uiApp) : base(uiApp, "MCP: geometría de planta")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string levelName = parameters?["level"]?.Value<string>();
            var include = (parameters?["include"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                          ?? AllParts.Where(p => p != "cad").ToList();
            var extraCategories = (parameters?["categories"] as JArray)?.Select(t => t.Value<string>()).ToList() ?? new List<string>();
            JObject region = parameters?["region"] as JObject;
            bool inline = parameters?["inline"]?.Value<bool>() ?? false;
            string outputPath = parameters?["outputPath"]?.Value<string>();
            int maxCadCurves = parameters?["maxCadCurves"]?.Value<int>() ?? 20000;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                Level level = string.IsNullOrWhiteSpace(levelName) ? null
                    : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == levelName)
                      ?? throw new InvalidOperationException("No level named '" + levelName + "' in " + doc.Title);
                Outline area = region == null ? null : new Outline(
                    Metric.Point(region["min"], -10000), Metric.Point(region["max"], 10000));

                var data = new JObject
                {
                    ["document"] = doc.Title,
                    ["level"] = level?.Name,
                    ["levelElevation"] = level == null ? null : (JToken)Metric.M(level.Elevation),
                    ["units"] = "m"
                };
                if (include.Contains("walls")) data["walls"] = Walls(doc, level, area);
                if (include.Contains("openings")) data["openings"] = Openings(doc, level, area);
                if (include.Contains("floors")) data["floors"] = Floors(doc, level, area);
                if (include.Contains("rooms")) data["rooms"] = Rooms(doc, level, area);
                if (include.Contains("columns")) data["columns"] = Boxes(doc, level, area, new[] { "OST_Columns", "OST_StructuralColumns" });
                if (include.Contains("cad")) data["cad"] = Cad(doc, level, area, maxCadCurves);
                if (extraCategories.Count > 0) data["other"] = Boxes(doc, level, area, extraCategories);

                var summary = new JObject();
                foreach (var p in data.Properties().Where(p => p.Value is JArray))
                    summary[p.Name] = ((JArray)p.Value).Count;

                if (inline)
                    return data;

                string file = string.IsNullOrWhiteSpace(outputPath)
                    ? Path.Combine(McpPaths.Root, "exports", SetPerspectiveViewCommand.Sanitize($"geometry_{doc.Title}_{level?.Name ?? "all"}") + ".json")
                    : outputPath;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, data.ToString(Formatting.Indented));
                return new { document = doc.Title, level = level?.Name, file, counts = summary };
            }, 600);
        }

        private static bool OnLevel(Element e, Level level, params BuiltInParameter[] levelParams)
        {
            if (level == null) return true;
            if (e.LevelId == level.Id) return true;
            foreach (var bip in levelParams)
                if (e.get_Parameter(bip)?.AsElementId() == level.Id) return true;
            return false;
        }

        private static bool InRegion(Element e, Outline area)
        {
            if (area == null) return true;
            BoundingBoxXYZ b = e.get_BoundingBox(null);
            return b != null && area.Intersects(new Outline(b.Min, b.Max), 0);
        }

        private static JArray Walls(Document doc, Level level, Outline area)
        {
            var result = new JArray();
            foreach (Wall w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!OnLevel(w, level, BuiltInParameter.WALL_BASE_CONSTRAINT) || !InRegion(w, area)) continue;
                var item = new JObject
                {
                    ["id"] = Utils.ElementIdExtensions.GetValue(w.Id),
                    ["type"] = w.WallType.Name,
                    ["kind"] = w.WallType.Kind.ToString(),
                    ["width"] = Metric.M(w.Width),
                    ["height"] = Metric.M(w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0),
                    ["baseOffset"] = Metric.M(w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0),
                    ["structural"] = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1,
                    ["locationLine"] = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsValueString(),
                    ["exteriorNormal"] = new JArray(Math.Round(w.Orientation.X, 4), Math.Round(w.Orientation.Y, 4))
                };
                Curve curve = (w.Location as LocationCurve)?.Curve;
                if (curve is Line line)
                {
                    // "axis" is the location curve, which sits on the location line (centre, core or a face);
                    // the faces come from the wall geometry so they are right whatever the location line.
                    item["axis"] = Segment(line, XYZ.Zero);
                    XYZ n = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
                    double here = n.DotProduct(line.GetEndPoint(0));
                    double? ext = FacePosition(w, ShellLayerType.Exterior, n), inn = FacePosition(w, ShellLayerType.Interior, n);
                    if (ext != null) item["exteriorFace"] = Segment(line, n * (ext.Value - here));
                    if (inn != null) item["interiorFace"] = Segment(line, n * (inn.Value - here));
                    if (ext != null && inn != null) item["centerline"] = Segment(line, n * ((ext.Value + inn.Value) / 2 - here));
                }
                else if (curve != null)
                {
                    item["axisPoints"] = new JArray(curve.Tessellate().Select(p => new JArray(Metric.Out2(p))));
                }
                result.Add(item);
            }
            return result;
        }

        private static JArray Segment(Line line, XYZ shift) =>
            new JArray(new JArray(Metric.Out2(line.GetEndPoint(0) + shift)), new JArray(Metric.Out2(line.GetEndPoint(1) + shift)));

        private static double? FacePosition(Wall w, ShellLayerType side, XYZ normal)
        {
            foreach (Reference r in HostObjectUtils.GetSideFaces(w, side))
                if (w.GetGeometryObjectFromReference(r) is PlanarFace f)
                    return normal.DotProduct(f.Origin);
            return null;
        }

        private static JArray Openings(Document doc, Level level, Outline area)
        {
            var result = new JArray();
            var filter = new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows });
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(filter).Cast<FamilyInstance>())
            {
                if (!OnLevel(fi, level) || !InRegion(fi, area)) continue;
                XYZ p = (fi.Location as LocationPoint)?.Point;
                double width = FirstPositive(fi, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);
                double height = FirstPositive(fi, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                result.Add(new JObject
                {
                    ["id"] = Utils.ElementIdExtensions.GetValue(fi.Id),
                    ["category"] = fi.Category?.Name,
                    ["family"] = fi.Symbol?.FamilyName,
                    ["type"] = fi.Name,
                    ["hostId"] = fi.Host == null ? null : (JToken)Utils.ElementIdExtensions.GetValue(fi.Host.Id),
                    ["point"] = p == null ? null : new JArray(Metric.Out(p)),
                    ["width"] = Metric.M(width),
                    ["height"] = Metric.M(height),
                    ["sill"] = Metric.M(fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0),
                    ["facing"] = new JArray(Math.Round(fi.FacingOrientation.X, 4), Math.Round(fi.FacingOrientation.Y, 4)),
                    ["hand"] = new JArray(Math.Round(fi.HandOrientation.X, 4), Math.Round(fi.HandOrientation.Y, 4)),
                    ["facingFlipped"] = fi.FacingFlipped,
                    ["handFlipped"] = fi.HandFlipped,
                    ["mirrored"] = fi.Mirrored
                });
            }
            return result;
        }

        /// <summary>Width and height live on the instance or on the type depending on the family.</summary>
        private static double FirstPositive(FamilyInstance fi, params BuiltInParameter[] candidates)
        {
            foreach (var bip in candidates)
            {
                double v = fi.get_Parameter(bip)?.AsDouble() ?? 0;
                if (v <= 0) v = fi.Symbol?.get_Parameter(bip)?.AsDouble() ?? 0;
                if (v > 0) return v;
            }
            return 0;
        }

        private static JArray Floors(Document doc, Level level, Outline area)
        {
            var result = new JArray();
            foreach (Floor f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                if (!OnLevel(f, level) || !InRegion(f, area)) continue;
                var loops = new JArray();
                foreach (Reference r in HostObjectUtils.GetTopFaces(f))
                {
                    if (!(f.GetGeometryObjectFromReference(r) is Face face)) continue;
                    // GetEdgesAsCurveLoops keeps the curves head to tail; raw edges may run backwards.
                    foreach (CurveLoop curveLoop in face.GetEdgesAsCurveLoops())
                    {
                        var pts = new JArray();
                        foreach (Curve c in curveLoop)
                        {
                            IList<XYZ> tess = c.Tessellate();
                            for (int i = 0; i < tess.Count - 1; i++) pts.Add(new JArray(Metric.Out2(tess[i])));
                        }
                        loops.Add(pts);
                    }
                }
                result.Add(new JObject
                {
                    ["id"] = Utils.ElementIdExtensions.GetValue(f.Id),
                    ["type"] = f.FloorType?.Name,
                    ["offset"] = Metric.M(f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0),
                    ["thickness"] = Metric.M(f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0),
                    ["areaM2"] = Math.Round((f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * Metric.SquareMetersPerSquareFoot, 2),
                    ["topLoops"] = loops
                });
            }
            return result;
        }

        private static JArray Rooms(Document doc, Level level, Outline area)
        {
            var result = new JArray();
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center };
            foreach (Room room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                if (room.Area <= 0 || !OnLevel(room, level) || !InRegion(room, area)) continue;
                var loops = new JArray();
                foreach (IList<BoundarySegment> loop in room.GetBoundarySegments(options))
                {
                    var pts = new JArray();
                    foreach (BoundarySegment s in loop)
                    {
                        IList<XYZ> tess = s.GetCurve().Tessellate();
                        for (int i = 0; i < tess.Count - 1; i++) pts.Add(new JArray(Metric.Out2(tess[i])));
                    }
                    loops.Add(pts);
                }
                XYZ p = (room.Location as LocationPoint)?.Point;
                result.Add(new JObject
                {
                    ["id"] = Utils.ElementIdExtensions.GetValue(room.Id),
                    ["number"] = room.Number,
                    ["name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                    ["department"] = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
                    ["areaM2"] = Math.Round(room.Area * Metric.SquareMetersPerSquareFoot, 2),
                    ["point"] = p == null ? null : new JArray(Metric.Out2(p)),
                    ["loopsAtWallCentres"] = loops
                });
            }
            return result;
        }

        private static JArray Boxes(Document doc, Level level, Outline area, IEnumerable<string> categoryNames)
        {
            var result = new JArray();
            foreach (string name in categoryNames)
            {
                if (!Enum.TryParse(name.StartsWith("OST_") ? name : "OST_" + name, out BuiltInCategory bic))
                    throw new ArgumentException("Unknown category '" + name + "'; use a BuiltInCategory name such as OST_Stairs");
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (!OnLevel(e, level, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)
                        || !InRegion(e, area)) continue;
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null) continue;
                    var item = new JObject
                    {
                        ["id"] = Utils.ElementIdExtensions.GetValue(e.Id),
                        ["category"] = e.Category?.Name,
                        ["type"] = doc.GetElement(e.GetTypeId())?.Name,
                        ["min"] = new JArray(Metric.Out(b.Min)),
                        ["max"] = new JArray(Metric.Out(b.Max))
                    };
                    if (e.Location is LocationPoint lp)
                    {
                        item["point"] = new JArray(Metric.Out(lp.Point));
                        try { item["rotationDeg"] = Math.Round(lp.Rotation * 180 / Math.PI, 2); }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { /* point without rotation */ }
                    }
                    result.Add(item);
                }
            }
            return result;
        }

        /// <summary>Curves of imported or linked CAD, flattened to plan, grouped by layer.</summary>
        private static JArray Cad(Document doc, Level level, Outline area, int maxCurves)
        {
            var result = new JArray();
            int total = 0;
            foreach (ImportInstance cad in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                if (level != null && cad.LevelId != ElementId.InvalidElementId && cad.LevelId != level.Id) continue;
                if (!InRegion(cad, area)) continue;
                var layers = new Dictionary<string, JArray>();
                Collect(doc, cad.get_Geometry(new Options()), Transform.Identity, layers, ref total, maxCurves);
                result.Add(new JObject
                {
                    ["id"] = Utils.ElementIdExtensions.GetValue(cad.Id),
                    ["name"] = doc.GetElement(cad.GetTypeId())?.Name,
                    ["viewSpecific"] = cad.ViewSpecific,
                    ["layers"] = new JObject(layers.Select(kv => new JProperty(kv.Key, kv.Value)))
                });
            }
            return result;
        }

        private static void Collect(Document doc, GeometryElement geometry, Transform transform, Dictionary<string, JArray> layers,
            ref int total, int max)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                if (total >= max) return;
                if (obj is GeometryInstance gi)
                {
                    Collect(doc, gi.GetSymbolGeometry(), transform.Multiply(gi.Transform), layers, ref total, max);
                    continue;
                }
                IList<XYZ> points = obj is Curve c ? c.Tessellate() : obj is PolyLine pl ? pl.GetCoordinates() : null;
                if (points == null || points.Count < 2) continue;
                string layer = (doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "?";
                if (!layers.TryGetValue(layer, out JArray list)) layers[layer] = list = new JArray();
                list.Add(new JArray(points.Select(p => new JArray(Metric.Out2(transform.OfPoint(p))))));
                total++;
            }
        }
    }
}
