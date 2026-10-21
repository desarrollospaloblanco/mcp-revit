using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Floors from polygons in metres (outer loop first, holes after), optionally repeated on several
    /// levels, or finish floors from room boundaries at the finish face.
    /// </summary>
    public class CreateFloorsCommand : DelegateCommand
    {
        public override string CommandName => "create_floors";

        public CreateFloorsCommand(UIApplication uiApp) : base(uiApp, "MCP: crear pisos")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var specs = (parameters?["floors"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            JObject fromRooms = parameters?["fromRooms"] as JObject;
            if (specs.Count == 0 && fromRooms == null) throw new ArgumentException("Give floors (polygons) or fromRooms");
            var levels = (parameters?["levels"] as JArray)?.Select(t => t.Value<string>()).ToList();
            bool replaceMark = parameters?["replacePrevious"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var created = new List<object>();
                ToolTransaction.Run(doc, "MCP: pisos", options, warnings, () =>
                {
                    if (replaceMark && !string.IsNullOrWhiteSpace(options.RunMark))
                    {
                        int removed = RunMarks.DeletePrevious(doc, options.RunMark);
                        if (removed > 0) notes.Add($"deleted {removed} elements of the previous run '{options.RunMark}'");
                    }
                    foreach (JObject spec in specs)
                    {
                        var targetLevels = levels ?? new List<string> { spec["level"]?.Value<string>() ?? throw new ArgumentException("every floor needs level (or give levels)") };
                        foreach (string levelName in targetLevels)
                        {
                            Level level = FindLevel(doc, levelName);
                            var loops = (spec["loops"] as JArray ?? throw new ArgumentException("every floor needs loops"))
                                .Select(l => Metric.Loop(l, level.Elevation)).ToList();
                            Floor floor = Create(doc, loops, FloorTypeId(doc, spec["type"]?.Value<string>()), level,
                                spec["structural"]?.Value<bool>() ?? false, notes);
                            SetOffset(floor, spec["offsetMeters"]?.Value<double>() ?? 0);
                            RunMarks.Set(floor, options.RunMark);
                            created.Add(Describe(floor, levelName));
                        }
                    }
                    if (fromRooms != null) FromRooms(doc, fromRooms, options, created, notes);
                });
                return new { document = doc.Title, created = created.Count, floors = created, notes, warnings };
            }, 900);
        }

        private static Floor Create(Document doc, List<CurveLoop> loops, ElementId typeId, Level level, bool structural, List<string> notes)
        {
#if REVIT2022_OR_GREATER
            return Floor.Create(doc, loops, typeId, level.Id, structural, null, 0);
#else
            var outer = new CurveArray();
            foreach (Curve c in loops[0]) outer.Append(c);
            if (loops.Count > 1) notes.Add("holes need Revit 2022 or later through the API; only the outer loop was used");
            return doc.Create.NewFloor(outer, (FloorType)doc.GetElement(typeId), level, structural);
#endif
        }

        private static void FromRooms(Document doc, JObject p, ToolOptions options, List<object> created, List<string> notes)
        {
            var rooms = ElementSelection.Resolve(doc, p, BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.Area > 0).ToList();
            string dept = p["departmentContains"]?.Value<string>();
            if (dept != null)
                rooms = rooms.Where(r => (r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "").IndexOf(dept, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            ElementId typeId = FloorTypeId(doc, p["type"]?.Value<string>());
            double offset = p["offsetMeters"]?.Value<double>() ?? 0;
            string where = (p["boundary"]?.Value<string>() ?? "finish").ToLowerInvariant();
            var boundary = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = where == "center" ? SpatialElementBoundaryLocation.Center : SpatialElementBoundaryLocation.Finish
            };
            foreach (Room room in rooms)
            {
                var loops = new List<CurveLoop>();
                foreach (IList<BoundarySegment> segs in room.GetBoundarySegments(boundary))
                {
                    var loop = new CurveLoop();
                    foreach (BoundarySegment s in segs)
                    {
                        Curve c = s.GetCurve();
                        loop.Append(c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, room.Level.Elevation - c.GetEndPoint(0).Z))));
                    }
                    loops.Add(loop);
                }
                if (loops.Count == 0) continue;
                // the outer loop goes first
                loops = loops.OrderByDescending(l => Math.Abs(Area(l))).ToList();
                try
                {
                    Floor floor = Create(doc, loops, typeId, room.Level, false, notes);
                    SetOffset(floor, offset);
                    RunMarks.Set(floor, options.RunMark);
                    created.Add(new { room = room.Number + " " + room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(), floor = Describe(floor, room.Level.Name) });
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException || ex is Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    notes.Add($"room {room.Number}: {ex.Message.Split('\n')[0]}");
                }
            }
        }

        private static double Area(CurveLoop loop)
        {
            double a = 0;
            foreach (Curve c in loop)
            {
                XYZ p = c.GetEndPoint(0), q = c.GetEndPoint(1);
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }

        private static void SetOffset(Floor floor, double meters)
        {
            if (Math.Abs(meters) > 1e-9) floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(Metric.Ft(meters));
        }

        /// <summary>The computed area exists only after a regeneration.</summary>
        private static object Describe(Floor f, string level)
        {
            f.Document.Regenerate();
            return new
            {
                id = ElementSelection.Id(f),
                type = f.FloorType?.Name,
                level,
                areaM2 = Math.Round((f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * Metric.SquareMetersPerSquareFoot, 2)
            };
        }

        internal static Level FindLevel(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name)
            ?? throw new InvalidOperationException("No level named '" + name + "'");

        internal static ElementId FloorTypeId(Document doc, string name)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
            if (string.IsNullOrWhiteSpace(name)) return types.First().Id;
            return (types.FirstOrDefault(t => t.Name == name) ?? throw new InvalidOperationException(
                "No floor type '" + name + "'. Types: " + string.Join(", ", types.Select(t => t.Name)))).Id;
        }
    }
}
