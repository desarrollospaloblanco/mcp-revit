using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Presentation graphics on existing views: solid cut fill, per category overrides, element
    /// overrides by rules (category, type name, base level elevation, plan region), hidden
    /// categories, display style, detail level and overrides copied from another view. The same
    /// settings can go to several open documents, one transaction each.
    /// </summary>
    public class SetViewGraphicsCommand : DelegateCommand
    {
        private static readonly BuiltInCategory[] CommonModelCategories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ramps,
            BuiltInCategory.OST_Railings, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_CurtainWallMullions
        };

        public override string CommandName => "set_view_graphics";

        public SetViewGraphicsCommand(UIApplication uiApp) : base(uiApp, "MCP: graficos de vista")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            // Everything is parsed before Revit is touched, so bad input never leaves a half-dressed model.
            var spec = Spec.From(parameters);
            var titles = (parameters?["documents"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if ((titles == null || titles.Count == 0) && options.DocumentTitle != null)
                titles = new List<string> { options.DocumentTitle };

            return RunInRevit(app =>
            {
                var docs = ToolDocuments.ResolveMany(app, titles);
                var targets = docs.Select(doc => new
                {
                    doc,
                    views = spec.Views.Select(key => EditableView(doc, key)).ToList(),
                    source = spec.CopyFrom == null ? null : EditableView(doc, spec.CopyFrom.View)
                }).ToList();

                var results = new List<object>();
                var errors = new List<string>();
                foreach (var target in targets)
                {
                    var warnings = new List<string>();
                    try
                    {
                        var reports = ToolTransaction.Run(target.doc, "MCP: graficos de vista", options, warnings,
                            () => target.views.Select(v => Apply(target.doc, v, target.source, spec)).ToList(), spec.DryRun);
                        results.Add(new { document = target.doc.Title, dryRun = spec.DryRun, views = reports, warnings });
                    }
                    catch (Exception ex) when (targets.Count > 1)
                    {
                        // One model failing must not hide what was already committed in the others.
                        errors.Add(target.doc.Title + ": " + ex.Message);
                        results.Add(new { document = target.doc.Title, error = ex.Message, warnings });
                    }
                }
                if (errors.Count == targets.Count)
                    throw new InvalidOperationException("set_view_graphics failed in every document: " + string.Join(" | ", errors));
                return new { documents = results };
            }, 600);
        }

        private static object Apply(Document doc, View view, View source, Spec spec)
        {
            var skipped = new List<object>();
            var warnings = new List<string>();
            View template = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(view.ViewTemplateId) as View;
            ICollection<ElementId> free = template?.GetNonControlledTemplateParameterIds();
            bool Controls(BuiltInParameter bip) => template != null && !free.Contains(new ElementId(bip));
            if (Controls(BuiltInParameter.VIS_GRAPHICS_MODEL) || Controls(BuiltInParameter.VIS_GRAPHICS_ANNOTATION))
                warnings.Add($"View template '{template.Name}' controls V/G overrides: category overrides and hidden categories " +
                             "are stored on the view but will not show until the template stops controlling them; element overrides do show");

            ElementId solid = SolidFill(doc);
            object copied = source == null ? null : CopyOverrides(doc, source, view, spec.CopyFrom, skipped);
            var categoryOverrides = SetCategoryOverrides(doc, view, spec.CategoryLooks, solid, skipped);
            object rules = spec.Rules.Count == 0 ? null : ApplyRules(doc, view, spec, solid, skipped);

            var hidden = new List<string>();
            foreach (BuiltInCategory bic in spec.Hide)
            {
                var id = new ElementId(bic);
                if (Category.GetCategory(doc, bic) == null) { skipped.Add(new { hide = bic.ToString(), reason = "category not in this document" }); continue; }
                if (!view.CanCategoryBeHidden(id)) { skipped.Add(new { hide = bic.ToString(), reason = "Revit does not let this category be hidden in this view" }); continue; }
                view.SetCategoryHidden(id, true);
                hidden.Add(bic.ToString());
            }

            string displayStyle = null, detailLevel = null;
            if (spec.ViewStyle.HasValue)
            {
                if (Controls(BuiltInParameter.MODEL_GRAPHICS_STYLE))
                    skipped.Add(new { displayStyle = spec.ViewStyle.ToString(), reason = $"controlled by view template '{template.Name}'" });
                else { view.DisplayStyle = spec.ViewStyle.Value; displayStyle = view.DisplayStyle.ToString(); }
            }
            if (spec.ViewDetail.HasValue)
            {
                if (Controls(BuiltInParameter.VIEW_DETAIL_LEVEL))
                    skipped.Add(new { detailLevel = spec.ViewDetail.ToString(), reason = $"controlled by view template '{template.Name}'" });
                else { view.DetailLevel = spec.ViewDetail.Value; detailLevel = view.DetailLevel.ToString(); }
            }

            return new
            {
                view = view.Name,
                id = ElementSelection.Id(view),
                viewType = view.ViewType.ToString(),
                template = template?.Name,
                copied,
                categoryOverrides,
                rules,
                hidden,
                displayStyle,
                detailLevel,
                skipped,
                warnings
            };
        }

        /// <summary>Category overrides start from what the view already has unless the entry says reset.</summary>
        private static List<string> SetCategoryOverrides(Document doc, View view, List<CategoryLook> looks, ElementId solid, List<object> skipped)
        {
            var set = new List<string>();
            foreach (var group in looks.GroupBy(l => l.Category))
            {
                var id = new ElementId(group.Key);
                if (Category.GetCategory(doc, group.Key) == null) { skipped.Add(new { category = group.Key.ToString(), reason = "category not in this document" }); continue; }
                if (!view.IsCategoryOverridable(id)) { skipped.Add(new { category = group.Key.ToString(), reason = "Revit does not allow overrides of this category in this view" }); continue; }
                OverrideGraphicSettings o = view.GetCategoryOverrides(id);
                foreach (CategoryLook look in group)
                    o = look.Look.ApplyTo(look.Reset ? new OverrideGraphicSettings() : o, solid, doc);
                try
                {
                    view.SetCategoryOverrides(id, o);
                    set.Add(group.Key.ToString());
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    skipped.Add(new { category = group.Key.ToString(), reason = ex.Message.Split('\n')[0].Trim() });
                }
            }
            return set;
        }

        private static object ApplyRules(Document doc, View view, Spec spec, ElementId solid, List<object> skipped)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l => l.Id, l => l.Elevation);
            var ruleCategories = spec.Rules.SelectMany(r => r.Categories).Distinct().ToList();
            var hostedCategories = spec.Hosted.SelectMany(h => h.Categories).Distinct().ToList();
            var failures = new Dictionary<string, List<long>>();

            int reset = 0;
            if (spec.ResetElements)
            {
                foreach (Element e in InView(doc, view, ruleCategories.Union(hostedCategories)))
                {
                    if (IsEmpty(view.GetElementOverrides(e.Id))) continue;
                    if (TrySet(view, e, new OverrideGraphicSettings(), failures)) reset++;
                }
            }

            var counts = spec.Rules.ToDictionary(r => r.Name, r => 0);
            var ruleOf = new Dictionary<ElementId, string>();
            var overrides = spec.Rules.ToDictionary(r => r.Name, r => r.Look.IsEmpty ? null : r.Look.ApplyTo(new OverrideGraphicSettings(), solid, doc));
            int unmatched = 0;
            foreach (Element e in InView(doc, view, ruleCategories))
            {
                Rule rule = spec.Rules.FirstOrDefault(r => r.Matches(doc, e, levels));
                if (rule == null) { unmatched++; continue; }
                ruleOf[e.Id] = rule.Name;
                // A rule without overrides is an exclusion: the element keeps the category look.
                OverrideGraphicSettings o = overrides[rule.Name];
                if (o != null && !TrySet(view, e, o, failures)) continue;
                counts[rule.Name]++;
            }

            var hosted = new List<object>();
            foreach (Hosted h in spec.Hosted)
            {
                OverrideGraphicSettings o = h.Look.ApplyTo(new OverrideGraphicSettings(), solid, doc);
                int n = 0;
                foreach (FamilyInstance fi in InView(doc, view, h.Categories).OfType<FamilyInstance>())
                {
                    if (fi.Host == null || !ruleOf.TryGetValue(fi.Host.Id, out string hostRule) || !h.HostRules.Contains(hostRule)) continue;
                    if (TrySet(view, fi, o, failures)) n++;
                }
                hosted.Add(new { hostRules = h.HostRules, categories = h.Categories.Select(c => c.ToString()), elements = n });
            }

            foreach (var f in failures)
                skipped.Add(new { elements = f.Value.Count, sampleIds = f.Value.Take(10), reason = f.Key });
            return new
            {
                elementsReset = reset,
                perRule = spec.Rules.Select(r => new { rule = r.Name, elements = counts[r.Name], overrides = !r.Look.IsEmpty }),
                unmatched,
                hosted
            };
        }

        private static IEnumerable<Element> InView(Document doc, View view, IEnumerable<BuiltInCategory> categories)
        {
            var list = categories.ToList();
            if (list.Count == 0) return Enumerable.Empty<Element>();
            return new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(list))
                .WhereElementIsNotElementType().ToElements();
        }

        private static bool TrySet(View view, Element e, OverrideGraphicSettings o, Dictionary<string, List<long>> failures)
        {
            try
            {
                view.SetElementOverrides(e.Id, o);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                string reason = ex.Message.Split('\n')[0].Trim();
                if (!failures.TryGetValue(reason, out var ids)) failures[reason] = ids = new List<long>();
                ids.Add(ElementSelection.Id(e));
                return false;
            }
        }

        private static object CopyOverrides(Document doc, View source, View target, CopySpec copy, List<object> skipped)
        {
            if (source.Id == target.Id)
            {
                skipped.Add(new { copyFrom = source.Name, reason = "source and target are the same view" });
                return null;
            }
            var categories = new List<string>();
            foreach (BuiltInCategory bic in copy.Categories)
            {
                var id = new ElementId(bic);
                if (Category.GetCategory(doc, bic) == null || !target.IsCategoryOverridable(id)) continue;
                try
                {
                    target.SetCategoryOverrides(id, source.GetCategoryOverrides(id));
                    categories.Add(bic.ToString());
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    skipped.Add(new { copyCategory = bic.ToString(), reason = ex.Message.Split('\n')[0].Trim() });
                }
            }
            int elements = 0;
            var failures = new Dictionary<string, List<long>>();
            if (copy.Elements)
            {
                // Document-wide: an element override can exist for elements the source crop does not show.
                foreach (Element e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(copy.Categories))
                             .WhereElementIsNotElementType())
                {
                    OverrideGraphicSettings o = source.GetElementOverrides(e.Id);
                    if (IsEmpty(o)) continue;
                    if (TrySet(target, e, o, failures)) elements++;
                }
            }
            foreach (var f in failures)
                skipped.Add(new { copiedElements = f.Value.Count, sampleIds = f.Value.Take(10), reason = f.Key });
            return new { from = source.Name, categories, elements };
        }

        private static bool IsEmpty(OverrideGraphicSettings o) =>
            o.SurfaceForegroundPatternId == ElementId.InvalidElementId && o.SurfaceBackgroundPatternId == ElementId.InvalidElementId
            && o.CutForegroundPatternId == ElementId.InvalidElementId && o.CutBackgroundPatternId == ElementId.InvalidElementId
            && !o.SurfaceForegroundPatternColor.IsValid && !o.CutForegroundPatternColor.IsValid
            && !o.ProjectionLineColor.IsValid && !o.CutLineColor.IsValid
            && o.ProjectionLinePatternId == ElementId.InvalidElementId && o.CutLinePatternId == ElementId.InvalidElementId
            && o.ProjectionLineWeight == OverrideGraphicSettings.InvalidPenNumber && o.CutLineWeight == OverrideGraphicSettings.InvalidPenNumber
            && !o.Halftone && o.Transparency == 0 && o.DetailLevel == ViewDetailLevel.Undefined;

        private static ElementId SolidFill(Document doc)
        {
            var solids = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Where(f => f.GetFillPattern().IsSolidFill).ToList();
            return (solids.FirstOrDefault(f => f.GetFillPattern().Target == FillPatternTarget.Drafting) ?? solids.FirstOrDefault())?.Id
                   ?? ElementId.InvalidElementId;
        }

        /// <summary>A view by id or name; templates are found too, only to be refused with a clear message.</summary>
        private static View EditableView(Document doc, string key)
        {
            View view = null;
            if (long.TryParse(key, out long id)) view = doc.GetElement(Utils.ElementIdExtensions.ToElementId(id)) as View;
            if (view == null)
            {
                var named = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => string.Equals(v.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
                view = named.FirstOrDefault(v => !v.IsTemplate) ?? named.FirstOrDefault();
            }
            if (view == null) throw new InvalidOperationException($"No view named or numbered '{key}' in {doc.Title}");
            if (view.IsTemplate)
                throw new InvalidOperationException($"'{view.Name}' in {doc.Title} is a view template; set_view_graphics only changes views. " +
                                                    "Edit the template in Revit or give the views that use it.");
            if (!view.AreGraphicsOverridesAllowed())
                throw new InvalidOperationException($"'{view.Name}' in {doc.Title} ({view.ViewType}) does not take graphic overrides");
            return view;
        }

        private sealed class Look
        {
            public Color Surface, Cut, CutLine, Line;
            public int? Transparency;
            public bool? Halftone;

            public bool IsEmpty => Surface == null && Cut == null && CutLine == null && Line == null && Transparency == null && Halftone == null;

            public static Look From(JObject o, string where)
            {
                var look = new Look
                {
                    Surface = ParseColor(o?["surfaceColor"], where + ".surfaceColor"),
                    Cut = ParseColor(o?["cutColor"], where + ".cutColor"),
                    CutLine = ParseColor(o?["cutLineColor"], where + ".cutLineColor"),
                    Line = ParseColor(o?["lineColor"], where + ".lineColor"),
                    Transparency = o?["transparency"]?.Value<int>(),
                    Halftone = o?["halftone"]?.Value<bool>()
                };
                if (look.Transparency < 0 || look.Transparency > 100)
                    throw new ArgumentException(where + ".transparency goes from 0 to 100");
                return look;
            }

            public OverrideGraphicSettings ApplyTo(OverrideGraphicSettings start, ElementId solid, Document doc)
            {
                var o = new OverrideGraphicSettings(start);
                if ((Surface != null || Cut != null) && solid == ElementId.InvalidElementId)
                    throw new InvalidOperationException(doc.Title + " has no solid fill pattern to paint surfaces or cuts with");
                if (Surface != null)
                {
                    o.SetSurfaceForegroundPatternId(solid);
                    o.SetSurfaceForegroundPatternColor(Surface);
                    o.SetSurfaceForegroundPatternVisible(true);
                }
                if (Cut != null)
                {
                    o.SetCutForegroundPatternId(solid);
                    o.SetCutForegroundPatternColor(Cut);
                    o.SetCutForegroundPatternVisible(true);
                }
                if (CutLine != null) o.SetCutLineColor(CutLine);
                if (Line != null) o.SetProjectionLineColor(Line);
                if (Transparency.HasValue) o.SetSurfaceTransparency(Transparency.Value);
                if (Halftone.HasValue) o.SetHalftone(Halftone.Value);
                return o;
            }
        }

        private sealed class CategoryLook
        {
            public BuiltInCategory Category;
            public bool Reset;
            public Look Look;
        }

        private sealed class Rule
        {
            public string Name;
            public HashSet<BuiltInCategory> Categories;
            public string TypeStartsWith, TypeContains;
            public double? LevelMin, LevelMax;
            public BoundingBoxXYZ Region;
            public bool? CurtainWall;
            public Look Look;

            public bool Matches(Document doc, Element e, Dictionary<ElementId, double> levels)
            {
                if (e.Category == null || !Categories.Contains((BuiltInCategory)Utils.ElementIdExtensions.GetValue(e.Category.Id))) return false;
                if (CurtainWall.HasValue && CurtainWall.Value != (e is Wall w && w.WallType.Kind == WallKind.Curtain)) return false;
                if (TypeStartsWith != null || TypeContains != null)
                {
                    string type = doc.GetElement(e.GetTypeId())?.Name ?? "";
                    if (TypeStartsWith != null && !type.StartsWith(TypeStartsWith, StringComparison.OrdinalIgnoreCase)) return false;
                    if (TypeContains != null && type.IndexOf(TypeContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
                if (LevelMin.HasValue || LevelMax.HasValue)
                {
                    if (!levels.TryGetValue(ElementSelection.LevelOf(e), out double z)) return false;
                    if (LevelMin.HasValue && z < LevelMin.Value - 1e-6) return false;
                    if (LevelMax.HasValue && z >= LevelMax.Value - 1e-6) return false;
                }
                if (Region != null)
                {
                    XYZ p = PlanPoint(e);
                    if (p == null || p.X < Region.Min.X || p.X > Region.Max.X || p.Y < Region.Min.Y || p.Y > Region.Max.Y) return false;
                }
                return true;
            }

            private static XYZ PlanPoint(Element e)
            {
                if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
                if (e.Location is LocationPoint lp) return lp.Point;
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                return b == null ? null : (b.Min + b.Max) / 2;
            }
        }

        private sealed class Hosted
        {
            public HashSet<string> HostRules;
            public List<BuiltInCategory> Categories;
            public Look Look;
        }

        private sealed class CopySpec
        {
            public string View;
            public List<BuiltInCategory> Categories;
            public bool Elements;
        }

        private sealed class Spec
        {
            public List<string> Views;
            public List<CategoryLook> CategoryLooks = new List<CategoryLook>();
            public List<Rule> Rules = new List<Rule>();
            public List<Hosted> Hosted = new List<Hosted>();
            public bool ResetElements;
            public List<BuiltInCategory> Hide;
            public DisplayStyle? ViewStyle;
            public ViewDetailLevel? ViewDetail;
            public CopySpec CopyFrom;
            public bool DryRun;

            public static Spec From(JObject p)
            {
                var spec = new Spec
                {
                    Views = Strings(p?["views"]),
                    ResetElements = p?["resetElements"]?.Value<bool>() ?? true,
                    Hide = Strings(p?["hideCategories"]).Select(ViewSetup.Category).ToList(),
                    ViewStyle = p?["displayStyle"] == null ? (DisplayStyle?)null : Style(p["displayStyle"].Value<string>()),
                    ViewDetail = p?["detailLevel"] == null ? (ViewDetailLevel?)null : Detail(p["detailLevel"].Value<string>()),
                    DryRun = p?["dryRun"]?.Value<bool>() ?? false
                };
                if (spec.Views.Count == 0) throw new ArgumentException("views is required: names or ids of the views to dress");

                if (p?["cut"] is JObject cut)
                {
                    Color color = ParseColor(cut["color"], "cut.color") ?? new Color(0, 0, 0);
                    Color line = ParseColor(cut["lineColor"], "cut.lineColor") ?? color;
                    var categories = Strings(cut["categories"]);
                    if (categories.Count == 0) throw new ArgumentException("cut.categories is required");
                    foreach (string name in categories)
                        spec.CategoryLooks.Add(new CategoryLook { Category = ViewSetup.Category(name), Look = new Look { Cut = color, CutLine = line } });
                }

                int i = 0;
                foreach (JObject c in (p?["categories"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    string where = "categories[" + i++ + "]";
                    string name = c["category"]?.Value<string>() ?? throw new ArgumentException(where + ".category is required");
                    var look = Look.From(c, where + " (" + name + ")");
                    bool reset = c["reset"]?.Value<bool>() ?? false;
                    if (look.IsEmpty && !reset) throw new ArgumentException(where + " (" + name + ") sets nothing");
                    spec.CategoryLooks.Add(new CategoryLook { Category = ViewSetup.Category(name), Reset = reset, Look = look });
                }

                i = 0;
                foreach (JObject r in (p?["rules"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    i++;
                    string name = r["name"]?.Value<string>() ?? "rule " + i;
                    if (spec.Rules.Any(x => x.Name == name)) throw new ArgumentException("Two rules are named '" + name + "'");
                    JObject m = r["match"] as JObject ?? throw new ArgumentException("rule '" + name + "' needs match");
                    var categories = Strings(m["categories"]);
                    if (categories.Count == 0) throw new ArgumentException("rule '" + name + "': match.categories is required");
                    JObject level = m["levelElevation"] as JObject;
                    spec.Rules.Add(new Rule
                    {
                        Name = name,
                        Categories = new HashSet<BuiltInCategory>(categories.Select(ViewSetup.Category)),
                        TypeStartsWith = m["typeStartsWith"]?.Value<string>(),
                        TypeContains = m["typeContains"]?.Value<string>(),
                        LevelMin = level?["min"] == null ? (double?)null : Metric.Ft(level["min"].Value<double>()),
                        LevelMax = level?["max"] == null ? (double?)null : Metric.Ft(level["max"].Value<double>()),
                        Region = ViewSetup.Rectangle(m["region"] as JObject),
                        CurtainWall = m["curtainWall"]?.Value<bool>(),
                        Look = Look.From(r, "rule '" + name + "'")
                    });
                }

                i = 0;
                foreach (JObject h in (p?["hostedFollowHost"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    string where = "hostedFollowHost[" + i++ + "]";
                    var hostRules = new HashSet<string>(Strings(h["hostRules"]));
                    if (hostRules.Count == 0) throw new ArgumentException(where + ".hostRules is required");
                    var unknown = hostRules.Where(n => spec.Rules.All(r => r.Name != n)).ToList();
                    if (unknown.Count > 0)
                        throw new ArgumentException($"{where}: no rule named {string.Join(", ", unknown.Select(n => "'" + n + "'"))}. Rules: {string.Join(", ", spec.Rules.Select(r => r.Name))}");
                    var categories = Strings(h["categories"]);
                    var look = Look.From(h, where);
                    if (look.IsEmpty) throw new ArgumentException(where + " sets nothing");
                    spec.Hosted.Add(new Hosted
                    {
                        HostRules = hostRules,
                        Categories = categories.Count == 0
                            ? new List<BuiltInCategory> { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors }
                            : categories.Select(ViewSetup.Category).ToList(),
                        Look = look
                    });
                }

                if (p?["copyFrom"] is JObject copy)
                {
                    var categories = Strings(copy["categories"]);
                    spec.CopyFrom = new CopySpec
                    {
                        View = copy["view"]?.Value<string>() ?? throw new ArgumentException("copyFrom.view is required"),
                        Categories = categories.Count == 0 ? CommonModelCategories.ToList() : categories.Select(ViewSetup.Category).ToList(),
                        Elements = copy["elementOverrides"]?.Value<bool>() ?? true
                    };
                }

                if (spec.CategoryLooks.Count == 0 && spec.Rules.Count == 0 && spec.Hide.Count == 0 && spec.CopyFrom == null
                    && spec.ViewStyle == null && spec.ViewDetail == null)
                    throw new ArgumentException("Nothing to do: give cut, categories, rules, hideCategories, copyFrom, displayStyle or detailLevel");
                return spec;
            }

            private static List<string> Strings(JToken token) =>
                (token as JArray)?.Select(t => t.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();

            private static DisplayStyle Style(string value)
            {
                switch ((value ?? "").Trim().ToLowerInvariant())
                {
                    case "wireframe": return Autodesk.Revit.DB.DisplayStyle.Wireframe;
                    case "hidden": return Autodesk.Revit.DB.DisplayStyle.HLR;
                    case "shading": return Autodesk.Revit.DB.DisplayStyle.Shading;
                    case "shadingwithedges": return Autodesk.Revit.DB.DisplayStyle.ShadingWithEdges;
                    case "consistent": return Autodesk.Revit.DB.DisplayStyle.FlatColors;
                    case "realistic": return Autodesk.Revit.DB.DisplayStyle.Realistic;
                    default: throw new ArgumentException("displayStyle is wireframe, hidden, shading, shadingWithEdges, consistent or realistic");
                }
            }

            private static ViewDetailLevel Detail(string value)
            {
                switch ((value ?? "").Trim().ToLowerInvariant())
                {
                    case "coarse": return ViewDetailLevel.Coarse;
                    case "medium": return ViewDetailLevel.Medium;
                    case "fine": return ViewDetailLevel.Fine;
                    default: throw new ArgumentException("detailLevel is coarse, medium or fine");
                }
            }
        }

        private static Color ParseColor(JToken token, string where)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JArray a)
            {
                if (a.Count != 3 || a.Any(v => v.Type != JTokenType.Integer || v.Value<int>() < 0 || v.Value<int>() > 255))
                    throw new ArgumentException(where + ": a color is [r, g, b] with values 0 to 255, or '#RRGGBB'");
                return new Color((byte)a[0].Value<int>(), (byte)a[1].Value<int>(), (byte)a[2].Value<int>());
            }
            string hex = (token.Value<string>() ?? "").Trim().TrimStart('#');
            if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
                throw new ArgumentException(where + ": a color is [r, g, b] with values 0 to 255, or '#RRGGBB'");
            return new Color(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }
    }
}
