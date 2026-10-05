using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Quantification
{
    /// <summary>
    /// Replicates ramps as sloped floors of a given type.
    /// </summary>
    public class CopyRampsAsFloorsCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private CopyRampsAsFloorsEventHandler _handler => (CopyRampsAsFloorsEventHandler)Handler;

        public override string CommandName => "copy_ramps_as_floors";

        public CopyRampsAsFloorsCommand(UIApplication uiApp)
            : base(new CopyRampsAsFloorsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string floorTypeName = parameters?["floorTypeName"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(floorTypeName))
                        throw new ArgumentException("floorTypeName is required");

                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string comment = parameters?["comment"] == null ? "ramp" : parameters["comment"].Value<string>();
                    bool structural = parameters?["structural"]?.Value<bool>() ?? true;

                    _handler.SetParameters(documentTitle, floorTypeName, comment, structural);

                    if (!RaiseAndWaitForCompletion(600000))
                        throw new TimeoutException("Timed out while copying ramps as floors");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to copy ramps as floors: " + ex.Message);
                }
            }
        }
    }
}
