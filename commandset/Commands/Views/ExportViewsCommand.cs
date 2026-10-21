using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Exports several views, from one or several open documents, to PNG at a given pixel width.
    /// </summary>
    public class ExportViewsCommand : DelegateCommand
    {
        public override string CommandName => "export_views";

        public ExportViewsCommand(UIApplication uiApp) : base(uiApp, "MCP: exportar vistas")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var requests = (parameters?["views"] as JArray)?.OfType<JObject>().ToList();
            if (requests == null || requests.Count == 0)
                throw new ArgumentException("views is required: [{ view, document?, fileName? }]");
            int width = Math.Max(200, Math.Min(15000, parameters["width"]?.Value<int>() ?? 3000));
            string folder = parameters["outputFolder"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(folder)) folder = PresentationViewService.ExportFolder;

            return RunInRevit(app =>
            {
                var results = new List<object>();
                foreach (JObject request in requests)
                {
                    string viewKey = request["view"]?.Value<string>();
                    string title = request["document"]?.Value<string>();
                    try
                    {
                        Document doc = QuantificationUtils.ResolveDocument(app, title, out string error)
                                       ?? throw new InvalidOperationException(error);
                        View view = FindView(doc, viewKey)
                                    ?? throw new InvalidOperationException("No view named or numbered '" + viewKey + "' in " + doc.Title);
                        string prefix = SetPerspectiveViewCommand.Sanitize(request["fileName"]?.Value<string>() ?? doc.Title + "_" + view.Name);
                        string file = PresentationViewService.Export(doc, view, width, folder, prefix);
                        results.Add(new { document = doc.Title, view = view.Name, image = file });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { document = title, view = viewKey, error = ex.Message });
                    }
                }
                return new { width, folder, exports = results };
            }, Math.Min(3600, 60 + 90 * requests.Count));
        }

        internal static View FindView(Document doc, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (long.TryParse(key, out long id) &&
                doc.GetElement(Utils.ElementIdExtensions.ToElementId(id)) is View byId && !byId.IsTemplate)
                return byId;
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
