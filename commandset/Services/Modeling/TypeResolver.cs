using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// Finds or creates the element types a spec refers to, by name.
    ///
    /// A type that already exists is used as it is. Types this spec created are stamped, so
    /// when the spec later changes their thickness or section they are updated; a type the
    /// spec did not create is never modified — a mismatch is reported instead, because that
    /// type may be in use elsewhere in the model.
    /// </summary>
    public class TypeResolver
    {
        private const double FeetPerMetre = 1.0 / 0.3048;
        private const double Tolerance = 1e-6;

        private static readonly string[] WidthNames = { "b", "Width", "Anchura", "Ancho", "Base" };
        private static readonly string[] DepthNames = { "h", "Depth", "Profundidad", "Peralte", "Altura", "Height" };

        private readonly Document _doc;
        private readonly TypesSpec _spec;
        private readonly string _specName;
        private readonly BuildReport _report;
        private readonly Dictionary<string, Element> _cache = new Dictionary<string, Element>(StringComparer.Ordinal);

        public TypeResolver(Document doc, TypesSpec spec, string specName, BuildReport report)
        {
            _doc = doc;
            _spec = spec ?? new TypesSpec();
            _specName = specName;
            _report = report;
        }

        /// <summary>Forgets resolved types, after a stage whose transaction rolled back.</summary>
        public void Reset() => _cache.Clear();

        public WallType Wall(string name, out string error)
        {
            return ResolveLayered<WallType>("wallType", name, _spec.Walls, out error);
        }

        public FloorType Floor(string name, out string error)
        {
            return ResolveLayered<FloorType>("floorType", name, _spec.Floors, out error);
        }

        public FamilySymbol Column(string name, out string error)
        {
            SectionTypeSpec def = _spec.Columns.FirstOrDefault(t => t.Name == name);
            var category = def != null && def.Architectural ? BuiltInCategory.OST_Columns : BuiltInCategory.OST_StructuralColumns;
            return ResolveSection("columnType", name, def, category, out error);
        }

        public FamilySymbol Beam(string name, out string error)
        {
            SectionTypeSpec def = _spec.Beams.FirstOrDefault(t => t.Name == name);
            return ResolveSection("beamType", name, def, BuiltInCategory.OST_StructuralFraming, out error);
        }

        public FamilySymbol Door(string name, out string error)
        {
            OpeningTypeSpec def = _spec.Doors.FirstOrDefault(t => t.Name == name);
            return ResolveOpening("doorType", name, def, BuiltInCategory.OST_Doors,
                BuiltInParameter.DOOR_WIDTH, BuiltInParameter.DOOR_HEIGHT, out error);
        }

        /// <summary>
        /// A window type. Sliding balcony doors are often window families (they sit in the
        /// facade with the windows), so they resolve here as well.
        /// </summary>
        public FamilySymbol Window(string name, out string error)
        {
            OpeningTypeSpec def = _spec.Windows.FirstOrDefault(t => t.Name == name);
            return ResolveOpening("windowType", name, def, BuiltInCategory.OST_Windows,
                BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.WINDOW_HEIGHT, out error);
        }

        private static readonly string[] OpeningWidthNames = { "Width", "Anchura", "Ancho" };
        private static readonly string[] OpeningHeightNames = { "Height", "Altura", "Alto" };

        private FamilySymbol ResolveOpening(string kind, string name, OpeningTypeSpec def, BuiltInCategory category,
            BuiltInParameter widthId, BuiltInParameter heightId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "no type given";
                return null;
            }

            string key = kind + ":" + name;
            if (_cache.TryGetValue(key, out Element cached))
                return (FamilySymbol)cached;

            List<FamilySymbol> symbols = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().ToList();

            FamilySymbol existing = symbols.FirstOrDefault(s => s.Name == name &&
                (def == null || string.IsNullOrWhiteSpace(def.Family) || s.FamilyName == def.Family));
            if (existing != null)
            {
                if (def != null && IsOurs(existing))
                    SetOpeningSize(existing, def, widthId, heightId);
                _cache[key] = existing;
                return existing;
            }

            if (def == null || def.Width <= 0 || def.Height <= 0 || string.IsNullOrWhiteSpace(def.Family))
            {
                error = "type '" + name + "' does not exist and is not defined with family, width and height under types";
                return null;
            }

            FamilySymbol template = symbols.FirstOrDefault(s => s.FamilyName == def.Family);
            if (template == null)
            {
                error = "no loaded " + category.ToString().Replace("OST_", "") + " family is named '" + def.Family +
                        "'. Loaded: " + string.Join(", ", symbols.Select(s => s.FamilyName).Distinct());
                return null;
            }

            var created = (FamilySymbol)template.Duplicate(name);
            try
            {
                if (!SetOpeningSize(created, def, widthId, heightId))
                    throw new InvalidOperationException("family '" + def.Family + "' has no editable width and height type parameters");
                SpecTagStorage.Write(created, Stamp(key, def));
            }
            catch
            {
                _doc.Delete(created.Id);
                throw;
            }
            _report.TypesCreated.Add(name + " (from " + template.FamilyName + ": " + template.Name + ")");

            _cache[key] = created;
            return created;
        }

        private static bool SetOpeningSize(FamilySymbol symbol, OpeningTypeSpec def, BuiltInParameter widthId, BuiltInParameter heightId)
        {
            Parameter width = Writable(symbol.get_Parameter(widthId)) ?? FindLengthParameter(symbol, null, OpeningWidthNames);
            Parameter height = Writable(symbol.get_Parameter(heightId)) ?? FindLengthParameter(symbol, null, OpeningHeightNames);
            if (width == null || height == null)
                return false;
            width.Set(def.Width * FeetPerMetre);
            height.Set(def.Height * FeetPerMetre);
            return true;
        }

        private static Parameter Writable(Parameter parameter)
        {
            return parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double ? parameter : null;
        }

        private T ResolveLayered<T>(string kind, string name, List<LayeredTypeSpec> defs, out string error)
            where T : HostObjAttributes
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "no type given";
                return null;
            }

            string key = kind + ":" + name;
            if (_cache.TryGetValue(key, out Element cached))
                return (T)cached;

            LayeredTypeSpec def = defs.FirstOrDefault(t => t.Name == name);
            T existing = new FilteredElementCollector(_doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault(t => t.Name == name);

            if (existing != null)
            {
                if (def != null && def.Thickness > 0)
                {
                    double current = existing.GetCompoundStructure()?.GetWidth() ?? 0;
                    double wanted = def.Thickness * FeetPerMetre;
                    if (Math.Abs(current - wanted) > Tolerance)
                    {
                        if (IsOurs(existing))
                        {
                            SetThickness(existing, wanted, ResolveMaterial(def.Material));
                            SpecTagStorage.Write(existing, Stamp(key, def));
                            _report.TypesUpdated.Add(name);
                        }
                        else
                        {
                            _report.Notes.Add("Type '" + name + "' already exists at " + Metres(current) +
                                              " m; the spec asks for " + def.Thickness +
                                              " m but the type was not created by this spec, so it was left alone.");
                        }
                    }
                }

                _cache[key] = existing;
                return existing;
            }

            if (def == null || def.Thickness <= 0)
            {
                error = "type '" + name + "' does not exist and is not defined with a thickness under types";
                return null;
            }

            T baseType = FindLayeredBase<T>(def.Base, out error);
            if (baseType == null)
                return null;

            ElementId material = ResolveMaterial(def.Material);
            if (!string.IsNullOrWhiteSpace(def.Material) && material == null)
            {
                error = "material '" + def.Material + "' does not exist in the document";
                return null;
            }

            var created = (T)baseType.Duplicate(name);
            try
            {
                SetThickness(created, def.Thickness * FeetPerMetre, material);
                SpecTagStorage.Write(created, Stamp(key, def));
            }
            catch
            {
                // A half-built type would be picked up by name on the next entry, with the
                // base type's thickness, so it must not survive the failure.
                _doc.Delete(created.Id);
                throw;
            }
            _report.TypesCreated.Add(name + " (from " + baseType.Name + ")");

            _cache[key] = created;
            return created;
        }

        private ElementId ResolveMaterial(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            Material material = new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => m.Name == name);
            return material?.Id;
        }

        private T FindLayeredBase<T>(string baseName, out string error) where T : HostObjAttributes
        {
            error = null;
            var candidates = new FilteredElementCollector(_doc).OfClass(typeof(T)).Cast<T>()
                .Where(t => t.GetCompoundStructure() != null && t.GetCompoundStructure().LayerCount > 0)
                .ToList();

            if (typeof(T) == typeof(WallType))
                candidates = candidates.Where(t => ((WallType)(object)t).Kind == WallKind.Basic).ToList();
            if (typeof(T) == typeof(FloorType))
                candidates = candidates.Where(t => !((FloorType)(object)t).IsFoundationSlab).ToList();

            if (!string.IsNullOrWhiteSpace(baseName))
            {
                T named = candidates.FirstOrDefault(t => t.Name == baseName);
                if (named == null)
                    error = "base type '" + baseName + "' was not found or has no layers";
                return named;
            }

            // Schematic structure is concrete, so prefer a base whose structural layer already
            // is; its material then carries over into the new type.
            T first = candidates.FirstOrDefault(t => StructuralMaterialName(t).IndexOf("concret", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     StructuralMaterialName(t).IndexOf("hormig", StringComparison.OrdinalIgnoreCase) >= 0)
                      ?? candidates.FirstOrDefault();
            if (first == null)
                error = "the document has no layered " + typeof(T).Name + " to duplicate from";
            return first;
        }

        private string StructuralMaterialName(HostObjAttributes type)
        {
            CompoundStructure structure = type.GetCompoundStructure();
            int index = structure.StructuralMaterialIndex;
            if (index < 0 || index >= structure.LayerCount)
                return string.Empty;
            return _doc.GetElement(structure.GetLayers()[index].MaterialId)?.Name ?? string.Empty;
        }

        /// <summary>
        /// Replaces the layers with a single structural layer of the given width. At schematic
        /// design the finishes are not modelled, and adjusting one layer of a multi-layer base
        /// type would leave its finishes baked into the total.
        ///
        /// The base type's own structure is edited rather than a new one built: a structure made
        /// from scratch carries wall end-cap settings that Revit rejects on floor types.
        /// </summary>
        private static void SetThickness(HostObjAttributes type, double widthFeet, ElementId material)
        {
            CompoundStructure structure = type.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = structure.GetLayers();
            if (material == null)
            {
                int structural = structure.StructuralMaterialIndex;
                material = structural >= 0 && structural < layers.Count
                    ? layers[structural].MaterialId
                    : layers[0].MaterialId;
            }

            structure.SetLayers(new List<CompoundStructureLayer>
            {
                new CompoundStructureLayer(widthFeet, MaterialFunctionAssignment.Structure, material)
            });
            structure.StructuralMaterialIndex = 0;
            type.SetCompoundStructure(structure);
        }

        private FamilySymbol ResolveSection(string kind, string name, SectionTypeSpec def, BuiltInCategory category, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "no type given";
                return null;
            }

            string key = kind + ":" + name;
            if (_cache.TryGetValue(key, out Element cached))
                return (FamilySymbol)cached;

            List<FamilySymbol> symbols = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(category)
                .Cast<FamilySymbol>()
                .ToList();

            FamilySymbol existing = symbols.FirstOrDefault(s => s.Name == name &&
                (def == null || string.IsNullOrWhiteSpace(def.Family) || s.FamilyName == def.Family));

            if (existing != null)
            {
                if (def != null && def.B > 0 && def.H > 0)
                    ReconcileSection(existing, def, key);

                _cache[key] = existing;
                return existing;
            }

            if (def == null || def.B <= 0 || def.H <= 0)
            {
                error = "type '" + name + "' does not exist and is not defined with b and h under types";
                return null;
            }

            FamilySymbol template = null;
            Parameter width = null, depth = null;
            foreach (FamilySymbol candidate in symbols)
            {
                if (!string.IsNullOrWhiteSpace(def.Family) && candidate.FamilyName != def.Family)
                    continue;

                width = FindLengthParameter(candidate, def.WidthParam, WidthNames);
                depth = FindLengthParameter(candidate, def.DepthParam, DepthNames);
                if (width != null && depth != null && width.Id != depth.Id)
                {
                    template = candidate;
                    break;
                }
            }

            if (template == null)
            {
                var families = symbols.Select(s => s.FamilyName).Distinct().ToList();
                error = "no loaded " + category.ToString().Replace("OST_", "") + " family" +
                        (string.IsNullOrWhiteSpace(def.Family) ? "" : " named '" + def.Family + "'") +
                        " has editable width and depth type parameters. Loaded families: " +
                        (families.Count == 0 ? "(none)" : string.Join(", ", families));
                return null;
            }

            var created = (FamilySymbol)template.Duplicate(name);
            try
            {
                FindLengthParameter(created, width.Definition.Name, WidthNames).Set(def.B * FeetPerMetre);
                FindLengthParameter(created, depth.Definition.Name, DepthNames).Set(def.H * FeetPerMetre);
                SpecTagStorage.Write(created, Stamp(key, def));
            }
            catch
            {
                _doc.Delete(created.Id);
                throw;
            }
            _report.TypesCreated.Add(name + " (from " + template.FamilyName + ": " + template.Name +
                                     ", " + width.Definition.Name + " × " + depth.Definition.Name + ")");

            _cache[key] = created;
            return created;
        }

        private void ReconcileSection(FamilySymbol existing, SectionTypeSpec def, string key)
        {
            Parameter width = FindLengthParameter(existing, def.WidthParam, WidthNames);
            Parameter depth = FindLengthParameter(existing, def.DepthParam, DepthNames);
            if (width == null || depth == null)
                return;

            double b = def.B * FeetPerMetre, h = def.H * FeetPerMetre;
            if (Math.Abs(width.AsDouble() - b) < Tolerance && Math.Abs(depth.AsDouble() - h) < Tolerance)
                return;

            if (IsOurs(existing))
            {
                width.Set(b);
                depth.Set(h);
                SpecTagStorage.Write(existing, Stamp(key, def));
                _report.TypesUpdated.Add(existing.Name);
            }
            else
            {
                _report.Notes.Add("Type '" + existing.Name + "' already exists at " + Metres(width.AsDouble()) + " × " +
                                  Metres(depth.AsDouble()) + " m; the spec asks for " + def.B + " × " + def.H +
                                  " m but the type was not created by this spec, so it was left alone.");
            }
        }

        /// <summary>
        /// A writable length type parameter, by explicit name or the first common name found.
        /// Matching ignores case because families spell "b" and "B" interchangeably.
        /// </summary>
        private static Parameter FindLengthParameter(FamilySymbol symbol, string explicitName, string[] candidates)
        {
            IEnumerable<string> names = string.IsNullOrWhiteSpace(explicitName) ? candidates : new[] { explicitName };
            foreach (string wanted in names)
            {
                foreach (Parameter parameter in symbol.Parameters)
                {
                    if (parameter.StorageType == StorageType.Double && !parameter.IsReadOnly &&
                        string.Equals(parameter.Definition.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        return parameter;
                }
            }
            return null;
        }

        private bool IsOurs(Element type)
        {
            SpecTag tag = SpecTagStorage.Read(type);
            return tag != null && tag.Spec == _specName;
        }

        private SpecTag Stamp(string key, object def)
        {
            return new SpecTag { Spec = _specName, Key = key, Signature = JsonConvert.SerializeObject(def) };
        }

        private static double Metres(double feet) => Math.Round(feet * 0.3048, 4);
    }
}
