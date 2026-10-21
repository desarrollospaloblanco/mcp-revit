using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>
    /// Fixes for doors and windows (selection defaults to both categories): flip hand or facing,
    /// face a point (the room or corridor the door opens into), slide along the wall so a jamb sits
    /// a given distance from the nearest crossing wall, mirror across a plane, and re-host on another
    /// wall (the opening is recreated with the same type, flips and parameters).
    /// </summary>
    public class FixOpeningsCommand : DelegateCommand
    {
        public override string CommandName => "fix_openings";

        public FixOpeningsCommand(UIApplication uiApp) : base(uiApp, "MCP: corregir aberturas")
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
                var openings = ElementSelection.Resolve(doc, parameters, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows)
                    .OfType<FamilyInstance>().ToList();
                var warnings = new List<string>();
                var changes = new List<object>();
                var skipped = new List<object>();
                ToolTransaction.Run(doc, "MCP: aberturas " + op, options, warnings, () =>
                {
                    foreach (FamilyInstance fi in openings)
                    {
                        long id = ElementSelection.Id(fi);
                        try
                        {
                            object change;
                            switch (op)
                            {
                                case "fliphand": change = Flip(fi, hand: true); break;
                                case "flipfacing": change = Flip(fi, hand: false); break;
                                case "facepoint": change = FacePoint(fi, Metric.Point(parameters?["point"] ?? throw new ArgumentException("facePoint needs point"))); break;
                                case "jamb": change = Jamb(doc, fi, Metric.Ft(parameters?["jambMeters"]?.Value<double>() ?? 0.10), Metric.Ft(parameters?["searchMeters"]?.Value<double>() ?? 0.6)); break;
                                case "mirror": change = Mirror(doc, fi, parameters); break;
                                case "rehost": change = Rehost(doc, fi, parameters, options); break;
                                default: throw new ArgumentException("operation must be flipHand, flipFacing, facePoint, jamb, mirror or rehost");
                            }
                            if (change != null) changes.Add(new { id, change });
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                        {
                            skipped.Add(new { id, reason = ex.Message.Split('\n')[0] });
                        }
                    }
                }, dryRun);
                return new { document = doc.Title, operation = op, dryRun, selected = openings.Count, changed = changes.Count, changes, skipped, warnings };
            }, 900);
        }

        private static object Flip(FamilyInstance fi, bool hand)
        {
            if (hand) { if (!fi.CanFlipHand) throw new InvalidOperationException("cannot flip hand"); fi.flipHand(); }
            else { if (!fi.CanFlipFacing) throw new InvalidOperationException("cannot flip facing"); fi.flipFacing(); }
            return hand ? "hand flipped" : "facing flipped";
        }

        /// <summary>Flips the facing when it points away from the given point.</summary>
        private static object FacePoint(FamilyInstance fi, XYZ target)
        {
            XYZ at = (fi.Location as LocationPoint)?.Point ?? throw new InvalidOperationException("no insertion point");
            XYZ toTarget = new XYZ(target.X - at.X, target.Y - at.Y, 0);
            if (fi.FacingOrientation.DotProduct(toTarget) >= 0) return null;
            return Flip(fi, hand: false);
        }

        /// <summary>
        /// Slides the opening along its wall so its nearer side sits jamb metres from the face of the
        /// closest wall that crosses or abuts the host within the search distance.
        /// </summary>
        private static object Jamb(Document doc, FamilyInstance fi, double jamb, double search)
        {
            if (!(fi.Host is Wall host) || !((host.Location as LocationCurve)?.Curve is Line axis))
                throw new InvalidOperationException("not hosted by a straight wall");
            XYZ at = ((LocationPoint)fi.Location).Point;
            XYZ d = new XYZ(axis.Direction.X, axis.Direction.Y, 0).Normalize();
            double width = Width(fi);
            if (width <= 0) throw new InvalidOperationException("unknown width");
            double best = double.MaxValue, shift = 0;
            foreach (Wall other in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (other.Id == host.Id || !((other.Location as LocationCurve)?.Curve is Line oa)) continue;
                XYZ od = new XYZ(oa.Direction.X, oa.Direction.Y, 0).Normalize();
                if (Math.Abs(od.DotProduct(d)) > 0.1) continue; // only walls across the host
                // where the other wall's axis meets the host axis, measured along the host from the opening
                XYZ n = new XYZ(-od.Y, od.X, 0);
                double denom = d.DotProduct(n);
                double t = (oa.GetEndPoint(0) - at).DotProduct(n) / denom;
                // the other wall must reach the host line
                XYZ hit = at + d * t;
                double along = (hit - new XYZ(oa.GetEndPoint(0).X, oa.GetEndPoint(0).Y, hit.Z)).DotProduct(od);
                if (along < -host.Width || along > oa.Length + host.Width) continue;
                double face = Math.Abs(t) - other.Width / 2;      // from the opening centre to the near face
                double gap = face - width / 2;                    // from the opening side to that face
                if (gap < -width || gap > search || Math.Abs(t) >= best) continue;
                best = Math.Abs(t);
                shift = Math.Sign(t) * (gap - jamb);
            }
            if (best == double.MaxValue) throw new InvalidOperationException("no crossing wall within the search distance");
            if (Math.Abs(shift) < 1e-5) return null;
            ElementTransformUtils.MoveElement(doc, fi.Id, d * shift);
            return new { movedMeters = Metric.M(shift) };
        }

        private static double Width(FamilyInstance fi)
        {
            foreach (var bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                double v = fi.get_Parameter(bip)?.AsDouble() ?? 0;
                if (v <= 0) v = fi.Symbol?.get_Parameter(bip)?.AsDouble() ?? 0;
                if (v > 0) return v;
            }
            return 0;
        }

        /// <summary>Mirrors in place across the plane x = value or y = value (metres).</summary>
        private static object Mirror(Document doc, FamilyInstance fi, JObject p)
        {
            JObject plane = p?["plane"] as JObject ?? throw new ArgumentException("mirror needs plane {axis:'x'|'y', value}");
            bool x = plane["axis"]?.Value<string>() == "x";
            double v = Metric.Ft(plane["value"].Value<double>());
            Plane mirror = Plane.CreateByNormalAndOrigin(x ? XYZ.BasisX : XYZ.BasisY, x ? new XYZ(v, 0, 0) : new XYZ(0, v, 0));
            ElementTransformUtils.MirrorElements(doc, new List<ElementId> { fi.Id }, mirror, false);
            return "mirrored";
        }

        /// <summary>
        /// Recreates the opening on another wall at the same point: the given hostId, or the other
        /// wall passing through the opening (a duplicated wall). Type, flips and writable instance
        /// parameters are carried over; the old opening is deleted.
        /// </summary>
        private static object Rehost(Document doc, FamilyInstance fi, JObject p, ToolOptions options)
        {
            XYZ at = ((LocationPoint)fi.Location).Point;
            Wall target;
            long? hostId = p?["hostId"]?.Value<long>();
            if (hostId != null)
                target = doc.GetElement(Utils.ElementIdExtensions.ToElementId(hostId.Value)) as Wall ?? throw new InvalidOperationException("hostId is not a wall");
            else
            {
                target = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                    .Where(w => w.Id != fi.Host?.Id && (w.Location as LocationCurve)?.Curve is Line)
                    .Select(w => (w, d: ((LocationCurve)w.Location).Curve.Distance(new XYZ(at.X, at.Y, ((LocationCurve)w.Location).Curve.GetEndPoint(0).Z))))
                    .Where(t => t.d <= t.w.Width / 2 + 0.01)
                    .OrderBy(t => t.d).Select(t => t.w).FirstOrDefault()
                    ?? throw new InvalidOperationException("no other wall passes through the opening; give hostId");
            }
            Level level = doc.GetElement(fi.LevelId) as Level ?? doc.GetElement(target.LevelId) as Level;
            Curve curve = ((LocationCurve)target.Location).Curve;
            XYZ onTarget = curve.Project(new XYZ(at.X, at.Y, curve.GetEndPoint(0).Z)).XYZPoint;
            var values = fi.Parameters.Cast<Parameter>().Where(x => !x.IsReadOnly && x.HasValue && x.StorageType != StorageType.ElementId
                                                                    && x.Definition.Name != "Level")
                .Select(x => (name: x.Definition.Name, storage: x.StorageType, s: x.AsString(), i: x.AsInteger(), d: x.AsDouble())).ToList();
            bool hand = fi.HandFlipped, facing = fi.FacingFlipped;
            long oldId = ElementSelection.Id(fi);
            FamilyInstance created = doc.Create.NewFamilyInstance(new XYZ(onTarget.X, onTarget.Y, at.Z), fi.Symbol, target, level, StructuralType.NonStructural);
            doc.Regenerate();
            if (created.HandFlipped != hand && created.CanFlipHand) created.flipHand();
            if (created.FacingFlipped != facing && created.CanFlipFacing) created.flipFacing();
            foreach (var v in values)
            {
                Parameter prm = created.LookupParameter(v.name);
                if (prm == null || prm.IsReadOnly) continue;
                if (v.storage == StorageType.String) prm.Set(v.s ?? "");
                else if (v.storage == StorageType.Integer) prm.Set(v.i);
                else if (v.storage == StorageType.Double) prm.Set(v.d);
            }
            RunMarks.Set(created, options.RunMark);
            doc.Delete(fi.Id);
            return new { replacedBy = ElementSelection.Id(created), oldId, host = ElementSelection.Id(target) };
        }
    }
}
