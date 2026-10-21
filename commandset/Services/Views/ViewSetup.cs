using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>Settings shared by the view-creating tools: template, scale, detail, hidden categories.</summary>
    public static class ViewSetup
    {
        public static T FindView<T>(Document doc, string name) where T : View =>
            new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);

        public static View FindTemplate(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.IsTemplate && v.Name == name)
            ?? throw new InvalidOperationException("No view template named '" + name + "'");

        public static ViewFamilyType FamilyType(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == family)
            ?? throw new InvalidOperationException("The document has no view type of family " + family);

        public static BuiltInCategory Category(string name)
        {
            string key = name.StartsWith("OST_") ? name : "OST_" + name;
            if (!Enum.TryParse(key, out BuiltInCategory bic))
                throw new ArgumentException("Unknown category '" + name + "'; use a BuiltInCategory name such as OST_Furniture");
            return bic;
        }

        public static ViewDetailLevel Detail(string value)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "coarse": return ViewDetailLevel.Coarse;
                case "medium": return ViewDetailLevel.Medium;
                default: return ViewDetailLevel.Fine;
            }
        }

        /// <summary>
        /// Applies the common view settings found in the parameters. A template wins over the rest:
        /// settings it controls cannot be changed on the view, so they are skipped and reported.
        /// </summary>
        public static void Apply(Document doc, View view, JObject p, List<string> notes)
        {
            string template = p?["template"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(template))
                view.ViewTemplateId = template == "none" ? ElementId.InvalidElementId : FindTemplate(doc, template).Id;

            Try(notes, "scale", () => { int? s = p?["scale"]?.Value<int>(); if (s > 0) view.Scale = s.Value; });
            Try(notes, "detailLevel", () => { string d = p?["detailLevel"]?.Value<string>(); if (d != null) view.DetailLevel = Detail(d); });
            Try(notes, "displayStyle", () =>
            {
                string s = p?["displayStyle"]?.Value<string>();
                if (s != null) view.DisplayStyle = PresentationViewService.ParseStyle(s);
            });

            foreach (string name in (p?["hideCategories"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>())
                Try(notes, "hide " + name, () => view.SetCategoryHidden(new ElementId(Category(name)), true));

            if (p?["hideImports"]?.Value<bool>() ?? true)
            {
                var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                    .Where(i => i.CanBeHidden(view) && !i.IsHidden(view)).Select(i => i.Id).ToList();
                if (imports.Count > 0) Try(notes, "hide imports", () => view.HideElements(imports));
            }
        }

        public static void Try(List<string> notes, string what, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                notes.Add(what + ": " + ex.Message.Split('\n')[0].Trim());
            }
        }

        /// <summary>Plan rectangle {min:[x,y], max:[x,y]} in metres as an XY box in feet.</summary>
        public static BoundingBoxXYZ Rectangle(JObject rect, double zMin = -1000, double zMax = 1000)
        {
            if (rect == null) return null;
            XYZ a = Metric.Point(rect["min"]), b = Metric.Point(rect["max"]);
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Metric.Ft(zMin)),
                Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Metric.Ft(zMax))
            };
        }

        /// <summary>Box of the model elements of some categories on a level (walls by default).</summary>
        public static BoundingBoxXYZ ModelExtent(Document doc, Level level, IEnumerable<BuiltInCategory> categories)
        {
            XYZ min = null, max = null;
            foreach (BuiltInCategory bic in categories)
            {
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (level != null && e.LevelId != level.Id &&
                        e.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() != level.Id) continue;
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null) continue;
                    min = min == null ? b.Min : new XYZ(Math.Min(min.X, b.Min.X), Math.Min(min.Y, b.Min.Y), Math.Min(min.Z, b.Min.Z));
                    max = max == null ? b.Max : new XYZ(Math.Max(max.X, b.Max.X), Math.Max(max.Y, b.Max.Y), Math.Max(max.Z, b.Max.Z));
                }
            }
            return min == null ? null : new BoundingBoxXYZ { Min = min, Max = max };
        }
    }
}
