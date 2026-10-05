using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>Outcome counts for one kind of element.</summary>
    public class KindCounts
    {
        public int Created;
        public int Updated;
        public int Unchanged;
        public int Adopted;
        public int Failed;
        public int Deleted;

        public Dictionary<string, int> ToDictionary()
        {
            var result = new Dictionary<string, int>();
            if (Created > 0) result["created"] = Created;
            if (Updated > 0) result["updated"] = Updated;
            if (Unchanged > 0) result["unchanged"] = Unchanged;
            if (Adopted > 0) result["adopted"] = Adopted;
            if (Failed > 0) result["failed"] = Failed;
            if (Deleted > 0) result["deleted"] = Deleted;
            return result;
        }
    }

    /// <summary>
    /// Everything a build did, keyed by spec key, so a failure can be traced back to the spec
    /// entry that caused it rather than to a Revit element id nobody knows.
    /// </summary>
    public class BuildReport
    {
        public const int ListCap = 200;

        public Dictionary<string, KindCounts> Counts { get; } = new Dictionary<string, KindCounts>();
        public List<string> Problems { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public List<string> RevitWarnings { get; } = new List<string>();
        public List<string> TypesCreated { get; } = new List<string>();
        public List<string> TypesUpdated { get; } = new List<string>();
        public List<string> ViewsCreated { get; } = new List<string>();
        public List<string> Orphans { get; } = new List<string>();

        /// <summary>Spec key → Revit element id, for every element that exists after the build.</summary>
        public Dictionary<string, long> Ids { get; } = new Dictionary<string, long>();

        public KindCounts For(string kind)
        {
            if (!Counts.TryGetValue(kind, out KindCounts counts))
            {
                counts = new KindCounts();
                Counts[kind] = counts;
            }
            return counts;
        }

        public void Fail(string kind, string key, string reason)
        {
            For(kind).Failed++;
            Problems.Add(key + ": " + reason);
        }

        public void Warn(IEnumerable<string> warnings)
        {
            foreach (string warning in warnings)
            {
                if (!RevitWarnings.Contains(warning))
                    RevitWarnings.Add(warning);
            }
        }

        /// <summary>
        /// The report as a plain dictionary. Long lists are capped so a 5,000-element build does
        /// not flood the response; the full report can be written to disk instead.
        /// </summary>
        public Dictionary<string, object> ToDictionary(bool full)
        {
            var counts = new Dictionary<string, object>();
            foreach (var pair in Counts)
                counts[pair.Key] = pair.Value.ToDictionary();

            return new Dictionary<string, object>
            {
                { "counts", counts },
                { "problems", Cap(Problems, full) },
                { "problemCount", Problems.Count },
                { "notes", Cap(Notes, full) },
                { "revitWarnings", Cap(RevitWarnings, full) },
                { "typesCreated", TypesCreated },
                { "typesUpdated", TypesUpdated },
                { "viewsCreated", ViewsCreated },
                { "orphans", Cap(Orphans, full) },
                { "orphanCount", Orphans.Count }
            };
        }

        private static List<string> Cap(List<string> items, bool full)
        {
            if (full || items.Count <= ListCap)
                return items;
            var capped = items.Take(ListCap).ToList();
            capped.Add("... and " + (items.Count - ListCap) + " more");
            return capped;
        }
    }
}
