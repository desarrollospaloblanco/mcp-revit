using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.MEP
{
    /// <summary>
    /// Read-only MEP model review: content summary, open connectors, ducts and pipes outside any
    /// system, system list, uninsulated runs, sanitary pipes below the minimum slope and electrical
    /// devices without a circuit. Nothing is modified. Each list returns counts plus at most
    /// maxIds element ids, so the agent can select them afterwards.
    /// </summary>
    public class MepReviewEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public static readonly string[] AllChecks =
            { "content", "open_connectors", "outside_systems", "systems", "uninsulated", "slopes", "circuits" };

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        private string _documentTitle;
        private HashSet<string> _checks;
        private double _minSlope;
        private int _maxIds;

        public Dictionary<string, object> Result { get; private set; }
        public string Error { get; private set; }

        private static readonly (string Name, BuiltInCategory Category)[] Content =
        {
            ("Ductos", BuiltInCategory.OST_DuctCurves), ("Ductos flexibles", BuiltInCategory.OST_FlexDuctCurves),
            ("Accesorios de ducto", BuiltInCategory.OST_DuctFitting), ("Complementos de ducto", BuiltInCategory.OST_DuctAccessory),
            ("Terminales de aire", BuiltInCategory.OST_DuctTerminal), ("Equipo mecánico", BuiltInCategory.OST_MechanicalEquipment),
            ("Tuberías", BuiltInCategory.OST_PipeCurves), ("Tuberías flexibles", BuiltInCategory.OST_FlexPipeCurves),
            ("Accesorios de tubería", BuiltInCategory.OST_PipeFitting), ("Complementos de tubería", BuiltInCategory.OST_PipeAccessory),
            ("Aparatos sanitarios", BuiltInCategory.OST_PlumbingFixtures), ("Rociadores", BuiltInCategory.OST_Sprinklers),
            ("Bandejas de cable", BuiltInCategory.OST_CableTray), ("Conduits", BuiltInCategory.OST_Conduit),
            ("Equipo eléctrico", BuiltInCategory.OST_ElectricalEquipment), ("Aparatos eléctricos", BuiltInCategory.OST_ElectricalFixtures),
            ("Luminarias", BuiltInCategory.OST_LightingFixtures)
        };

        private static readonly BuiltInCategory[] Connectable =
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers
        };

        private static readonly BuiltInCategory[] ElectricalDevices =
        {
            BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_CommunicationDevices
        };

        public void SetParameters(string documentTitle, IEnumerable<string> checks, double minSlopePercent, int maxIds)
        {
            _documentTitle = documentTitle;
            var requested = (checks ?? Enumerable.Empty<string>()).Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).ToList();
            _checks = new HashSet<string>(requested.Count > 0 ? requested : AllChecks);
            _minSlope = minSlopePercent / 100.0;
            _maxIds = maxIds;
            Result = null;
            Error = null;
            _resetEvent.Reset();
        }

        public string GetName() => "MCP: revisión MEP";

        public bool WaitForCompletion(int timeoutMilliseconds = 300000) => _resetEvent.WaitOne(timeoutMilliseconds);

        public void Execute(UIApplication app)
        {
            try
            {
                Document doc = QuantificationUtils.ResolveDocument(app, _documentTitle, out string error);
                if (doc == null)
                    throw new InvalidOperationException(error);

                var result = new Dictionary<string, object> { ["document"] = doc.Title, ["checks"] = _checks.ToList() };
                if (_checks.Contains("content")) result["content"] = ContentSummary(doc);
                if (_checks.Contains("open_connectors")) result["open_connectors"] = OpenConnectors(doc);
                if (_checks.Contains("outside_systems")) result["outside_systems"] = OutsideSystems(doc);
                if (_checks.Contains("systems")) result["systems"] = Systems(doc);
                if (_checks.Contains("uninsulated")) result["uninsulated"] = Uninsulated(doc);
                if (_checks.Contains("slopes")) result["slopes"] = Slopes(doc);
                if (_checks.Contains("circuits")) result["circuits"] = Circuits(doc);
                Result = result;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static IEnumerable<Element> Instances(Document doc, BuiltInCategory category) =>
            new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType();

        private static long Id(Element e) => Utils.ElementIdExtensions.GetValue(e.Id);

        private object ContentSummary(Document doc) =>
            Content.Select(c => new { category = c.Name, count = Instances(doc, c.Category).Count() })
                .Where(c => c.count > 0).ToList();

        private static IEnumerable<Connector> Connectors(Element element)
        {
            ConnectorManager manager = (element as MEPCurve)?.ConnectorManager
                                       ?? (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            return manager == null ? Enumerable.Empty<Connector>() : manager.Connectors.Cast<Connector>();
        }

        private static string LevelName(Document doc, Element element)
        {
            Level level = (element as MEPCurve)?.ReferenceLevel;
            if (level == null && element.LevelId != ElementId.InvalidElementId)
                level = doc.GetElement(element.LevelId) as Level;
            return level?.Name ?? "(sin nivel)";
        }

        private object OpenConnectors(Document doc)
        {
            var byLevel = new Dictionary<string, int>();
            var sample = new List<object>();
            foreach (var category in Connectable)
            {
                foreach (Element element in Instances(doc, category))
                {
                    try
                    {
                        var open = Connectors(element).Where(c => c.ConnectorType == ConnectorType.End &&
                                                                  c.Domain != Domain.DomainElectrical && !c.IsConnected).ToList();
                        if (open.Count == 0) continue;
                        string level = LevelName(doc, element);
                        byLevel[level] = byLevel.TryGetValue(level, out int n) ? n + 1 : 1;
                        if (sample.Count < _maxIds)
                        {
                            XYZ p = open[0].Origin;
                            sample.Add(new
                            {
                                id = Id(element), category = element.Category?.Name, level, openEnds = open.Count,
                                pointM = new[] { Math.Round(p.X * 0.3048, 2), Math.Round(p.Y * 0.3048, 2), Math.Round(p.Z * 0.3048, 2) }
                            });
                        }
                    }
                    catch (Exception) { /* elements without usable connectors are skipped */ }
                }
            }
            return new
            {
                elementsWithOpenEnds = byLevel.Values.Sum(),
                byLevel,
                elements = sample,
                note = "Los extremos abiertos en equipos son normales mientras el modelo está en proceso."
            };
        }

        private object OutsideSystems(Document doc)
        {
            var result = new Dictionary<string, object>();
            foreach (var (name, category) in new[] { ("Ductos", BuiltInCategory.OST_DuctCurves), ("Ductos flexibles", BuiltInCategory.OST_FlexDuctCurves),
                         ("Tuberías", BuiltInCategory.OST_PipeCurves), ("Tuberías flexibles", BuiltInCategory.OST_FlexPipeCurves) })
            {
                var ids = new List<long>();
                int total = 0;
                foreach (Element element in Instances(doc, category))
                {
                    try
                    {
                        if ((element as MEPCurve)?.MEPSystem != null) continue;
                    }
                    catch (Exception) { continue; }
                    total++;
                    if (ids.Count < _maxIds) ids.Add(Id(element));
                }
                result[name] = new { count = total, ids };
            }
            return result;
        }

        private object Systems(Document doc)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var (type, kind) in new[] { (typeof(MechanicalSystem), "HVAC"), (typeof(PipingSystem), "Tubería") })
            {
                foreach (MEPSystem system in new FilteredElementCollector(doc).OfClass(type).Cast<MEPSystem>())
                {
                    try
                    {
                        rows.Add(new Dictionary<string, object>
                        {
                            ["kind"] = kind,
                            ["name"] = system.Name,
                            ["classification"] = system.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString(),
                            ["elementCount"] = system.Elements.Size,
                            ["baseEquipment"] = system.BaseEquipment?.Name
                        });
                    }
                    catch (Exception) { /* a system in an invalid state is left out of the list */ }
                }
            }
            return new
            {
                count = rows.Count,
                withoutBaseEquipment = rows.Count(r => r["baseEquipment"] == null),
                systems = rows.OrderBy(r => r["kind"]).ThenBy(r => r["name"]).Take(300).ToList()
            };
        }

        private object Uninsulated(Document doc)
        {
            var result = new Dictionary<string, object>();
            foreach (var (name, category, systemParam) in new[]
                     {
                         ("Ductos", BuiltInCategory.OST_DuctCurves, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM),
                         ("Tuberías", BuiltInCategory.OST_PipeCurves, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                     })
            {
                var groups = new Dictionary<string, (int count, List<long> ids)>();
                foreach (Element element in Instances(doc, category))
                {
                    string systemType;
                    try
                    {
                        if (InsulationLiningBase.GetInsulationIds(doc, element.Id).Count > 0) continue;
                        systemType = element.get_Parameter(systemParam)?.AsValueString();
                    }
                    catch (Exception) { continue; }
                    string key = string.IsNullOrEmpty(systemType) ? "(sin tipo de sistema)" : systemType;
                    var g = groups.TryGetValue(key, out var existing) ? existing : (0, new List<long>());
                    if (g.Item2.Count < 50) g.Item2.Add(Id(element));
                    groups[key] = (g.Item1 + 1, g.Item2);
                }
                result[name] = groups.ToDictionary(kv => kv.Key, kv => (object)new { count = kv.Value.count, ids = kv.Value.ids });
            }
            return result;
        }

        private object Slopes(Document doc)
        {
            int total = 0;
            var sample = new List<object>();
            foreach (Pipe pipe in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>())
            {
                double slope;
                string system;
                try
                {
                    if (!(pipe.MEPSystem is PipingSystem ps) || ps.SystemType != PipeSystemType.Sanitary) continue;
                    if (pipe.Location is LocationCurve lc && lc.Curve is Line line && Math.Abs(line.Direction.Z) > 0.99)
                        continue; // vertical stacks have no slope
                    slope = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE)?.AsDouble() ?? 0;
                    system = ps.Name;
                }
                catch (Exception) { continue; }

                if (slope >= _minSlope) continue;
                total++;
                if (sample.Count < _maxIds)
                    sample.Add(new { id = Id(pipe), system, slopePercent = Math.Round(slope * 100, 2) });
            }
            return new { minSlopePercent = _minSlope * 100, pipesBelowMinimum = total, pipes = sample };
        }

        private object Circuits(Document doc)
        {
            int total = 0;
            var sample = new List<object>();
            foreach (var category in ElectricalDevices)
            {
                foreach (FamilyInstance fi in Instances(doc, category).OfType<FamilyInstance>())
                {
                    try
                    {
                        var manager = fi.MEPModel?.ConnectorManager;
                        if (manager == null || !manager.Connectors.Cast<Connector>().Any(c => c.Domain == Domain.DomainElectrical))
                            continue;
#if REVIT2022_OR_GREATER
                        if (fi.MEPModel.GetElectricalSystems().Count > 0) continue;
#else
                        if (fi.MEPModel.ElectricalSystems != null && fi.MEPModel.ElectricalSystems.Size > 0) continue;
#endif
                    }
                    catch (Exception) { continue; }

                    total++;
                    if (sample.Count < _maxIds)
                        sample.Add(new { id = Id(fi), category = fi.Category?.Name, family = fi.Symbol?.FamilyName, type = fi.Name });
                }
            }
            return new { devicesWithoutCircuit = total, devices = sample };
        }
    }
}
