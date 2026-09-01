using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Parameters
{
    /// <summary>
    /// Writes a single parameter on one element (elementId) or on many (elementIds).
    /// </summary>
    public class SetParameterCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private SetParameterEventHandler _handler => (SetParameterEventHandler)Handler;

        public override string CommandName => "set_parameter";

        public SetParameterCommand(UIApplication uiApp)
            : base(new SetParameterEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var elementIds = ParseElementIds(parameters);

                    var parameterName = parameters?["parameterName"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(parameterName))
                        throw new ArgumentException("parameterName is required");

                    var change = new ParameterChangeInfo
                    {
                        ParameterName = parameterName,
                        Value = ParameterCommandParsing.ParseValue(parameters?["value"]),
                        UseDisplayUnits = parameters?["useDisplayUnits"]?.Value<bool>() ?? false
                    };

                    _handler.SetParameters(elementIds, change);

                    if (!RaiseAndWaitForCompletion(15000))
                        throw new TimeoutException("Timed out while setting '" + parameterName + "'");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to set parameter: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Accepts either a single elementId or an elementIds array.
        /// </summary>
        private static List<long> ParseElementIds(JObject parameters)
        {
            var ids = new List<long>();

            var array = parameters?["elementIds"] as JArray;
            if (array != null && array.Count > 0)
            {
                foreach (var token in array)
                {
                    ids.Add(ParameterCommandParsing.ParseElementId(token));
                }
                return ids;
            }

            ids.Add(ParameterCommandParsing.ParseElementId(parameters?["elementId"]));
            return ids;
        }
    }
}
