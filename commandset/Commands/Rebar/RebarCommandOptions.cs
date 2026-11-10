using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Rebar;

namespace RevitMCPCommandSet.Commands.Rebar
{
    /// <summary>Parameters the steel tools share: commercial length, laps, hooks and the placement switches.</summary>
    public static class RebarCommandOptions
    {
        public static RebarCatalog Catalog(JObject p)
        {
            double stock = p?["stockLength"]?.Value<double>() ?? 9.0;
            return new RebarCatalog(stock, p?["lapFactor"]?.Value<double>() ?? 40, Lengths(p?["lapLengths"]), Lengths(p?["hookLengths"]));
        }

        private static Dictionary<string, double> Lengths(JToken t) =>
            (t as JObject)?.Properties().ToDictionary(k => k.Name, k => k.Value.Value<double>()) ?? new Dictionary<string, double>();

        public static int Timeout(JObject p, int fallback) => Math.Max(60, Math.Min(7200, p?["timeoutSeconds"]?.Value<int>() ?? fallback));

        /// <summary>Deletes what the previous run with this mark placed, and the lap group types left without instances.</summary>
        public static int DeletePrevious(Document doc, ToolOptions options, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(options.RunMark)) return 0;
            int removed = 0;
            ToolTransaction.Run(doc, "MCP: borrar acero anterior", options, warnings, () =>
            {
                removed = RunMarks.DeletePrevious(doc, options.RunMark);
                var unused = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                    .Where(g => g.Name.StartsWith("Traslape ") && g.Groups.Size == 0).Select(g => g.Id).ToList();
                if (unused.Count > 0) doc.Delete(unused);
            });
            return removed;
        }

        /// <summary>The inside-concrete check of the pieces (verifyInsideConcrete, true by default), or null when switched off.</summary>
        public static object InsideConcrete(JObject p, Func<RebarConcrete> concrete, IEnumerable<BarSpec> pieces) =>
            p?["verifyInsideConcrete"]?.Value<bool>() ?? true ? RebarAudit.Check(concrete(), pieces) : null;

        public static object Report(RebarPlacer placer, IList<BarSpec> pieces, IList<MeshSpec> meshes, bool dryRun) => new
        {
            dryRun,
            barSets = new { requested = pieces.Count, created = placer.BarSetsCreated, failed = pieces.Count - placer.BarSetsCreated },
            meshes = new { requested = meshes.Count, created = placer.MeshesCreated },
            laps = new
            {
                count = placer.Laps.Count,
                outsideAllowedZones = placer.Laps.Count(l => !l.InAllowedZone),
                markers = placer.MarkersCreated,
                list = placer.Laps.Take(200).Select(l => l.Describe())
            },
            longestPiece = Math.Round(placer.LongestPiece, 3),
            errors = placer.Errors.Take(100),
            notes = placer.Notes.Distinct().Take(100),
            warnings = placer.Warnings.Distinct().Take(50)
        };
    }
}
