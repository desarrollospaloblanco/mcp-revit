using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Schedules;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Schedules
{
    public class DuplicateSchedulesWithParameterCommand : ExternalEventCommandBase
    {
        private DuplicateSchedulesWithParameterEventHandler _handler => (DuplicateSchedulesWithParameterEventHandler)Handler;

        public override string CommandName => "duplicate_schedules_with_parameter";

        public DuplicateSchedulesWithParameterCommand(UIApplication uiApp)
            : base(new DuplicateSchedulesWithParameterEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (parameters["parameterName"] == null)
                    throw new ArgumentException("Missing required parameter: 'parameterName'");

                if (parameters["newValue"] == null)
                    throw new ArgumentException("Missing required parameter: 'newValue'");

                List<long> scheduleIds = parameters["scheduleIds"] is JArray idArray
                    ? idArray.ToObject<List<long>>()
                    : new List<long>();

                _handler.SetParameters(
                    scheduleIds,
                    parameters["parameterName"].ToString(),
                    parameters["newValue"].ToString(),
                    parameters["namePrefix"]?.ToString(),
                    parameters["prefixToReplace"]?.ToString(),
                    parameters["fieldType"]?.ToString(),
                    parameters["updateFilter"]?.ToObject<bool>() ?? true,
                    parameters["addFilterIfMissing"]?.ToObject<bool>() ?? false,
                    parameters["setViewParameter"]?.ToObject<bool>() ?? true);

                if (RaiseAndWaitForCompletion(60000))
                    return _handler.Results;

                throw new TimeoutException("Duplicating schedules timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to duplicate schedules: {ex.Message}");
            }
        }
    }
}
