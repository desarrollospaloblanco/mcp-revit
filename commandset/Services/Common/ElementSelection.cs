using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Services.Common
{
    /// <summary>
    /// The element filter the modelling tools share: explicit ids, or categories narrowed by level,
    /// type name, a plan region and a run mark.
    /// </summary>
    public static class ElementSelection
    {
        public static List<Element> Resolve(Document doc, JObject p, params BuiltInCategory[] defaultCategories)
        {
            var ids = (p?["elementIds"] as JArray)?.Select(t => Utils.ElementIdExtensions.ToElementId(t.Value<long>())).ToList();
            if (ids != null && ids.Count > 0)
                return ids.Select(doc.GetElement).Where(e => e != null).ToList();

            JObject f = p?["filter"] as JObject;
            var categories = (f?["categories"] as JArray)?.Select(t => ViewSetup.Category(t.Value<string>())).ToList()
                             ?? defaultCategories.ToList();
            if (categories.Count == 0)
                throw new ArgumentException("Give elementIds or filter.categories");
            string level = f?["level"]?.Value<string>();
            string typeContains = f?["typeContains"]?.Value<string>();
            string mark = f?["runMark"]?.Value<string>();
            BoundingBoxXYZ region = ViewSetup.Rectangle(f?["region"] as JObject);
            ElementId levelId = level == null ? null
                : (new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == level)
                   ?? throw new InvalidOperationException("No level named '" + level + "'")).Id;

            var result = new List<Element>();
            foreach (BuiltInCategory bic in categories)
            {
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (levelId != null && LevelOf(e) != levelId) continue;
                    if (typeContains != null && (doc.GetElement(e.GetTypeId())?.Name ?? "").IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (mark != null && RunMarks.Get(e) != mark) continue;
                    if (region != null)
                    {
                        BoundingBoxXYZ b = e.get_BoundingBox(null);
                        if (b == null || b.Max.X < region.Min.X || b.Min.X > region.Max.X || b.Max.Y < region.Min.Y || b.Min.Y > region.Max.Y) continue;
                    }
                    result.Add(e);
                }
            }
            return result;
        }

        /// <summary>The level an element is based on, whatever parameter carries it.</summary>
        public static ElementId LevelOf(Element e)
        {
            if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId) return e.LevelId;
            foreach (var bip in new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                         BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM })
            {
                ElementId id = e.get_Parameter(bip)?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            return ElementId.InvalidElementId;
        }

        public static long Id(Element e) => Utils.ElementIdExtensions.GetValue(e.Id);
    }
}
