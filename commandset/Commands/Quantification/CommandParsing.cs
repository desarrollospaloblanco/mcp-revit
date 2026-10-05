using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Commands.Quantification
{
    /// <summary>
    /// Argument parsing shared by the quantification commands.
    /// </summary>
    public static class CommandParsing
    {
        /// <summary>
        /// Turns a JSON array of category names into BuiltInCategory values. Accepts both the
        /// enum spelling ("OST_Floors") and the bare name ("Floors"); unknown names are ignored
        /// so one typo does not sink the whole call.
        /// </summary>
        public static List<BuiltInCategory> ParseCategories(JArray raw, List<BuiltInCategory> fallback)
        {
            if (raw == null || raw.Count == 0)
                return fallback;

            var result = new List<BuiltInCategory>();
            foreach (JToken token in raw)
            {
                string name = token?.Value<string>();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                name = name.Trim();
                if (!name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
                    name = "OST_" + name;

                try
                {
                    var parsed = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), name, true);
                    if (!result.Contains(parsed))
                        result.Add(parsed);
                }
                catch
                {
                    // Unknown category name, skip it.
                }
            }

            return result.Count == 0 ? fallback : result;
        }

        /// <summary>Turns a JSON array into a list of non-empty strings.</summary>
        public static List<string> ParseStrings(JArray raw)
        {
            var result = new List<string>();
            if (raw == null)
                return result;

            foreach (JToken token in raw)
            {
                string value = token?.Value<string>();
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value);
            }

            return result;
        }
    }
}
