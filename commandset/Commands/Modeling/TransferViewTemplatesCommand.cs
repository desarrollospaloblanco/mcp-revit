using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Copies view templates (with their filters) from one open model to others. A template that
    /// already exists in a target is replaced: the copy comes in, every view using the old one is
    /// switched to it, the old one is deleted and the copy takes its name.
    /// </summary>
    public class TransferViewTemplatesCommand : DelegateCommand
    {
        public override string CommandName => "transfer_view_templates";

        public TransferViewTemplatesCommand(UIApplication uiApp) : base(uiApp, "MCP: plantillas de vista")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var targets = (parameters?["targetDocuments"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (targets == null || targets.Count == 0) throw new ArgumentException("targetDocuments is required");
            var names = (parameters?["templates"] as JArray)?.Select(t => t.Value<string>()).ToList();
            bool replace = parameters?["replaceExisting"]?.Value<bool>() ?? true;

            return RunInRevit(app =>
            {
                Document source = ToolDocuments.Resolve(app, parameters?["sourceDocument"]?.Value<string>());
                var templates = new FilteredElementCollector(source).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.IsTemplate && (names == null || names.Contains(v.Name))).ToList();
                if (templates.Count == 0) throw new InvalidOperationException("No matching view templates in " + source.Title);
                var missing = names?.Where(n => templates.All(t => t.Name != n)).ToList();
                var results = new List<object>();

                foreach (Document target in ToolDocuments.ResolveMany(app, targets))
                {
                    if (target.Equals(source)) continue;
                    var warnings = new List<string>();
                    var report = new List<object>();
                    ToolTransaction.Run(target, "MCP: plantillas desde " + source.Title, options, warnings, () =>
                    {
                        foreach (View template in templates)
                        {
                            View existing = new FilteredElementCollector(target).OfClass(typeof(View)).Cast<View>()
                                .FirstOrDefault(v => v.IsTemplate && v.Name == template.Name);
                            if (existing != null && !replace) { report.Add(new { template = template.Name, action = "kept existing" }); continue; }

                            var opts = new CopyPasteOptions();
                            ElementId copyId = ElementTransformUtils.CopyElements(source, new List<ElementId> { template.Id }, target, Transform.Identity, opts).First();
                            var copy = (View)target.GetElement(copyId);
                            int switched = 0;
                            if (existing != null)
                            {
                                foreach (View v in new FilteredElementCollector(target).OfClass(typeof(View)).Cast<View>()
                                             .Where(v => !v.IsTemplate && v.ViewTemplateId == existing.Id).ToList())
                                {
                                    v.ViewTemplateId = copy.Id;
                                    switched++;
                                }
                                target.Delete(existing.Id);
                                copy.Name = template.Name;
                            }
                            report.Add(new { template = template.Name, action = existing == null ? "added" : "replaced", viewsSwitched = switched });
                        }
                    });
                    results.Add(new { document = target.Title, templates = report, warnings });
                }
                return new { source = source.Title, missing, targets = results };
            }, 1800);
        }
    }
}
