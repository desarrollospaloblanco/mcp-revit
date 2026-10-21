using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Commands.Views;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    /// <summary>
    /// Rebuilds the linear dimensions of a source plan in other plans, typically another level of a
    /// typical floor. Each reference is mapped to the target view: grids and elements visible in
    /// both views are kept; a wall or column face is replaced by the face of the element of the same
    /// category visible in the target view that lies in the same plane.
    /// </summary>
    public class CopyDimensionsCommand : DelegateCommand
    {
        public override string CommandName => "copy_dimensions";

        public CopyDimensionsCommand(UIApplication uiApp) : base(uiApp, "MCP: copiar cotas")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string sourceName = parameters?["sourceView"]?.Value<string>() ?? throw new ArgumentException("sourceView is required");
            var targetNames = (parameters?["targetViews"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (targetNames == null || targetNames.Count == 0) throw new ArgumentException("targetViews is required");
            bool replace = parameters?["replaceExisting"]?.Value<bool>() ?? false;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                View source = ExportViewsCommand.FindView(doc, sourceName) ?? throw new InvalidOperationException("No view '" + sourceName + "'");
                var dims = new FilteredElementCollector(doc, source.Id).OfClass(typeof(Dimension)).Cast<Dimension>()
                    .Where(d => !(d is SpotDimension) && d.Curve is Line).ToList();
                var warnings = new List<string>();
                var results = new List<object>();

                ToolTransaction.Run(doc, "MCP: copiar cotas desde " + source.Name, options, warnings, () =>
                {
                    foreach (string name in targetNames)
                    {
                        View target = ExportViewsCommand.FindView(doc, name) ?? throw new InvalidOperationException("No view '" + name + "'");
                        if (replace)
                        {
                            var old = new FilteredElementCollector(doc, target.Id).OfClass(typeof(Dimension))
                                .Where(d => !(d is SpotDimension)).Select(d => d.Id).ToList();
                            if (old.Count > 0) doc.Delete(old);
                        }
                        var visible = new HashSet<ElementId>(new FilteredElementCollector(doc, target.Id).WhereElementIsNotElementType().ToElementIds());
                        var pool = new FilteredElementCollector(doc, target.Id).WhereElementIsNotElementType()
                            .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model).ToList();
                        double z = (target as ViewPlan)?.GenLevel?.Elevation ?? 0;
                        int made = 0, lost = 0, unmatched = 0;

                        foreach (Dimension dim in dims)
                        {
                            var refs = new ReferenceArray();
                            foreach (Reference r in dim.References)
                            {
                                Reference mapped = Map(doc, r, visible, pool);
                                if (mapped == null) { unmatched++; continue; }
                                refs.Append(mapped);
                            }
                            if (refs.Size < 2) { lost++; continue; }
                            Line line = (Line)dim.Curve;
                            Line at = Line.CreateUnbound(new XYZ(line.Origin.X, line.Origin.Y, z), line.Direction);
                            try
                            {
                                Dimension created = doc.Create.NewDimension(target, at, refs, dim.DimensionType);
                                if (created == null) { lost++; continue; }
                                RunMarks.Set(created, options.RunMark);
                                made++;
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException) { lost++; }
                            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { lost++; }
                        }
                        results.Add(new { view = target.Name, created = made, lost, referencesWithoutMatch = unmatched });
                    }
                });
                return new { document = doc.Title, source = source.Name, dimensions = dims.Count, targets = results, warnings };
            }, 900);
        }

        private static Reference Map(Document doc, Reference r, HashSet<ElementId> visible, List<Element> pool)
        {
            Element e = doc.GetElement(r.ElementId);
            if (e == null) return null;
            if (e is Grid || e is Level || e is ReferencePlane || visible.Contains(e.Id)) return r;
            if (!(e.GetGeometryObjectFromReference(r) is PlanarFace face)) return null;

            XYZ n = face.FaceNormal;
            double d0 = n.X * face.Origin.X + n.Y * face.Origin.Y;
            BoundingBoxXYZ sb = e.get_BoundingBox(null);
            const double near = 1.0, plane = 0.005;
            var options = new Options { ComputeReferences = true };
            foreach (Element c in pool)
            {
                if (c.Category.Id != e.Category?.Id) continue;
                BoundingBoxXYZ cb = c.get_BoundingBox(null);
                if (cb == null || sb == null || cb.Max.X < sb.Min.X - near || cb.Min.X > sb.Max.X + near ||
                    cb.Max.Y < sb.Min.Y - near || cb.Min.Y > sb.Max.Y + near) continue;
                foreach (Solid solid in Solids(c.get_Geometry(options)))
                    foreach (Face f in solid.Faces)
                    {
                        if (!(f is PlanarFace q) || q.Reference == null) continue;
                        if (q.FaceNormal.DotProduct(n) < 0.9999) continue;
                        if (Math.Abs(n.X * q.Origin.X + n.Y * q.Origin.Y - d0) > plane) continue;
                        return q.Reference;
                    }
            }
            return null;
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (GeometryObject g in geometry)
            {
                if (g is Solid s && s.Faces.Size > 0) yield return s;
                else if (g is GeometryInstance gi)
                    foreach (Solid inner in Solids(gi.GetInstanceGeometry())) yield return inner;
            }
        }
    }
}
