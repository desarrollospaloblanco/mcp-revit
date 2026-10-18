using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Publishes this Revit session (process id, port, version, open documents) as a JSON file in
    /// %APPDATA%\RevitMCP\instances, so the MCP server can list running sessions and choose which one
    /// to talk to. The file is rewritten when documents open or close and removed when the service stops.
    /// </summary>
    public static class InstanceRegistry
    {
        private static UIApplication _uiApp;
        private static int _port;
        private static string _version;
        private static DateTime _startedAt;

        private static string Folder
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "instances");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string OwnFile => Path.Combine(Folder, Process.GetCurrentProcess().Id + ".json");

        public static void Register(UIApplication uiApp, int port, string version)
        {
            _uiApp = uiApp;
            _port = port;
            _version = version;
            _startedAt = DateTime.Now;

            uiApp.Application.DocumentOpened += OnDocumentsChanged;
            uiApp.Application.DocumentCreated += OnDocumentsChanged;
            uiApp.Application.DocumentClosed += OnDocumentsChanged;
            Write();
        }

        public static void Unregister(UIApplication uiApp)
        {
            try
            {
                uiApp.Application.DocumentOpened -= OnDocumentsChanged;
                uiApp.Application.DocumentCreated -= OnDocumentsChanged;
                uiApp.Application.DocumentClosed -= OnDocumentsChanged;
                if (File.Exists(OwnFile)) File.Delete(OwnFile);
            }
            catch (Exception)
            {
                // A stale file is ignored by the server once the process is gone.
            }
        }

        private static void OnDocumentsChanged(object sender, EventArgs e) => Write();

        private static void Write()
        {
            try
            {
                var documents = _uiApp.Application.Documents.Cast<Autodesk.Revit.DB.Document>()
                    .Where(d => !d.IsLinked)
                    .Select(d => d.Title)
                    .ToList();

                var info = new
                {
                    pid = Process.GetCurrentProcess().Id,
                    port = _port,
                    revitVersion = _version,
                    startedAt = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    documents
                };
                File.WriteAllText(OwnFile, JsonConvert.SerializeObject(info, Formatting.Indented));
            }
            catch (Exception)
            {
                // Must never break Revit: an outdated document list only affects how sessions are labelled.
            }
        }
    }
}
