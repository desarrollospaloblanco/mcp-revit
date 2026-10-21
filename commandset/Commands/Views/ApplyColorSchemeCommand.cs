using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Presentation of a room or area plan: a color fill scheme on the view (an existing one, or a
    /// new one by parameter), entry colors, a color legend and tags without leader on every room or
    /// area that has none. Color fill schemes need Revit 2022 or later through the API.
    /// </summary>
    public class ApplyColorSchemeCommand : DelegateCommand
    {
        private static readonly Dictionary<string, BuiltInParameter> BuiltIns = new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
        {
            { "Name", BuiltInParameter.ROOM_NAME },
            { "Number", BuiltInParameter.ROOM_NUMBER },
            { "Department", BuiltInParameter.ROOM_DEPARTMENT },
            { "Occupancy", BuiltInParameter.ROOM_OCCUPANCY },
            { "Area", BuiltInParameter.ROOM_AREA },
        };

        public override string CommandName => "apply_color_scheme";

        public ApplyColorSchemeCommand(UIApplication uiApp) : base(uiApp, "MCP: esquema de color")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string viewName = parameters?["view"]?.Value<string>() ?? throw new ArgumentException("view is required");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                View view = ExportViewsCommand.FindView(doc, viewName) ?? throw new InvalidOperationException("No view '" + viewName + "'");
                bool areas = view.ViewType == ViewType.AreaPlan;
                var category = new ElementId(areas ? BuiltInCategory.OST_Areas : BuiltInCategory.OST_Rooms);
                var warnings = new List<string>();
                var notes = new List<string>();
                object scheme = null;
                int tagged = 0;

                ToolTransaction.Run(doc, "MCP: esquema de color " + view.Name, options, warnings, () =>
                {
#if REVIT2022_OR_GREATER
                    scheme = Scheme(doc, view, category, parameters, notes);
                    if (scheme != null && (parameters?["legend"]?.Value<bool>() ?? true))
                        Legend(doc, view, category, parameters, options, notes);
#else
                    notes.Add("Color fill schemes need Revit 2022 or later through the API; only tags were placed");
#endif
                    if (parameters?["tags"]?.Value<bool>() ?? true)
                        tagged = Tag(doc, view, areas, options, notes);
                });
                return new { document = doc.Title, view = view.Name, category = areas ? "areas" : "rooms", scheme, tagged, notes, warnings };
            }, 300);
        }

#if REVIT2022_OR_GREATER
        private static object Scheme(Document doc, View view, ElementId category, JObject p, List<string> notes)
        {
            string name = p?["scheme"]?.Value<string>();
            string byParameter = p?["byParameter"]?.Value<string>();
            ElementId areaScheme = (view as ViewPlan)?.AreaScheme?.Id ?? ElementId.InvalidElementId;
            var schemes = new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme)).Cast<ColorFillScheme>()
                .Where(c => c.CategoryId == category && (areaScheme == ElementId.InvalidElementId || c.AreaSchemeId == areaScheme)).ToList();

            ColorFillScheme scheme = name == null ? null : schemes.FirstOrDefault(c => c.Name == name);
            if (scheme == null && byParameter != null)
            {
                ColorFillScheme source = schemes.FirstOrDefault()
                    ?? throw new InvalidOperationException("The document has no color scheme for this category to start from");
                scheme = (ColorFillScheme)doc.GetElement(source.Duplicate(name ?? ("Por " + byParameter)));
                scheme.ParameterDefinition = ParameterId(doc, category, byParameter);
                scheme.Title = p?["title"]?.Value<string>() ?? byParameter;
            }
            else if (scheme != null && byParameter != null)
            {
                scheme.ParameterDefinition = ParameterId(doc, category, byParameter);
            }
            if (scheme == null)
                throw new InvalidOperationException(name == null ? "Give scheme (an existing name) or byParameter to create one"
                    : $"No color scheme '{name}' for this view. Schemes: {string.Join(", ", schemes.Select(c => c.Name))}");

            try { view.SetColorFillSchemeId(category, scheme.Id); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                View template = doc.GetElement(view.ViewTemplateId) as View;
                if (template == null || !(p?["setOnTemplate"]?.Value<bool>() ?? false))
                    throw new InvalidOperationException($"Could not set the scheme on the view ({ex.Message.Split('\n')[0]}). " +
                        "If its template controls color schemes, pass setOnTemplate=true (changes every view using it).");
                template.SetColorFillSchemeId(category, scheme.Id);
                notes.Add("scheme set on the template " + template.Name);
            }
            doc.Regenerate();

            if (p?["colors"] is JObject colors)
            {
                var entries = scheme.GetEntries().ToList();
                foreach (var prop in colors.Properties())
                {
                    Color color = ParseColor(prop.Value);
                    ColorFillSchemeEntry entry = entries.FirstOrDefault(e => e.StorageType == StorageType.String && e.GetStringValue() == prop.Name);
                    if (entry == null)
                    {
                        if (scheme.StorageType != StorageType.String) { notes.Add($"'{prop.Name}': only text schemes get new entries"); continue; }
                        entry = new ColorFillSchemeEntry(StorageType.String) { Color = color, FillPatternId = SolidFill(doc) };
                        entry.SetStringValue(prop.Name);
                        scheme.AddEntry(entry);
                        continue;
                    }
                    if (entry.Color.Red == color.Red && entry.Color.Green == color.Green && entry.Color.Blue == color.Blue) continue;
                    entry.Color = color;
                    scheme.UpdateEntry(entry);
                }
            }

            if (p?["removeUnusedEntries"]?.Value<bool>() ?? true)
            {
                doc.Regenerate();
                foreach (ColorFillSchemeEntry entry in scheme.GetEntries().Where(x => !x.IsInUse).ToList())
                {
                    try { scheme.RemoveEntry(entry); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex) { notes.Add("entry kept: " + ex.Message.Split('\n')[0]); }
                }
            }

            return new
            {
                name = scheme.Name,
                parameter = Utils.ElementIdExtensions.GetValue(scheme.ParameterDefinition) < 0
                    ? LabelUtils.GetLabelFor((BuiltInParameter)Utils.ElementIdExtensions.GetValue(scheme.ParameterDefinition))
                    : doc.GetElement(scheme.ParameterDefinition)?.Name,
                entries = scheme.GetEntries().Select(e => new
                {
                    value = e.StorageType == StorageType.String ? e.GetStringValue() : e.GetDoubleValue().ToString(),
                    rgb = new[] { (int)e.Color.Red, e.Color.Green, e.Color.Blue }
                })
            };
        }

        private static ElementId ParameterId(Document doc, ElementId category, string name)
        {
            if (BuiltIns.TryGetValue(name, out BuiltInParameter bip)) return new ElementId(bip);
            Element sample = new FilteredElementCollector(doc).OfCategoryId(category).WhereElementIsNotElementType().FirstElement();
            Parameter prm = sample?.LookupParameter(name)
                            ?? throw new InvalidOperationException($"No parameter '{name}' on {(category.Equals(new ElementId(BuiltInCategory.OST_Areas)) ? "areas" : "rooms")}");
            return prm.Id;
        }

        private static ElementId SolidFill(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;

        private static Color ParseColor(JToken token)
        {
            if (token is JArray a && a.Count == 3) return new Color((byte)a[0].Value<int>(), (byte)a[1].Value<int>(), (byte)a[2].Value<int>());
            string hex = token.Value<string>().TrimStart('#');
            if (hex.Length != 6) throw new ArgumentException("colors are [r, g, b] or '#RRGGBB'");
            return new Color(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }

        /// <summary>One legend per view: the old one is replaced, at a given point or right of the crop.</summary>
        private static void Legend(Document doc, View view, ElementId category, JObject p, ToolOptions options, List<string> notes)
        {
            var old = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ColorFillLegend)).ToElementIds();
            if (old.Count > 0) doc.Delete(old);
            double z = (view as ViewPlan)?.GenLevel?.Elevation ?? 0;
            XYZ at;
            if (p?["legendPoint"] is JArray lp) at = Metric.Point(lp, 0);
            else
            {
                BoundingBoxXYZ crop = view.CropBoxActive ? view.CropBox : null;
                BoundingBoxXYZ ext = crop ?? ViewSetup.ModelExtent(doc, (view as ViewPlan)?.GenLevel, new[] { BuiltInCategory.OST_Walls });
                if (ext == null) { notes.Add("no place found for the legend; give legendPoint"); return; }
                at = new XYZ(ext.Max.X + Metric.Ft(1), ext.Max.Y, 0);
            }
            ColorFillLegend legend = ColorFillLegend.Create(doc, view.Id, category, new XYZ(at.X, at.Y, z));
            RunMarks.Set(legend, options.RunMark);
        }
#endif

        /// <summary>Tags rooms or areas of the view that have no tag, head on the element's point, no leader.</summary>
        private static int Tag(Document doc, View view, bool areas, ToolOptions options, List<string> notes)
        {
            var taggedIds = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag))
                .Cast<SpatialElementTag>().Select(t => t is RoomTag rt ? rt.Room?.Id : t is AreaTag at ? at.Area?.Id : null)
                .Where(id => id != null));
            double z = (view as ViewPlan)?.GenLevel?.Elevation ?? 0;
            int count = 0;
            var elements = new FilteredElementCollector(doc, view.Id).OfCategory(areas ? BuiltInCategory.OST_Areas : BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<SpatialElement>().Where(e => e.Area > 0 && !taggedIds.Contains(e.Id)).ToList();
            foreach (SpatialElement e in elements)
            {
                XYZ p = (e.Location as LocationPoint)?.Point;
                if (p == null) continue;
                SpatialElementTag tag = areas
                    ? (SpatialElementTag)doc.Create.NewAreaTag((ViewPlan)view, (Area)e, new UV(p.X, p.Y))
                    : doc.Create.NewRoomTag(new LinkElementId(e.Id), new UV(p.X, p.Y), view.Id);
                if (tag == null) continue;
                try
                {
                    // head first, then drop the leader: the other order is refused as "outside the host"
                    tag.TagHeadPosition = new XYZ(p.X, p.Y, z);
                    tag.HasLeader = false;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    notes.Add($"{e.Name}: tag kept its leader ({ex.Message.Split('\n')[0].Trim()})");
                }
                RunMarks.Set(tag, options.RunMark);
                count++;
            }
            return count;
        }
    }
}
