using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json;

using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCPCommandSet.Services.Execution
{
    /// <summary>
    /// Records which elements a run adds, modifies and deletes in every open document, from
    /// DocumentChanged (which fires for each transaction committed inside a transaction group).
    /// The summary must be taken before a rollback, while the elements still exist.
    /// </summary>
    public sealed class ChangeTracker : IDisposable
    {
        private readonly RevitApplication _app;
        private readonly Dictionary<string, DocumentChanges> _byDocument = new Dictionary<string, DocumentChanges>();

        public ChangeTracker(RevitApplication app)
        {
            _app = app;
            _app.DocumentChanged += OnDocumentChanged;
        }

        public bool HasChanges => _byDocument.Values.Any(c => c.Count > 0);

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document doc = e.GetDocument();
                if (!_byDocument.TryGetValue(doc.Title, out var changes))
                {
                    changes = new DocumentChanges(doc);
                    _byDocument[doc.Title] = changes;
                }

                foreach (ElementId id in e.GetAddedElementIds()) changes.Added.Add(Utils.ElementIdExtensions.GetValue(id));
                foreach (ElementId id in e.GetModifiedElementIds()) changes.Modified.Add(Utils.ElementIdExtensions.GetValue(id));
                foreach (ElementId id in e.GetDeletedElementIds())
                {
                    long value = Utils.ElementIdExtensions.GetValue(id);
                    // An element created and deleted inside the same run never existed for the user.
                    if (!changes.Added.Remove(value))
                        changes.Deleted.Add(value);
                    changes.Modified.Remove(value);
                }
            }
            catch (Exception)
            {
                // An exception escaping a Revit event handler can take Revit down; a missed
                // notification only makes the summary less complete.
            }
        }

        /// <summary>Elements added or modified in a document that still exist, for an isolate preview.</summary>
        public ICollection<ElementId> ChangedElementIds(Document doc)
        {
            if (!_byDocument.TryGetValue(doc.Title, out var changes))
                return new List<ElementId>();

            return changes.Added.Concat(changes.Modified)
                .Select(Utils.ElementIdExtensions.ToElementId)
                .Where(id => doc.GetElement(id) is Element el && el.Category != null && !(el is ElementType))
                .ToList();
        }

        public List<DocumentChangeSummary> Summarize(int sampleSize = 20)
        {
            var result = new List<DocumentChangeSummary>();
            foreach (var changes in _byDocument.Values.Where(c => c.Count > 0))
            {
                var modifiedOnly = changes.Modified.Where(v => !changes.Added.Contains(v)).ToList();
                result.Add(new DocumentChangeSummary
                {
                    Document = changes.Document.Title,
                    Added = changes.Added.Count,
                    Modified = modifiedOnly.Count,
                    Deleted = changes.Deleted.Count,
                    AddedByCategory = CountByCategory(changes.Document, changes.Added),
                    ModifiedByCategory = CountByCategory(changes.Document, modifiedOnly),
                    AddedSample = changes.Added.Take(sampleSize).ToList(),
                    ModifiedSample = modifiedOnly.Take(sampleSize).ToList(),
                    DeletedSample = changes.Deleted.Take(sampleSize).ToList()
                });
            }
            return result;
        }

        private static Dictionary<string, int> CountByCategory(Document doc, IEnumerable<long> ids)
        {
            var counts = new Dictionary<string, int>();
            foreach (long value in ids)
            {
                Element element = doc.GetElement(Utils.ElementIdExtensions.ToElementId(value));
                string key = element?.Category?.Name ?? (element is ElementType ? "Tipos" : "Otros");
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            return counts.OrderByDescending(kv => kv.Value).Take(12).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public void Dispose()
        {
            _app.DocumentChanged -= OnDocumentChanged;
        }

        private sealed class DocumentChanges
        {
            public DocumentChanges(Document doc) { Document = doc; }
            public Document Document { get; }
            public HashSet<long> Added { get; } = new HashSet<long>();
            public HashSet<long> Modified { get; } = new HashSet<long>();
            public HashSet<long> Deleted { get; } = new HashSet<long>();
            public int Count => Added.Count + Modified.Count + Deleted.Count;
        }
    }

    public class DocumentChangeSummary
    {
        [JsonProperty("document")] public string Document { get; set; }
        [JsonProperty("added")] public int Added { get; set; }
        [JsonProperty("modified")] public int Modified { get; set; }
        [JsonProperty("deleted")] public int Deleted { get; set; }
        [JsonProperty("addedByCategory")] public Dictionary<string, int> AddedByCategory { get; set; }
        [JsonProperty("modifiedByCategory")] public Dictionary<string, int> ModifiedByCategory { get; set; }
        [JsonProperty("addedSample")] public List<long> AddedSample { get; set; }
        [JsonProperty("modifiedSample")] public List<long> ModifiedSample { get; set; }
        [JsonProperty("deletedSample")] public List<long> DeletedSample { get; set; }

        public override string ToString() =>
            $"{Document}: {Added} creados, {Modified} modificados, {Deleted} borrados";
    }
}
