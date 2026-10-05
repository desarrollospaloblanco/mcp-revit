using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Modeling;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Exports a floor plan, a named view or the 3D view to PNG for visual checking.
    /// </summary>
    public class ExportViewImageCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private ExportViewImageEventHandler _handler => (ExportViewImageEventHandler)Handler;

        public override string CommandName => "export_view_image";

        public ExportViewImageCommand(UIApplication uiApp)
            : base(new ExportViewImageEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string view = parameters?["view"]?.Value<string>();
                    string level = parameters?["level"]?.Value<string>();
                    bool threeD = parameters?["threeD"]?.Value<bool>() ?? false;
                    string outputPath = parameters?["outputPath"]?.Value<string>();
                    int pixelSize = parameters?["pixelSize"]?.Value<int>() ?? 2400;
                    if (pixelSize < 256 || pixelSize > 15000)
                        throw new ArgumentException("pixelSize must be between 256 and 15000");

                    _handler.SetParameters(documentTitle, view, level, threeD, outputPath, pixelSize);

                    if (!RaiseAndWaitForCompletion(120000))
                        throw new TimeoutException("Timed out while exporting the view image");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to export view image: " + ex.Message);
                }
            }
        }
    }
}
