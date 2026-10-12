using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Parameters
{
    /// <summary>
    /// 批量规范文本参数大小写的事件处理器
    /// Normalizes the text case of a parameter across element types, instances and materials.
    /// </summary>
    public class NormalizeParameterTextEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private string _parameterName;
        private string _textCase;
        private IList<string> _targets;
        private IList<string> _categories;
        private bool _removeAccents;
        private bool _trim;
        private bool _onlyInUse;
        private bool _dryRun;

        public object Results { get; private set; }

        public void SetParameters(
            string parameterName,
            string textCase,
            IList<string> targets,
            IList<string> categories,
            bool removeAccents,
            bool trim,
            bool onlyInUse,
            bool dryRun)
        {
            _parameterName = parameterName;
            _textCase = string.IsNullOrWhiteSpace(textCase) ? "Upper" : textCase;
            _targets = targets != null && targets.Count > 0 ? targets : new List<string> { "ElementType", "Material" };
            _categories = categories;
            _removeAccents = removeAccents;
            _trim = trim;
            _onlyInUse = onlyInUse;
            _dryRun = dryRun;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication uiApp)
        {
            try
            {
                Document doc = uiApp.ActiveUIDocument.Document;

                bool doTypes = HasTarget("ElementType");
                bool doInstances = HasTarget("Instance");
                bool doMaterials = HasTarget("Material");

                HashSet<ElementId> typesInUse = doTypes && _onlyInUse ? CollectTypesInUse(doc) : null;

                var pending = new List<PendingChange>();

                if (doTypes)
                {
                    foreach (Element element in new FilteredElementCollector(doc).WhereElementIsElementType())
                    {
                        if (typesInUse != null && !typesInUse.Contains(element.Id)) continue;
                        Collect(element, "ElementType", pending);
                    }
                }

                if (doInstances)
                {
                    foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                        Collect(element, "Instance", pending);
                }

                if (doMaterials)
                {
                    foreach (Element element in new FilteredElementCollector(doc).OfClass(typeof(Material)))
                        Collect(element, "Material", pending);
                }

                int changed = 0;
                int failed = 0;
                var errors = new List<string>();

                if (!_dryRun && pending.Count > 0)
                {
                    using (Transaction tran = new Transaction(doc, $"Normalize '{_parameterName}' to {_textCase}"))
                    {
                        tran.Start();

                        foreach (PendingChange change in pending)
                        {
                            try
                            {
                                if (change.Parameter.Set(change.NewValue))
                                    changed++;
                                else
                                {
                                    failed++;
                                    if (errors.Count < 20) errors.Add($"{change.ElementName}: Set returned false");
                                }
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                if (errors.Count < 20) errors.Add($"{change.ElementName}: {ex.Message}");
                            }
                        }

                        tran.Commit();
                    }
                }

                Results = new
                {
                    success = true,
                    dryRun = _dryRun,
                    message = _dryRun
                        ? $"{pending.Count} values would change for '{_parameterName}'. Nothing was written."
                        : $"{changed} values updated for '{_parameterName}', {failed} failed.",
                    candidates = pending.Count,
                    changed,
                    failed,
                    errors,
                    byTarget = pending.GroupBy(p => p.Target).Select(g => new { target = g.Key, count = g.Count() }).ToList(),
                    samples = pending.Take(25).Select(p => new
                    {
                        p.Target,
                        element = p.ElementName,
                        from = p.OldValue,
                        to = p.NewValue
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                Results = new { success = false, message = ex.Message };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private bool HasTarget(string target) =>
            _targets.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));

        private static HashSet<ElementId> CollectTypesInUse(Document doc)
        {
            var inUse = new HashSet<ElementId>();
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId) inUse.Add(typeId);
            }

            return inUse;
        }

        private void Collect(Element element, string target, List<PendingChange> pending)
        {
            if (!MatchesCategory(element)) return;

            Parameter parameter = element.LookupParameter(_parameterName);
            if (parameter == null || parameter.StorageType != StorageType.String || parameter.IsReadOnly) return;

            string current = parameter.AsString();
            if (string.IsNullOrWhiteSpace(current)) return;

            string normalized = Normalize(current);
            if (normalized == current) return;

            pending.Add(new PendingChange
            {
                Parameter = parameter,
                Target = target,
                ElementName = element.Name,
                OldValue = current,
                NewValue = normalized
            });
        }

        private bool MatchesCategory(Element element)
        {
            if (_categories == null || _categories.Count == 0) return true;
            if (element.Category == null) return false;

            return _categories.Any(c => string.Equals(c, element.Category.Name, StringComparison.OrdinalIgnoreCase));
        }

        private string Normalize(string value)
        {
            string result = _trim ? value.Trim() : value;

            switch (_textCase.ToUpperInvariant())
            {
                case "UPPER":
                    result = result.ToUpper();
                    break;
                case "LOWER":
                    result = result.ToLower();
                    break;
                case "TITLE":
                    result = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(result.ToLower());
                    break;
                case "SENTENCE":
                    result = result.ToLower();
                    if (result.Length > 0) result = char.ToUpper(result[0]) + result.Substring(1);
                    break;
                case "NONE":
                    break;
                default:
                    throw new ArgumentException($"Unknown textCase '{_textCase}'.");
            }

            return _removeAccents ? StripAccents(result) : result;
        }

        private static string StripAccents(string value)
        {
            string decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(c);
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private class PendingChange
        {
            public Parameter Parameter { get; set; }
            public string Target { get; set; }
            public string ElementName { get; set; }
            public string OldValue { get; set; }
            public string NewValue { get; set; }
        }

        public string GetName() => "Normalize parameter text";
    }
}
