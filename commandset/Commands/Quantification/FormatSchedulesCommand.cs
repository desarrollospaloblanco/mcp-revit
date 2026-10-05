using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Quantification
{
    /// <summary>
    /// Renames schedule column headings and applies a consistent look.
    /// </summary>
    public class FormatSchedulesCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private FormatSchedulesEventHandler _handler => (FormatSchedulesEventHandler)Handler;

        public override string CommandName => "format_schedules";

        public FormatSchedulesCommand(UIApplication uiApp)
            : base(new FormatSchedulesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    string fontName = parameters?["fontName"]?.Value<string>();
                    bool highlightFirstColumn = parameters?["highlightFirstColumn"]?.Value<bool>() ?? true;
                    bool alignNumbersRight = parameters?["alignNumbersRight"]?.Value<bool>() ?? true;

                    var scheduleNames = CommandParsing.ParseStrings(parameters?["scheduleNames"] as JArray);

                    var headings = new Dictionary<string, string>();
                    var raw = parameters?["headings"] as JObject;
                    if (raw != null)
                    {
                        foreach (var property in raw)
                        {
                            string value = property.Value?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(value))
                                headings[property.Key] = value;
                        }
                    }

                    _handler.SetParameters(
                        documentTitle,
                        headings,
                        scheduleNames,
                        fontName,
                        ParseColor(parameters?["headerColor"] as JArray, new int[] { 47, 84, 150 }),
                        ParseColor(parameters?["titleColor"] as JArray, new int[] { 31, 56, 100 }),
                        highlightFirstColumn,
                        alignNumbersRight);

                    if (!RaiseAndWaitForCompletion(120000))
                        throw new TimeoutException("Timed out while formatting schedules");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to format schedules: " + ex.Message);
                }
            }
        }

        private static int[] ParseColor(JArray raw, int[] fallback)
        {
            if (raw == null || raw.Count < 3)
                return fallback;

            var color = new int[3];
            for (int i = 0; i < 3; i++)
            {
                int value = raw[i].Value<int>();
                color[i] = value < 0 ? 0 : (value > 255 ? 255 : value);
            }
            return color;
        }
    }
}
