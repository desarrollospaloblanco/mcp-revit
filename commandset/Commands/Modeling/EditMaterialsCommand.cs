using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Materials: create, recolour (shaded color and, optionally, the render appearance, duplicating
    /// an appearance shared with other materials so they do not change too) and find where a
    /// material is used (category defaults, type layers, element counts by category).
    /// </summary>
    public class EditMaterialsCommand : DelegateCommand
    {
        public override string CommandName => "edit_materials";

        public EditMaterialsCommand(UIApplication uiApp) : base(uiApp, "MCP: materiales")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string action = (parameters?["action"]?.Value<string>() ?? "usage").ToLowerInvariant();
            var names = (parameters?["materials"] as JArray)?.Select(t => t.Value<string>()).ToList() ?? new List<string>();
            if (names.Count == 0) throw new ArgumentException("materials (names) is required");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
                switch (action)
                {
                    case "usage":
                        return new { document = doc.Title, usage = names.Select(n => Usage(doc, Find(all, n))) };
                    case "create":
                    case "color":
                        var result = new List<object>();
                        ToolTransaction.Run(doc, "MCP: materiales", options, warnings, () =>
                        {
                            foreach (string name in names)
                            {
                                Material m = all.FirstOrDefault(x => x.Name == name);
                                if (m == null)
                                {
                                    if (action != "create") throw new InvalidOperationException("No material '" + name + "'");
                                    m = (Material)doc.GetElement(Material.Create(doc, name));
                                    RunMarks.Set(m, options.RunMark);
                                }
                                Apply(doc, m, all, parameters, notes);
                                result.Add(new { name = m.Name, id = ElementSelection.Id(m), color = new[] { (int)m.Color.Red, m.Color.Green, m.Color.Blue }, transparency = m.Transparency });
                            }
                        });
                        return new { document = doc.Title, materials = result, notes, warnings };
                    default:
                        throw new ArgumentException("action must be usage, create or color");
                }
            }, 600);
        }

        private static Material Find(List<Material> all, string name) =>
            all.FirstOrDefault(x => x.Name == name) ?? throw new InvalidOperationException("No material '" + name + "'");

        private static void Apply(Document doc, Material m, List<Material> all, JObject p, List<string> notes)
        {
            if (p?["color"] is JToken c)
            {
                Color color = Rgb(c);
                m.Color = color;
                if (p?["surfacePattern"]?.Value<bool>() ?? false) m.SurfaceForegroundPatternColor = color;
                if (p?["renderAppearance"]?.Value<bool>() ?? true) Appearance(doc, m, all, color, p?["removeTexture"]?.Value<bool>() ?? false, notes);
            }
            if (p?["transparency"] != null) m.Transparency = Math.Max(0, Math.Min(100, p["transparency"].Value<int>()));
        }

        private static Color Rgb(JToken token)
        {
            if (token is JArray a && a.Count == 3) return new Color((byte)a[0].Value<int>(), (byte)a[1].Value<int>(), (byte)a[2].Value<int>());
            string hex = token.Value<string>().TrimStart('#');
            return new Color(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }

        /// <summary>
        /// Sets the diffuse color of the render appearance. An appearance shared with other materials is
        /// duplicated first. A texture on the color hides it, so removeTexture drops the texture.
        /// </summary>
        private static void Appearance(Document doc, Material m, List<Material> all, Color color, bool removeTexture, List<string> notes)
        {
            if (!(doc.GetElement(m.AppearanceAssetId) is AppearanceAssetElement asset))
            {
                // A new material has no appearance: give it a copy of a generic one from the model.
                AppearanceAssetElement template = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement))
                    .Cast<AppearanceAssetElement>().FirstOrDefault(a => a.GetRenderingAsset()?.FindByName(Generic.GenericDiffuse) != null);
                if (template == null)
                {
                    notes.Add(m.Name + ": no render appearance and no generic one to copy; only the shaded color changed");
                    return;
                }
                asset = template.Duplicate(UniqueAssetName(doc, m.Name));
                m.AppearanceAssetId = asset.Id;
            }
            if (all.Count(x => x.AppearanceAssetId == m.AppearanceAssetId) > 1)
            {
                string name = UniqueAssetName(doc, m.Name);
                asset = asset.Duplicate(name);
                m.AppearanceAssetId = asset.Id;
                notes.Add(m.Name + ": its appearance was shared, now it has its own '" + name + "'");
            }
            using (var scope = new AppearanceAssetEditScope(doc))
            {
                Asset editable = scope.Start(asset.Id);
                AssetPropertyDoubleArray4d diffuse = editable.FindByName(Generic.GenericDiffuse) as AssetPropertyDoubleArray4d
                                                     ?? FirstColor(editable);
                if (diffuse == null)
                {
                    scope.Cancel();
                    notes.Add(m.Name + ": the appearance has no color property; only the shaded color changed");
                    return;
                }
                if (diffuse.NumberOfConnectedProperties > 0)
                {
                    if (removeTexture) diffuse.RemoveConnectedAsset();
                    else notes.Add(m.Name + ": a texture covers the render color; pass removeTexture=true to drop it");
                }
                diffuse.SetValueAsColor(color);
                scope.Commit(true);
            }
        }

        private static string UniqueAssetName(Document doc, string baseName)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Select(a => a.Name));
            string name = baseName;
            for (int i = 1; names.Contains(name); i++) name = baseName + " " + i;
            return name;
        }

        private static AssetPropertyDoubleArray4d FirstColor(Asset asset)
        {
            for (int i = 0; i < asset.Size; i++)
                if (asset.Get(i) is AssetPropertyDoubleArray4d c && (c.Name.EndsWith("_color") || c.Name.Contains("diffuse")))
                    return c;
            return null;
        }

        private static object Usage(Document doc, Material m)
        {
            var categories = new List<string>();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.Material?.Id == m.Id) categories.Add(cat.Name);
                foreach (Category sub in cat.SubCategories)
                    if (sub.Material?.Id == m.Id) categories.Add(cat.Name + " / " + sub.Name);
            }
            var types = new FilteredElementCollector(doc).OfClass(typeof(HostObjAttributes)).Cast<HostObjAttributes>()
                .Where(t => t.GetCompoundStructure()?.GetLayers().Any(l => l.MaterialId == m.Id) == true)
                .Select(t => t.Category?.Name + " : " + t.Name).ToList();
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .Where(e => { try { return e.GetMaterialIds(false).Contains(m.Id); } catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; } })
                .GroupBy(e => e.Category.Name).ToDictionary(g => g.Key, g => g.Count());
            return new { material = m.Name, id = ElementSelection.Id(m), categoryDefaults = categories, typeLayers = types, elementsByCategory = elements };
        }
    }
}
