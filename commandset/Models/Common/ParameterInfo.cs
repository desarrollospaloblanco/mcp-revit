using System.Collections.Generic;

namespace RevitMCPCommandSet.Models.Common
{
    /// <summary>
    /// Serialized view of a single Revit parameter.
    /// </summary>
    public class ParameterValueInfo
    {
        /// <summary>Parameter definition name.</summary>
        public string Name { get; set; }

        /// <summary>Raw internal value as a string. Doubles are in Revit internal units (feet).</summary>
        public string Value { get; set; }

        /// <summary>Formatted value using the project display units, when available.</summary>
        public string ValueString { get; set; }

        /// <summary>String, Integer, Double or ElementId.</summary>
        public string StorageType { get; set; }

        public bool IsReadOnly { get; set; }

        public bool HasValue { get; set; }
    }

    /// <summary>
    /// All parameters read from a single element.
    /// </summary>
    public class ElementParametersInfo
    {
        public long ElementId { get; set; }

        public string ElementName { get; set; }

        public string Category { get; set; }

        public List<ParameterValueInfo> Parameters { get; set; } = new List<ParameterValueInfo>();
    }

    /// <summary>
    /// A single requested parameter change.
    /// </summary>
    public class ParameterChangeInfo
    {
        public string ParameterName { get; set; }

        /// <summary>New value, always transported as a string and converted by StorageType.</summary>
        public string Value { get; set; }

        /// <summary>
        /// When true, a Double value is parsed with the project display units via SetValueString
        /// instead of being treated as Revit internal units (feet).
        /// </summary>
        public bool UseDisplayUnits { get; set; }
    }

    /// <summary>
    /// Outcome of applying one <see cref="ParameterChangeInfo"/>.
    /// </summary>
    public class ParameterChangeResult
    {
        public string ParameterName { get; set; }

        /// <summary>"ok" or "error".</summary>
        public string Status { get; set; }

        public string Message { get; set; }

        public string StorageType { get; set; }

        /// <summary>Resulting formatted value, so the caller can confirm what Revit stored.</summary>
        public string ValueString { get; set; }
    }
}
