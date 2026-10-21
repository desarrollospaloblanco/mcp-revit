using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Wall, floor, roof and ceiling types: list them with their layers, duplicate one with a new
    /// layer build-up (exterior to interior, the structure layers form the core), and change the
    /// type of selected elements.
    /// </summary>
    public class ManageTypesCommand : DelegateCommand
    {
        public override string CommandName => "manage_types";

        public ManageTypesCommand(UIApplication uiApp) : base(uiApp, "MCP: tipos")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string action = (parameters?["action"]?.Value<string>() ?? "list").ToLowerInvariant();
            string kind = (parameters?["kind"]?.Value<string>() ?? "walls").ToLowerInvariant();

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                switch (action)
                {
                    case "list":
                        return new { document = doc.Title, kind, types = Types(doc, kind).Select(Describe) };
                    case "duplicate":
                        HostObjAttributes created = null;
                        ToolTransaction.Run(doc, "MCP: tipo " + parameters?["newName"], options, warnings,
                            () => created = Duplicate(doc, kind, parameters));
                        return new { document = doc.Title, type = Describe(created), warnings };
                    case "change_type":
                        var changed = new List<object>();
                        var failed = new List<object>();
                        ToolTransaction.Run(doc, "MCP: cambiar tipo", options, warnings, () => ChangeType(doc, parameters, changed, failed));
                        return new { document = doc.Title, changed = changed.Count, elements = changed, failed, warnings };
                    default:
                        throw new ArgumentException("action must be list, duplicate or change_type");
                }
            }, 600);
        }

        private static Type ClassOf(string kind)
        {
            switch (kind)
            {
                case "walls": return typeof(WallType);
                case "floors": return typeof(FloorType);
                case "roofs": return typeof(RoofType);
                case "ceilings": return typeof(CeilingType);
                default: throw new ArgumentException("kind must be walls, floors, roofs or ceilings");
            }
        }

        private static List<HostObjAttributes> Types(Document doc, string kind) =>
            new FilteredElementCollector(doc).OfClass(ClassOf(kind)).Cast<HostObjAttributes>().OrderBy(t => t.Name).ToList();

        private static object Describe(HostObjAttributes t)
        {
            CompoundStructure cs = t.GetCompoundStructure();
            return new
            {
                id = ElementSelection.Id(t),
                name = t.Name,
                widthMeters = cs == null ? (double?)null : Metric.M(cs.GetWidth()),
                layers = cs?.GetLayers().Select((l, i) => new
                {
                    function = l.Function.ToString(),
                    thicknessMeters = Metric.M(l.Width),
                    material = t.Document.GetElement(l.MaterialId)?.Name,
                    core = i >= cs.GetFirstCoreLayerIndex() && i <= cs.GetLastCoreLayerIndex()
                })
            };
        }

        private static HostObjAttributes Duplicate(Document doc, string kind, JObject p)
        {
            string baseName = p?["baseType"]?.Value<string>() ?? throw new ArgumentException("baseType is required");
            string newName = p?["newName"]?.Value<string>() ?? throw new ArgumentException("newName is required");
            var types = Types(doc, kind);
            HostObjAttributes type = types.FirstOrDefault(t => t.Name == newName);
            if (type == null)
            {
                HostObjAttributes source = types.FirstOrDefault(t => t.Name == baseName)
                                           ?? throw new InvalidOperationException($"No {kind} type '{baseName}'");
                type = (HostObjAttributes)source.Duplicate(newName);
            }
            else if (!(p?["updateExisting"]?.Value<bool>() ?? false))
                throw new InvalidOperationException($"'{newName}' exists; pass updateExisting=true to rewrite its layers");

            var specs = (p?["layers"] as JArray)?.OfType<JObject>().ToList();
            if (specs == null || specs.Count == 0) return type;

            bool createMaterials = p?["createMissingMaterials"]?.Value<bool>() ?? true;
            var layers = specs.Select(l => new CompoundStructureLayer(
                Metric.Ft(l["thicknessMeters"]?.Value<double>() ?? throw new ArgumentException("every layer needs thicknessMeters")),
                Function(l["function"]?.Value<string>()),
                MaterialId(doc, l["material"]?.Value<string>(), createMaterials))).ToList();
            CompoundStructure cs = CompoundStructure.CreateSimpleCompoundStructure(layers);
            int first = layers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
            int last = layers.FindLastIndex(l => l.Function == MaterialFunctionAssignment.Structure);
            if (first >= 0)
            {
                cs.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
                cs.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - last);
            }
            if (type is WallType) cs.StructuralMaterialIndex = Math.Max(0, first);
            type.SetCompoundStructure(cs);
            return type;
        }

        private static MaterialFunctionAssignment Function(string name)
        {
            switch ((name ?? "structure").ToLowerInvariant())
            {
                case "finish1": case "finish": return MaterialFunctionAssignment.Finish1;
                case "finish2": return MaterialFunctionAssignment.Finish2;
                case "substrate": return MaterialFunctionAssignment.Substrate;
                case "insulation": case "thermal": return MaterialFunctionAssignment.Insulation;
                case "membrane": return MaterialFunctionAssignment.Membrane;
                default: return MaterialFunctionAssignment.Structure;
            }
        }

        internal static ElementId MaterialId(Document doc, string name, bool create)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            Material m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
            if (m != null) return m.Id;
            if (!create) throw new InvalidOperationException("No material '" + name + "'");
            return Material.Create(doc, name);
        }

        private static void ChangeType(Document doc, JObject p, List<object> changed, List<object> failed)
        {
            string target = p?["targetType"]?.Value<string>() ?? throw new ArgumentException("targetType is required");
            foreach (Element e in ElementSelection.Resolve(doc, p))
            {
                ElementType current = doc.GetElement(e.GetTypeId()) as ElementType;
                ElementId newType = current == null ? null
                    : current.GetSimilarTypes().Select(doc.GetElement).FirstOrDefault(t => t.Name == target
                        || (t is FamilySymbol fs && fs.FamilyName + " : " + fs.Name == target))?.Id;
                if (newType == null) { failed.Add(new { id = ElementSelection.Id(e), reason = "no compatible type '" + target + "'" }); continue; }
                if (newType == e.GetTypeId()) continue;
                e.ChangeTypeId(newType);
                changed.Add(new { id = ElementSelection.Id(e), from = current.Name, to = target });
            }
        }
    }
}
