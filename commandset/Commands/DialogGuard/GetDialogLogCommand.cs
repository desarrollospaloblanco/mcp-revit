using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DialogGuard;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.DialogGuard
{
    /// <summary>
    /// Reads the dialog guard log directly on the socket thread instead of through an external
    /// event, so it still answers while Revit is busy or blocked by a dialog the guard could not
    /// close. It touches no Revit API object.
    /// </summary>
    public class GetDialogLogCommand : IRevitCommand
    {
        public string CommandName => "get_dialog_log";

        public object Execute(JObject parameters, string requestId)
        {
            bool clear = parameters?["clear"]?.ToObject<bool>() ?? false;
            int? limit = parameters?["limit"]?.ToObject<int?>();

            var (entries, total) = RevitDialogGuard.ReadLog(limit, clear);

            return new
            {
                status = RevitDialogGuard.GetStatus(),
                totalEntries = total,
                returnedEntries = entries.Count,
                cleared = clear,
                entries = entries.Select(e => new
                {
                    time = e.Time,
                    source = e.Source,
                    dialogId = e.DialogId,
                    message = e.Message,
                    response = e.Response
                })
            };
        }
    }
}
