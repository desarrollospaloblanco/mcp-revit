using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Copies elements: to other levels (re-hosting each copy on the target level with the same
    /// height above it, so nothing ends up with the level elevation counted twice), to another open
    /// document at the same coordinates, or view-specific elements from one view to others.
    /// </summary>
    public class CopyElementsCommand : DelegateCommand
    {
        public override string CommandName => "copy_elements";

        public CopyElementsCommand(UIApplication uiApp) : base(uiApp, "MCP: copiar elementos")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string mode = (parameters?["mode"]?.Value<string>() ?? "levels").ToLowerInvariant();

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                switch (mode)
                {
                    case "levels": return ToLevels(doc, parameters, options, warnings, notes);
                    case "documents": return ToDocument(app, doc, parameters, options, warnings, notes);
                    case "view": return ToViews(app, doc, parameters, options, warnings);
                    default: throw new ArgumentException("mode must be levels, documents or view");
                }
            }, 1800);
        }

        private static CopyPasteOptions PasteOptions()
        {
            var o = new CopyPasteOptions();
            o.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
            return o;
        }

        private class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }

        private static object ToLevels(Document doc, JObject p, ToolOptions options, List<string> warnings, List<string> notes)
        {
            var elements = ElementSelection.Resolve(doc, p);
            if (elements.Count == 0) throw new InvalidOperationException("Nothing matches the selection");
            string sourceName = p?["sourceLevel"]?.Value<string>() ?? throw new ArgumentException("sourceLevel is required");
            var targets = (p?["targetLevels"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (targets == null || targets.Count == 0) throw new ArgumentException("targetLevels is required");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            Level source = levels.FirstOrDefault(l => l.Name == sourceName) ?? throw new InvalidOperationException("No level '" + sourceName + "'");
            // hosted elements travel with their host; copying them too would duplicate them
            var ids = elements.Where(e => !(e is FamilyInstance fi && fi.Host != null && elements.Any(h => h.Id == fi.Host.Id)))
                .Select(e => e.Id).ToList();

            var results = new List<object>();
            ToolTransaction.Run(doc, "MCP: copiar a niveles", options, warnings, () =>
            {
                foreach (string name in targets)
                {
                    Level target = levels.FirstOrDefault(l => l.Name == name) ?? throw new InvalidOperationException("No level '" + name + "'");
                    double delta = target.Elevation - source.Elevation;
                    var copies = ElementTransformUtils.CopyElements(doc, ids, new XYZ(0, 0, delta));
                    doc.Regenerate();
                    int releveled = 0;
                    foreach (ElementId id in copies)
                    {
                        Element e = doc.GetElement(id);
                        if (e == null) continue;
                        if (Relevel(e, target, delta, levels, notes)) releveled++;
                        RunMarks.Set(e, options.RunMark);
                    }
                    results.Add(new { level = name, copied = copies.Count, releveled });
                }
            });
            return new { document = doc.Title, selected = elements.Count, targets = results, notes = notes.Distinct().Take(50), warnings };
        }

        /// <summary>
        /// Puts a copied element on the target level keeping its absolute height. Walls also get their
        /// top level mapped by the same shift when such a level exists, otherwise an unconnected height.
        /// </summary>
        private static bool Relevel(Element e, Level target, double delta, List<Level> levels, List<string> notes)
        {
            Document doc = e.Document;
            if (e is Wall w)
            {
                double baseZ = Z(doc, w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)) + Off(w, BuiltInParameter.WALL_BASE_OFFSET);
                Parameter top = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                double height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
                ElementId oldTop = top?.AsElementId() ?? ElementId.InvalidElementId;
                double topZ = oldTop != ElementId.InvalidElementId ? Z(doc, top) + Off(w, BuiltInParameter.WALL_TOP_OFFSET) : 0;
                w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).Set(target.Id);
                w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(baseZ - target.Elevation);
                if (oldTop != ElementId.InvalidElementId)
                {
                    double wanted = ((Level)doc.GetElement(oldTop)).Elevation + delta;
                    Level mapped = levels.FirstOrDefault(l => Math.Abs(l.Elevation - wanted) < 0.01);
                    if (mapped != null && mapped.Id != oldTop) top.Set(mapped.Id);
                    else if (mapped == null)
                    {
                        top.Set(ElementId.InvalidElementId);
                        w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(topZ - baseZ);
                        notes.Add("no level above matches the shifted top; some walls got an unconnected height");
                    }
                }
                else if (height > 0) w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(height);
                return true;
            }
            foreach (var (level, offset) in new[]
                     {
                         (BuiltInParameter.LEVEL_PARAM, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
                         (BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM),
                         (BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM),
                         (BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM)
                     })
            {
                Parameter lp = e.get_Parameter(level), op = e.get_Parameter(offset);
                if (lp == null || lp.IsReadOnly || op == null || op.IsReadOnly) continue;
                double abs = Z(doc, lp) + op.AsDouble();
                lp.Set(target.Id);
                op.Set(abs - target.Elevation);
                if (e.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM) is Parameter tp && !tp.IsReadOnly)
                {
                    double wanted = Z(doc, tp) + delta;
                    Level mapped = levels.FirstOrDefault(l => Math.Abs(l.Elevation - wanted) < 0.01);
                    if (mapped != null) tp.Set(mapped.Id);
                }
                return true;
            }
            return false;
        }

        private static double Z(Document doc, Parameter levelParameter) =>
            (doc.GetElement(levelParameter?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Elevation ?? 0;

        private static double Off(Element e, BuiltInParameter bip) => e.get_Parameter(bip)?.AsDouble() ?? 0;

        private static object ToDocument(UIApplication app, Document source, JObject p, ToolOptions options, List<string> warnings, List<string> notes)
        {
            Document target = ToolDocuments.Resolve(app, p?["targetDocument"]?.Value<string>() ?? throw new ArgumentException("targetDocument is required"));
            if (target.Equals(source)) throw new ArgumentException("targetDocument is the source document");
            var ids = ElementSelection.Resolve(source, p).Select(e => e.Id).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("Nothing matches the selection");
            ICollection<ElementId> copies = null;
            ToolTransaction.Run(target, "MCP: copiar desde " + source.Title, options, warnings, () =>
            {
                copies = ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, PasteOptions());
                foreach (ElementId id in copies) RunMarks.Set(target.GetElement(id), options.RunMark);
            });
            return new { source = source.Title, target = target.Title, selected = ids.Count, copied = copies.Count, warnings };
        }

        private static object ToViews(UIApplication app, Document doc, JObject p, ToolOptions options, List<string> warnings)
        {
            View from = Views.ExportViewsCommand.FindView(doc, p?["sourceView"]?.Value<string>()) ?? throw new ArgumentException("sourceView not found");
            Document targetDoc = p?["targetDocument"] != null ? ToolDocuments.Resolve(app, p["targetDocument"].Value<string>()) : doc;
            var targets = (p?["targetViews"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (targets == null || targets.Count == 0) throw new ArgumentException("targetViews is required");
            var ids = p?["elementIds"] is JArray
                ? ElementSelection.Resolve(doc, p).Where(e => e.OwnerViewId == from.Id).Select(e => e.Id).ToList()
                : new FilteredElementCollector(doc, from.Id).OwnedByView(from.Id).WhereElementIsNotElementType()
                    .Where(e => e.Category != null).Select(e => e.Id).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("The source view owns no elements to copy");
            bool clear = p?["clearTarget"]?.Value<bool>() ?? false;

            var results = new List<object>();
            ToolTransaction.Run(targetDoc, "MCP: copiar contenido de vista", options, warnings, () =>
            {
                foreach (string name in targets)
                {
                    View to = Views.ExportViewsCommand.FindView(targetDoc, name) ?? throw new InvalidOperationException("No view '" + name + "' in " + targetDoc.Title);
                    if (clear)
                    {
                        var old = new FilteredElementCollector(targetDoc, to.Id).OwnedByView(to.Id).WhereElementIsNotElementType()
                            .Where(e => e.Category != null).Select(e => e.Id).ToList();
                        if (old.Count > 0) targetDoc.Delete(old);
                    }
                    var copies = ElementTransformUtils.CopyElements(from, ids, to, Transform.Identity, PasteOptions());
                    foreach (ElementId id in copies) RunMarks.Set(targetDoc.GetElement(id), options.RunMark);
                    results.Add(new { view = to.Name, copied = copies.Count });
                }
            });
            return new { source = from.Name, targetDocument = targetDoc.Title, elements = ids.Count, targets = results, warnings };
        }
    }
}
