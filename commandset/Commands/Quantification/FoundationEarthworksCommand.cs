using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Quantification
{
    /// <summary>
    /// Builds excavation and backfill masses for foundations.
    /// </summary>
    public class FoundationEarthworksCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private FoundationEarthworksEventHandler _handler => (FoundationEarthworksEventHandler)Handler;

        public override string CommandName => "create_foundation_earthworks";

        public FoundationEarthworksCommand(UIApplication uiApp)
            : base(new FoundationEarthworksEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string framingCodePrefix = parameters?["framingCodePrefix"] == null
                        ? "A"
                        : parameters["framingCodePrefix"].Value<string>();
                    string excavationCode = parameters?["excavationCode"]?.Value<string>();
                    string fillCode = parameters?["fillCode"]?.Value<string>();
                    string fillParameterName = parameters?["fillParameterName"]?.Value<string>();
                    string originParameterName = parameters?["originParameterName"]?.Value<string>();
                    bool cutColumns = parameters?["cutColumns"]?.Value<bool>() ?? true;
                    bool replaceExisting = parameters?["replaceExisting"]?.Value<bool>() ?? true;

                    var categories = CommandParsing.ParseCategories(
                        parameters?["categories"] as JArray,
                        new List<BuiltInCategory>
                        {
                            BuiltInCategory.OST_StructuralFoundation,
                            BuiltInCategory.OST_StructuralFraming
                        });

                    _handler.SetParameters(
                        documentTitle,
                        categories,
                        framingCodePrefix,
                        excavationCode,
                        fillCode,
                        fillParameterName,
                        originParameterName,
                        cutColumns,
                        replaceExisting);

                    if (!RaiseAndWaitForCompletion(600000))
                        throw new TimeoutException("Timed out while building the earthworks masses");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to build the earthworks masses: " + ex.Message);
                }
            }
        }
    }
}
