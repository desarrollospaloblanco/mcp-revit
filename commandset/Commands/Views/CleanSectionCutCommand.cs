using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Finds what a section or elevation cuts lengthwise and hides it in that view only. Walls (or
    /// beams) running parallel to the cut plane with the plane inside their thickness are cut along
    /// their whole length and fill as solid black rectangles; optionally, elements of other
    /// categories whose box the plane crosses inside a plan region (a pile or column at the edge of
    /// the building) are added. unhide restores only what this tool hid.
    /// </summary>
    public class CleanSectionCutCommand : DelegateCommand
    {
        public override string CommandName => "clean_section_cut";

        public CleanSectionCutCommand(UIApplication uiApp) : base(uiApp, "MCP: limpiar corte de sección")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            JToken viewToken = parameters?["view"] ?? throw new ArgumentException("view (name or id of a section or elevation) is required");
            var titles = (parameters?["documents"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if ((titles == null || titles.Count == 0) && options.DocumentTitle != null) titles = new List<string> { options.DocumentTitle };
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;
            string action = (parameters?["action"]?.Value<string>() ?? "list").ToLowerInvariant();
            if (action != "list" && action != "hide" && action != "unhide")
                throw new ArgumentException("action must be list, hide or unhide");
            var parallelCategories = ((parameters?["categories"] as JArray)?.Select(t => ViewSetup.Category(t.Value<string>())).ToList()
                                      ?? new List<BuiltInCategory> { BuiltInCategory.OST_Walls });
            double tolerance = Metric.Ft(parameters?["toleranceMeters"]?.Value<double>() ?? 0.03);
            double angleTolerance = (parameters?["angleToleranceDegrees"]?.Value<double>() ?? 1) * Math.PI / 180;
            Crossing crossing = Crossing.From(parameters?["crossing"] as JObject);

            return RunInRevit(app =>
            {
                var docs = ToolDocuments.ResolveMany(app, titles);
                // Every view is resolved before anything changes, so a missing view stops the call
                // instead of leaving some models cleaned and others not.
                var targets = docs.Select(d => (doc: d, view: FindSection(d, viewToken))).ToList();
                var results = new List<object>();

                foreach (var (doc, view) in targets)
                {
                    var warnings = new List<string>();
                    var skipped = new List<object>();
                    CutPlane plane = CutPlane.Of(view);
                    var found = new List<Hit>();
                    int changed = 0;

                    if (action == "unhide")
                    {
                        ToolTransaction.Run(doc, "MCP: mostrar corte de sección", options, warnings,
                            () => { changed = Unhide(doc, view, found, skipped); }, dryRun);
                    }
                    else
                    {
                        found.AddRange(FindParallel(doc, view, plane, parallelCategories, tolerance, angleTolerance, skipped));
                        if (crossing != null)
                            found.AddRange(FindCrossing(doc, view, plane, crossing, tolerance, found.Select(h => h.Element.Id).ToList()));
                        if (action == "hide")
                            ToolTransaction.Run(doc, "MCP: ocultar corte de sección", options, warnings,
                                () => { changed = Hide(view, found, skipped); }, dryRun);
                    }

                    results.Add(new
                    {
                        document = doc.Title,
                        view = view.Name,
                        viewId = ElementSelection.Id(view),
                        plane = new { origin = Metric.Out(plane.Origin), normal = Round(plane.Normal) },
                        foundByCategory = found.GroupBy(h => h.Element.Category?.Name ?? "?").ToDictionary(g => g.Key, g => g.Count()),
                        hidden = action == "hide" ? changed : 0,
                        unhidden = action == "unhide" ? changed : 0,
                        elements = found.Select(h => Describe(doc, plane, h)).ToList(),
                        skipped,
                        warnings
                    });
                }
                return new { action, dryRun, documents = results };
            }, 300);
        }

        private class Hit
        {
            public Element Element;
            public string Reason;
            public XYZ Start, End;
            public double? SpanMin, SpanMax;
        }

        /// <summary>Optional second pass: other categories crossed by the plane, only inside a plan region.</summary>
        private class Crossing
        {
            public List<BuiltInCategory> Categories;
            public BoundingBoxXYZ Region;
            public string Level;

            public static Crossing From(JObject c)
            {
                if (c == null) return null;
                var categories = (c["categories"] as JArray)?.Select(t => ViewSetup.Category(t.Value<string>())).ToList();
                if (categories == null || categories.Count == 0)
                    throw new ArgumentException("crossing.categories is required, e.g. [\"OST_StructuralColumns\"]");
                // Every cut column shows as solid; hiding them all would empty the section, so a region is mandatory.
                BoundingBoxXYZ region = ViewSetup.Rectangle(c["region"] as JObject)
                                        ?? throw new ArgumentException("crossing.region {min:[x,y], max:[x,y]} is required so only the elements at that spot are taken");
                return new Crossing { Categories = categories, Region = region, Level = c["level"]?.Value<string>() };
            }
        }

        /// <summary>The cut plane of a section or elevation: the crop box face nearest to the viewer.</summary>
        private class CutPlane
        {
            public XYZ Origin, Normal, Along;

            public double Distance(XYZ p) => (p - Origin).DotProduct(Normal);

            public static CutPlane Of(ViewSection view)
            {
                BoundingBoxXYZ crop = view.CropBox;
                Transform t = crop.Transform;
                XYZ normal = view.ViewDirection.Normalize();
                double cx = (crop.Min.X + crop.Max.X) / 2, cy = (crop.Min.Y + crop.Max.Y) / 2;
                XYZ atMin = t.OfPoint(new XYZ(cx, cy, crop.Min.Z)), atMax = t.OfPoint(new XYZ(cx, cy, crop.Max.Z));
                // ViewDirection points towards the viewer, so the near (cut) face is the one further along it,
                // whichever sign the crop transform's BasisZ has.
                XYZ origin = atMax.DotProduct(normal) >= atMin.DotProduct(normal) ? atMax : atMin;
                return new CutPlane { Origin = origin, Normal = normal, Along = view.RightDirection.Normalize() };
            }
        }

        private static ViewSection FindSection(Document doc, JToken token)
        {
            View view;
            if (token.Type == JTokenType.Integer)
            {
                view = doc.GetElement(Utils.ElementIdExtensions.ToElementId(token.Value<long>())) as View
                       ?? throw new InvalidOperationException($"'{doc.Title}' has no view with id {token}");
            }
            else
            {
                string name = token.Value<string>();
                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && v.Name == name).ToList();
                if (views.Count == 0) throw new InvalidOperationException($"'{doc.Title}' has no view named '{name}'");
                view = views.FirstOrDefault(v => v is ViewSection) ?? views[0];
            }
            if (!(view is ViewSection section) || view.IsTemplate)
                throw new InvalidOperationException($"View '{view.Name}' in '{doc.Title}' is a {view.ViewType}, not a section or elevation");
            return section;
        }

        private static List<Hit> FindParallel(Document doc, View view, CutPlane plane, List<BuiltInCategory> categories,
            double tolerance, double angleTolerance, List<object> skipped)
        {
            var hits = new List<Hit>();
            double maxSine = Math.Sin(angleTolerance);
            foreach (BuiltInCategory bic in categories)
            {
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (!((e.Location as LocationCurve)?.Curve is Line axis))
                    {
                        // Only report the ones the plane actually reaches, not every curved wall in the view.
                        if (BoxSpan(e, plane, out double lo, out double hi) && lo <= tolerance && hi >= -tolerance)
                            skipped.Add(new { id = ElementSelection.Id(e), category = e.Category?.Name, reason = "crossed by the plane but has no straight location line; use crossing with a region" });
                        continue;
                    }
                    if (Math.Abs(axis.Direction.DotProduct(plane.Normal)) > maxSine) continue;
                    ThicknessSpan(e, axis, plane, out double min, out double max);
                    if (min > tolerance || max < -tolerance) continue;
                    hits.Add(new Hit { Element = e, Reason = "parallel", Start = axis.GetEndPoint(0), End = axis.GetEndPoint(1), SpanMin = min, SpanMax = max });
                }
            }
            return hits;
        }

        /// <summary>
        /// Signed distances of the element's solid from the plane, so the location line being on a
        /// face or the core, or a beam's justification, does not matter. Falls back to axis +- width/2.
        /// </summary>
        private static void ThicknessSpan(Element e, Line axis, CutPlane plane, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            foreach (Solid solid in Solids(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
                foreach (Edge edge in solid.Edges)
                    foreach (XYZ p in edge.Tessellate())
                    {
                        double d = plane.Distance(p);
                        if (d < min) min = d;
                        if (d > max) max = d;
                    }
            if (min <= max) return;

            double half = (e as Wall)?.Width / 2 ?? 0;
            double a = plane.Distance(axis.GetEndPoint(0)), b = plane.Distance(axis.GetEndPoint(1));
            min = Math.Min(a, b) - half;
            max = Math.Max(a, b) + half;
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (GeometryObject o in geometry)
            {
                if (o is Solid s && s.Volume > 0) yield return s;
                else if (o is GeometryInstance gi)
                    foreach (Solid inner in Solids(gi.GetInstanceGeometry()))
                        yield return inner;
            }
        }

        private static bool BoxSpan(Element e, CutPlane plane, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            BoundingBoxXYZ b = e.get_BoundingBox(null);
            if (b == null) return false;
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                    {
                        double d = plane.Distance(new XYZ(x, y, z));
                        min = Math.Min(min, d);
                        max = Math.Max(max, d);
                    }
            return true;
        }

        private static List<Hit> FindCrossing(Document doc, View view, CutPlane plane, Crossing crossing, double tolerance, List<ElementId> already)
        {
            ElementId levelId = crossing.Level == null ? null
                : (new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == crossing.Level)
                   ?? throw new InvalidOperationException($"'{doc.Title}' has no level named '{crossing.Level}'")).Id;
            BoundingBoxXYZ r = crossing.Region;
            var hits = new List<Hit>();
            foreach (BuiltInCategory bic in crossing.Categories)
            {
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (already.Contains(e.Id)) continue;
                    if (levelId != null && ElementSelection.LevelOf(e) != levelId) continue;
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null || b.Max.X < r.Min.X || b.Min.X > r.Max.X || b.Max.Y < r.Min.Y || b.Min.Y > r.Max.Y) continue;
                    if (!BoxSpan(e, plane, out double min, out double max) || min > tolerance || max < -tolerance) continue;
                    hits.Add(new Hit { Element = e, Reason = "crossing", Start = b.Min, End = b.Max, SpanMin = min, SpanMax = max });
                }
            }
            return hits;
        }

        private static int Hide(View view, List<Hit> found, List<object> skipped)
        {
            var ids = new List<ElementId>();
            foreach (Hit h in found)
            {
                if (h.Element.CanBeHidden(view)) ids.Add(h.Element.Id);
                else skipped.Add(new { id = ElementSelection.Id(h.Element), category = h.Element.Category?.Name, reason = "Revit does not allow hiding it in this view" });
            }
            if (ids.Count == 0) return 0;
            view.HideElements(ids);
            HiddenRecord.Write(view, HiddenRecord.Read(view).Union(ids).ToList());
            return ids.Count;
        }

        /// <summary>Unhides only the ids recorded on the view by earlier hides; what the user hid by hand stays hidden.</summary>
        private static int Unhide(Document doc, View view, List<Hit> found, List<object> skipped)
        {
            List<ElementId> recorded = HiddenRecord.Read(view);
            if (recorded.Count == 0) return 0;
            var ids = new List<ElementId>();
            foreach (ElementId id in recorded)
            {
                Element e = doc.GetElement(id);
                if (e == null) { skipped.Add(new { id = Utils.ElementIdExtensions.GetValue(id), reason = "deleted since it was hidden" }); continue; }
                if (!e.IsHidden(view)) { skipped.Add(new { id = ElementSelection.Id(e), category = e.Category?.Name, reason = "already visible" }); continue; }
                ids.Add(id);
                found.Add(new Hit { Element = e, Reason = "recorded" });
            }
            if (ids.Count > 0) view.UnhideElements(ids);
            HiddenRecord.Write(view, new List<ElementId>());
            return ids.Count;
        }

        private static object Describe(Document doc, CutPlane plane, Hit h)
        {
            Element e = h.Element;
            object distance = null, extent = null;
            if (h.Start != null && h.End != null)
            {
                distance = Metric.M(plane.Distance((h.Start + h.End) / 2));
                double a = (h.Start - plane.Origin).DotProduct(plane.Along), b = (h.End - plane.Origin).DotProduct(plane.Along);
                extent = new { from = Metric.Out(h.Start), to = Metric.Out(h.End), lengthAlongPlane = Metric.M(Math.Abs(b - a)) };
            }
            ElementId levelId = ElementSelection.LevelOf(e);
            return new
            {
                id = ElementSelection.Id(e),
                category = e.Category?.Name,
                type = doc.GetElement(e.GetTypeId())?.Name,
                level = levelId == ElementId.InvalidElementId ? null : doc.GetElement(levelId)?.Name,
                match = h.Reason,
                distanceFromPlane = distance,
                // Where the element's solid (or box) lies across the plane: the plane is inside when min <= 0 <= max.
                spanAcrossPlane = h.SpanMin == null ? null : new[] { Metric.M(h.SpanMin.Value), Metric.M(h.SpanMax.Value) },
                extent
            };
        }

        private static double[] Round(XYZ v) => new[] { Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4) };

        /// <summary>
        /// Ids this tool hid in a view, kept in extensible storage on the view itself. Storing it on the
        /// view (not on each element) means only the view is touched, which hiding needs anyway, so it
        /// works in workshared models without borrowing the walls.
        /// </summary>
        private static class HiddenRecord
        {
            private static readonly Guid SchemaGuid = new Guid("B3D4E2A1-7C55-4F0E-8A61-5E2C9D1F3A47");
            private const string Field = "HiddenIds";

            private static Schema Schema
            {
                get
                {
                    Schema schema = Schema.Lookup(SchemaGuid);
                    if (schema != null) return schema;
                    var builder = new SchemaBuilder(SchemaGuid);
                    builder.SetSchemaName("RevitMcpSectionCutHidden");
                    builder.SetReadAccessLevel(AccessLevel.Public);
                    builder.SetWriteAccessLevel(AccessLevel.Public);
                    builder.AddArrayField(Field, typeof(ElementId));
                    return builder.Finish();
                }
            }

            public static List<ElementId> Read(View view)
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return new List<ElementId>();
                Entity entity = view.GetEntity(schema);
                return entity.IsValid() ? entity.Get<IList<ElementId>>(Field).ToList() : new List<ElementId>();
            }

            /// <summary>Must run inside a transaction.</summary>
            public static void Write(View view, List<ElementId> ids)
            {
                var entity = new Entity(Schema);
                entity.Set<IList<ElementId>>(Field, ids);
                view.SetEntity(entity);
            }
        }
    }
}
