using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Commands.Views;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>
    /// Room clean-up and set-up: draw room separation lines, remove duplicated separation lines and
    /// those lying on a wall, delete unplaced, unenclosed or redundant rooms, place rooms in every
    /// enclosed region of a level, and set name, number and department of rooms by point.
    /// </summary>
    public class ManageRoomsCommand : DelegateCommand
    {
        public override string CommandName => "manage_rooms";

        public ManageRoomsCommand(UIApplication uiApp) : base(uiApp, "MCP: habitaciones")
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
                var warnings = new List<string>();
                object result = null;
                ToolTransaction.Run(doc, "MCP: habitaciones " + op, options, warnings, () =>
                {
                    switch (op)
                    {
                        case "separators": result = Separators(doc, parameters, options); break;
                        case "dedupe_separators": result = DedupeSeparators(doc, parameters); break;
                        case "delete_bad_rooms": result = DeleteBadRooms(doc, parameters); break;
                        case "place_all": result = PlaceAll(doc, parameters, options); break;
                        case "set_rooms": result = SetRooms(doc, parameters); break;
                        default: throw new ArgumentException("operation must be separators, dedupe_separators, delete_bad_rooms, place_all or set_rooms");
                    }
                }, dryRun);
                return new { document = doc.Title, operation = op, dryRun, result, warnings };
            }, 900);
        }

        private static Level Level(Document doc, JObject p) =>
            Modeling.CreateFloorsCommand.FindLevel(doc, p?["level"]?.Value<string>() ?? throw new ArgumentException("level is required"));

        private static object Separators(Document doc, JObject p, ToolOptions options)
        {
            View view = ExportViewsCommand.FindView(doc, p?["view"]?.Value<string>()) as ViewPlan
                        ?? throw new ArgumentException("view (a plan of the level) is required");
            double z = ((ViewPlan)view).GenLevel.Elevation;
            var curves = new CurveArray();
            foreach (JArray seg in (p?["lines"] as JArray ?? throw new ArgumentException("lines [[[x,y],[x,y]], ...] is required")).OfType<JArray>())
            {
                XYZ a = Metric.Point(seg[0]), b = Metric.Point(seg[1]);
                curves.Append(Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z)));
            }
            SketchPlane plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
            ModelCurveArray created = doc.Create.NewRoomBoundaryLines(plane, curves, view);
            foreach (ModelCurve c in created) RunMarks.Set(c, options.RunMark);
            return new { created = created.Size };
        }

        /// <summary>Separation lines that repeat another one, or run along a wall (the wall already bounds the room).</summary>
        private static object DedupeSeparators(Document doc, JObject p)
        {
            Level level = Level(doc, p);
            double tol = Metric.Ft(p?["toleranceMeters"]?.Value<double>() ?? 0.02);
            var lines = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomSeparationLines).WhereElementIsNotElementType()
                .OfType<CurveElement>().Where(c => c.LevelId == level.Id && c.GeometryCurve is Line).ToList();
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => ElementSelection.LevelOf(w) == level.Id && (w.Location as LocationCurve)?.Curve is Line).ToList();
            var delete = new List<ElementId>();
            var reasons = new Dictionary<string, int> { { "duplicate", 0 }, { "on a wall", 0 } };
            for (int i = 0; i < lines.Count; i++)
            {
                Line li = (Line)lines[i].GeometryCurve;
                bool dup = false;
                for (int j = 0; j < i && !dup; j++)
                    if (!delete.Contains(lines[j].Id) && Covers((Line)lines[j].GeometryCurve, li, tol)) dup = true;
                if (dup) { delete.Add(lines[i].Id); reasons["duplicate"]++; continue; }
                if (walls.Any(w => Covers((Line)((LocationCurve)w.Location).Curve, li, Math.Max(tol, w.Width / 2))))
                {
                    delete.Add(lines[i].Id);
                    reasons["on a wall"]++;
                }
            }
            if (delete.Count > 0) doc.Delete(delete);
            return new { checkedLines = lines.Count, deleted = delete.Count, reasons };
        }

        /// <summary>True when line b lies within tol of line a and inside its length.</summary>
        private static bool Covers(Line a, Line b, double tol)
        {
            XYZ a0 = new XYZ(a.GetEndPoint(0).X, a.GetEndPoint(0).Y, 0), a1 = new XYZ(a.GetEndPoint(1).X, a.GetEndPoint(1).Y, 0);
            Line flat = Line.CreateBound(a0, a1);
            foreach (XYZ q in new[] { b.GetEndPoint(0), b.GetEndPoint(1) })
                if (flat.Distance(new XYZ(q.X, q.Y, 0)) > tol) return false;
            return true;
        }

        /// <summary>Unplaced rooms, rooms not enclosed (area 0) and redundant rooms (two in one region).</summary>
        private static object DeleteBadRooms(Document doc, JObject p)
        {
            string levelName = p?["level"]?.Value<string>();
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                .Where(r => levelName == null || r.Level == null || r.Level.Name == levelName).ToList();
            var bad = new List<object>();
            var ids = new List<ElementId>();
            var regions = new Dictionary<string, Room>();
            foreach (Room r in rooms.OrderByDescending(r => r.Area))
            {
                string reason = r.Location == null ? "unplaced" : r.Area <= 0 ? "not enclosed" : null;
                if (reason == null)
                {
                    // two placed rooms with identical area and boundary box share one region: the smaller id stays
                    BoundingBoxXYZ b = r.get_BoundingBox(null);
                    string key = r.Level?.Name + "|" + Math.Round(r.Area, 3) + "|" + (b == null ? "" : $"{Math.Round(b.Min.X, 2)},{Math.Round(b.Min.Y, 2)}");
                    if (regions.ContainsKey(key)) reason = "redundant";
                    else regions[key] = r;
                }
                if (reason == null) continue;
                bad.Add(new { id = ElementSelection.Id(r), number = r.Number, name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(), reason });
                ids.Add(r.Id);
            }
            if (ids.Count > 0) doc.Delete(ids);
            return new { rooms = rooms.Count, deleted = ids.Count, details = bad };
        }

        private static object PlaceAll(Document doc, JObject p, ToolOptions options)
        {
            Level level = Level(doc, p);
            PlanTopology topology = doc.get_PlanTopology(level);
            var created = new List<object>();
            foreach (PlanCircuit circuit in topology.Circuits)
            {
                if (circuit.IsRoomLocated) continue;
                Room room = doc.Create.NewRoom(null, circuit);
                RunMarks.Set(room, options.RunMark);
                created.Add(new { id = ElementSelection.Id(room), number = room.Number });
            }
            return new { level = level.Name, created = created.Count, rooms = created };
        }

        /// <summary>Sets name, number, department and other parameters of the room at each point.</summary>
        private static object SetRooms(Document doc, JObject p)
        {
            Level level = Level(doc, p);
            var results = new List<object>();
            foreach (JObject spec in (p?["rooms"] as JArray ?? throw new ArgumentException("rooms [{point, name, ...}] is required")).OfType<JObject>())
            {
                XYZ pt = Metric.Point(spec["point"]);
                Room room = doc.GetRoomAtPoint(new XYZ(pt.X, pt.Y, level.Elevation + Metric.Ft(1)));
                if (room == null) { results.Add(new { point = spec["point"], error = "no room at this point" }); continue; }
                if (spec["name"] != null) room.get_Parameter(BuiltInParameter.ROOM_NAME).Set(spec["name"].Value<string>());
                if (spec["number"] != null) room.get_Parameter(BuiltInParameter.ROOM_NUMBER).Set(spec["number"].Value<string>());
                if (spec["department"] != null) room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT).Set(spec["department"].Value<string>());
                foreach (var prop in (spec["parameters"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    Metric.Set(room.LookupParameter(prop.Name), prop.Value);
                results.Add(new { id = ElementSelection.Id(room), number = room.Number, name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() });
            }
            return results;
        }
    }
}
