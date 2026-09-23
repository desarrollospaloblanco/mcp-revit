using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>
    /// Finds structural elements that overlap without being joined, and colours them in a view.
    ///
    /// Joining two elements resolves their overlap, so anything still reported as intersecting
    /// is genuinely double-counted concrete. Same-category overlaps (column on column, beam on
    /// beam) are duplicates or hard clashes and come back red; cross-category intersections
    /// that were never joined come back yellow.
    ///
    /// The search runs against the document, not a view, so the result does not change when the
    /// user changes what is visible.
    /// </summary>
    public class AnalyzeClashesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public string ViewName { get; private set; }
        public List<BuiltInCategory> Categories { get; private set; }
        public bool ClearExisting { get; private set; }
        public bool ComputeVolumes { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(
            string documentTitle,
            string viewName,
            List<BuiltInCategory> categories,
            bool clearExisting,
            bool computeVolumes)
        {
            DocumentTitle = documentTitle;
            ViewName = viewName;
            Categories = categories;
            ClearExisting = clearExisting;
            ComputeVolumes = computeVolumes;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 180000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                string error;
                Document doc = QuantificationUtils.ResolveDocument(app, DocumentTitle, out error);
                if (doc == null)
                {
                    Result = Fail(error);
                    return;
                }

                View view = ResolveView(doc, app);
                if (view == null)
                {
                    Result = Fail("No usable view was found to colour the results in");
                    return;
                }

                var filter = new ElementMulticategoryFilter(Categories);
                var subjects = new FilteredElementCollector(doc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var red = new HashSet<long>();
                var yellow = new HashSet<long>();
                int samePairs = 0, crossPairs = 0;
                double overlapVolume = 0;
                var worst = new List<string>();

                foreach (Element element in subjects)
                {
                    var exclude = new List<ElementId> { element.Id };
                    var hits = new FilteredElementCollector(doc)
                        .WherePasses(filter)
                        .WhereElementIsNotElementType()
                        .Excluding(exclude)
                        .WherePasses(new ElementIntersectsElementFilter(element))
                        .ToElements();

                    foreach (Element other in hits)
                    {
                        bool joined = false;
                        try
                        {
                            joined = JoinGeometryUtils.AreElementsJoined(doc, element, other);
                        }
                        catch
                        {
                        }

                        if (joined)
                            continue;

                        bool sameCategory = element.Category != null && other.Category != null &&
                                            element.Category.Id == other.Category.Id;

                        long a = Utils.ElementIdExtensions.GetValue(element.Id);
                        long b = Utils.ElementIdExtensions.GetValue(other.Id);

                        if (sameCategory)
                        {
                            red.Add(a);
                            red.Add(b);
                        }
                        else
                        {
                            yellow.Add(a);
                            yellow.Add(b);
                        }

                        // Count each pair once, from the lower id.
                        if (b <= a)
                            continue;

                        if (sameCategory)
                            samePairs++;
                        else
                            crossPairs++;

                        if (!ComputeVolumes)
                            continue;

                        double volume = IntersectionVolume(element, other);
                        overlapVolume += volume;

                        if (sameCategory && worst.Count < 20)
                        {
                            worst.Add(a + "+" + b + " " +
                                      Math.Round(volume * QuantificationUtils.CubicFeetToCubicMeters, 4) + " m3 (" +
                                      (element.Category == null ? "?" : element.Category.Name) + ")");
                        }
                    }
                }

                // Red wins: an element in both sets is a hard clash.
                foreach (long id in red)
                    yellow.Remove(id);

                int painted = 0;
                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Colour clashes"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    ElementId solidFill = FindSolidFill(doc);

                    if (ClearExisting)
                    {
                        var blank = new OverrideGraphicSettings();
                        foreach (Element element in subjects)
                        {
                            try
                            {
                                view.SetElementOverrides(element.Id, blank);
                            }
                            catch
                            {
                            }
                        }
                    }

                    OverrideGraphicSettings redStyle = Build(new Color(255, 0, 0), solidFill);
                    OverrideGraphicSettings yellowStyle = Build(new Color(255, 255, 0), solidFill);

                    foreach (long id in red)
                    {
                        try
                        {
                            view.SetElementOverrides(Services.ParameterUtils.ToElementId(id), redStyle);
                            painted++;
                        }
                        catch
                        {
                        }
                    }

                    foreach (long id in yellow)
                    {
                        try
                        {
                            view.SetElementOverrides(Services.ParameterUtils.ToElementId(id), yellowStyle);
                            painted++;
                        }
                        catch
                        {
                        }
                    }

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "view", view.Name },
                    { "analysed", subjects.Count },
                    { "redElements", red.Count },
                    { "yellowElements", yellow.Count },
                    { "sameCategoryPairs", samePairs },
                    { "crossCategoryPairs", crossPairs },
                    { "overlapVolumeCubicMeters", Math.Round(overlapVolume * QuantificationUtils.CubicFeetToCubicMeters, 3) },
                    { "painted", painted },
                    { "worstSameCategory", worst }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Found " + samePairs + " same-category and " + crossPairs +
                              " cross-category overlaps in " + doc.Title + failures.Summarize(),
                    Response = payload
                };
            }
            catch (Exception ex)
            {
                Result = Fail(ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private double IntersectionVolume(Element a, Element b)
        {
            double total = 0;
            foreach (Solid first in QuantificationUtils.GetSolids(a))
            {
                foreach (Solid second in QuantificationUtils.GetSolids(b))
                {
                    try
                    {
                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            first, second, BooleanOperationsType.Intersect);
                        if (intersection != null)
                            total += intersection.Volume;
                    }
                    catch
                    {
                    }
                }
            }
            return total;
        }

        private View ResolveView(Document doc, UIApplication app)
        {
            if (!string.IsNullOrWhiteSpace(ViewName))
            {
                foreach (View candidate in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    if (!candidate.IsTemplate && string.Equals(candidate.Name, ViewName, StringComparison.Ordinal))
                        return candidate;
                }
                return null;
            }

            View active = app.ActiveUIDocument?.ActiveView;
            if (active != null && active.Document.Equals(doc) && !active.IsTemplate)
                return active;

            foreach (View3D candidate in new FilteredElementCollector(doc).OfClass(typeof(View3D)))
            {
                if (!candidate.IsTemplate)
                    return candidate;
            }

            return null;
        }

        private static ElementId FindSolidFill(Document doc)
        {
            foreach (FillPatternElement pattern in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
            {
                FillPattern fill = pattern.GetFillPattern();
                if (fill != null && fill.IsSolidFill)
                    return pattern.Id;
            }
            return ElementId.InvalidElementId;
        }

        private static OverrideGraphicSettings Build(Color color, ElementId solidFill)
        {
            var settings = new OverrideGraphicSettings();
            settings.SetProjectionLineColor(color);
            settings.SetSurfaceForegroundPatternColor(color);
            settings.SetSurfaceForegroundPatternVisible(true);
            settings.SetCutForegroundPatternColor(color);
            settings.SetCutForegroundPatternVisible(true);
            settings.SetSurfaceTransparency(0);

            if (solidFill != ElementId.InvalidElementId)
            {
                settings.SetSurfaceForegroundPatternId(solidFill);
                settings.SetCutForegroundPatternId(solidFill);
            }

            return settings;
        }

        private static AIResult<Dictionary<string, object>> Fail(string message)
        {
            return new AIResult<Dictionary<string, object>>
            {
                Success = false,
                Message = message
            };
        }

        public string GetName()
        {
            return "Analyze clashes";
        }
    }
}
