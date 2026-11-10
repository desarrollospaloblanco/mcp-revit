using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Rebar;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.Rebar
{
    /// <summary>
    /// Reinforces the concrete of the model from rules keyed by type mark or type name (e.g. "C-B":
    /// 8#6 with #3@0.10 ties; "VA-1": 2#5 top and bottom with #3@0.10 stirrups; "Z-1": #4@0.15 both
    /// ways; walls and slabs with welded mesh), then places it like place_rebar: commercial pieces,
    /// laps away from joints and lap markers. Each element takes the first rule that matches it.
    /// </summary>
    public class RebarByTypeCommand : DelegateCommand
    {
        public override string CommandName => "rebar_by_type";

        public RebarByTypeCommand(UIApplication uiApp) : base(uiApp, "MCP: acero por tipo")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            RebarCatalog catalog = RebarCommandOptions.Catalog(parameters);
            var rules = (parameters?["rules"] as JArray)?.OfType<JObject>().ToList();
            if (rules == null || rules.Count == 0) throw new ArgumentException("rules is required: [{ kind, match, ... }]");
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;
            bool autoZones = parameters?["autoSpliceZones"]?.Value<bool>() ?? true;
            bool markers = parameters?["lapMarkers"]?.Value<bool>() ?? true;
            bool replace = parameters?["replacePrevious"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var generator = new RebarByType(doc, catalog);
                var taken = new HashSet<long>();
                foreach (JObject rule in rules)
                {
                    string kind = (rule["kind"]?.Value<string>() ?? "").ToLowerInvariant();
                    var categories = RebarByType.DefaultCategories(kind);
                    var scope = new JObject
                    {
                        ["elementIds"] = parameters?["elementIds"],
                        ["filter"] = MergeFilter(parameters?["filter"] as JObject, rule["categories"])
                    };
                    foreach (Element e in ElementSelection.Resolve(doc, scope, categories))
                    {
                        if (taken.Contains(e.Id.GetValue()) || !RebarByType.Matches(e, rule["match"]?.Value<string>())) continue;
                        if (kind == "wallmesh" && !(e is Wall)) continue;
                        try
                        {
                            generator.Build(e, rule);
                            taken.Add(e.Id.GetValue());
                        }
                        catch (Exception ex)
                        {
                            generator.Notes.Add($"{e.Id.GetValue()} ({rule["match"]}): {ex.Message}");
                        }
                    }
                }

                int removed = !dryRun && replace ? RebarCommandOptions.DeletePrevious(doc, options, warnings) : 0;
                var created = new List<string>();
                int covers = 0;
                ToolTransaction.Run(doc, "MCP: parámetros y recubrimientos de acero", options, warnings, () =>
                {
                    created.AddRange(RebarParameters.Ensure(doc, RebarParameters.Texts, RebarParameters.Numbers));
                    covers = RebarByType.ApplyCovers(doc, generator.HostCovers, generator.Notes);
                }, dryRun);

                var placer = new RebarPlacer(doc, catalog, options);
                List<BarSpec> pieces = placer.Split(generator.Bars, autoZones, false);
                placer.PlaceBars(pieces, dryRun);
                placer.PlaceMeshes(generator.Meshes, dryRun);
                if (markers) placer.PlaceMarkers(dryRun);
                placer.Notes.InsertRange(0, generator.Notes);
                placer.Warnings.AddRange(warnings);
                return new
                {
                    document = doc.Title,
                    stockLength = catalog.StockLength,
                    elements = generator.Matched.Count,
                    matched = generator.Matched.Take(300),
                    hostCoversSet = covers,
                    deletedFromPreviousRun = removed,
                    parametersCreated = created,
                    result = RebarCommandOptions.Report(placer, pieces, generator.Meshes, dryRun),
                    insideConcrete = RebarCommandOptions.InsideConcrete(parameters, () => generator.Concrete, pieces)
                };
            }, RebarCommandOptions.Timeout(parameters, 1800));
        }

        /// <summary>The call's filter (level, region, typeContains) with the rule's own categories when it gives them.</summary>
        private static JObject MergeFilter(JObject filter, JToken ruleCategories)
        {
            var merged = filter != null ? (JObject)filter.DeepClone() : new JObject();
            if (ruleCategories is JArray cats && cats.Count > 0) merged["categories"] = cats;
            else merged.Remove("categories");
            return merged.Count == 0 ? null : merged;
        }
    }
}
