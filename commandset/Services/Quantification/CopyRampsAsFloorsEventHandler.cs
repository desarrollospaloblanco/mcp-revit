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
    /// Replicates ramps as sloped floors so they can be quantified as slabs.
    ///
    /// A ramp is not a floor, so it never lands in a slab take-off. This copies the real
    /// geometry — the plan outline and the actual slope of the top face — into a floor of the
    /// given type.
    ///
    /// The slope comes from the measured face normal, not from the type name: in practice the
    /// two disagree, because names like "17%" often encode a 1:17 ratio instead.
    /// </summary>
    public class CopyRampsAsFloorsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public string FloorTypeName { get; private set; }
        public string Comment { get; private set; }
        public bool Structural { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(string documentTitle, string floorTypeName, string comment, bool structural)
        {
            DocumentTitle = documentTitle;
            FloorTypeName = floorTypeName;
            Comment = comment;
            Structural = structural;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 300000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
#if REVIT2022_OR_GREATER
            try
            {
                string error;
                Document doc = QuantificationUtils.ResolveDocument(app, DocumentTitle, out error);
                if (doc == null)
                {
                    Result = Fail(error);
                    return;
                }

                FloorType floorType = null;
                foreach (FloorType candidate in new FilteredElementCollector(doc).OfClass(typeof(FloorType)))
                {
                    if (string.Equals(candidate.Name, FloorTypeName, StringComparison.Ordinal))
                    {
                        floorType = candidate;
                        break;
                    }
                }

                if (floorType == null)
                {
                    Result = Fail("No floor type named '" + FloorTypeName + "' exists in " + doc.Title);
                    return;
                }

                var ramps = new List<Element>();
                foreach (Element ramp in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ramps)
                    .WhereElementIsNotElementType())
                {
                    ramps.Add(ramp);
                }

                if (ramps.Count == 0)
                {
                    Result = Fail("The document has no ramps");
                    return;
                }

                List<Level> levels = QuantificationUtils.GetLevelsByElevation(doc);
                if (levels.Count == 0)
                {
                    Result = Fail("The document has no levels");
                    return;
                }

                int created = 0, skipped = 0;
                double minSlope = double.MaxValue, maxSlope = 0;
                var pairs = new List<string>();
                var problems = new List<string>();

                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Copy ramps as floors"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    foreach (Element ramp in ramps)
                    {
                        PlanarFace top = TopFace(ramp);
                        if (top == null)
                        {
                            skipped++;
                            problems.Add(Utils.ElementIdExtensions.GetValue(ramp.Id) + ": no planar top face");
                            continue;
                        }

                        XYZ normal = top.FaceNormal;
                        double slope = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y) / normal.Z;

                        IList<CurveLoop> loops = top.GetEdgesAsCurveLoops();
                        if (loops == null || loops.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        var points = new List<XYZ>();
                        foreach (Curve curve in loops[0])
                            points.Add(curve.GetEndPoint(0));

                        // Direction of steepest ascent in plan, from the face normal.
                        XYZ ascent = (Math.Abs(normal.X) + Math.Abs(normal.Y) < 1e-9)
                            ? XYZ.BasisX
                            : new XYZ(-normal.X, -normal.Y, 0).Normalize();

                        XYZ low = points[0];
                        double lowT = double.MaxValue, highT = double.MinValue;
                        foreach (XYZ point in points)
                        {
                            double t = point.X * ascent.X + point.Y * ascent.Y;
                            if (t < lowT)
                            {
                                lowT = t;
                                low = point;
                            }
                            if (t > highT)
                                highT = t;
                        }

                        double baseElevation = low.Z;

                        var flat = new CurveLoop();
                        bool straight = true;
                        foreach (Curve curve in loops[0])
                        {
                            XYZ a = curve.GetEndPoint(0);
                            XYZ b = curve.GetEndPoint(1);
                            if (!(curve is Line))
                            {
                                straight = false;
                                break;
                            }
                            flat.Append(Line.CreateBound(
                                new XYZ(a.X, a.Y, baseElevation),
                                new XYZ(b.X, b.Y, baseElevation)));
                        }

                        if (!straight)
                        {
                            skipped++;
                            problems.Add(Utils.ElementIdExtensions.GetValue(ramp.Id) +
                                         ": the outline has arcs, which would distort when projected to plan");
                            continue;
                        }

                        Level host = levels[0];
                        foreach (Level level in levels)
                        {
                            if (level.Elevation <= baseElevation + 1e-6)
                                host = level;
                        }

                        try
                        {
                            var profile = new List<CurveLoop> { flat };
                            Floor floor;

                            if (Math.Abs(highT - lowT) < 1e-6 || slope < 1e-9)
                            {
                                floor = Floor.Create(doc, profile, floorType.Id, host.Id);
                            }
                            else
                            {
                                var tail = new XYZ(low.X, low.Y, baseElevation);
                                var head = new XYZ(
                                    tail.X + ascent.X * (highT - lowT),
                                    tail.Y + ascent.Y * (highT - lowT),
                                    baseElevation);

                                floor = Floor.Create(doc, profile, floorType.Id, host.Id,
                                    Structural, Line.CreateBound(tail, head), slope);
                            }

                            // Revit puts the floor at the level, so the offset lifts it back to
                            // the elevation the ramp actually starts at.
                            Parameter offset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                            if (offset != null && !offset.IsReadOnly)
                                offset.Set(baseElevation - host.Elevation);

                            if (!string.IsNullOrWhiteSpace(Comment))
                            {
                                Parameter comments = floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                                if (comments != null && !comments.IsReadOnly)
                                    comments.Set(Comment);
                            }

                            created++;
                            double percent = slope * 100.0;
                            if (percent < minSlope)
                                minSlope = percent;
                            if (percent > maxSlope)
                                maxSlope = percent;

                            if (pairs.Count < 60)
                            {
                                pairs.Add(Utils.ElementIdExtensions.GetValue(ramp.Id) + ">" +
                                          Utils.ElementIdExtensions.GetValue(floor.Id) + " " +
                                          Math.Round(percent, 2) + "%");
                            }
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            problems.Add(Utils.ElementIdExtensions.GetValue(ramp.Id) + ": " + ex.Message);
                        }
                    }

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "ramps", ramps.Count },
                    { "floorsCreated", created },
                    { "skipped", skipped },
                    { "minSlopePercent", created == 0 ? 0 : Math.Round(minSlope, 2) },
                    { "maxSlopePercent", Math.Round(maxSlope, 2) },
                    { "pairs", pairs },
                    { "problems", problems }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Created " + created + " floors from " + ramps.Count + " ramps in " +
                              doc.Title + failures.Summarize(),
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
#else
            Result = Fail("Creating a sloped floor from a profile requires Revit 2022 or newer");
            _resetEvent.Set();
#endif
        }

        /// <summary>Largest upward-facing planar face of an element.</summary>
        private static PlanarFace TopFace(Element element)
        {
            PlanarFace best = null;
            foreach (Solid solid in QuantificationUtils.GetSolids(element))
            {
                foreach (Face face in solid.Faces)
                {
                    var planar = face as PlanarFace;
                    if (planar == null || planar.FaceNormal.Z <= 0.01)
                        continue;
                    if (best == null || planar.Area > best.Area)
                        best = planar;
                }
            }
            return best;
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
            return "Copy ramps as floors";
        }
    }
}
