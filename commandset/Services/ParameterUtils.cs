using System;
using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Shared read/write logic for element parameters, used by the
    /// get_parameter, set_parameter and modify_element commands.
    /// </summary>
    public static class ParameterUtils
    {
        /// <summary>
        /// Builds an ElementId from a numeric id, honouring the Int64-based API
        /// introduced in Revit 2024.
        /// </summary>
        public static ElementId ToElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            return new ElementId((int)id);
#endif
        }

        /// <summary>
        /// Returns the numeric value behind an ElementId.
        /// </summary>
        public static long FromElementId(ElementId id)
        {
            if (id == null)
                return -1;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        /// <summary>
        /// Finds a parameter by name. Falls back to a case-insensitive scan so that
        /// slightly different casing does not silently fail.
        /// </summary>
        public static Parameter FindParameter(Element element, string parameterName)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return null;

            var param = element.LookupParameter(parameterName);
            if (param != null)
                return param;

            foreach (Parameter candidate in element.Parameters)
            {
                if (string.Equals(candidate.Definition?.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Serializes a parameter into a transport-friendly shape.
        /// </summary>
        public static ParameterValueInfo Serialize(Parameter param)
        {
            var info = new ParameterValueInfo
            {
                Name = param.Definition?.Name,
                StorageType = param.StorageType.ToString(),
                IsReadOnly = param.IsReadOnly,
                HasValue = param.HasValue
            };

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        info.Value = param.AsString();
                        break;
                    case StorageType.Integer:
                        info.Value = param.AsInteger().ToString(CultureInfo.InvariantCulture);
                        break;
                    case StorageType.Double:
                        info.Value = param.AsDouble().ToString(CultureInfo.InvariantCulture);
                        break;
                    case StorageType.ElementId:
                        info.Value = FromElementId(param.AsElementId()).ToString(CultureInfo.InvariantCulture);
                        break;
                }
            }
            catch (Exception ex)
            {
                // A parameter can exist without a readable value; report it instead of
                // failing the whole read.
                info.Value = null;
                info.ValueString = "<unreadable: " + ex.Message + ">";
                return info;
            }

            try
            {
                info.ValueString = param.AsValueString();
            }
            catch (Exception)
            {
                info.ValueString = null;
            }

            return info;
        }

        /// <summary>
        /// Applies a single change to an element. Must be called inside an open transaction.
        /// Never throws: failures are reported through the returned result.
        /// </summary>
        public static ParameterChangeResult ApplyChange(Element element, ParameterChangeInfo change)
        {
            var result = new ParameterChangeResult
            {
                ParameterName = change?.ParameterName,
                Status = "error"
            };

            if (change == null || string.IsNullOrWhiteSpace(change.ParameterName))
            {
                result.Message = "parameterName is required";
                return result;
            }

            var param = FindParameter(element, change.ParameterName);
            if (param == null)
            {
                result.Message = "Parameter not found on this element";
                return result;
            }

            result.StorageType = param.StorageType.ToString();

            if (param.IsReadOnly)
            {
                result.Message = "Parameter is read-only";
                return result;
            }

            try
            {
                string failure;
                if (!SetValue(param, change.Value, change.UseDisplayUnits, out failure))
                {
                    result.Message = failure;
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                return result;
            }

            result.Status = "ok";
            try
            {
                result.ValueString = param.AsValueString();
            }
            catch (Exception)
            {
                result.ValueString = null;
            }

            return result;
        }

        /// <summary>
        /// Writes a string-transported value into a parameter, converting by StorageType.
        /// </summary>
        private static bool SetValue(Parameter param, string rawValue, bool useDisplayUnits, out string failure)
        {
            failure = null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    if (param.Set(rawValue ?? string.Empty))
                        return true;
                    failure = "Revit rejected the string value: " + rawValue;
                    return false;

                case StorageType.Integer:
                    int intValue;
                    if (!TryParseInteger(rawValue, out intValue))
                    {
                        failure = "Not a valid Integer value: " + rawValue;
                        return false;
                    }
                    if (param.Set(intValue))
                        return true;
                    failure = "Revit rejected the integer value: " + rawValue;
                    return false;

                case StorageType.Double:
                    if (useDisplayUnits)
                    {
                        if (param.SetValueString(rawValue))
                            return true;
                        failure = "Revit could not parse using the project display units: " + rawValue;
                        return false;
                    }

                    double doubleValue;
                    if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue))
                    {
                        failure = "Not a valid Double value: " + rawValue;
                        return false;
                    }
                    if (param.Set(doubleValue))
                        return true;
                    failure = "Revit rejected the double value: " + rawValue;
                    return false;

                case StorageType.ElementId:
                    long idValue;
                    if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out idValue))
                    {
                        failure = "Not a valid element id: " + rawValue;
                        return false;
                    }
                    if (param.Set(ToElementId(idValue)))
                        return true;
                    failure = "Revit rejected the element id: " + rawValue;
                    return false;

                default:
                    failure = "StorageType " + param.StorageType + " is not supported";
                    return false;
            }
        }

        /// <summary>
        /// Accepts "1", "true"/"false" and "1.0" for Yes/No and other integer parameters.
        /// </summary>
        private static bool TryParseInteger(string rawValue, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;

            bool boolValue;
            if (bool.TryParse(rawValue, out boolValue))
            {
                value = boolValue ? 1 : 0;
                return true;
            }

            double doubleValue;
            if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue) &&
                Math.Abs(doubleValue - Math.Round(doubleValue)) < double.Epsilon)
            {
                value = (int)Math.Round(doubleValue);
                return true;
            }

            return false;
        }
    }
}
