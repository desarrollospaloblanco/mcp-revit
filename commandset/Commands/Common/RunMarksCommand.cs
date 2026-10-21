using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Common
{
    /// <summary>
    /// Counts, lists, selects or deletes the elements that tools labelled with a run mark.
    /// </summary>
    public class RunMarksCommand : DelegateCommand
    {
        public override string CommandName => "run_marks";

        public RunMarksCommand(UIApplication uiApp) : base(uiApp, "MCP: marcas de corrida")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string action = (parameters?["action"]?.Value<string>() ?? "count").Trim().ToLowerInvariant();
            string mark = parameters?["mark"]?.Value<string>();
            int limit = Math.Max(1, parameters?["limit"]?.Value<int>() ?? 500);
            if ((action == "delete" || action == "select") && string.IsNullOrWhiteSpace(mark))
                throw new ArgumentException($"'{action}' needs a mark");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                List<Element> elements = RunMarks.Find(doc, string.IsNullOrWhiteSpace(mark) ? null : mark);
                switch (action)
                {
                    case "count":
                        return new
                        {
                            document = doc.Title,
                            marks = elements.GroupBy(RunMarks.Get).OrderBy(g => g.Key)
                                .Select(g => new
                                {
                                    mark = g.Key,
                                    count = g.Count(),
                                    categories = g.GroupBy(e => e.Category?.Name ?? e.GetType().Name)
                                        .ToDictionary(c => c.Key, c => c.Count())
                                })
                        };
                    case "list":
                        return new
                        {
                            document = doc.Title,
                            mark,
                            count = elements.Count,
                            elements = elements.Take(limit).Select(e => new
                            {
                                id = Utils.ElementIdExtensions.GetValue(e.Id),
                                mark = RunMarks.Get(e),
                                category = e.Category?.Name,
                                name = e.Name
                            })
                        };
                    case "select":
                        if (!doc.Equals(app.ActiveUIDocument?.Document))
                            throw new InvalidOperationException("Selection works only in the active document; activate " + doc.Title + " first");
                        app.ActiveUIDocument.Selection.SetElementIds(elements.Select(e => e.Id).ToList());
                        return new { document = doc.Title, mark, selected = elements.Count };
                    case "delete":
                        var warnings = new List<string>();
                        int deleted = ToolTransaction.Run(doc, "MCP: borrar marca " + mark, options, warnings,
                            () => RunMarks.DeletePrevious(doc, mark));
                        return new { document = doc.Title, mark, marked = elements.Count, deleted, warnings };
                    default:
                        throw new ArgumentException("action must be count, list, select or delete");
                }
            }, 300);
        }
    }
}
