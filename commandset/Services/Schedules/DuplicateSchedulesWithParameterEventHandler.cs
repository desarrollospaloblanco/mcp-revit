using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Schedules
{
    /// <summary>
    /// 复制明细表并重设参数值的事件处理器
    /// Duplicates schedules, renames them and retargets both the parameter filter and the view
    /// parameter to a new value, so the copies group next to the originals in the project browser.
    /// </summary>
    public class DuplicateSchedulesWithParameterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private IList<long> _scheduleIds;
        private string _parameterName;
        private string _newValue;
        private string _namePrefix;
        private string _prefixToReplace;
        private string _fieldType;
        private bool _updateFilter;
        private bool _addFilterIfMissing;
        private bool _setViewParameter;

        public object Results { get; private set; }

        public void SetParameters(
            IList<long> scheduleIds,
            string parameterName,
            string newValue,
            string namePrefix,
            string prefixToReplace,
            string fieldType,
            bool updateFilter,
            bool addFilterIfMissing,
            bool setViewParameter)
        {
            _scheduleIds = scheduleIds;
            _parameterName = parameterName;
            _newValue = newValue;
            _namePrefix = namePrefix ?? string.Empty;
            _prefixToReplace = prefixToReplace;
            _fieldType = fieldType;
            _updateFilter = updateFilter;
            _addFilterIfMissing = addFilterIfMissing;
            _setViewParameter = setViewParameter;
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
                UIDocument uiDoc = uiApp.ActiveUIDocument;
                Document doc = uiDoc.Document;

                ScheduleFieldType fieldType = ScheduleFieldHelper.ParseFieldType(_fieldType);
                List<ViewSchedule> schedules = ScheduleFieldHelper.ResolveSchedules(uiDoc, _scheduleIds);

                if (schedules.Count == 0)
                {
                    Results = new
                    {
                        success = false,
                        message = "No schedules to duplicate. Pass scheduleIds or select schedules in the project browser.",
                        results = new List<object>()
                    };
                    return;
                }

                var results = new List<object>();
                int created = 0;

                using (Transaction tran = new Transaction(doc, "Duplicate schedules for parameter value"))
                {
                    tran.Start();

                    foreach (ViewSchedule original in schedules)
                    {
                        string originalName = original.Name;
                        try
                        {
                            results.Add(DuplicateOne(doc, original, fieldType));
                            created++;
                        }
                        catch (Exception ex)
                        {
                            results.Add(new
                            {
                                sourceId = ScheduleFieldHelper.ToLong(original.Id),
                                sourceName = originalName,
                                success = false,
                                message = ex.Message
                            });
                        }
                    }

                    tran.Commit();
                }

                Results = new
                {
                    success = true,
                    message = $"{created} of {schedules.Count} schedules duplicated for '{_parameterName}' = '{_newValue}'.",
                    results
                };
            }
            catch (Exception ex)
            {
                Results = new
                {
                    success = false,
                    message = ex.Message,
                    results = new List<object>()
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private object DuplicateOne(Document doc, ViewSchedule original, ScheduleFieldType fieldType)
        {
            if (!original.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                throw new InvalidOperationException("This schedule cannot be duplicated.");

            ElementId newId = original.Duplicate(ViewDuplicateOption.Duplicate);
            var copy = (ViewSchedule)doc.GetElement(newId);

            string newName = BuildName(doc, original.Name);
            copy.Name = newName;

            ScheduleDefinition definition = copy.Definition;
            string filterState = "not requested";

            if (_updateFilter)
            {
                ElementId parameterId = ScheduleFieldHelper.FindSchedulableParameterId(doc, definition, _parameterName, fieldType);
                ScheduleField field = parameterId == null
                    ? null
                    : ScheduleFieldHelper.FindExistingField(definition, parameterId, fieldType);

                if (field == null && parameterId != null && _addFilterIfMissing)
                {
                    field = definition.AddField(fieldType, parameterId);
                    field.IsHidden = true;
                }

                if (field == null)
                {
                    filterState = parameterId == null
                        ? $"skipped: '{_parameterName}' is not schedulable in this category"
                        : "skipped: the schedule has no field for this parameter";
                }
                else
                {
                    ScheduleFilter filter = ScheduleFieldHelper.BuildFilter(field.FieldId, ScheduleFilterType.Equal, _newValue);
                    int index = ScheduleFieldHelper.FindFilterIndex(definition, field.FieldId);

                    if (index >= 0)
                    {
                        definition.SetFilter(index, filter);
                        filterState = "updated";
                    }
                    else if (_addFilterIfMissing)
                    {
                        definition.AddFilter(filter);
                        filterState = "added";
                    }
                    else
                    {
                        filterState = "skipped: no existing filter on this parameter";
                    }
                }
            }

            string viewParameterState = "not requested";
            if (_setViewParameter)
            {
                Parameter viewParameter = copy.LookupParameter(_parameterName);
                if (viewParameter == null)
                    viewParameterState = "skipped: the view has no such parameter";
                else if (viewParameter.IsReadOnly)
                    viewParameterState = "skipped: read only";
                else
                    viewParameterState = viewParameter.Set(_newValue) ? "set" : "failed";
            }

            return new
            {
                sourceId = ScheduleFieldHelper.ToLong(original.Id),
                sourceName = original.Name,
                newId = ScheduleFieldHelper.ToLong(newId),
                newName,
                success = true,
                filterState,
                viewParameterState
            };
        }

        private string BuildName(Document doc, string originalName)
        {
            string baseName = originalName;

            if (!string.IsNullOrEmpty(_prefixToReplace) && baseName.StartsWith(_prefixToReplace, StringComparison.Ordinal))
                baseName = baseName.Substring(_prefixToReplace.Length);

            string candidate = _namePrefix + baseName;

            var taken = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!taken.Contains(candidate))
                return candidate;

            for (int i = 2; i < 1000; i++)
            {
                string numbered = $"{candidate} ({i})";
                if (!taken.Contains(numbered))
                    return numbered;
            }

            throw new InvalidOperationException($"Could not find a free name for '{candidate}'.");
        }

        public string GetName() => "Duplicate schedules with parameter value";
    }
}
