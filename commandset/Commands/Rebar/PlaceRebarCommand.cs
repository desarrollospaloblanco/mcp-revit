using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Rebar;

namespace RevitMCPCommandSet.Commands.Rebar
{
    /// <summary>
    /// Places bar sets and welded mesh given as geometry in metres (the reinforcement read from the
    /// drawings): bars longer than the commercial length (6, 9 or 12 m) are cut into pieces joined by
    /// laps of 40 db, never at a joint, with a marker on every splice; hosts and splice zones are
    /// found from the model when not given.
    /// </summary>
    public class PlaceRebarCommand : DelegateCommand
    {
        public override string CommandName => "place_rebar";

        public PlaceRebarCommand(UIApplication uiApp) : base(uiApp, "MCP: colocar acero")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            RebarCatalog catalog = RebarCommandOptions.Catalog(parameters);
            var bars = (parameters?["bars"] as JArray)?.OfType<JObject>().Select(j => BarSpec.From(j, catalog)).ToList() ?? new List<BarSpec>();
            var meshes = (parameters?["meshes"] as JArray)?.OfType<JObject>().Select(MeshSpec.From).ToList() ?? new List<MeshSpec>();
            if (bars.Count == 0 && meshes.Count == 0) throw new ArgumentException("Give bars and/or meshes");
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;
            bool autoZones = parameters?["autoSpliceZones"]?.Value<bool>() ?? true;
            bool autoHosts = parameters?["autoHosts"]?.Value<bool>() ?? true;
            bool markers = parameters?["lapMarkers"]?.Value<bool>() ?? true;
            bool replace = parameters?["replacePrevious"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                int removed = !dryRun && replace ? RebarCommandOptions.DeletePrevious(doc, options, warnings) : 0;
                var created = new List<string>();
                ToolTransaction.Run(doc, "MCP: parámetros de acero", options, warnings,
                    () => created.AddRange(RebarParameters.Ensure(doc, RebarParameters.Texts, RebarParameters.Numbers)), dryRun);

                var placer = new RebarPlacer(doc, catalog, options);
                List<BarSpec> pieces = placer.Split(bars, autoZones, autoHosts);
                placer.PlaceBars(pieces, dryRun);
                placer.PlaceMeshes(meshes, dryRun);
                if (markers) placer.PlaceMarkers(dryRun);
                placer.Warnings.AddRange(warnings);
                return new
                {
                    document = doc.Title,
                    stockLength = catalog.StockLength,
                    deletedFromPreviousRun = removed,
                    parametersCreated = created,
                    result = RebarCommandOptions.Report(placer, pieces, meshes, dryRun)
                };
            }, RebarCommandOptions.Timeout(parameters, 1800));
        }
    }
}
