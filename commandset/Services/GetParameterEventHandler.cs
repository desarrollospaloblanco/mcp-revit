using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Reads one parameter, or every parameter, from a single element.
    /// </summary>
    public class GetParameterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        /// <summary>Element to read.</summary>
        public long ElementId { get; private set; }

        /// <summary>Optional single parameter name. When null every parameter is returned.</summary>
        public string ParameterName { get; private set; }

        public AIResult<ElementParametersInfo> Result { get; private set; }

        public void SetParameters(long elementId, string parameterName)
        {
            ElementId = elementId;
            ParameterName = parameterName;
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
                    Result = new AIResult<ElementParametersInfo>
                    {
                        Success = false,
                        Message = "Element " + ElementId + " was not found in the active document"
                    };
                    return;
                }

                var info = new ElementParametersInfo
                {
                    ElementId = ElementId,
                    ElementName = element.Name,
                    Category = element.Category?.Name
                };

                if (!string.IsNullOrWhiteSpace(ParameterName))
                {
                    var param = ParameterUtils.FindParameter(element, ParameterName);
                    if (param == null)
                    {
                        Result = new AIResult<ElementParametersInfo>
                        {
                            Success = false,
                            Message = "Parameter '" + ParameterName + "' was not found on element " + ElementId,
                            Response = info
                        };
                        return;
                    }

                    info.Parameters.Add(ParameterUtils.Serialize(param));
                }
                else
                {
                    foreach (Parameter param in element.Parameters)
                    {
                        info.Parameters.Add(ParameterUtils.Serialize(param));
                    }
                }

                Result = new AIResult<ElementParametersInfo>
                {
                    Success = true,
                    Message = info.Parameters.Count + " parameter(s) read from element " + ElementId,
                    Response = info
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<ElementParametersInfo>
                {
                    Success = false,
                    Message = "Failed to read parameters: " + ex.Message
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
            return "Get element parameters";
        }
    }
}
