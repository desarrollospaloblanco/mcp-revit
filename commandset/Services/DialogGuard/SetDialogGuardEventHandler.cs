using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DialogGuard
{
    /// <summary>
    /// Switches the dialog guard on or off. Runs as an external event because Revit only accepts
    /// event subscriptions from a valid API context.
    /// </summary>
    public class SetDialogGuardEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private bool _enabled;
        private DialogGuardPolicy _policy;
        private bool _deleteWarnings;

        public object Results { get; private set; }

        public void SetParameters(bool enabled, DialogGuardPolicy policy, bool deleteWarnings)
        {
            _enabled = enabled;
            _policy = policy;
            _deleteWarnings = deleteWarnings;
            Results = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication uiApp)
        {
            try
            {
                RevitDialogGuard.Apply(uiApp, _enabled, _policy, _deleteWarnings);
                Results = new { success = true, status = RevitDialogGuard.GetStatus() };
            }
            catch (Exception ex)
            {
                Results = new { success = false, error = ex.Message, status = RevitDialogGuard.GetStatus() };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Set Dialog Guard";
        }
    }
}
