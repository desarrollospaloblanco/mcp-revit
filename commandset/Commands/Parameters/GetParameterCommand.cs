using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Parameters
{
    /// <summary>
    /// Reads one parameter, or every parameter, from an element.
    /// </summary>
    public class GetParameterCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private GetParameterEventHandler _handler => (GetParameterEventHandler)Handler;

        public override string CommandName => "get_parameter";

        public GetParameterCommand(UIApplication uiApp)
            : base(new GetParameterEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    long elementId = ParameterCommandParsing.ParseElementId(parameters?["elementId"]);
                    string parameterName = parameters?["parameterName"]?.Value<string>();

                    _handler.SetParameters(elementId, parameterName);

                    if (!RaiseAndWaitForCompletion(15000))
                        throw new TimeoutException("Timed out while reading parameters of element " + elementId);

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to read parameters: " + ex.Message);
                }
            }
        }
    }
}
