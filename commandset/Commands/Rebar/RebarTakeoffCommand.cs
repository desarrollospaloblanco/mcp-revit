using System.IO;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Execution;
using RevitMCPCommandSet.Services.Rebar;

namespace RevitMCPCommandSet.Commands.Rebar
{
    /// <summary>
    /// Steel take-off of the model in quintals of 100 lb: fills the "MCP Acero" parameters (weight,
    /// weight per side of a dividing line and per level, cut length, piece mark), builds the ACERO
    /// schedules and 3D views, and exports JSON and CSV with the cutting of 6, 9 or 12 m bars, the
    /// laps and totals with waste rounded up to whole quintals.
    /// </summary>
    public class RebarTakeoffCommand : DelegateCommand
    {
        public override string CommandName => "rebar_takeoff";

        public RebarTakeoffCommand(UIApplication uiApp) : base(uiApp, "MCP: cuantificar acero")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            RebarCatalog catalog = RebarCommandOptions.Catalog(parameters);
            var split = RebarTakeoff.Split.From(parameters?["split"]);
            var levels = (parameters?["levels"] as JArray)?.Select(t => t.Value<string>()).ToList();
            string foundation = parameters?["foundationName"]?.Value<string>() ?? "Cimentación";
            double waste = parameters?["waste"]?.Value<double>() ?? 0.03;
            if (waste >= 1) waste /= 100;
            bool byWires = string.Equals(parameters?["meshWeight"]?.Value<string>(), "wires", StringComparison.OrdinalIgnoreCase);
            bool write = parameters?["writeParameters"]?.Value<bool>() ?? true;
            bool schedules = parameters?["schedules"]?.Value<bool>() ?? true;
            bool views = parameters?["views"]?.Value<bool>() ?? true;
            bool replace = parameters?["replaceSchedulesAndViews"]?.Value<bool>() ?? false;
            string prefix = parameters?["schedulePrefix"]?.Value<string>() ?? "ACERO";
            string onlyMark = (parameters?["filter"] as JObject)?["runMark"]?.Value<string>();
            string exportPath = parameters?["exportPath"]?.Value<string>();

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                bool Keep(Element e) => onlyMark == null || RunMarks.Get(e) == onlyMark;
                var rebars = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Structure.Rebar)).Where(Keep).ToList();
                var meshes = new FilteredElementCollector(doc).OfClass(typeof(FabricArea)).Cast<FabricArea>().Where(Keep).ToList();
                int inSystem = new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount();
                if (inSystem > 0) notes.Add($"{inSystem} bar sets belong to area or path reinforcement and are not counted; remove the system to count them");
                if (rebars.Count == 0 && meshes.Count == 0) throw new InvalidOperationException("No rebar or fabric areas to measure" + (onlyMark != null ? " with run mark " + onlyMark : ""));

                var takeoff = new RebarTakeoff(doc, catalog, split, foundation, levels, byWires);
                takeoff.Measure(rebars, meshes);
                var made = new List<string>();
                var parametersCreated = new List<string>();
                int written = 0;
                if (write || schedules || views)
                {
                    ToolTransaction.Run(doc, "MCP: cuantificación de acero", options, warnings, () =>
                    {
                        parametersCreated.AddRange(takeoff.EnsureParameters());
                        if (write) written = takeoff.WriteParameters();
                        if (schedules) made.AddRange(RebarDocumentation.Schedules(doc, prefix, takeoff.ZoneNames.ToList(), takeoff.LevelNames.ToList(), replace));
                        if (views) made.AddRange(RebarDocumentation.Views(doc, replace, notes));
                    });
                }

                JObject summary = takeoff.Summary(waste);
                object cutting = takeoff.Cutting();
                object laps = takeoff.LapsFromModel();
                string path = string.IsNullOrWhiteSpace(exportPath)
                    ? Path.Combine(McpPaths.Root, "takeoffs", Safe(doc.Title) + "_acero_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".json")
                    : exportPath;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(path, Safe(doc.Title) + "_acero.json");
                string csv = takeoff.Export(path, summary, cutting, laps);
                notes.AddRange(takeoff.Notes.Distinct().Take(50));
                return new
                {
                    document = doc.Title,
                    stockLength = catalog.StockLength,
                    summary,
                    cutting,
                    lapCount = ((System.Collections.ICollection)laps).Count,
                    parametersWritten = written,
                    parametersCreated,
                    created = made,
                    export = new { json = path, csv },
                    notes,
                    warnings
                };
            }, RebarCommandOptions.Timeout(parameters, 1200));
        }

        private static string Safe(string name) => new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
    }
}
