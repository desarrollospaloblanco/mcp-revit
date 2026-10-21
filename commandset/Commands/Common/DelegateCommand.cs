using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Common;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Common
{
    /// <summary>
    /// Base for commands that run one function on Revit's main thread. Calls are serialized per
    /// command, and a failure inside Revit comes back as an exception with its message.
    /// </summary>
    public abstract class DelegateCommand : ExternalEventCommandBase
    {
        private readonly object _executionLock = new object();

        protected DelegateCommand(UIApplication uiApp, string name)
            : base(new DelegateEventHandler(name), uiApp)
        {
        }

        private DelegateEventHandler DelegateHandler => (DelegateEventHandler)Handler;

        protected object RunInRevit(Func<UIApplication, object> work, int timeoutSeconds)
        {
            lock (_executionLock)
            {
                DelegateHandler.SetWork(work);
                if (!RaiseAndWaitForCompletion(timeoutSeconds * 1000))
                    throw new TimeoutException($"{CommandName} did not finish in {timeoutSeconds} s; it may still be running in Revit");

                Exception error = DelegateHandler.Error;
                if (error != null)
                {
                    Exception inner = error is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : error;
                    throw new Exception($"{CommandName} failed: {inner.Message}", inner);
                }
                return DelegateHandler.Result;
            }
        }
    }
}
