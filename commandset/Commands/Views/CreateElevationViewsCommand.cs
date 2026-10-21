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
    /// shows: "north" looks south at the face whose outside points north.
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
            var sides = (parameters?["sides"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                        ?? new List<string> { "north", "south", "east", "west" };
            string format = parameters?["nameFormat"]?.Value<string>() ?? "Elevación {side}";
            var sideNames = (parameters?["sideNames"] as JObject)?.Properties().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.Value<string>())
                            ?? new Dictionary<string, string> { { "north", "Norte" }, { "south", "Sur" }, { "east", "Oriente" }, { "west", "Poniente" } };

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var created = new List<object>();
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
                });
                return new { document = doc.Title, views = created, notes, warnings };
            }, 300);
        }
    }
}
