using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Execution;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// Reads or changes the execution policy of send_code_to_revit. Runs on the socket thread: it
    /// only touches the settings file, so it answers even while Revit is busy.
    /// </summary>
    public class SetExecutionPolicyCommand : IRevitCommand
    {
        public string CommandName => "set_execution_policy";

        public object Execute(JObject parameters, string requestId)
        {
            string requested = parameters?["policy"]?.Value<string>();
            string previous = ExecutionPolicy.Current;

            if (!string.IsNullOrWhiteSpace(requested))
                ExecutionPolicy.Set(requested.Trim().ToLowerInvariant());

            return new
            {
                previous,
                policy = ExecutionPolicy.Current,
                settingsFile = McpPaths.SettingsFile,
                meaning = new
                {
                    auto = "changes are applied as one undo step",
                    ask = "Revit isolates the changes and waits for Apply or Discard",
                    @readonly = "every run is a dry run; nothing is kept"
                }
            };
        }
    }
}
