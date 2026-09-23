using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>
    /// Traces the grid with railings so the setting-out run can be quantified.
    ///
    /// Revit has no element that measures the length of a grid layout, but a railing reports
    /// its length and schedules like any other element. A stripped railing type — one rail, no
    /// balusters, no posts — placed along each grid line turns the layout into a quantity.
    ///
    /// Each line is trimmed to its intersections with the outermost grids running the other
    /// way, so the result is a closed grid rather than the full extent of every line. The
    /// intersections are solved as infinite lines, so the command does not assume the grid is
    /// orthogonal.
    /// </summary>
    public class CreateGridRailingsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public string TypeName { get; private set; }
        public double BaseOffsetMeters { get; private set; }
        public string LevelName { get; private set; }
        public bool TrimToOuterGrids { get; private set; }
        public string CommentPrefix { get; private set; }
        public string AssemblyCode { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(
            string documentTitle,
            string typeName,
            double baseOffsetMeters,
            string levelName,
            bool trimToOuterGrids,
            string commentPrefix,
            string assemblyCode)
        {
            DocumentTitle = documentTitle;
            TypeName = string.IsNullOrWhiteSpace(typeName) ? "TRAZO DE EJES" : typeName;
            BaseOffsetMeters = baseOffsetMeters;
            LevelName = levelName;
            TrimToOuterGrids = trimToOuterGrids;
            CommentPrefix = commentPrefix ?? "EJE ";
            AssemblyCode = assemblyCode;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 120000)
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

                List<Level> levels = QuantificationUtils.GetLevelsByElevation(doc);
                if (levels.Count == 0)
                {
                    Result = Fail("The document has no levels");
                    return;
                }

                Level level = levels[0];
                if (!string.IsNullOrWhiteSpace(LevelName))
                {
                    foreach (Level candidate in levels)
                    {
                        if (string.Equals(candidate.Name, LevelName, StringComparison.OrdinalIgnoreCase))
                        {
                            level = candidate;
                            break;
                        }
                    }
                }

                // Grid geometry is read before anything is created: adding elements while a
                // FilteredElementCollector is being iterated invalidates the iterator.
                var names = new List<string>();
                var origins = new List<XYZ>();
                var directions = new List<XYZ>();
                foreach (Grid grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)))
                {
                    Curve curve = grid.Curve;
                    if (curve == null)
                        continue;

                    XYZ a = curve.GetEndPoint(0);
                    XYZ b = curve.GetEndPoint(1);
                    var direction = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                    if (direction.GetLength() < 1e-9)
                        continue;

                    names.Add(grid.Name);
                    origins.Add(new XYZ(a.X, a.Y, 0));
                    directions.Add(direction.Normalize());
                }

                if (names.Count == 0)
                {
                    Result = Fail("The document has no grids with a usable curve");
                    return;
                }

                int created = 0, replaced = 0, skipped = 0;
                double totalLength = 0;
                var perGrid = new Dictionary<string, double>();
                var problems = new List<string>();

                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Trace grid with railings"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    RailingType railingType = EnsureType(doc, out string typeNote);
                    if (railingType == null)
                    {
                        transaction.RollBack();
                        Result = Fail(typeNote);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(AssemblyCode))
                    {
                        Parameter code = railingType.LookupParameter("Assembly Code");
                        if (code != null && !code.IsReadOnly)
                            code.Set(AssemblyCode);
                    }

                    // Replace any previous run so the command is idempotent.
                    var stale = new List<ElementId>();
                    foreach (Element existing in new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StairsRailing)
                        .WhereElementIsNotElementType())
                    {
                        if (existing.GetTypeId() == railingType.Id)
                            stale.Add(existing.Id);
                    }
                    foreach (ElementId id in stale)
                    {
                        try
                        {
                            doc.Delete(id);
                            replaced++;
                        }
                        catch
                        {
                        }
                    }

                    double offset = BaseOffsetMeters / QuantificationUtils.FeetToMeters;

                    for (int i = 0; i < names.Count; i++)
                    {
                        double from, to;
                        if (!Extent(i, origins, directions, TrimToOuterGrids, out from, out to))
                        {
                            skipped++;
                            problems.Add(names[i] + ": no crossing grid to trim against");
                            continue;
                        }

                        var start = new XYZ(origins[i].X + directions[i].X * from, origins[i].Y + directions[i].Y * from, 0);
                        var end = new XYZ(origins[i].X + directions[i].X * to, origins[i].Y + directions[i].Y * to, 0);

                        var path = new CurveLoop();
                        try
                        {
                            path.Append(Line.CreateBound(start, end));
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            problems.Add(names[i] + ": " + ex.Message);
                            continue;
                        }

                        if (!Railing.IsValidPathForRailing(path))
                        {
                            skipped++;
                            problems.Add(names[i] + ": Revit rejected the path");
                            continue;
                        }

                        try
                        {
                            Railing railing = Railing.Create(doc, path, railingType.Id, level.Id);

                            // "Base Offset" is the vertical drop. "Offset from Path" is a lateral
                            // shift and setting that one instead deforms the trace.
                            Parameter baseOffset = railing.LookupParameter("Base Offset");
                            if (baseOffset != null && !baseOffset.IsReadOnly)
                                baseOffset.Set(offset);

                            Parameter comments = railing.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                            if (comments != null && !comments.IsReadOnly)
                                comments.Set(CommentPrefix + names[i]);

                            created++;
                            double length = to - from;
                            totalLength += length;
                            perGrid[names[i]] = Math.Round(length * QuantificationUtils.FeetToMeters, 3);
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            problems.Add(names[i] + ": " + ex.Message);
                        }
                    }

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "typeName", TypeName },
                    { "level", level.Name },
                    { "baseOffsetMeters", BaseOffsetMeters },
                    { "created", created },
                    { "replaced", replaced },
                    { "skipped", skipped },
                    { "totalLengthMeters", Math.Round(totalLength * QuantificationUtils.FeetToMeters, 3) },
                    { "byGrid", perGrid },
                    { "problems", problems }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Traced " + created + " grid lines in " + doc.Title + ", total " +
                              Math.Round(totalLength * QuantificationUtils.FeetToMeters, 2) + " m" + failures.Summarize(),
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

        /// <summary>
        /// Range of the grid line to trace, as parameters along its direction. With trimming on,
        /// it runs between the first and last crossing grid; otherwise the full drawn extent.
        /// </summary>
        private static bool Extent(
            int index,
            List<XYZ> origins,
            List<XYZ> directions,
            bool trim,
            out double from,
            out double to)
        {
            from = 0;
            to = 0;

            if (!trim)
            {
                to = 1;
                return false;
            }

            double min = double.MaxValue;
            double max = double.MinValue;
            int crossings = 0;

            for (int other = 0; other < origins.Count; other++)
            {
                if (other == index)
                    continue;

                double cross = directions[index].X * directions[other].Y - directions[index].Y * directions[other].X;
                if (Math.Abs(cross) < 1e-6)
                    continue;

                double wx = origins[other].X - origins[index].X;
                double wy = origins[other].Y - origins[index].Y;
                double t = (wx * directions[other].Y - wy * directions[other].X) / cross;

                if (t < min)
                    min = t;
                if (t > max)
                    max = t;
                crossings++;
            }

            if (crossings == 0 || max - min < 1e-6)
                return false;

            from = min;
            to = max;
            return true;
        }

        /// <summary>
        /// Finds or builds the tracing railing type: one rail, no balusters, no posts.
        /// </summary>
        private RailingType EnsureType(Document doc, out string note)
        {
            note = null;

            RailingType existing = null;
            RailingType template = null;
            foreach (RailingType candidate in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StairsRailing)
                .WhereElementIsElementType())
            {
                if (string.Equals(candidate.Name, TypeName, StringComparison.Ordinal))
                    existing = candidate;
                else if (template == null)
                    template = candidate;
            }

            if (existing == null)
            {
                if (template == null)
                {
                    note = "The document has no railing type to duplicate";
                    return null;
                }
                existing = template.Duplicate(TypeName) as RailingType;
            }

            if (existing == null)
            {
                note = "Could not create the railing type '" + TypeName + "'";
                return null;
            }

            try
            {
                NonContinuousRailStructure rails = existing.RailStructure;
                for (int i = rails.GetNonContinuousRailCount() - 1; i >= 0; i--)
                    rails.RemoveNonContinuousRail(i);
            }
            catch
            {
            }

            try
            {
                BalusterPlacement placement = existing.BalusterPlacement;
                BalusterPattern pattern = placement.BalusterPattern;
                for (int i = pattern.GetBalusterCount() - 1; i >= 0; i--)
                    pattern.RemoveBaluster(i);

                PostPattern posts = placement.PostPattern;
                posts.StartPost.BalusterFamilyId = ElementId.InvalidElementId;
                posts.CornerPost.BalusterFamilyId = ElementId.InvalidElementId;
                posts.EndPost.BalusterFamilyId = ElementId.InvalidElementId;
            }
            catch
            {
            }

            try
            {
                Parameter useTopRail = existing.LookupParameter("Use Top Rail");
                if (useTopRail != null && !useTopRail.IsReadOnly)
                    useTopRail.Set(1);
            }
            catch
            {
            }

            return existing;
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
            return "Trace grid with railings";
        }
    }
}
