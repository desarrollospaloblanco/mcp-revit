using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Writes a single parameter on one or more elements inside one transaction.
    /// </summary>
    public class SetParameterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        /// <summary>Elements to write to.</summary>
        public List<long> ElementIds { get; private set; }

        /// <summary>The change applied to every element.</summary>
        public ParameterChangeInfo Change { get; private set; }

        /// <summary>One result per element, keyed by element id in the message.</summary>
        public AIResult<List<ParameterChangeResult>> Result { get; private set; }

        public void SetParameters(List<long> elementIds, ParameterChangeInfo change)
        {
            ElementIds = elementIds;
            Change = change;
            Result = null;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var results = new List<ParameterChangeResult>();

                using (var transaction = new Transaction(doc, "MCP: set_parameter"))
                {
                    transaction.Start();

                    foreach (var elementId in ElementIds)
                    {
                        var element = doc.GetElement(ParameterUtils.ToElementId(elementId));
                        if (element == null)
                        {
                            results.Add(new ParameterChangeResult
                            {
                                ParameterName = Change.ParameterName,
                                Status = "error",
                                Message = "Element " + elementId + " was not found in the active document"
                            });
                            continue;
                        }

                        var result = ParameterUtils.ApplyChange(element, Change);
                        // Keep the element id visible: one call can touch many elements.
                        result.Message = result.Status == "ok"
                            ? "Element " + elementId
                            : "Element " + elementId + ": " + result.Message;
                        results.Add(result);
                    }

                    bool anyApplied = results.Exists(r => r.Status == "ok");
                    if (anyApplied)
                        transaction.Commit();
                    else
                        transaction.RollBack();
                }

                int okCount = results.FindAll(r => r.Status == "ok").Count;
                Result = new AIResult<List<ParameterChangeResult>>
                {
                    Success = okCount > 0,
                    Message = "'" + Change.ParameterName + "' updated on " + okCount + " of " + results.Count + " element(s)",
                    Response = results
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<ParameterChangeResult>>
                {
                    Success = false,
                    Message = "Failed to set parameter: " + ex.Message,
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
            return "Set element parameter";
        }
    }
}
