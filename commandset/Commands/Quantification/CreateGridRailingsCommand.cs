using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Quantification
{
    /// <summary>
    /// Traces the grid with railings so the setting-out run can be quantified.
    /// </summary>
    public class CreateGridRailingsCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private CreateGridRailingsEventHandler _handler => (CreateGridRailingsEventHandler)Handler;

        public override string CommandName => "create_grid_railings";

        public CreateGridRailingsCommand(UIApplication uiApp)
            : base(new CreateGridRailingsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string typeName = parameters?["typeName"]?.Value<string>();
                    string levelName = parameters?["levelName"]?.Value<string>();
                    string commentPrefix = parameters?["commentPrefix"]?.Value<string>();
                    string assemblyCode = parameters?["assemblyCode"]?.Value<string>();
                    double baseOffset = parameters?["baseOffsetMeters"]?.Value<double>() ?? -20.0;
                    bool trim = parameters?["trimToOuterGrids"]?.Value<bool>() ?? true;

                    _handler.SetParameters(
                        documentTitle,
                        typeName,
                        baseOffset,
                        levelName,
                        trim,
                        commentPrefix,
                        assemblyCode);

                    if (!RaiseAndWaitForCompletion(120000))
                        throw new TimeoutException("Timed out while tracing the grid");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to trace the grid: " + ex.Message);
                }
            }
        }
    }
}
