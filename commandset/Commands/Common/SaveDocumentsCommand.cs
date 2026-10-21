using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;

namespace RevitMCPCommandSet.Commands.Common
{
    /// <summary>
    /// Saves open documents and, for workshared ones, optionally synchronizes them with central.
    /// Runs as its own external event, outside any transaction, which is what Revit requires to save.
    /// When saving the active document through the API fails, the Save or Synchronize command is
    /// posted instead; Revit runs it right after this call, so its outcome is reported as "posted".
    /// </summary>
    public class SaveDocumentsCommand : DelegateCommand
    {
        private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

        public override string CommandName => "save_documents";

        public SaveDocumentsCommand(UIApplication uiApp) : base(uiApp, "MCP: guardar y sincronizar")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var titles = (parameters?["documents"] as JArray)?.Select(t => t.Value<string>()).ToList();
            bool synchronize = parameters?["synchronize"]?.Value<bool>() ?? false;
            bool relinquish = parameters?["relinquish"]?.Value<bool>() ?? true;
            bool onlyModified = parameters?["onlyModified"]?.Value<bool>() ?? true;
            string comment = parameters?["comment"]?.Value<string>() ?? "MCP";
            int timeout = Math.Max(30, Math.Min(3600, parameters?["timeoutSeconds"]?.Value<int>() ?? 900));

            return RunInRevit(app =>
            {
                var docs = titles == null || titles.Count == 0
                    ? app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList()
                    : titles.Select(t => QuantificationUtils.ResolveDocument(app, t, out string error)
                                         ?? throw new InvalidOperationException(error)).ToList();
                Document active = app.ActiveUIDocument?.Document;
                var results = new List<object>();
                bool posted = false;

                foreach (Document doc in docs)
                {
                    bool isActive = doc.Equals(active);
                    bool wasModified = doc.IsModified;
                    bool sync = synchronize && doc.IsWorkshared && !doc.IsDetached;
                    string outcome, detail = null;

                    if (doc.IsLinked)
                        outcome = "skipped: linked";
                    else if (string.IsNullOrEmpty(doc.PathName))
                        outcome = "skipped: never saved, use Save As in Revit";
                    else if (doc.IsDetached)
                        outcome = "skipped: detached model, Revit would ask for Save As";
                    else if (onlyModified && !wasModified && !sync)
                        outcome = "unchanged";
                    else if (!HasFreeSpace(doc.PathName, out string space))
                        outcome = "skipped: " + space;
                    else
                    {
                        try
                        {
                            if (sync) Synchronize(doc, comment, relinquish);
                            else doc.Save();
                            outcome = sync ? "synchronized" : "saved";
                        }
                        catch (Exception ex) when (isActive && !posted)
                        {
                            // The active document sometimes refuses an API save; the UI command does it.
                            var command = RevitCommandId.LookupPostableCommandId(sync ? PostableCommand.SynchronizeNow : PostableCommand.Save);
                            if (!app.CanPostCommand(command))
                                throw;
                            app.PostCommand(command);
                            posted = true;
                            outcome = "posted";
                            detail = "API refused (" + ex.Message + "); the " + (sync ? "Synchronize Now" : "Save") +
                                     " command was posted to Revit. Check isModified with a later call.";
                        }
                        catch (Exception ex)
                        {
                            outcome = "failed";
                            detail = ex.Message;
                        }
                    }

                    results.Add(new
                    {
                        document = doc.Title,
                        path = doc.PathName,
                        active = isActive,
                        workshared = doc.IsWorkshared,
                        wasModified,
                        isModified = outcome == "posted" ? (bool?)null : doc.IsModified,
                        outcome,
                        detail
                    });
                }
                return new { documents = results };
            }, timeout);
        }

        private static void Synchronize(Document doc, string comment, bool relinquish)
        {
            var options = new SynchronizeWithCentralOptions
            {
                Comment = comment,
                SaveLocalBefore = true,
                SaveLocalAfter = true
            };
            options.SetRelinquishOptions(new RelinquishOptions(relinquish));
            doc.SynchronizeWithCentral(new TransactWithCentralOptions(), options);
        }

        /// <summary>A full disk shows up as cryptic archive errors in Revit, so it is checked first.</summary>
        private static bool HasFreeSpace(string path, out string message)
        {
            message = null;
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return true;
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free >= MinFreeBytes) return true;
                message = $"only {free / (1024 * 1024)} MB free on {root}";
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                return true; // the save itself reports the real problem
            }
        }
    }
}
