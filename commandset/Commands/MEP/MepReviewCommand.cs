using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.MEP;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.MEP
{
    /// <summary>
    /// Read-only MEP review of the model (see MepReviewEventHandler).
    /// </summary>
    public class MepReviewCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private MepReviewEventHandler _handler => (MepReviewEventHandler)Handler;

        public override string CommandName => "mep_review";

        public MepReviewCommand(UIApplication uiApp)
            : base(new MepReviewEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                var checks = (parameters?["checks"] as JArray)?.Select(t => t.Value<string>());
                double minSlope = parameters?["minSlopePercent"]?.Value<double>() ?? 1.0;
                int maxIds = Math.Max(10, Math.Min(1000, parameters?["maxIds"]?.Value<int>() ?? 200));

                _handler.SetParameters(parameters?["documentTitle"]?.Value<string>(), checks, minSlope, maxIds);

                if (!RaiseAndWaitForCompletion(600000))
                    throw new TimeoutException("Timed out while reviewing the MEP model");
                if (_handler.Error != null)
                    throw new Exception("MEP review failed: " + _handler.Error);
                return _handler.Result;
            }
        }
    }
}
