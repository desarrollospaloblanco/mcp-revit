using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Applies a batch of parameter changes to a single element inside one transaction.
    /// </summary>
    public class ModifyElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        /// <summary>Element to modify.</summary>
        public long ElementId { get; private set; }

        /// <summary>Changes requested by the caller.</summary>
        public List<ParameterChangeInfo> Changes { get; private set; }

        /// <summary>Per-change outcome, plus the overall status.</summary>
        public AIResult<List<ParameterChangeResult>> Result { get; private set; }

        public void SetParameters(long elementId, List<ParameterChangeInfo> changes)
        {
            ElementId = elementId;
            Changes = changes;
            Result = null;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var element = doc.GetElement(ParameterUtils.ToElementId(ElementId));

                if (element == null)
                {
                    Result = new AIResult<List<ParameterChangeResult>>
                    {
                        Success = false,
                        Message = "Element " + ElementId + " was not found in the active document",
                        Response = new List<ParameterChangeResult>()
                    };
                    return;
                }

                var results = new List<ParameterChangeResult>();
                var failures = new TransactionFailureHandler();

                using (var transaction = new Transaction(doc, "MCP: modify_element"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    foreach (var change in Changes)
                    {
                        results.Add(ParameterUtils.ApplyChange(element, change));
                    }

                    // Roll back when nothing succeeded, so a fully failed call leaves no
                    // empty undo step behind.
                    bool anyApplied = results.Exists(r => r.Status == "ok");
                    if (anyApplied)
                        transaction.Commit();
                    else
                        transaction.RollBack();
                }

                // A Revit-level error rolls the whole transaction back, so no change stuck
                // even if the individual writes reported ok.
                if (failures.HasErrors)
                {
                    foreach (var result in results)
                    {
                        if (result.Status != "ok") continue;
                        result.Status = "error";
                        result.Message = "Rolled back by Revit";
                    }
                }

                int okCount = results.FindAll(r => r.Status == "ok").Count;
                Result = new AIResult<List<ParameterChangeResult>>
                {
                    Success = okCount > 0,
                    Message = okCount + " of " + results.Count + " parameter(s) updated on element "
                        + ElementId + failures.Summarize(),
                    Response = results
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<ParameterChangeResult>>
                {
                    Success = false,
                    Message = "Failed to modify element: " + ex.Message,
                    Response = new List<ParameterChangeResult>()
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "Modify element parameters";
        }
    }
}
