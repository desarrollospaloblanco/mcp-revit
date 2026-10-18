using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Services.Execution
{
    /// <summary>
    /// How send_code_to_revit treats model changes, stored in %APPDATA%\RevitMCP\settings.json so it
    /// survives restarts and applies to every Revit session on the machine.
    /// auto: changes are applied as one undo step. ask: Revit shows the changes and waits for the
    /// user to apply or discard them. readonly: every run is a dry run and nothing is kept.
    /// </summary>
    public static class ExecutionPolicy
    {
        public const string Auto = "auto";
        public const string Ask = "ask";
        public const string ReadOnly = "readonly";

        private static readonly object _lock = new object();

        public static bool IsValid(string policy) =>
            policy == Auto || policy == Ask || policy == ReadOnly;

        public static string Current
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        if (!File.Exists(McpPaths.SettingsFile))
                            return Auto;
                        string value = JObject.Parse(File.ReadAllText(McpPaths.SettingsFile))["executionPolicy"]?.Value<string>();
                        return IsValid(value) ? value : Auto;
                    }
                    catch (Exception)
                    {
                        // A damaged settings file must not block execution: fall back to the default.
                        return Auto;
                    }
                }
            }
        }

        public static void Set(string policy)
        {
            if (!IsValid(policy))
                throw new ArgumentException("Policy must be 'auto', 'ask' or 'readonly'");

            lock (_lock)
            {
                JObject settings;
                try
                {
                    settings = File.Exists(McpPaths.SettingsFile)
                        ? JObject.Parse(File.ReadAllText(McpPaths.SettingsFile))
                        : new JObject();
                }
                catch (Exception)
                {
                    settings = new JObject();
                }

                settings["executionPolicy"] = policy;
                settings["executionPolicyUpdated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(McpPaths.SettingsFile, settings.ToString());
            }
        }
    }
}
