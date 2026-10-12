using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Parameters;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Parameters
{
    public class NormalizeParameterTextCommand : ExternalEventCommandBase
    {
        private NormalizeParameterTextEventHandler _handler => (NormalizeParameterTextEventHandler)Handler;

        public override string CommandName => "normalize_parameter_text";

        public NormalizeParameterTextCommand(UIApplication uiApp)
            : base(new NormalizeParameterTextEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (parameters["parameterName"] == null)
                    throw new ArgumentException("Missing required parameter: 'parameterName'");

                List<string> targets = parameters["targets"] is JArray targetArray
                    ? targetArray.ToObject<List<string>>()
                    : new List<string>();

                List<string> categories = parameters["categories"] is JArray categoryArray
                    ? categoryArray.ToObject<List<string>>()
                    : new List<string>();

                _handler.SetParameters(
                    parameters["parameterName"].ToString(),
                    parameters["textCase"]?.ToString(),
                    targets,
                    categories,
                    parameters["removeAccents"]?.ToObject<bool>() ?? false,
                    parameters["trim"]?.ToObject<bool>() ?? false,
                    parameters["onlyInUse"]?.ToObject<bool>() ?? false,
                    parameters["dryRun"]?.ToObject<bool>() ?? false);

                // Sweeping every type and instance in a large model takes well over a minute.
                if (RaiseAndWaitForCompletion(300000))
                    return _handler.Results;

                throw new TimeoutException("Normalizing the parameter timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to normalize parameter text: {ex.Message}");
            }
        }
    }
}
