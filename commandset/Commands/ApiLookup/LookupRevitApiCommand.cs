using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.ApiLookup;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ApiLookup
{
    /// <summary>
    /// Returns signatures of a Revit API type or member from the running Revit. Runs on the socket
    /// thread because it only uses reflection, so it answers even while Revit is busy.
    /// </summary>
    public class LookupRevitApiCommand : IRevitCommand
    {
        public string CommandName => "lookup_revit_api";

        public object Execute(JObject parameters, string requestId)
        {
            string name = parameters?["name"]?.Value<string>();
            string member = parameters?["member"]?.Value<string>();
            int maxMembers = parameters?["maxMembers"]?.Value<int>() ?? 80;
            return RevitApiReflector.Lookup(name, member, Math.Max(5, Math.Min(400, maxMembers)));
        }
    }
}
