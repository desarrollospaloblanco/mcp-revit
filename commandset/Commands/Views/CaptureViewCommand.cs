using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Renders a temporary 3D view or an existing view to PNG and returns its path.
    /// </summary>
    public class CaptureViewCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private CaptureViewEventHandler _handler => (CaptureViewEventHandler)Handler;

        public override string CommandName => "capture_view";

        public CaptureViewCommand(UIApplication uiApp)
            : base(new CaptureViewEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                var ids = (parameters?["elementIds"] as JArray)?.Select(t => t.Value<long>()).ToList();
                int width = Math.Max(400, Math.Min(4000, parameters?["width"]?.Value<int>() ?? 1600));

                _handler.SetParameters(
                    parameters?["documentTitle"]?.Value<string>(),
                    parameters?["view"]?.Value<string>(),
                    ids,
                    parameters?["direction"]?.Value<string>(),
                    width,
                    parameters?["style"]?.Value<string>(),
                    parameters?["paddingMeters"]?.Value<double>() ?? 2.0);

                if (!RaiseAndWaitForCompletion(180000))
                    throw new TimeoutException("Timed out while capturing the view");
                if (_handler.Error != null)
                    throw new Exception("Capture failed: " + _handler.Error);
                return _handler.Result;
            }
        }
    }
}
