using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Lays out levels and grids in sections and elevations: level lines across the building with the
    /// head on a chosen side, grid lines from the crop bottom to above the top level, and a crop wide
    /// enough that no head is cut. Optionally extends the levels' 3D extents first so levels missing
    /// from a view (their extent stops short of the view's plane) show up.
    /// </summary>
    public class ArrangeDatumsCommand : DelegateCommand
    {
        public override string CommandName => "arrange_datums";

        private const double Tolerance = 1e-6;
        private const double EndPadPaperMm = 3;

        public ArrangeDatumsCommand(UIApplication uiApp) : base(uiApp, "MCP: niveles y ejes en vistas")
        {
        }

        private class DatumSettings
        {
            public string Mode;
            public JArray Include;
            public string Bubble;
            public double? Low, High;   // levels: metres beyond the region (left, right); grids: elevations (bottom, top)

            public static DatumSettings From(JObject p, string defaultBubble, string[] bubbles, string lowKey, string highKey, double? lowDefault, double? highDefault, string kind)
            {
                var s = new DatumSettings
                {
                    Mode = (p?["mode"]?.Value<string>() ?? "arrange").ToLowerInvariant(),
                    Include = p?["include"] as JArray,
                    Bubble = (p?["bubble"]?.Value<string>() ?? defaultBubble).ToLowerInvariant(),
                    Low = p?[lowKey]?.Value<double>() ?? lowDefault,
                    High = p?[highKey]?.Value<double>() ?? highDefault
                };
                if (s.Mode != "arrange" && s.Mode != "heads" && s.Mode != "skip")
                    throw new ArgumentException(kind + ".mode is arrange, heads or skip");
                if (!bubbles.Contains(s.Bubble))
                    throw new ArgumentException(kind + ".bubble is " + string.Join(", ", bubbles));
                return s;
            }
        }

        private class ViewReport
        {
            public List<string> LevelsAdjusted = new List<string>();
            public List<object> LevelsSkipped = new List<object>();
            public List<string> GridsAdjusted = new List<string>();
            public List<object> GridsSkipped = new List<object>();
            public List<string> Notes = new List<string>();
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var viewKeys = (parameters?["views"] as JArray)?.Select(t => t.ToString()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            if (viewKeys == null || viewKeys.Count == 0) throw new ArgumentException("views is required: names or ids of sections or elevations");
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;
            bool extend = parameters?["extendModelExtents"]?.Value<bool>() ?? false;
            double extendMargin = Metric.Ft(parameters?["extendMarginMeters"]?.Value<double>() ?? 2);
            var levelSettings = DatumSettings.From(parameters?["levels"] as JObject, "right", new[] { "right", "left", "both", "none" }, "leftMeters", "rightMeters", 1, 3, "levels");
            var gridSettings = DatumSettings.From(parameters?["grids"] as JObject, "top", new[] { "top", "bottom", "both", "none" }, "bottomMeters", "topMeters", null, null, "grids");
            var crop = parameters?["crop"] as JObject;
            double? cropBottom = crop?["bottomMeters"]?.Value<double>();
            double? cropTop = crop?["topMeters"]?.Value<double>();
            bool fitHeads = crop?["fitHeads"]?.Value<bool>() ?? true;
            double headPaperMm = crop?["headPaperMm"]?.Value<double>() ?? 25;
            if (cropBottom != null && cropTop != null && cropBottom >= cropTop)
                throw new ArgumentException("crop.bottomMeters must be below crop.topMeters");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var views = viewKeys.Select(k => FindSectionView(doc, k)).ToList();
                var levels = Pick<Level>(doc, levelSettings.Include, "level");
                var grids = Pick<Grid>(doc, gridSettings.Include, "grid");

                bool needsRegion = extend || levelSettings.Mode == "arrange";
                BoundingBoxXYZ region = !needsRegion ? null
                    : ViewSetup.Rectangle(parameters?["region"] as JObject)
                      ?? ViewSetup.ModelExtent(doc, null, new[] { BuiltInCategory.OST_Walls })
                      ?? throw new InvalidOperationException("No region given and no walls in " + doc.Title + " to measure it from");

                object extension = null;
                var reports = new List<object>();
                ToolTransaction.Run(doc, "MCP: niveles y ejes en vistas", options, warnings, () =>
                {
                    if (extend)
                    {
                        extension = ExtendLevels(doc, levels, views, region, extendMargin);
                        doc.Regenerate();
                    }
                    foreach (View view in views)
                        reports.Add(Arrange(doc, view, levels, grids, levelSettings, gridSettings, region,
                            cropBottom, cropTop, fitHeads, headPaperMm));
                }, dryRun);
                if (dryRun) warnings.Add("dryRun: everything was rolled back");
                return new { document = doc.Title, dryRun, levelExtents = extension, views = reports, warnings };
            }, 600);
        }

        private static View FindSectionView(Document doc, string key)
        {
            View view = ExportViewsCommand.FindView(doc, key)
                        ?? throw new InvalidOperationException("No view named or numbered '" + key + "' in " + doc.Title);
            if (!(view is ViewSection) || (view.ViewType != ViewType.Section && view.ViewType != ViewType.Elevation && view.ViewType != ViewType.Detail))
                throw new InvalidOperationException("View '" + view.Name + "' is a " + view.ViewType + "; arrange_datums works on sections and elevations");
            if (view.UpDirection.Z < 1 - 1e-3 || view.CropBox.Transform.BasisY.Z < 1 - 1e-3)
                throw new InvalidOperationException("View '" + view.Name + "' is not upright (its up direction is not vertical)");
            return view;
        }

        private static List<T> Pick<T>(Document doc, JArray include, string kind) where T : DatumPlane
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            var names = include?.Select(t => t.Value<string>()).ToList();
            if (names == null || names.Count == 0) return all;
            var missing = names.Where(n => !all.Any(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                throw new ArgumentException("No " + kind + " named " + string.Join(", ", missing.Select(m => "'" + m + "'")) + " in " + doc.Title);
            return all.Where(d => names.Any(n => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>Why the datum is not drawn in the view, or null when it is.</summary>
        private static string NotShown(DatumPlane datum, View view)
        {
            try
            {
                // CanBeVisibleInView is false (and GetCurvesInView throws) when the datum's 3D extent misses the view's plane.
                if (!datum.CanBeVisibleInView(view)) return "its 3D extent does not reach this view";
                if (datum.IsHidden(view)) return "hidden in this view";
                return null;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                return "Revit could not tell whether it shows here: " + FirstLine(ex.Message);
            }
        }

        private static bool CanShow(DatumPlane datum, View view, out string error)
        {
            error = null;
            try
            {
                return datum.CanBeVisibleInView(view);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                error = "Revit could not tell whether it shows here: " + FirstLine(ex.Message);
                return false;
            }
        }

        private static string FirstLine(string message) => (message ?? "").Split('\n')[0].Trim();

        /// <summary>
        /// Widens each level's 3D extent so it spans the region plus a margin and reaches the plane of
        /// every target view. A 3D extent can only be edited through a view that shows the datum, and
        /// each view edits it along its own horizontal axis, so the work goes through any section or
        /// elevation running along X and one running along Y. Extents are only ever lengthened.
        /// </summary>
        private static object ExtendLevels(Document doc, List<Level> levels, List<View> targets, BoundingBoxXYZ region, double margin)
        {
            var points = new List<XYZ>
            {
                new XYZ(region.Min.X - margin, region.Min.Y - margin, 0), new XYZ(region.Max.X + margin, region.Min.Y - margin, 0),
                new XYZ(region.Max.X + margin, region.Max.Y + margin, 0), new XYZ(region.Min.X - margin, region.Max.Y + margin, 0)
            };
            foreach (View view in targets) points.AddRange(NearFaceCorners(view));

            var carriers = targets.Concat(new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<View>()
                    .Where(v => !v.IsTemplate && (v.ViewType == ViewType.Section || v.ViewType == ViewType.Elevation)))
                .GroupBy(v => v.Id).Select(g => g.First())
                .Select(v => (view: v, axis: Math.Abs(v.RightDirection.X) > 1 - 1e-3 ? XYZ.BasisX : Math.Abs(v.RightDirection.Y) > 1 - 1e-3 ? XYZ.BasisY : null))
                .Where(c => c.axis != null).ToList();

            var done = levels.ToDictionary(l => l.Id, l => new List<string>());
            var failures = new Dictionary<string, string>();
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                foreach (Level level in levels)
                {
                    foreach (XYZ axis in new[] { XYZ.BasisX, XYZ.BasisY })
                    {
                        string axisName = axis.IsAlmostEqualTo(XYZ.BasisX) ? "x" : "y";
                        if (done[level.Id].Contains(axisName)) continue;
                        foreach (var carrier in carriers.Where(c => c.axis.IsAlmostEqualTo(axis)))
                        {
                            // hidden-in-view does not matter here: the 3D extent can be edited through any view that can show it
                            if (!CanShow(level, carrier.view, out string check))
                            {
                                if (check != null) failures[level.Id + axisName] = carrier.view.Name + ": " + check;
                                continue;
                            }
                            string error = ExtendAlong(level, carrier.view, axis, points);
                            if (error != null) { failures[level.Id + axisName] = carrier.view.Name + ": " + error; continue; }
                            done[level.Id].Add(axisName);
                            changed = true;
                            break;
                        }
                    }
                }
                if (!changed) break;
                // a level lengthened along one axis may now cross a view that lets us edit the other axis
                doc.Regenerate();
            }

            return new
            {
                extended = levels.Where(l => done[l.Id].Count > 0).Select(l => new { level = l.Name, axes = done[l.Id] }).ToList(),
                notExtended = levels.SelectMany(l => new[] { "x", "y" }.Except(done[l.Id]).Select(axis => new
                {
                    level = l.Name,
                    axis,
                    reason = failures.TryGetValue(l.Id + axis, out string f) ? f
                        : "no section or elevation running along " + axis + " shows the level, so its 3D extent cannot be edited along " + axis
                })).ToList()
            };
        }

        /// <summary>Plan corners of the crop box face nearest the viewer, where a section cuts.</summary>
        private static IEnumerable<XYZ> NearFaceCorners(View view)
        {
            BoundingBoxXYZ box = view.CropBox;
            Transform t = box.Transform;
            double near = t.BasisZ.DotProduct(view.ViewDirection) > 0 ? box.Max.Z : box.Min.Z;
            yield return t.OfPoint(new XYZ(box.Min.X, 0, near));
            yield return t.OfPoint(new XYZ(box.Max.X, 0, near));
        }

        private static string ExtendAlong(Level level, View view, XYZ axis, List<XYZ> points)
        {
            try
            {
                IList<Curve> curves = level.GetCurvesInView(DatumExtentType.Model, view);
                if (curves.Count == 0) return "no 3D curve in this view";
                XYZ a = curves[0].GetEndPoint(0), b = curves[0].GetEndPoint(1);
                double lo = Math.Min(points.Min(p => p.DotProduct(axis)), Math.Min(a.DotProduct(axis), b.DotProduct(axis)));
                double hi = Math.Max(points.Max(p => p.DotProduct(axis)), Math.Max(a.DotProduct(axis), b.DotProduct(axis)));
                double at = a.DotProduct(axis);
                Line line = Line.CreateBound(a + axis * (lo - at), a + axis * (hi - at));
                if (!level.IsCurveValidInView(DatumExtentType.Model, view, line)) return "Revit rejects the longer 3D extent";
                level.SetCurveInView(DatumExtentType.Model, view, line);
                return null;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                return FirstLine(ex.Message);
            }
        }

        private static object Arrange(Document doc, View view, List<Level> levels, List<Grid> grids, DatumSettings levelSettings, DatumSettings gridSettings,
            BoundingBoxXYZ region, double? cropBottom, double? cropTop, bool fitHeads, double headPaperMm)
        {
            var report = new ViewReport();
            bool cropChanged = false;
            if (cropBottom != null || cropTop != null)
            {
                BoundingBoxXYZ box = view.CropBox;
                double z0 = box.Transform.Origin.Z;
                double minY = cropBottom != null ? Metric.Ft(cropBottom.Value) - z0 : box.Min.Y;
                double maxY = cropTop != null ? Metric.Ft(cropTop.Value) - z0 : box.Max.Y;
                if (maxY - minY < Metric.Ft(0.1))
                    throw new InvalidOperationException("View '" + view.Name + "': the crop would be " + Metric.M(maxY - minY) + " m tall");
                box.Min = new XYZ(box.Min.X, minY, box.Min.Z);
                box.Max = new XYZ(box.Max.X, maxY, box.Max.Z);
                view.CropBox = box;
                view.CropBoxActive = true;
                cropChanged = true;
                doc.Regenerate();
            }
            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Levels))) report.Notes.Add("the Levels category is hidden in this view");
            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Grids))) report.Notes.Add("the Grids category is hidden in this view");

            XYZ right = view.RightDirection;
            double PaperFt(double paperMm) => Metric.Ft(paperMm / 1000.0 * view.Scale);
            var shownLevels = new List<Level>();
            var shownGrids = new List<Grid>();

            if (levelSettings.Mode != "skip")
            {
                double x0 = 0, x1 = 0;
                if (levelSettings.Mode == "arrange")
                {
                    var corners = new[] { region.Min, new XYZ(region.Max.X, region.Min.Y, 0), region.Max, new XYZ(region.Min.X, region.Max.Y, 0) };
                    x0 = corners.Min(p => p.DotProduct(right)) - Metric.Ft(levelSettings.Low.Value);
                    x1 = corners.Max(p => p.DotProduct(right)) + Metric.Ft(levelSettings.High.Value);
                    if (x1 - x0 < Metric.Ft(0.1))
                        throw new InvalidOperationException("View '" + view.Name + "': leftMeters and rightMeters leave the level lines with no length");
                }
                bool left = levelSettings.Bubble == "left" || levelSettings.Bubble == "both";
                bool rightHead = levelSettings.Bubble == "right" || levelSettings.Bubble == "both";
                foreach (Level level in levels)
                {
                    string reason = NotShown(level, view)
                                    ?? Adjust(level, view, levelSettings.Mode == "arrange", curve =>
                                    {
                                        XYZ q = curve.GetEndPoint(0);
                                        double at = q.DotProduct(right);
                                        return Line.CreateBound(q + right * (x0 - at), q + right * (x1 - at));
                                    }, p => p.DotProduct(right), left, rightHead);
                    if (reason == null) { report.LevelsAdjusted.Add(level.Name); shownLevels.Add(level); }
                    else report.LevelsSkipped.Add(new { name = level.Name, reason });
                }
            }

            double? gridBottom = null, gridTop = null;
            if (gridSettings.Mode != "skip")
            {
                if (gridSettings.Mode == "arrange")
                {
                    BoundingBoxXYZ box = view.CropBox;
                    gridBottom = gridSettings.Low != null ? Metric.Ft(gridSettings.Low.Value) : box.Transform.Origin.Z + box.Min.Y + Metric.Ft(0.2);
                    double? topLevel = HighestShownLevel(doc, view);
                    gridTop = gridSettings.High != null ? Metric.Ft(gridSettings.High.Value)
                        : topLevel != null ? topLevel.Value + Metric.Ft(1.5)
                        : box.Transform.Origin.Z + box.Max.Y - Metric.Ft(0.2);
                    if (topLevel == null && gridSettings.High == null) report.Notes.Add("no level shows in this view; grids run to 0.20 m below the crop top");
                    if (gridTop.Value - gridBottom.Value < Metric.Ft(0.1))
                        throw new InvalidOperationException("View '" + view.Name + "': grid bottom " + Metric.M(gridBottom.Value) + " m is not below grid top " + Metric.M(gridTop.Value) + " m");
                }
                bool bottom = gridSettings.Bubble == "bottom" || gridSettings.Bubble == "both";
                bool top = gridSettings.Bubble == "top" || gridSettings.Bubble == "both";
                foreach (Grid grid in grids)
                {
                    string reason = NotShown(grid, view)
                                    ?? Adjust(grid, view, gridSettings.Mode == "arrange", curve =>
                                    {
                                        XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
                                        if (Math.Abs(a.X - b.X) > 1e-4 || Math.Abs(a.Y - b.Y) > 1e-4) return null;
                                        return Line.CreateBound(new XYZ(a.X, a.Y, gridBottom.Value), new XYZ(a.X, a.Y, gridTop.Value));
                                    }, p => p.Z, bottom, top);
                    if (reason == null) { report.GridsAdjusted.Add(grid.Name); shownGrids.Add(grid); }
                    else report.GridsSkipped.Add(new { name = grid.Name, reason });
                }
            }

            if (fitHeads && view.CropBoxActive && (shownLevels.Count > 0 || shownGrids.Count > 0))
                cropChanged |= FitCrop(view, shownLevels, shownGrids, PaperFt(headPaperMm), PaperFt(EndPadPaperMm), cropBottom == null, cropTop == null);

            BoundingBoxXYZ final = view.CropBox;
            Transform t = final.Transform;
            return new
            {
                view = view.Name,
                id = Utils.ElementIdExtensions.GetValue(view.Id),
                levels = new { adjusted = report.LevelsAdjusted, skipped = report.LevelsSkipped },
                grids = new
                {
                    adjusted = report.GridsAdjusted,
                    skipped = report.GridsSkipped,
                    bottomMeters = gridBottom != null ? Metric.M(gridBottom.Value) : (double?)null,
                    topMeters = gridTop != null ? Metric.M(gridTop.Value) : (double?)null
                },
                crop = new
                {
                    active = view.CropBoxActive,
                    changed = cropChanged,
                    bottomMeters = Metric.M(t.Origin.Z + final.Min.Y),
                    topMeters = Metric.M(t.Origin.Z + final.Max.Y),
                    widthMeters = Metric.M(final.Max.X - final.Min.X),
                    leftEdge = Metric.Out2(t.OfPoint(new XYZ(final.Min.X, 0, 0))),
                    rightEdge = Metric.Out2(t.OfPoint(new XYZ(final.Max.X, 0, 0)))
                },
                notes = report.Notes
            };
        }

        /// <summary>
        /// Sets the datum's 2D extent in the view (when reshape gives a line) and its heads. Heads are
        /// chosen by position, not by End0/End1, because Revit may keep the curve's original direction.
        /// Returns why the datum was skipped, or null.
        /// </summary>
        private static string Adjust(DatumPlane datum, View view, bool reshapeLine, Func<Curve, Line> reshape, Func<XYZ, double> along, bool headAtLow, bool headAtHigh)
        {
            try
            {
                IList<Curve> curves = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                if (curves.Count != 1) return curves.Count == 0 ? "no curve in this view" : "drawn with " + curves.Count + " segments in this view";
                if (reshapeLine)
                {
                    Line line = reshape(curves[0]);
                    if (line == null) return "not perpendicular to this view, so it is not a vertical line here";
                    if (!datum.IsCurveValidInView(DatumExtentType.ViewSpecific, view, line)) return "Revit rejects the new 2D extent";
                    datum.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific);
                    datum.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.ViewSpecific);
                    datum.SetCurveInView(DatumExtentType.ViewSpecific, view, line);
                    curves = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                }
                bool end0Low = along(curves[0].GetEndPoint(0)) <= along(curves[0].GetEndPoint(1));
                ShowHead(datum, view, DatumEnds.End0, end0Low ? headAtLow : headAtHigh);
                ShowHead(datum, view, DatumEnds.End1, end0Low ? headAtHigh : headAtLow);
                return null;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                return FirstLine(ex.Message);
            }
        }

        private static void ShowHead(DatumPlane datum, View view, DatumEnds end, bool show)
        {
            if (show) datum.ShowBubbleInView(end, view);
            else datum.HideBubbleInView(end, view);
        }

        /// <summary>Elevation in feet of the highest level drawn in the view.</summary>
        private static double? HighestShownLevel(Document doc, View view)
        {
            double? top = null;
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (NotShown(level, view) != null) continue;
                IList<Curve> curves = level.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                if (curves.Count == 0) continue;
                double z = curves[0].GetEndPoint(0).Z;
                if (top == null || z > top) top = z;
            }
            return top;
        }

        /// <summary>
        /// Widens the crop so every datum end lies inside it, with room for a head where one shows:
        /// Revit clips datums at the crop, and a clipped end loses its head. Only widens; vertical edges
        /// the caller set explicitly stay where they were put.
        /// </summary>
        private static bool FitCrop(View view, List<Level> levels, List<Grid> grids, double head, double pad, bool moveBottom, bool moveTop)
        {
            BoundingBoxXYZ box = view.CropBox;
            Transform t = box.Transform;
            double minX = box.Min.X, maxX = box.Max.X, minY = box.Min.Y, maxY = box.Max.Y;

            void Reach(DatumPlane datum, Func<XYZ, double> local, Action<double> low, Action<double> high)
            {
                IList<Curve> curves = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                if (curves.Count == 0) return;
                for (int i = 0; i < 2; i++)
                {
                    XYZ end = curves[0].GetEndPoint(i);
                    XYZ other = curves[0].GetEndPoint(1 - i);
                    double room = datum.IsBubbleVisibleInView(i == 0 ? DatumEnds.End0 : DatumEnds.End1, view) ? head : pad;
                    if (local(end) <= local(other)) low(local(end) - room);
                    else high(local(end) + room);
                }
            }

            foreach (Level level in levels)
                Reach(level, p => (p - t.Origin).DotProduct(t.BasisX), v => minX = Math.Min(minX, v), v => maxX = Math.Max(maxX, v));
            foreach (Grid grid in grids)
                Reach(grid, p => (p - t.Origin).DotProduct(t.BasisY),
                    v => { if (moveBottom) minY = Math.Min(minY, v); },
                    v => { if (moveTop) maxY = Math.Max(maxY, v); });

            bool changed = minX < box.Min.X - Tolerance || maxX > box.Max.X + Tolerance || minY < box.Min.Y - Tolerance || maxY > box.Max.Y + Tolerance;
            if (!changed) return false;
            box.Min = new XYZ(minX, minY, box.Min.Z);
            box.Max = new XYZ(maxX, maxY, box.Max.Z);
            view.CropBox = box;
            return true;
        }
    }
}
