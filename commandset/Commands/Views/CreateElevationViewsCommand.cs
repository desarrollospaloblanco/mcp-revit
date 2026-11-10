using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Orthographic facade views (north, south, east, west) built as sections around a plan box,
    /// all sharing one box so several models can be compared. A side is named after the facade it
    /// shows: "north" looks south at the face whose outside points north. Sections cut the same box
    /// at a given coordinate (x = at or y = at) and look one way from there.
    /// </summary>
    public class CreateElevationViewsCommand : DelegateCommand
    {
        public override string CommandName => "create_elevation_views";

        public CreateElevationViewsCommand(UIApplication uiApp) : base(uiApp, "MCP: elevaciones")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            // Asking only for sections must not replace the facades, which may carry manual work.
            bool sectionsOnly = parameters?["sections"] is JArray && parameters?["sides"] == null;
            var sides = (parameters?["sides"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                        ?? (sectionsOnly ? new List<string>() : new List<string> { "north", "south", "east", "west" });
            string format = parameters?["nameFormat"]?.Value<string>() ?? "Elevación {side}";
            var sideNames = (parameters?["sideNames"] as JObject)?.Properties().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.Value<string>())
                            ?? new Dictionary<string, string> { { "north", "Norte" }, { "south", "Sur" }, { "east", "Oriente" }, { "west", "Poniente" } };

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var created = new List<object>();
                var sections = new List<object>();
                var sectionSpecs = (parameters?["sections"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                BoundingBoxXYZ plan = ViewSetup.Rectangle(parameters?["region"] as JObject)
                                      ?? ViewSetup.ModelExtent(doc, null, new[] { BuiltInCategory.OST_Walls })
                                      ?? throw new InvalidOperationException("No region given and no walls to frame");
                double margin = Metric.Ft(parameters?["marginMeters"]?.Value<double>() ?? 3);
                double offset = Metric.Ft(parameters?["offsetMeters"]?.Value<double>() ?? 10);
                double zMin = parameters?["bottomMeters"] != null ? Metric.Ft(parameters["bottomMeters"].Value<double>()) : plan.Min.Z;
                double zMax = parameters?["topMeters"] != null ? Metric.Ft(parameters["topMeters"].Value<double>()) : plan.Max.Z;
                if (zMax - zMin < 1) { zMin = -1; zMax = Metric.Ft(30); }

                ToolTransaction.Run(doc, "MCP: elevaciones", options, warnings, () =>
                {
                    ViewFamilyType type = ViewSetup.FamilyType(doc, ViewFamily.Section);
                    foreach (string side in sides)
                    {
                        // outward normal of the facade = where the viewer stands
                        XYZ toViewer = side == "north" ? XYZ.BasisY : side == "south" ? -XYZ.BasisY
                            : side == "east" ? XYZ.BasisX : side == "west" ? -XYZ.BasisX
                            : throw new ArgumentException("sides are north, south, east and west");
                        string name = format.Replace("{side}", sideNames.TryGetValue(side, out string n) ? n : side);
                        ViewSection old = ViewSetup.FindView<ViewSection>(doc, name);
                        if (old != null) doc.Delete(old.Id);

                        double cx = (plan.Min.X + plan.Max.X) / 2, cy = (plan.Min.Y + plan.Max.Y) / 2;
                        bool alongX = toViewer.Y != 0;
                        double halfWidth = (alongX ? plan.Max.X - plan.Min.X : plan.Max.Y - plan.Min.Y) / 2 + margin;
                        double halfDepth = (alongX ? plan.Max.Y - plan.Min.Y : plan.Max.X - plan.Min.X) / 2 + offset;

                        // Measured in Revit 2025: the section looks along BasisZ (ViewDirection comes back as
                        // -BasisZ), so BasisZ points from the viewer into the model. The box is centred on the
                        // model and reaches past both faces, so whichever end Revit cuts at, nothing is cut.
                        XYZ look = -toViewer;
                        Transform t = Transform.Identity;
                        t.Origin = new XYZ(cx, cy, 0);
                        t.BasisZ = look;
                        t.BasisY = XYZ.BasisZ;
                        t.BasisX = XYZ.BasisZ.CrossProduct(look);
                        var box = new BoundingBoxXYZ { Transform = t, Min = new XYZ(-halfWidth, zMin - margin, -halfDepth), Max = new XYZ(halfWidth, zMax + margin, halfDepth) };

                        ViewSection view = ViewSection.CreateSection(doc, type.Id, box);
                        view.Name = name;
                        if (parameters?["displayStyle"] == null) view.DisplayStyle = DisplayStyle.HLR;
                        view.CropBoxActive = true;
                        view.CropBoxVisible = false;
                        ViewSetup.Apply(doc, view, parameters, notes);
                        RunMarks.Set(view, options.RunMark);
                        created.Add(new { side, name, id = Utils.ElementIdExtensions.GetValue(view.Id), viewDirection = new[] { Math.Round(view.ViewDirection.X, 3), Math.Round(view.ViewDirection.Y, 3) } });
                    }

                    foreach (JObject spec in sectionSpecs)
                        sections.Add(CreateSection(doc, type, spec, plan, zMin, zMax, margin, parameters, options, notes));
                });
                return new { document = doc.Title, views = created, sections, notes, warnings };
            }, 300);
        }

        /// <summary>
        /// A section whose cut plane lies exactly on x = at or y = at. Measured in Revit 2025 (PDS
        /// session): the cut is made at the box's Min.Z and the view sees from there towards Max.Z,
        /// along BasisZ; a box from -depth to 0 cut at the far end and came out as a facade.
        /// </summary>
        private static object CreateSection(Document doc, ViewFamilyType type, JObject spec, BoundingBoxXYZ plan,
            double zMin, double zMax, double margin, JObject parameters, ToolOptions options, List<string> notes)
        {
            string name = spec["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("every section needs a name");
            string axis = (spec["axis"]?.Value<string>() ?? "").ToLowerInvariant();
            if (axis != "x" && axis != "y") throw new ArgumentException($"section '{name}': axis must be 'x' (plane x = at) or 'y' (plane y = at)");
            double at = Metric.Ft(spec["at"]?.Value<double>() ?? throw new ArgumentException($"section '{name}' needs 'at' in metres"));
            string lookName = (spec["look"]?.Value<string>() ?? (axis == "y" ? "north" : "east")).ToLowerInvariant();
            XYZ look = lookName == "north" ? XYZ.BasisY : lookName == "south" ? -XYZ.BasisY
                : lookName == "east" ? XYZ.BasisX : lookName == "west" ? -XYZ.BasisX
                : throw new ArgumentException($"section '{name}': look must be north, south, east or west");
            if ((axis == "y") != (look.Y != 0))
                throw new ArgumentException($"section '{name}': a plane {axis} = at is seen looking " + (axis == "y" ? "north or south" : "east or west"));

            double cx = (plan.Min.X + plan.Max.X) / 2, cy = (plan.Min.Y + plan.Max.Y) / 2;
            XYZ origin = axis == "y" ? new XYZ(cx, at, 0) : new XYZ(at, cy, 0);
            Transform t = Transform.Identity;
            t.Origin = origin;
            t.BasisZ = look;
            t.BasisY = XYZ.BasisZ;
            t.BasisX = XYZ.BasisZ.CrossProduct(look);

            var corners = new[] { new XYZ(plan.Min.X, plan.Min.Y, 0), new XYZ(plan.Max.X, plan.Min.Y, 0), new XYZ(plan.Max.X, plan.Max.Y, 0), new XYZ(plan.Min.X, plan.Max.Y, 0) };
            double xMin = corners.Min(c => (c - origin).DotProduct(t.BasisX)), xMax = corners.Max(c => (c - origin).DotProduct(t.BasisX));
            double far = corners.Max(c => (c - origin).DotProduct(look));
            double depth = spec["depthMeters"] != null ? Metric.Ft(spec["depthMeters"].Value<double>()) : far + margin;
            if (depth <= Metric.Ft(0.1))
                throw new ArgumentException($"section '{name}': nothing of the region lies {lookName} of the plane; use the opposite look or move 'at'");

            ViewSection old = ViewSetup.FindView<ViewSection>(doc, name);
            if (old != null)
            {
                try { doc.Delete(old.Id); }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new InvalidOperationException($"section '{name}' exists and Revit refused to delete it (is it open in a window? close it and retry): {ex.Message}");
                }
            }

            var box = new BoundingBoxXYZ { Transform = t, Min = new XYZ(xMin - margin, zMin - margin, 0), Max = new XYZ(xMax + margin, zMax + margin, depth) };
            ViewSection view = ViewSection.CreateSection(doc, type.Id, box);
            view.Name = name;
            if (parameters?["displayStyle"] == null) view.DisplayStyle = DisplayStyle.HLR;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
            ViewSetup.Apply(doc, view, parameters, notes);
            RunMarks.Set(view, options.RunMark);
            return new
            {
                name,
                id = Utils.ElementIdExtensions.GetValue(view.Id),
                plane = axis + " = " + Metric.M(at),
                look = lookName,
                depthMeters = Metric.M(depth),
                viewDirection = new[] { Math.Round(view.ViewDirection.X, 3), Math.Round(view.ViewDirection.Y, 3) }
            };
        }
    }
}
