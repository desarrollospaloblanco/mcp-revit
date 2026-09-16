using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Schedules;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Schedules
{
    public class SetScheduleParameterFilterCommand : ExternalEventCommandBase
    {
        private SetScheduleParameterFilterEventHandler _handler => (SetScheduleParameterFilterEventHandler)Handler;

        public override string CommandName => "set_schedule_parameter_filter";

        public SetScheduleParameterFilterCommand(UIApplication uiApp)
            : base(new SetScheduleParameterFilterEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (parameters["parameterName"] == null)
                    throw new ArgumentException("Missing required parameter: 'parameterName'");

                if (parameters["value"] == null)
                    throw new ArgumentException("Missing required parameter: 'value'");

                List<long> scheduleIds = parameters["scheduleIds"] is JArray idArray
                    ? idArray.ToObject<List<long>>()
                    : new List<long>();

                _handler.SetParameters(
                    scheduleIds,
                    parameters["parameterName"].ToString(),
                    parameters["value"].ToString(),
                    parameters["filterType"]?.ToString(),
                    parameters["fieldType"]?.ToString(),
                    parameters["replaceExistingFilters"]?.ToObject<bool>() ?? false,
                    parameters["hideField"]?.ToObject<bool>() ?? true);

                if (RaiseAndWaitForCompletion(30000))
                    return _handler.Results;

                throw new TimeoutException("Filtering schedules timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to filter schedules: {ex.Message}");
            }
        }
    }
}
