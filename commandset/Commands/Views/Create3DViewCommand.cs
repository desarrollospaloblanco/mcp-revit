using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// A saved isometric 3D view seen from a compass direction, with a section box around a plan
    /// rectangle and a level range, or around some elements.
    /// </summary>
    public class Create3DViewCommand : DelegateCommand
    {
        public override string CommandName => "create_3d_view";

        public Create3DViewCommand(UIApplication uiApp) : base(uiApp, "MCP: vista 3D")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string name = parameters?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                View3D view = null;
                bool isNew = false;
                ToolTransaction.Run(doc, "MCP: vista 3D " + name, options, warnings, () =>
                {
                    view = ViewSetup.FindView<View3D>(doc, name);
                    if (view == null)
                    {
                        view = View3D.CreateIsometric(doc, ViewSetup.FamilyType(doc, ViewFamily.ThreeDimensional).Id);
                        view.Name = name;
                        isNew = true;
                    }
                    if (view.IsPerspective) throw new InvalidOperationException(name + " is a perspective; use set_perspective_view");

                    view.SetOrientation(Orientation(parameters?["direction"]?.Value<string>() ?? "southwest"));
                    BoundingBoxXYZ box = SectionBox(doc, parameters);
                    if (box != null) view.SetSectionBox(box);
                    view.IsSectionBoxActive = box != null || view.IsSectionBoxActive;
                    if (parameters?["displayStyle"] == null) view.DisplayStyle = DisplayStyle.ShadingWithEdges;
                    ViewSetup.Apply(doc, view, parameters, notes);
                    RunMarks.Set(view, options.RunMark);
                });
                return new { document = doc.Title, view = view.Name, id = Utils.ElementIdExtensions.GetValue(view.Id), created = isNew, notes, warnings };
            }, 300);
        }

        private static ViewOrientation3D Orientation(string direction)
        {
            double x, y, z = -0.6;
            switch (direction.ToLowerInvariant())
            {
                case "southeast": x = -1; y = 1; break;
                case "northwest": x = 1; y = -1; break;
                case "northeast": x = -1; y = -1; break;
                case "north": x = 0; y = -1; break;
                case "south": x = 0; y = 1; break;
                case "east": x = -1; y = 0; break;
                case "west": x = 1; y = 0; break;
                case "top": x = 0; y = 0.0001; z = -1; break;
                default: x = 1; y = 1; break;
            }
            XYZ forward = new XYZ(x, y, z).Normalize();
            XYZ up = (XYZ.BasisZ - forward * XYZ.BasisZ.DotProduct(forward)).Normalize();
            return new ViewOrientation3D(XYZ.Zero - forward * 1000, up, forward);
        }

        /// <summary>Section box from region + level range, or around elementIds, plus padding.</summary>
        private static BoundingBoxXYZ SectionBox(Document doc, JObject p)
        {
            double pad = Metric.Ft(p?["paddingMeters"]?.Value<double>() ?? 1.0);
            var ids = (p?["elementIds"] as JArray)?.Select(t => Utils.ElementIdExtensions.ToElementId(t.Value<long>())).ToList();
            XYZ min = null, max = null;
            if (ids != null && ids.Count > 0)
            {
                foreach (ElementId id in ids)
                {
                    BoundingBoxXYZ b = doc.GetElement(id)?.get_BoundingBox(null);
                    if (b == null) continue;
                    min = min == null ? b.Min : new XYZ(Math.Min(min.X, b.Min.X), Math.Min(min.Y, b.Min.Y), Math.Min(min.Z, b.Min.Z));
                    max = max == null ? b.Max : new XYZ(Math.Max(max.X, b.Max.X), Math.Max(max.Y, b.Max.Y), Math.Max(max.Z, b.Max.Z));
                }
                if (min == null) throw new ArgumentException("None of elementIds has geometry");
            }
            else if (p?["region"] is JObject region)
            {
                BoundingBoxXYZ r = ViewSetup.Rectangle(region);
                min = r.Min; max = r.Max;
            }

            string bottom = p?["bottomLevel"]?.Value<string>(), top = p?["topLevel"]?.Value<string>();
            if (bottom != null || top != null)
            {
                double zb = bottom == null ? -1000 : LevelZ(doc, bottom) + Metric.Ft(p?["bottomOffsetMeters"]?.Value<double>() ?? 0);
                double zt = top == null ? 1000 : LevelZ(doc, top) + Metric.Ft(p?["topOffsetMeters"]?.Value<double>() ?? 0);
                if (min == null)
                {
                    BoundingBoxXYZ all = ViewSetup.ModelExtent(doc, null, new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors });
                    if (all == null) throw new InvalidOperationException("No walls or floors to frame");
                    min = all.Min; max = all.Max;
                }
                return new BoundingBoxXYZ { Min = new XYZ(min.X - pad, min.Y - pad, zb), Max = new XYZ(max.X + pad, max.Y + pad, zt) };
            }
            if (min == null) return null;
            return new BoundingBoxXYZ { Min = min - new XYZ(pad, pad, pad), Max = max + new XYZ(pad, pad, pad) };
        }

        private static double LevelZ(Document doc, string name) =>
            (new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name)
             ?? throw new InvalidOperationException("No level named '" + name + "'")).Elevation;
    }
}
