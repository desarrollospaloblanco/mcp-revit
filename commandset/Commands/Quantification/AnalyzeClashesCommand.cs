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
    /// Finds unjoined overlapping structure and colours it.
    /// </summary>
    public class AnalyzeClashesCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private AnalyzeClashesEventHandler _handler => (AnalyzeClashesEventHandler)Handler;

        public override string CommandName => "analyze_clashes";

        public AnalyzeClashesCommand(UIApplication uiApp)
            : base(new AnalyzeClashesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string viewName = parameters?["viewName"]?.Value<string>();
                    bool clearExisting = parameters?["clearExisting"]?.Value<bool>() ?? true;
                    bool computeVolumes = parameters?["computeVolumes"]?.Value<bool>() ?? true;

                    var categories = CommandParsing.ParseCategories(
                        parameters?["categories"] as JArray,
                        new List<BuiltInCategory>
                        {
                            BuiltInCategory.OST_StructuralColumns,
                            BuiltInCategory.OST_StructuralFraming,
                            BuiltInCategory.OST_Floors,
                            BuiltInCategory.OST_StructuralFoundation
                        });

                    _handler.SetParameters(documentTitle, viewName, categories, clearExisting, computeVolumes);

                    if (!RaiseAndWaitForCompletion(300000))
                        throw new TimeoutException("Timed out while analysing clashes");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to analyse clashes: " + ex.Message);
                }
            }
        }
    }
}
