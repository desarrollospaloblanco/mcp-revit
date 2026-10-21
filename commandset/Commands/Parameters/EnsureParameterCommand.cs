using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Execution;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Parameters
{
    /// <summary>
    /// Makes sure a project parameter exists and is bound to some categories (e.g. "Tipo de
    /// Apartamento" on Rooms and Areas). The definition comes from a shared parameter file (the
    /// user's own, or one kept under %APPDATA%\RevitMCP); the application's shared parameter file
    /// setting is restored afterwards. An existing binding gets the missing categories added.
    /// </summary>
    public class EnsureParameterCommand : DelegateCommand
    {
        public override string CommandName => "ensure_parameter";

        public EnsureParameterCommand(UIApplication uiApp) : base(uiApp, "MCP: parámetro")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string name = parameters?["name"]?.Value<string>() ?? throw new ArgumentException("name is required");
            var categoryNames = (parameters?["categories"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (categoryNames == null || categoryNames.Count == 0) throw new ArgumentException("categories is required, e.g. ['OST_Rooms', 'OST_Areas']");
            string dataType = (parameters?["dataType"]?.Value<string>() ?? "text").ToLowerInvariant();
            bool instance = parameters?["instance"]?.Value<bool>() ?? true;
            string file = parameters?["sharedParameterFile"]?.Value<string>() ?? Path.Combine(McpPaths.Root, "shared_parameters.txt");
            string groupName = parameters?["sharedGroup"]?.Value<string>() ?? "MCP";

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var categories = new CategorySet();
                foreach (string c in categoryNames)
                    categories.Insert(doc.Settings.Categories.get_Item(ViewSetup.Category(c)) ?? throw new ArgumentException("No category " + c));

                Definition existing = ExistingDefinition(doc, name);
                var warnings = new List<string>();
                string action = null;
                ToolTransaction.Run(doc, "MCP: parámetro " + name, options, warnings, () =>
                {
                    if (existing != null)
                    {
                        var binding = (ElementBinding)doc.ParameterBindings.get_Item(existing);
                        int before = binding.Categories.Size;
                        foreach (Category c in categories) binding.Categories.Insert(c);
                        if (binding.Categories.Size > before) { doc.ParameterBindings.ReInsert(existing, binding); action = "categories added"; }
                        else action = "already bound";
                        return;
                    }
                    ExternalDefinition definition = SharedDefinition(app.Application, file, groupName, name, dataType);
                    ElementBinding newBinding = instance ? (ElementBinding)new InstanceBinding(categories) : new TypeBinding(categories);
#if REVIT2022_OR_GREATER
                    doc.ParameterBindings.Insert(definition, newBinding, GroupTypeId.IdentityData);
#else
                    doc.ParameterBindings.Insert(definition, newBinding, BuiltInParameterGroup.PG_IDENTITY_DATA);
#endif
                    action = "created";
                });
                return new { document = doc.Title, parameter = name, action, instance, categories = categoryNames, sharedParameterFile = existing == null ? file : null, warnings };
            }, 300);
        }

        private static Definition ExistingDefinition(Document doc, string name)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Key.Name == name) return it.Key;
            return null;
        }

        /// <summary>Finds or adds the definition in the shared parameter file, then restores the user's file setting.</summary>
        private static ExternalDefinition SharedDefinition(Autodesk.Revit.ApplicationServices.Application app, string file, string group, string name, string dataType)
        {
            string previous = app.SharedParametersFilename;
            try
            {
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, "");
                }
                app.SharedParametersFilename = file;
                DefinitionFile defs = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Cannot open the shared parameter file " + file);
                foreach (DefinitionGroup g in defs.Groups)
                    if (g.Definitions.get_Item(name) is ExternalDefinition found) return found;
                DefinitionGroup target = defs.Groups.get_Item(group) ?? defs.Groups.Create(group);
#if REVIT2022_OR_GREATER
                ForgeTypeId spec = dataType == "number" ? SpecTypeId.Number : dataType == "length" ? SpecTypeId.Length
                    : dataType == "area" ? SpecTypeId.Area : dataType == "yesno" ? SpecTypeId.Boolean.YesNo : dataType == "integer" ? SpecTypeId.Int.Integer
                    : SpecTypeId.String.Text;
                return (ExternalDefinition)target.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec));
#else
                ParameterType type = dataType == "number" ? ParameterType.Number : dataType == "length" ? ParameterType.Length
                    : dataType == "area" ? ParameterType.Area : dataType == "yesno" ? ParameterType.YesNo : dataType == "integer" ? ParameterType.Integer
                    : ParameterType.Text;
                return (ExternalDefinition)target.Definitions.Create(new ExternalDefinitionCreationOptions(name, type));
#endif
            }
            finally
            {
                app.SharedParametersFilename = previous ?? "";
            }
        }
    }
}
