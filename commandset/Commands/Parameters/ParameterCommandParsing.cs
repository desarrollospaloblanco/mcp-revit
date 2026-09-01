using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Commands.Parameters
{
    /// <summary>
    /// Argument parsing shared by the parameter commands. Element ids and values arrive
    /// either as JSON numbers or as strings depending on the caller, so both are accepted.
    /// </summary>
    internal static class ParameterCommandParsing
    {
        public static long ParseElementId(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException("elementId is required");

            long id;
            if (!long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                throw new ArgumentException("elementId must be a whole number, got: " + token);

            return id;
        }

        public static ParameterChangeInfo ParseChange(JToken token)
        {
            var change = token as JObject;
            if (change == null)
                throw new ArgumentException("Each entry of changes[] must be an object");

            var name = change["parameterName"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("parameterName is required in every change");

            return new ParameterChangeInfo
            {
                ParameterName = name,
                Value = ParseValue(change["value"]),
                UseDisplayUnits = change["useDisplayUnits"]?.Value<bool>() ?? false
            };
        }

        /// <summary>
        /// Normalizes a value to the string form the add-in converts by StorageType.
        /// Booleans become "true"/"false", which the Integer branch understands.
        /// </summary>
        public static string ParseValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException("value is required");

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);

            return token.ToString();
        }
    }
}
