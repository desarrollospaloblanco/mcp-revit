using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Schedules
{
    /// <summary>
    /// 为多个明细表添加按参数过滤的事件处理器
    /// Adds or updates a parameter based filter on a set of schedules.
    /// </summary>
    public class SetScheduleParameterFilterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private IList<long> _scheduleIds;
        private string _parameterName;
        private string _value;
        private string _filterType;
        private string _fieldType;
        private bool _replaceExistingFilters;
        private bool _hideField;

        public object Results { get; private set; }

        public void SetParameters(
            IList<long> scheduleIds,
            string parameterName,
            string value,
            string filterType,
            string fieldType,
            bool replaceExistingFilters,
            bool hideField)
        {
            _scheduleIds = scheduleIds;
            _parameterName = parameterName;
            _value = value;
            _filterType = filterType;
            _fieldType = fieldType;
            _replaceExistingFilters = replaceExistingFilters;
            _hideField = hideField;
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

                ScheduleFilterType filterType = ScheduleFieldHelper.ParseFilterType(_filterType);
                ScheduleFieldType fieldType = ScheduleFieldHelper.ParseFieldType(_fieldType);
                List<ViewSchedule> schedules = ScheduleFieldHelper.ResolveSchedules(uiDoc, _scheduleIds);

                if (schedules.Count == 0)
                {
                    Results = new
                    {
                        success = false,
                        message = "No schedules to process. Pass scheduleIds or select schedules in the project browser.",
                        results = new List<object>()
                    };
                    return;
                }

                var results = new List<object>();
                int applied = 0;

                using (Transaction tran = new Transaction(doc, "Filter schedules by parameter"))
                {
                    tran.Start();

                    foreach (ViewSchedule schedule in schedules)
                    {
                        string scheduleName = schedule.Name;
                        try
                        {
                            results.Add(ApplyFilter(doc, schedule, fieldType, filterType));
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            results.Add(new
                            {
                                scheduleId = ScheduleFieldHelper.ToLong(schedule.Id),
                                scheduleName,
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
                    message = $"{applied} of {schedules.Count} schedules updated with filter '{_parameterName} {filterType} {_value}'.",
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

        private object ApplyFilter(
            Document doc,
            ViewSchedule schedule,
            ScheduleFieldType fieldType,
            ScheduleFilterType filterType)
        {
            ScheduleDefinition definition = schedule.Definition;

            ElementId parameterId = ScheduleFieldHelper.FindSchedulableParameterId(doc, definition, _parameterName, fieldType);
            if (parameterId == null)
            {
                throw new InvalidOperationException(
                    $"Parameter '{_parameterName}' is not schedulable as {fieldType} here. " +
                    "The project parameter is most likely not bound to this schedule's category.");
            }

            ScheduleField field = ScheduleFieldHelper.FindExistingField(definition, parameterId, fieldType);
            bool fieldAdded = false;

            if (field == null)
            {
                field = definition.AddField(fieldType, parameterId);
                field.IsHidden = _hideField;
                fieldAdded = true;
            }

            if (_replaceExistingFilters)
                definition.ClearFilters();

            ScheduleFilter filter = ScheduleFieldHelper.BuildFilter(field.FieldId, filterType, _value);
            int existingIndex = _replaceExistingFilters
                ? -1
                : ScheduleFieldHelper.FindFilterIndex(definition, field.FieldId);

            if (existingIndex >= 0)
                definition.SetFilter(existingIndex, filter);
            else
                definition.AddFilter(filter);

            return new
            {
                scheduleId = ScheduleFieldHelper.ToLong(schedule.Id),
                scheduleName = schedule.Name,
                success = true,
                fieldAdded,
                fieldHidden = field.IsHidden,
                filterUpdated = existingIndex >= 0,
                filterCount = definition.GetFilterCount()
            };
        }

        public string GetName() => "Set schedule parameter filter";
    }
}
