using System.IO;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Services.Execution
{
    /// <summary>
    /// Keeps a record of every send_code_to_revit run (script, result, changes, outcome) as one JSON
    /// file per run under %APPDATA%\RevitMCP\runs\yyyyMMdd. Records older than the retention period
    /// are deleted the first time a run is saved in a Revit session.
    /// </summary>
    public static class RunHistory
    {
        public const int RetentionDays = 14;
        private static bool _purged;
        private static readonly object _lock = new object();

        public static string NewRunId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

        public static void Save(RunRecord record)
        {
            try
            {
                lock (_lock)
                {
                    if (!_purged)
                    {
                        Purge();
                        _purged = true;
                    }

                    string day = Path.Combine(McpPaths.Runs, record.Id.Substring(0, 8));
                    Directory.CreateDirectory(day);
                    File.WriteAllText(Path.Combine(day, record.Id + ".json"),
                        JsonConvert.SerializeObject(record, Formatting.Indented));
                }
            }
            catch (Exception)
            {
                // History is an aid: a full disk or a locked file must never fail the run itself.
            }
        }

        private static void Purge()
        {
            DateTime limit = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (string dir in Directory.GetDirectories(McpPaths.Runs))
            {
                if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out DateTime day) && day < limit)
                {
                    Directory.Delete(dir, true);
                }
            }
        }
    }

    public class RunRecord
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("time")] public string Time { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("activeDocument")] public string ActiveDocument { get; set; }
        [JsonProperty("policy")] public string Policy { get; set; }
        [JsonProperty("dryRun")] public bool DryRun { get; set; }
        [JsonProperty("grouped")] public bool Grouped { get; set; }
        [JsonProperty("transactionMode")] public string TransactionMode { get; set; }
        [JsonProperty("outcome")] public string Outcome { get; set; }
        [JsonProperty("durationMs")] public long DurationMs { get; set; }
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("result")] public string Result { get; set; }
        [JsonProperty("errorMessage")] public string ErrorMessage { get; set; }
        [JsonProperty("changes")] public List<DocumentChangeSummary> Changes { get; set; }
        [JsonProperty("parameters")] public object[] Parameters { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
    }
}
