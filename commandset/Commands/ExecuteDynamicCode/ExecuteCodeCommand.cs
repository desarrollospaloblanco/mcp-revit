using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Execution;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的命令类
    /// </summary>
    public class ExecuteCodeCommand : ExternalEventCommandBase
    {
        private const int DefaultTimeoutSeconds = 60;
        private const int DefaultAskTimeoutSeconds = 600;
        private const int MaxTimeoutSeconds = 3600;

        private static readonly object _executionLock = new object();
        private ExecuteCodeEventHandler _handler => (ExecuteCodeEventHandler)Handler;

        public override string CommandName => "send_code_to_revit";

        public ExecuteCodeCommand(UIApplication uiApp)
            : base(new ExecuteCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (!parameters.ContainsKey("code"))
                {
                    throw new ArgumentException("Missing required parameter: 'code'");
                }

                string code = parameters["code"].Value<string>();
                JArray parametersArray = parameters["parameters"] as JArray;
                object[] executionParameters = parametersArray?.ToObject<object[]>() ?? Array.Empty<object>();
                string transactionMode = parameters["transactionMode"]?.Value<string>() ?? ExecuteCodeEventHandler.TransactionModeAuto;
                bool dryRun = parameters["dryRun"]?.Value<bool>() ?? false;
                bool group = parameters["group"]?.Value<bool>() ?? true;
                string title = parameters["title"]?.Value<string>();

                // Waiting for the user's approval can take minutes, so the ask policy gets a longer default.
                int defaultTimeout = ExecutionPolicy.Current == ExecutionPolicy.Ask ? DefaultAskTimeoutSeconds : DefaultTimeoutSeconds;
                int timeoutSeconds = parameters["timeoutSeconds"]?.Value<int>() ?? defaultTimeout;
                timeoutSeconds = Math.Max(5, Math.Min(MaxTimeoutSeconds, timeoutSeconds));

                lock (_executionLock)
                {
                    // A previous run whose caller gave up may still be executing in Revit.
                    if (!_handler.WaitUntilIdle(timeoutSeconds * 1000))
                        throw new TimeoutException(
                            $"Revit is still executing the previous run ({_handler.CurrentRunTitle}); nothing was sent. Check get_run_history and retry later.");

                    _handler.SetExecutionParameters(code, executionParameters, transactionMode, dryRun, group, title);

                    if (RaiseAndWaitForCompletion(timeoutSeconds * 1000))
                    {
                        return _handler.ResultInfo;
                    }
                }

                throw new TimeoutException(
                    $"代码执行超时 ({timeoutSeconds} s). The run may still be going on in Revit; check get_run_history before retrying.");
            }
            catch (Exception ex)
            {
                throw new Exception($"执行代码失败: {ex.Message}", ex);
            }
        }
    }
}
