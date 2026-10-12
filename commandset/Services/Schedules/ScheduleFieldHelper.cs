using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Services.Schedules
{
    /// <summary>
    /// 明细表字段与过滤器的共享辅助方法
    /// Shared helpers to locate schedule fields and build filters by parameter name.
    /// </summary>
    internal static class ScheduleFieldHelper
    {
        public static ElementId ToElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            return new ElementId((int)id);
#endif
        }

        public static long ToLong(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        /// <summary>
        /// Resolves the schedulable parameter id matching a parameter name, or null when the
        /// parameter cannot be scheduled in this definition (wrong category binding, for example).
        /// </summary>
        public static ElementId FindSchedulableParameterId(
            Document doc,
            ScheduleDefinition definition,
            string parameterName,
            ScheduleFieldType fieldType)
        {
            foreach (SchedulableField schedulable in definition.GetSchedulableFields())
            {
                if (schedulable.FieldType != fieldType)
                    continue;

                string name = schedulable.GetName(doc);
                if (string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase))
                    return schedulable.ParameterId;
            }

            return null;
        }

        /// <summary>
        /// Returns the field already present in the definition for the given parameter, or null.
        /// </summary>
        public static ScheduleField FindExistingField(
            ScheduleDefinition definition,
            ElementId parameterId,
            ScheduleFieldType fieldType)
        {
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField field = definition.GetField(i);
                if (field.ParameterId == parameterId && field.FieldType == fieldType)
                    return field;
            }

            return null;
        }

        /// <summary>
        /// Index of the first filter applied to the given field, or -1.
        /// </summary>
        public static int FindFilterIndex(ScheduleDefinition definition, ScheduleFieldId fieldId)
        {
            for (int i = 0; i < definition.GetFilterCount(); i++)
            {
                if (definition.GetFilter(i).FieldId == fieldId)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Builds a filter for the field, falling back to numeric values when the parameter is not
        /// text based. Throws when the value cannot be applied to the field.
        /// </summary>
        public static ScheduleFilter BuildFilter(ScheduleFieldId fieldId, ScheduleFilterType filterType, string value)
        {
            try
            {
                return new ScheduleFilter(fieldId, filterType, value);
            }
            catch (Exception)
            {
                if (int.TryParse(value, out int intValue))
                    return new ScheduleFilter(fieldId, filterType, intValue);

                if (double.TryParse(value, out double doubleValue))
                    return new ScheduleFilter(fieldId, filterType, doubleValue);

                throw;
            }
        }

        public static ScheduleFilterType ParseFilterType(string filterType)
        {
            if (string.IsNullOrWhiteSpace(filterType))
                return ScheduleFilterType.Equal;

            if (Enum.TryParse(filterType, true, out ScheduleFilterType parsed))
                return parsed;

            throw new ArgumentException($"Unknown filterType '{filterType}'.");
        }

        public static ScheduleFieldType ParseFieldType(string fieldType)
        {
            if (string.IsNullOrWhiteSpace(fieldType))
                return ScheduleFieldType.Instance;

            if (Enum.TryParse(fieldType, true, out ScheduleFieldType parsed))
                return parsed;

            throw new ArgumentException($"Unknown fieldType '{fieldType}'.");
        }

        /// <summary>
        /// Resolves the target schedules: explicit ids when provided, otherwise the current selection.
        /// </summary>
        public static List<ViewSchedule> ResolveSchedules(UIDocument uiDoc, IList<long> scheduleIds)
        {
            Document doc = uiDoc.Document;
            var schedules = new List<ViewSchedule>();

            if (scheduleIds != null && scheduleIds.Count > 0)
            {
                foreach (long id in scheduleIds)
                {
                    if (doc.GetElement(ToElementId(id)) is ViewSchedule schedule)
                        schedules.Add(schedule);
                }

                return schedules;
            }

            foreach (ElementId id in uiDoc.Selection.GetElementIds())
            {
                if (doc.GetElement(id) is ViewSchedule schedule)
                    schedules.Add(schedule);
            }

            return schedules;
        }
    }
}
