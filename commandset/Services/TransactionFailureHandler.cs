using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Keeps Revit from blocking an automated call on a modal failure dialog.
    ///
    /// Without a preprocessor, a transaction that raises a warning (for example a wall
    /// overlapping a room separation line) makes Revit show a modal dialog during Commit.
    /// That freezes the UI thread, which freezes the external event queue, which hangs
    /// every subsequent MCP command until somebody clicks the dialog by hand.
    ///
    /// Warnings are recorded and dismissed so the call completes; errors roll the
    /// transaction back so a broken edit is never silently committed. Either way the
    /// caller gets the text back instead of a dialog nobody is there to answer.
    /// </summary>
    public class TransactionFailureHandler : IFailuresPreprocessor
    {
        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _errors = new List<string>();

        /// <summary>Warnings Revit raised and this handler dismissed.</summary>
        public List<string> Warnings => _warnings;

        /// <summary>Errors that forced a rollback.</summary>
        public List<string> Errors => _errors;

        public bool HasErrors => _errors.Count > 0;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var messages = failuresAccessor.GetFailureMessages();
            bool rollBack = false;

            foreach (var message in messages)
            {
                string text = message.GetDescriptionText();

                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    if (!_warnings.Contains(text))
                        _warnings.Add(text);
                    failuresAccessor.DeleteWarning(message);
                }
                else
                {
                    if (!_errors.Contains(text))
                        _errors.Add(text);
                    rollBack = true;
                }
            }

            return rollBack
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }

        /// <summary>
        /// Wires this handler into a transaction and turns off forced modal handling.
        /// Call right after Transaction.Start().
        /// </summary>
        public void Attach(Transaction transaction)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(this);
            options.SetForcedModalHandling(false);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }

        /// <summary>
        /// Text to append to a result message so swallowed warnings stay visible.
        /// </summary>
        public string Summarize()
        {
            var parts = new List<string>();
            if (_errors.Count > 0)
                parts.Add("Revit errors (rolled back): " + string.Join(" / ", _errors));
            if (_warnings.Count > 0)
                parts.Add("Revit warnings: " + string.Join(" / ", _warnings));

            return parts.Count == 0 ? string.Empty : " | " + string.Join(" | ", parts);
        }
    }
}
