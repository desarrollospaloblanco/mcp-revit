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
    /// Writes the quantification level onto slabs and beams.
    /// </summary>
    public class AssignBimLevelCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private AssignBimLevelEventHandler _handler => (AssignBimLevelEventHandler)Handler;

        public override string CommandName => "assign_bim_level";

        public AssignBimLevelCommand(UIApplication uiApp)
            : base(new AssignBimLevelEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string parameterName = parameters?["parameterName"]?.Value<string>();
                    string foundationText = parameters?["foundationText"]?.Value<string>();
                    string foundationCodePrefix = parameters?["foundationCodePrefix"] == null
                        ? "A"
                        : parameters["foundationCodePrefix"].Value<string>();
                    bool numberPrefix = parameters?["numberPrefix"]?.Value<bool>() ?? false;

                    var categories = CommandParsing.ParseCategories(
                        parameters?["categories"] as JArray,
                        new List<BuiltInCategory>
                        {
                            BuiltInCategory.OST_Floors,
                            BuiltInCategory.OST_StructuralFraming
                        });

                    var mergeLevels = CommandParsing.ParseStrings(parameters?["mergeLevels"] as JArray);

                    _handler.SetParameters(
                        documentTitle,
                        parameterName,
                        categories,
                        foundationText,
                        foundationCodePrefix,
                        mergeLevels,
                        numberPrefix);

                    if (!RaiseAndWaitForCompletion(120000))
                        throw new TimeoutException("Timed out while assigning the quantification level");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to assign the quantification level: " + ex.Message);
                }
            }
        }
    }
}
