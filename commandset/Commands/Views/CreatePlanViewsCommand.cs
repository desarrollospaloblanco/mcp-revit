using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Creates (or updates) plan views per level, or duplicates an existing view, and sets them up
    /// in one go: template, scale, detail, crop in metres or around the level's walls, view range,
    /// hidden categories and CAD, and grids trimmed to the crop.
    /// </summary>
    public class CreatePlanViewsCommand : DelegateCommand
    {
        public override string CommandName => "create_plan_views";

        public CreatePlanViewsCommand(UIApplication uiApp) : base(uiApp, "MCP: vistas de planta")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var levels = (parameters?["levels"] as JArray)?.Select(t => t.Value<string>()).ToList() ?? new List<string>();
            string duplicateOf = parameters?["duplicateOf"]?.Value<string>();
            var names = (parameters?["names"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (levels.Count == 0 && string.IsNullOrWhiteSpace(duplicateOf))
                throw new ArgumentException("Give levels to create plans on, or duplicateOf to copy an existing view");
            if (!string.IsNullOrWhiteSpace(duplicateOf) && (names == null || names.Count == 0))
                throw new ArgumentException("duplicateOf needs names for the copies");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var created = new List<object>();

                ToolTransaction.Run(doc, "MCP: vistas de planta", options, warnings, () =>
                {
                    var targets = new List<(ViewPlan view, bool isNew)>();
                    if (!string.IsNullOrWhiteSpace(duplicateOf))
                        targets.AddRange(Duplicate(doc, duplicateOf, names, parameters));
                    else
                        targets.AddRange(PerLevel(doc, levels, names, parameters));

                    doc.Regenerate();
                    foreach (var (view, isNew) in targets)
                    {
                        var viewNotes = new List<string>();
                        ViewSetup.Apply(doc, view, parameters, viewNotes);
                        SetViewRange(view, parameters?["viewRange"] as JObject, viewNotes);
                        BoundingBoxXYZ crop = Crop(doc, view, parameters, viewNotes);
                        if (crop != null && (parameters?["trimGrids"]?.Value<bool>() ?? false))
                            TrimGrids(doc, view, crop, viewNotes);
                        RunMarks.Set(view, options.RunMark);
                        notes.AddRange(viewNotes.Select(n => view.Name + ": " + n));
                        created.Add(new
                        {
                            id = Utils.ElementIdExtensions.GetValue(view.Id),
                            name = view.Name,
                            level = view.GenLevel?.Name,
                            created = isNew,
                            scale = view.Scale
                        });
                    }
                });
                return new { document = doc.Title, views = created, notes, warnings };
            }, 600);
        }

        private static IEnumerable<(ViewPlan, bool)> PerLevel(Document doc, List<string> levels, List<string> names, JObject p)
        {
            string kind = (p?["viewType"]?.Value<string>() ?? "floor").ToLowerInvariant();
            string format = p?["nameFormat"]?.Value<string>() ?? "{level}";
            ViewFamilyType type = ViewSetup.FamilyType(doc, kind == "ceiling" ? ViewFamily.CeilingPlan : kind == "structural" ? ViewFamily.StructuralPlan : ViewFamily.FloorPlan);
            for (int i = 0; i < levels.Count; i++)
            {
                Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == levels[i])
                              ?? throw new InvalidOperationException("No level named '" + levels[i] + "'");
                string name = names != null && i < names.Count ? names[i] : format.Replace("{level}", level.Name);
                ViewPlan existing = ViewSetup.FindView<ViewPlan>(doc, name);
                if (existing != null)
                {
                    yield return (existing, false);
                    continue;
                }
                ViewPlan view = ViewPlan.Create(doc, type.Id, level.Id);
                view.Name = name;
                yield return (view, true);
            }
        }

        private static IEnumerable<(ViewPlan, bool)> Duplicate(Document doc, string source, List<string> names, JObject p)
        {
            ViewPlan from = ViewSetup.FindView<ViewPlan>(doc, source) ?? throw new InvalidOperationException("No plan view named '" + source + "'");
            string mode = (p?["duplicateOption"]?.Value<string>() ?? "duplicate").ToLowerInvariant();
            ViewDuplicateOption option = mode == "withdetailing" ? ViewDuplicateOption.WithDetailing
                : mode == "dependent" ? ViewDuplicateOption.AsDependent : ViewDuplicateOption.Duplicate;
            foreach (string name in names)
            {
                ViewPlan existing = ViewSetup.FindView<ViewPlan>(doc, name);
                if (existing != null)
                {
                    yield return (existing, false);
                    continue;
                }
                var copy = (ViewPlan)doc.GetElement(from.Duplicate(option));
                copy.Name = name;
                yield return (copy, true);
            }
        }

        /// <summary>View range offsets in metres from the view's level: top, cut, bottom, depth.</summary>
        private static void SetViewRange(ViewPlan view, JObject range, List<string> notes)
        {
            if (range == null) return;
            ViewSetup.Try(notes, "viewRange", () =>
            {
                PlanViewRange r = view.GetViewRange();
                ElementId level = view.GenLevel.Id;
                void Set(PlanViewPlane plane, string key)
                {
                    double? v = range[key]?.Value<double>();
                    if (v == null) return;
                    r.SetLevelId(plane, level);
                    r.SetOffset(plane, Metric.Ft(v.Value));
                }
                Set(PlanViewPlane.TopClipPlane, "top");
                Set(PlanViewPlane.CutPlane, "cut");
                Set(PlanViewPlane.BottomClipPlane, "bottom");
                Set(PlanViewPlane.ViewDepthPlane, "depth");
                view.SetViewRange(r);
            });
        }

        /// <summary>Crop from a rectangle in metres, or around the level's walls plus a margin.</summary>
        private static BoundingBoxXYZ Crop(Document doc, ViewPlan view, JObject p, List<string> notes)
        {
            BoundingBoxXYZ rect = ViewSetup.Rectangle(p?["crop"] as JObject);
            if (rect == null && (p?["cropToModel"]?.Value<bool>() ?? false))
            {
                rect = ViewSetup.ModelExtent(doc, view.GenLevel, new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors });
                if (rect == null) { notes.Add("cropToModel: no walls or floors on the level"); return null; }
            }
            if (rect == null) return null;

            double margin = Metric.Ft(p?["cropMarginMeters"]?.Value<double>() ?? 1.0);
            var box = new BoundingBoxXYZ
            {
                Min = new XYZ(rect.Min.X - margin, rect.Min.Y - margin, view.CropBox.Min.Z),
                Max = new XYZ(rect.Max.X + margin, rect.Max.Y + margin, view.CropBox.Max.Z)
            };
            ViewSetup.Try(notes, "crop", () =>
            {
                // Plan crop boxes are in model coordinates when the view is not rotated.
                view.CropBox = box;
                view.CropBoxActive = true;
                view.CropBoxVisible = p?["cropVisible"]?.Value<bool>() ?? false;
                if (p?["annotationCrop"]?.Value<bool>() ?? false)
                    view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(1);
            });
            return box;
        }

        /// <summary>
        /// Grids drawn only inside the crop in this view: view-specific extents, same direction as
        /// the original so the bubbles stay on their side.
        /// </summary>
        private static void TrimGrids(Document doc, ViewPlan view, BoundingBoxXYZ box, List<string> notes)
        {
            foreach (Grid grid in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                ViewSetup.Try(notes, "grid " + grid.Name, () =>
                {
                    if (!(grid.GetCurvesInView(DatumExtentType.Model, view).FirstOrDefault() is Line line)) return;
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1), d = (b - a).Normalize();
                    double t0 = double.MinValue, t1 = double.MaxValue;
                    if (!Clip(a.X, d.X, box.Min.X, box.Max.X, ref t0, ref t1) || !Clip(a.Y, d.Y, box.Min.Y, box.Max.Y, ref t0, ref t1)) return;
                    grid.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific);
                    grid.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.ViewSpecific);
                    grid.SetCurveInView(DatumExtentType.ViewSpecific, view, Line.CreateBound(a + d * t0, a + d * t1));
                });
            }
        }

        private static bool Clip(double origin, double dir, double min, double max, ref double t0, ref double t1)
        {
            if (Math.Abs(dir) < 1e-9) return origin >= min && origin <= max;
            double ta = (min - origin) / dir, tb = (max - origin) / dir;
            t0 = Math.Max(t0, Math.Min(ta, tb));
            t1 = Math.Min(t1, Math.Max(ta, tb));
            return t1 > t0;
        }
    }
}
