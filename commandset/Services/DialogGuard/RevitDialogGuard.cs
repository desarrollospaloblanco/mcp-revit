using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitMCPCommandSet.Services.DialogGuard
{
    public enum DialogGuardPolicy
    {
        /// <summary>Answer "No" to join/attach questions and "Cancel" to everything else.</summary>
        NoForJoinQuestions,
        Cancel,
        Ok
    }

    /// <summary>
    /// Answers Revit's modal dialogs while an agent drives Revit through the API.
    ///
    /// A modal dialog freezes Revit's UI thread, and with it the external event queue, so every
    /// MCP call after it hangs until someone clicks the dialog by hand. Code sent through
    /// send_code_to_revit used to subscribe its own handlers for this, but when that call timed
    /// out the handlers stayed subscribed with nothing left to remove them. The state lives here,
    /// in the loaded command set, so there is at most one subscription per event and it can always
    /// be switched off again.
    ///
    /// Subscribe and unsubscribe only from a valid API context (an external event handler).
    /// The log can be read from any thread.
    /// </summary>
    public static class RevitDialogGuard
    {
        // Win32 button ids. TaskDialogResult and Revit's own dialogs use the same values.
        private const int IdOk = 1;
        private const int IdCancel = 2;
        private const int IdYes = 6;
        private const int IdNo = 7;
        private const int IdClose = 8;

        private const int MaxLogEntries = 1000;

        private static readonly Regex JoinQuestion = new Regex(
            @"\b(join|joined|joining|unjoin|attach|attached|unir|unen|unidos|uni[oó]n|adjuntar|adjuntad[oa]s?|enlazar)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly object Sync = new object();
        private static readonly List<DialogLogEntry> Log = new List<DialogLogEntry>();

        private static UIApplication _subscribedUiApp;
        private static bool _dialogSubscribed;
        private static bool _failuresSubscribed;

        public static bool Enabled { get; private set; }
        public static DialogGuardPolicy Policy { get; private set; } = DialogGuardPolicy.NoForJoinQuestions;
        public static bool DeleteWarnings { get; private set; }
        public static DateTime? EnabledSince { get; private set; }

        /// <summary>
        /// Switches the guard on or off. Repeated calls never add a second subscription.
        /// Must run inside an external event handler.
        /// </summary>
        public static void Apply(UIApplication uiApp, bool enabled, DialogGuardPolicy policy, bool deleteWarnings)
        {
            if (uiApp == null)
                throw new ArgumentNullException(nameof(uiApp));

            if (!enabled)
            {
                Unsubscribe();
                Enabled = false;
                EnabledSince = null;
                return;
            }

            Policy = policy;
            DeleteWarnings = deleteWarnings;

            if (!_dialogSubscribed)
            {
                uiApp.DialogBoxShowing += OnDialogBoxShowing;
                _dialogSubscribed = true;
                _subscribedUiApp = uiApp;
            }

            if (deleteWarnings && !_failuresSubscribed)
            {
                uiApp.Application.FailuresProcessing += OnFailuresProcessing;
                _failuresSubscribed = true;
                _subscribedUiApp = uiApp;
            }
            else if (!deleteWarnings && _failuresSubscribed)
            {
                _subscribedUiApp.Application.FailuresProcessing -= OnFailuresProcessing;
                _failuresSubscribed = false;
            }

            if (!Enabled)
                EnabledSince = DateTime.Now;
            Enabled = true;
        }

        public static object GetStatus()
        {
            return new
            {
                enabled = Enabled,
                policy = PolicyName(Policy),
                deleteWarnings = DeleteWarnings,
                enabledSince = EnabledSince?.ToString("yyyy-MM-dd HH:mm:ss"),
                dialogBoxShowingSubscribed = _dialogSubscribed,
                failuresProcessingSubscribed = _failuresSubscribed
            };
        }

        /// <summary>Returns the most recent entries, oldest first, and optionally empties the log.</summary>
        public static (List<DialogLogEntry> entries, int total) ReadLog(int? limit, bool clear)
        {
            lock (Sync)
            {
                int total = Log.Count;
                int take = limit.HasValue && limit.Value > 0 ? Math.Min(limit.Value, total) : total;
                var entries = Log.Skip(total - take).ToList();
                if (clear)
                    Log.Clear();
                return (entries, total);
            }
        }

        public static DialogGuardPolicy ParsePolicy(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "no_for_join_questions":
                    return DialogGuardPolicy.NoForJoinQuestions;
                case "cancel":
                    return DialogGuardPolicy.Cancel;
                case "ok":
                    return DialogGuardPolicy.Ok;
                default:
                    throw new ArgumentException(
                        $"Unknown policy '{value}'. Use 'no_for_join_questions', 'cancel' or 'ok'.");
            }
        }

        public static string PolicyName(DialogGuardPolicy policy)
        {
            switch (policy)
            {
                case DialogGuardPolicy.Cancel: return "cancel";
                case DialogGuardPolicy.Ok: return "ok";
                default: return "no_for_join_questions";
            }
        }

        private static void Unsubscribe()
        {
            if (_subscribedUiApp == null)
                return;

            if (_dialogSubscribed)
            {
                _subscribedUiApp.DialogBoxShowing -= OnDialogBoxShowing;
                _dialogSubscribed = false;
            }

            if (_failuresSubscribed)
            {
                _subscribedUiApp.Application.FailuresProcessing -= OnFailuresProcessing;
                _failuresSubscribed = false;
            }

            _subscribedUiApp = null;
        }

        /// <summary>
        /// Set while the plugin itself asks the user something (the send_code_to_revit approval
        /// prompt), so the guard does not answer it on the user's behalf.
        /// </summary>
        public static bool Suspended { get; set; }

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            if (Suspended)
                return;

            // An exception thrown out of a Revit event handler can take Revit down, so failures
            // are recorded in the log instead of propagating.
            var entry = new DialogLogEntry { Source = "dialog", DialogId = e.DialogId };
            try
            {
                entry.Message = DialogMessage(e);

                int[] answers = ChooseAnswers(e.DialogId, entry.Message);
                foreach (int answer in answers)
                {
                    if (e.OverrideResult(answer))
                    {
                        entry.Response = ButtonName(answer);
                        return;
                    }
                }

                entry.Response = "none (Revit rejected " +
                                 string.Join(", ", answers.Select(ButtonName)) + ")";
            }
            catch (Exception ex)
            {
                entry.Response = "none (error: " + ex.Message + ")";
            }
            finally
            {
                Append(entry);
            }
        }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            try
            {
                FailuresAccessor accessor = e.GetFailuresAccessor();
                foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                {
                    // Errors are left for Revit to report: deleting them would let a broken edit
                    // commit silently.
                    if (message.GetSeverity() != FailureSeverity.Warning)
                        continue;

                    string text = message.GetDescriptionText();
                    accessor.DeleteWarning(message);
                    Append(new DialogLogEntry
                    {
                        Source = "failure",
                        DialogId = message.GetFailureDefinitionId()?.Guid.ToString(),
                        Message = text,
                        Response = "warning deleted"
                    });
                }
            }
            catch (Exception ex)
            {
                Append(new DialogLogEntry
                {
                    Source = "failure",
                    Message = "FailuresProcessing handler failed",
                    Response = "none (error: " + ex.Message + ")"
                });
            }
        }

        /// <summary>Buttons to try in order; the first one Revit accepts wins.</summary>
        private static int[] ChooseAnswers(string dialogId, string message)
        {
            switch (Policy)
            {
                case DialogGuardPolicy.Ok:
                    return new[] { IdOk, IdYes, IdClose, IdCancel };
                case DialogGuardPolicy.Cancel:
                    return new[] { IdCancel, IdClose, IdNo };
                default:
                    bool isJoinQuestion = JoinQuestion.IsMatch(message ?? "") ||
                                          (dialogId ?? "").IndexOf("join", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          (dialogId ?? "").IndexOf("attach", StringComparison.OrdinalIgnoreCase) >= 0;
                    return isJoinQuestion
                        ? new[] { IdNo, IdCancel, IdClose }
                        : new[] { IdCancel, IdClose, IdNo };
            }
        }

        private static string DialogMessage(DialogBoxShowingEventArgs e)
        {
            switch (e)
            {
                case TaskDialogShowingEventArgs taskDialog:
                    return taskDialog.Message;
                case MessageBoxShowingEventArgs messageBox:
                    return messageBox.Message;
                default:
                    return null;
            }
        }

        private static string ButtonName(int id)
        {
            switch (id)
            {
                case IdOk: return "OK";
                case IdCancel: return "Cancel";
                case IdYes: return "Yes";
                case IdNo: return "No";
                case IdClose: return "Close";
                default: return id.ToString();
            }
        }

        private static void Append(DialogLogEntry entry)
        {
            lock (Sync)
            {
                Log.Add(entry);
                if (Log.Count > MaxLogEntries)
                    Log.RemoveRange(0, Log.Count - MaxLogEntries);
            }
        }
    }

    public class DialogLogEntry
    {
        public string Time { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>"dialog" for DialogBoxShowing, "failure" for a warning deleted in FailuresProcessing.</summary>
        public string Source { get; set; }

        public string DialogId { get; set; }
        public string Message { get; set; }
        public string Response { get; set; }
    }
}
