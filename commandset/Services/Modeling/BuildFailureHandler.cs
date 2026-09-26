using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// Failure handling for a batch build.
    ///
    /// TransactionFailureHandler rolls the whole transaction back on any error, which is right
    /// for a single edit but wrong for a batch: one wall Revit cannot build would throw away
    /// every other wall of the stage. Here an error that Revit can resolve by deleting the
    /// offending elements is resolved that way, and the deleted ids are recorded so the
    /// builder can report them against their spec keys. Only an error with no such resolution
    /// rolls the stage back. Warnings are recorded and dismissed, never shown as a dialog.
    /// </summary>
    public class BuildFailureHandler : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();

        /// <summary>Element id → the error that made Revit delete it.</summary>
        public Dictionary<ElementId, string> Deleted { get; } = new Dictionary<ElementId, string>();

        public bool RolledBack { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool resolved = false;
            bool rollBack = false;

            foreach (FailureMessageAccessor message in failuresAccessor.GetFailureMessages())
            {
                string text = message.GetDescriptionText();

                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    if (!Warnings.Contains(text))
                        Warnings.Add(text);
                    failuresAccessor.DeleteWarning(message);
                    continue;
                }

                if (message.HasResolutionOfType(FailureResolutionType.DeleteElements))
                {
                    foreach (ElementId id in message.GetFailingElementIds())
                        Deleted[id] = text;
                    message.SetCurrentResolutionType(FailureResolutionType.DeleteElements);
                    failuresAccessor.ResolveFailure(message);
                    resolved = true;
                    continue;
                }

                if (!Errors.Contains(text))
                    Errors.Add(text);
                rollBack = true;
            }

            if (rollBack)
            {
                RolledBack = true;
                return FailureProcessingResult.ProceedWithRollBack;
            }

            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }

        /// <summary>Wires this handler into a transaction. Call right after Start().</summary>
        public void Attach(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(this);
            options.SetForcedModalHandling(false);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }
    }
}
