using System.IO;
using RevitMCPCommandSet.Services.Execution;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// The shared parameters the steel tools write (group "MCP Acero", on Rebar, Fabric Areas and
    /// Fabric Sheets) and the line style of the lap markers. Weights per split zone and per level
    /// are added on demand as "Peso qq &lt;name&gt;".
    /// </summary>
    public static class RebarParameters
    {
        public const string Element = "Elemento estructural";
        public const string Group = "Grupo de acero";
        public const string Size = "Diámetro o malla";
        public const string PieceMark = "Marca pieza";
        public const string Description = "Descripción acero";
        public const string Lap = "Traslape";
        public const string CutLength = "Largo de corte m";
        public const string Weight = "Peso qq";
        public const string MeshArea = "Área malla m2";
        public const string LapLineStyle = "S-Zona de traslape";

        public static readonly string[] Texts = { Element, Group, Size, PieceMark, Description, Lap };
        public static readonly string[] Numbers = { CutLength, Weight, MeshArea };

        public static string WeightIn(string zone) => Weight + " " + zone;

        private static readonly BuiltInCategory[] Categories =
            { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_FabricAreas, BuiltInCategory.OST_FabricReinforcement };

        /// <summary>Binds the text and number parameters that are missing; call inside a transaction.</summary>
        public static List<string> Ensure(Document doc, IEnumerable<string> texts, IEnumerable<string> numbers)
        {
            var wanted = texts.Select(n => (name: n, number: false)).Concat(numbers.Select(n => (name: n, number: true)))
                .Where(p => !Bound(doc, p.name)).ToList();
            var created = new List<string>();
            if (wanted.Count == 0) return created;

            var app = doc.Application;
            string previous = app.SharedParametersFilename;
            string file = Path.Combine(McpPaths.Root, "shared_parameters.txt");
            try
            {
                if (!File.Exists(file)) File.WriteAllText(file, "");
                app.SharedParametersFilename = file;
                DefinitionFile defs = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Cannot open " + file);
                DefinitionGroup group = defs.Groups.get_Item("MCP Acero") ?? defs.Groups.Create("MCP Acero");
                var cats = new CategorySet();
                foreach (BuiltInCategory bic in Categories) cats.Insert(doc.Settings.Categories.get_Item(bic));
                foreach (var p in wanted)
                {
                    var def = FindDefinition(defs, p.name) ?? group.Definitions.Create(Options(p.name, p.number));
#if REVIT2022_OR_GREATER
                    doc.ParameterBindings.Insert(def, app.Create.NewInstanceBinding(cats), GroupTypeId.Data);
#else
                    doc.ParameterBindings.Insert(def, app.Create.NewInstanceBinding(cats), BuiltInParameterGroup.PG_DATA);
#endif
                    created.Add(p.name);
                }
            }
            finally
            {
                app.SharedParametersFilename = previous ?? "";
            }
            return created;
        }

        private static ExternalDefinitionCreationOptions Options(string name, bool number)
        {
#if REVIT2022_OR_GREATER
            return new ExternalDefinitionCreationOptions(name, number ? SpecTypeId.Number : SpecTypeId.String.Text);
#else
            return new ExternalDefinitionCreationOptions(name, number ? ParameterType.Number : ParameterType.Text);
#endif
        }

        private static Definition FindDefinition(DefinitionFile defs, string name)
        {
            foreach (DefinitionGroup g in defs.Groups)
                if (g.Definitions.get_Item(name) is ExternalDefinition d) return d;
            return null;
        }

        public static bool Bound(Document doc, string name)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Key.Name == name) return true;
            return false;
        }

        /// <summary>Id of the shared parameter, for schedule fields and filters.</summary>
        public static ElementId Id(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>()
                .FirstOrDefault(p => p.Name == name)?.Id ?? throw new InvalidOperationException("Parameter " + name + " is not in the model");

        public static void Set(Element e, string name, string value)
        {
            Parameter p = e.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value ?? "");
        }

        public static void Set(Element e, string name, double value)
        {
            Parameter p = e.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(value);
        }

        /// <summary>The lap-marker line style (magenta, heavy), created when missing; inside a transaction.</summary>
        public static GraphicsStyle LapStyle(Document doc)
        {
            Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category style = null;
            foreach (Category c in lines.SubCategories)
                if (c.Name == LapLineStyle) style = c;
            if (style == null)
            {
                style = doc.Settings.Categories.NewSubcategory(lines, LapLineStyle);
                style.LineColor = new Color(230, 0, 126);
                style.SetLineWeight(8, GraphicsStyleType.Projection);
            }
            return style.GetGraphicsStyle(GraphicsStyleType.Projection);
        }
    }
}
