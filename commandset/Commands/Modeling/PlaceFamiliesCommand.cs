using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Loads a family when needed and places many instances in one call: at points in metres, or on
    /// top of selected elements (e.g. a tree in every planter), with level, offset, rotation, mirror,
    /// wall host and instance parameters. The offset is written to the instance's offset parameter
    /// after creation, because creating at an absolute height on a level counts the level twice.
    /// </summary>
    public class PlaceFamiliesCommand : DelegateCommand
    {
        public override string CommandName => "place_families";

        public PlaceFamiliesCommand(UIApplication uiApp) : base(uiApp, "MCP: colocar familias")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string familyPath = parameters?["familyPath"]?.Value<string>();
            string typeName = parameters?["type"]?.Value<string>() ?? throw new ArgumentException("type ('Family : Type') is required");
            var instances = (parameters?["instances"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            JObject onTop = parameters?["onTopOf"] as JObject;
            if (instances.Count == 0 && onTop == null) throw new ArgumentException("Give instances or onTopOf");
            bool replace = parameters?["replacePrevious"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var placed = new List<object>();
                ToolTransaction.Run(doc, "MCP: colocar " + typeName, options, warnings, () =>
                {
                    if (!string.IsNullOrWhiteSpace(familyPath)) Load(doc, familyPath, notes);
                    FamilySymbol symbol = Symbol(doc, typeName);
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    if (replace && !string.IsNullOrWhiteSpace(options.RunMark))
                    {
                        int removed = RunMarks.DeletePrevious(doc, options.RunMark);
                        if (removed > 0) notes.Add($"deleted {removed} elements of the previous run '{options.RunMark}'");
                    }
                    if (onTop != null) instances.AddRange(OnTopOf(doc, onTop));
                    foreach (JObject spec in instances)
                    {
                        try
                        {
                            FamilyInstance fi = Place(doc, symbol, spec, parameters, notes);
                            RunMarks.Set(fi, options.RunMark);
                            doc.Regenerate();
                            BoundingBoxXYZ box = fi.get_BoundingBox(null);
                            placed.Add(new
                            {
                                id = ElementSelection.Id(fi),
                                point = Metric.Out((fi.Location as LocationPoint)?.Point ?? XYZ.Zero),
                                bottomMeters = box == null ? (double?)null : Metric.M(box.Min.Z)
                            });
                        }
                        catch (Exception ex) when (ex is ArgumentException || ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
                        {
                            notes.Add($"instance at {spec["point"]?.ToString(Newtonsoft.Json.Formatting.None)}: {ex.Message.Split('\n')[0]}");
                        }
                    }
                });
                return new { document = doc.Title, type = typeName, placed = placed.Count, instances = placed, notes, warnings };
            }, 1800);
        }

        private class OverwriteFamily : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true;
            }
        }

        private static void Load(Document doc, string path, List<string> notes)
        {
            if (!System.IO.File.Exists(path)) throw new ArgumentException("familyPath does not exist: " + path);
            if (doc.LoadFamily(path, new OverwriteFamily(), out Family family) && family != null) notes.Add("loaded " + family.Name);
        }

        internal static FamilySymbol Symbol(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            return all.FirstOrDefault(s => s.FamilyName + " : " + s.Name == name)
                   ?? all.FirstOrDefault(s => s.Name == name)
                   ?? throw new InvalidOperationException("No family type '" + name + "'; use 'Family : Type' or load it with familyPath");
        }

        /// <summary>One instance spec per selected element: its box centre in plan, on its top face.</summary>
        private static IEnumerable<JObject> OnTopOf(Document doc, JObject p)
        {
            foreach (Element e in ElementSelection.Resolve(doc, p))
            {
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b == null) continue;
                ElementId levelId = ElementSelection.LevelOf(e);
                var level = doc.GetElement(levelId) as Level;
                if (level == null) continue;
                yield return new JObject
                {
                    ["point"] = new JArray(Metric.M((b.Min.X + b.Max.X) / 2), Metric.M((b.Min.Y + b.Max.Y) / 2)),
                    ["level"] = level.Name,
                    ["offsetMeters"] = Metric.M(b.Max.Z - level.Elevation) + (p["extraOffsetMeters"]?.Value<double>() ?? 0)
                };
            }
        }

        private static FamilyInstance Place(Document doc, FamilySymbol symbol, JObject spec, JObject defaults, List<string> notes)
        {
            string levelName = spec["level"]?.Value<string>() ?? defaults?["level"]?.Value<string>();
            Level level = levelName == null ? null : CreateFloorsCommand.FindLevel(doc, levelName);
            double offset = Metric.Ft(spec["offsetMeters"]?.Value<double>() ?? defaults?["offsetMeters"]?.Value<double>() ?? 0);
            XYZ xy = Metric.Point(spec["point"] ?? throw new ArgumentException("every instance needs point"));
            FamilyInstance fi;

            long? hostId = spec["hostId"]?.Value<long>();
            if (hostId != null)
            {
                var host = doc.GetElement(Utils.ElementIdExtensions.ToElementId(hostId.Value))
                           ?? throw new ArgumentException("hostId " + hostId + " not found");
                Level hostLevel = level ?? doc.GetElement(ElementSelection.LevelOf(host)) as Level;
                double z = (hostLevel?.Elevation ?? 0) + offset;
                fi = doc.Create.NewFamilyInstance(new XYZ(xy.X, xy.Y, z), symbol, host, hostLevel, StructuralType.NonStructural);
            }
            else if (level != null)
            {
                fi = doc.Create.NewFamilyInstance(new XYZ(xy.X, xy.Y, level.Elevation), symbol, level, StructuralType.NonStructural);
                // The level-based overload treats z as an offset in some families, so the offset is written
                // to whichever offset parameter the family exposes ("Elevation from Level" or "Offset from Host").
                foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
                {
                    Parameter off = fi.get_Parameter(bip);
                    if (off != null && !off.IsReadOnly) off.Set(offset);
                }
            }
            else
            {
                fi = doc.Create.NewFamilyInstance(new XYZ(xy.X, xy.Y, xy.Z + offset), symbol, StructuralType.NonStructural);
            }

            // the location is only up to date after a regeneration; rotating before it pivots on the origin
            doc.Regenerate();
            if (level != null && hostId == null && Math.Abs(offset) > 1e-6)
            {
                // some families keep "Elevation from Level" read-only: move them instead, and say so if even that fails
                double z = (fi.Location as LocationPoint)?.Point.Z ?? level.Elevation;
                double missing = level.Elevation + offset - z;
                if (Math.Abs(missing) > 0.003)
                {
                    ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, missing));
                    doc.Regenerate();
                    double now = (fi.Location as LocationPoint)?.Point.Z ?? z;
                    if (Math.Abs(level.Elevation + offset - now) > 0.003)
                        notes.Add($"{symbol.FamilyName}: the family does not allow an offset from its level; placed on the level");
                }
            }
            double rotation = spec["rotationDeg"]?.Value<double>() ?? defaults?["rotationDeg"]?.Value<double>() ?? 0;
            XYZ at = (fi.Location as LocationPoint)?.Point ?? xy;
            if (Math.Abs(rotation) > 1e-9)
                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(at, at + XYZ.BasisZ), rotation * Math.PI / 180);
            string mirror = spec["mirror"]?.Value<string>();
            if (mirror == "x" || mirror == "y")
            {
                // mirror in place: the plane passes through the insertion point
                Plane plane = Plane.CreateByNormalAndOrigin(mirror == "x" ? XYZ.BasisX : XYZ.BasisY, at);
                ElementTransformUtils.MirrorElements(doc, new List<ElementId> { fi.Id }, plane, false);
            }
            foreach (var prop in (spec["parameters"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                Metric.Set(fi.LookupParameter(prop.Name), prop.Value);
            return fi;
        }
    }
}
