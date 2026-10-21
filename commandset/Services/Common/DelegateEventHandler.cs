using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Common
{
    /// <summary>
    /// External event handler that runs whatever function the command hands it on Revit's main
    /// thread, so commands whose work fits in one method do not need their own handler class.
    /// </summary>
    public class DelegateEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly string _name;
        private readonly ManualResetEvent _done = new ManualResetEvent(false);
        private Func<UIApplication, object> _work;

        public DelegateEventHandler(string name)
        {
            _name = name;
        }

        public object Result { get; private set; }
        public Exception Error { get; private set; }

        public void SetWork(Func<UIApplication, object> work)
        {
            _work = work;
            Result = null;
            Error = null;
            _done.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds) => _done.WaitOne(timeoutMilliseconds);

        public void Execute(UIApplication app)
        {
            try
            {
                Result = _work(app);
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                _done.Set();
            }
        }

        public string GetName() => _name;
    }
}
