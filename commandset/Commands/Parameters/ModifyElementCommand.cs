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
    /// Modifies one or more parameters of an existing element.
    /// </summary>
    public class ModifyElementCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private ModifyElementEventHandler _handler => (ModifyElementEventHandler)Handler;

        public override string CommandName => "modify_element";

        public ModifyElementCommand(UIApplication uiApp)
            : base(new ModifyElementEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    long elementId = ParameterCommandParsing.ParseElementId(parameters?["elementId"]);

                    var changesToken = parameters?["changes"] as JArray;
                    if (changesToken == null || changesToken.Count == 0)
                        throw new ArgumentException("changes[] is required and must not be empty");

                    var changes = new List<ParameterChangeInfo>();
                    foreach (var token in changesToken)
                    {
                        changes.Add(ParameterCommandParsing.ParseChange(token));
                    }

                    _handler.SetParameters(elementId, changes);

                    if (!RaiseAndWaitForCompletion(15000))
                        throw new TimeoutException("Timed out while modifying element " + elementId);

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to modify element: " + ex.Message);
                }
            }
        }
    }
}
