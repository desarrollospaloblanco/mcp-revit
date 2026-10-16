using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DialogGuard;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DialogGuard
{
    public class SetDialogGuardCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private SetDialogGuardEventHandler _handler => (SetDialogGuardEventHandler)Handler;

        public override string CommandName => "set_dialog_guard";

        public SetDialogGuardCommand(UIApplication uiApp)
            : base(new SetDialogGuardEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                if (parameters?["enabled"] == null)
                    throw new ArgumentException("Missing required parameter: 'enabled'");

                bool enabled = parameters["enabled"].ToObject<bool>();
                DialogGuardPolicy policy = RevitDialogGuard.ParsePolicy(parameters["policy"]?.ToString());
                bool deleteWarnings = parameters["deleteWarnings"]?.ToObject<bool>() ?? false;

                _handler.SetParameters(enabled, policy, deleteWarnings);

                if (RaiseAndWaitForCompletion(15000))
                    return _handler.Results;

                // The event stays queued and still applies once Revit is idle again.
                throw new TimeoutException(
                    "Revit did not run the request within 15 s, probably because a modal dialog or a long " +
                    "operation is blocking it. The change will apply as soon as Revit is idle; check with " +
                    "get_dialog_log.");
            }
        }
    }
}
