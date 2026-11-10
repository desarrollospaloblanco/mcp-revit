using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Common
{
    /// <summary>
    /// Open documents and their state, plus opening a file, creating a blank project, activating a
    /// document, closing one and saving a copy under a new name. Closing never saves; a modified
    /// document is closed only when the caller says the changes may be discarded.
    /// </summary>
    public class ManageDocumentsCommand : DelegateCommand
    {
        public override string CommandName => "manage_documents";

        public ManageDocumentsCommand(UIApplication uiApp) : base(uiApp, "MCP: documentos")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            string action = (parameters?["action"]?.Value<string>() ?? "list").Trim().ToLowerInvariant();
            string path = parameters?["path"]?.Value<string>();
            string title = parameters?["documentTitle"]?.Value<string>();
            string template = parameters?["template"]?.Value<string>();
            bool activate = parameters?["activate"]?.Value<bool>() ?? false;
            bool detach = parameters?["detachFromCentral"]?.Value<bool>() ?? false;
            bool discard = parameters?["discardChanges"]?.Value<bool>() ?? false;
            int timeout = Math.Max(30, Math.Min(3600, parameters?["timeoutSeconds"]?.Value<int>() ?? 900));

            return RunInRevit(app =>
            {
                switch (action)
                {
                    case "list":
                        return List(app);
                    case "open":
                        return Open(app, path, activate, detach);
                    case "new":
                        return New(app, template);
                    case "activate":
                        Document target = ToolDocuments.Resolve(app, title);
                        if (string.IsNullOrEmpty(target.PathName))
                            throw new InvalidOperationException(target.Title + " has never been saved; Revit can only activate documents from a file");
                        app.OpenAndActivateDocument(target.PathName);
                        return List(app);
                    case "close":
                        return Close(app, title, discard);
                    case "saveas":
                        return SaveAs(app, title, path);
                    default:
                        throw new ArgumentException("action must be list, open, new, activate, close or saveAs");
                }
            }, timeout);
        }

        private static object List(UIApplication app)
        {
            Document active = app.ActiveUIDocument?.Document;
            return new
            {
                active = active?.Title,
                documents = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).Select(d => new
                {
                    title = d.Title,
                    path = d.PathName,
                    active = d.Equals(active),
                    modified = d.IsModified,
                    workshared = d.IsWorkshared,
                    detached = d.IsDetached,
                    family = d.IsFamilyDocument,
                    readOnly = d.IsReadOnly
                })
            };
        }

        private static object Open(UIApplication app, string path, bool activate, bool detach)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new ArgumentException("path must be an existing .rvt or .rfa file");
            string full = Path.GetFullPath(path);
            Document already = app.Application.Documents.Cast<Document>()
                .FirstOrDefault(d => string.Equals(d.PathName, full, StringComparison.OrdinalIgnoreCase));
            if (already != null && !activate)
                return new { opened = already.Title, alreadyOpen = true };

            if (activate)
            {
                if (detach)
                    throw new ArgumentException("detachFromCentral opens in the background; do not combine it with activate");
                app.OpenAndActivateDocument(full);
                return new { opened = app.ActiveUIDocument.Document.Title, activated = true };
            }

            var options = new OpenOptions();
            if (detach) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            Document doc = app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(full), options);
            return new { opened = doc.Title, activated = false, workshared = doc.IsWorkshared, detached = doc.IsDetached };
        }

        private static object New(UIApplication app, string template)
        {
            Document doc = string.IsNullOrWhiteSpace(template)
                ? app.Application.NewProjectDocument(UnitSystem.Metric)
                : app.Application.NewProjectDocument(template);
            return new { created = doc.Title, note = "Unsaved and in the background; save it with Save As in Revit or close it with discardChanges" };
        }

        /// <summary>
        /// Saves the document under a new file, as when a model is copied to start a new version.
        /// Revit then keeps working on the new file and the old one stays on disk as it was saved,
        /// so a document with unsaved changes is refused: they would only reach the copy.
        /// </summary>
        private static object SaveAs(UIApplication app, string title, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path is required for saveAs: a full path, or a file name next to the current file");
            Document doc = ToolDocuments.Resolve(app, title);
            if (string.IsNullOrEmpty(doc.PathName))
                throw new InvalidOperationException(doc.Title + " has never been saved; give it a first name in Revit");
            if (doc.IsWorkshared)
                throw new InvalidOperationException(doc.Title + " is workshared; make the copy with Save As in Revit, which handles central and worksets");
            if (doc.IsModified)
                throw new InvalidOperationException(doc.Title + " has unsaved changes; save it first (save_documents) so the original keeps them");

            string extension = doc.IsFamilyDocument ? ".rfa" : ".rvt";
            string target = Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(doc.PathName), path);
            if (!target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                target += extension;
            target = Path.GetFullPath(target);
            if (File.Exists(target))
                throw new InvalidOperationException("A file already exists at " + target + "; choose another name");
            if (!Directory.Exists(Path.GetDirectoryName(target)))
                throw new InvalidOperationException("The folder " + Path.GetDirectoryName(target) + " does not exist");

            string original = doc.PathName;
            doc.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = false });
            return new { original, saved = doc.PathName, title = doc.Title, note = "Revit now works on the copy; the original file is unchanged" };
        }

        private static object Close(UIApplication app, string title, bool discard)
        {
            if (string.IsNullOrWhiteSpace(title))
                throw new ArgumentException("documentTitle is required to close a document");
            Document doc = ToolDocuments.Resolve(app, title);
            if (doc.Equals(app.ActiveUIDocument?.Document))
                throw new InvalidOperationException("Revit cannot close the active document through the API; activate another document first");
            if (doc.IsModified && !discard)
                throw new InvalidOperationException(doc.Title + " has unsaved changes; save it first or pass discardChanges=true");
            bool closed = doc.Close(false);
            return new { closed = closed ? title : null, discardedChanges = closed && discard };
        }
    }
}
