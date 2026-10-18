using System.IO;

namespace RevitMCPCommandSet.Services.Execution
{
    /// <summary>
    /// Folders shared by every Revit session and by the MCP server, under %APPDATA%\RevitMCP.
    /// The server reads run records and captures from here directly, so it can answer even while
    /// Revit is busy.
    /// </summary>
    public static class McpPaths
    {
        public static string Root => Ensure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP"));

        public static string Runs => Ensure(Path.Combine(Root, "runs"));

        public static string Captures => Ensure(Path.Combine(Root, "captures"));

        public static string SettingsFile => Path.Combine(Root, "settings.json");

        private static string Ensure(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
