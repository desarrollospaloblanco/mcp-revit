using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// Exports one view to a PNG so the model can be checked against the drawings it was
    /// built from. Without it, a build can only be verified by counts and ids, which say
    /// nothing about a wall drawn on the wrong side of a grid.
    ///
    /// The view is found by name, by level (its floor plan) or as the default 3D view, never
    /// from the active view: the user keeps working while commands run.
    /// </summary>
    public class ExportViewImageEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const string Created3DViewName = "MCP 3D";

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private string _documentTitle;
        private string _viewName;
        private string _levelName;
        private bool _threeD;
        private string _outputPath;
        private int _pixelSize;

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(string documentTitle, string viewName, string levelName, bool threeD,
            string outputPath, int pixelSize)
        {
            _documentTitle = documentTitle;
            _viewName = viewName;
            _levelName = levelName;
            _threeD = threeD;
            _outputPath = outputPath;
            _pixelSize = pixelSize;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 120000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                Document doc = QuantificationUtils.ResolveDocument(app, _documentTitle, out string error);
                if (doc == null)
                {
                    Result = Fail(error);
                    return;
                }

                View view = FindView(doc, out error);
                if (view == null)
                {
                    Result = Fail(error);
                    return;
                }

                string output = string.IsNullOrWhiteSpace(_outputPath)
                    ? Path.Combine(Path.GetTempPath(), "revit-mcp", Sanitize(doc.Title + " - " + view.Name) + ".png")
                    : Path.GetFullPath(_outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(output));

                // Revit decorates the file name with the view type and name, so export into an
                // empty folder and pick up whatever lands there.
                string staging = Path.Combine(Path.GetTempPath(), "revit-mcp", "export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);

                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(staging, "view"),
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = _pixelSize,
                    ImageResolution = ImageResolution.DPI_150,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(options);

                string produced = Directory.GetFiles(staging).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (produced == null)
                {
                    Result = Fail("Revit reported no error but wrote no image for view '" + view.Name + "'");
                    return;
                }

                if (File.Exists(output))
                    File.Delete(output);
                File.Move(produced, output);
                Directory.Delete(staging, true);

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Exported '" + view.Name + "' from " + doc.Title,
                    Response = new Dictionary<string, object>
                    {
                        { "document", doc.Title },
                        { "view", view.Name },
                        { "viewType", view.ViewType.ToString() },
                        { "path", output }
                    }
                };
            }
            catch (Exception ex)
            {
                Result = Fail("Export failed: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Export view image";
        }

        private View FindView(Document doc, out string error)
        {
            error = null;
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted).ToList();

            if (!string.IsNullOrWhiteSpace(_viewName))
            {
                View named = views.FirstOrDefault(v => v.Name == _viewName);
                if (named == null)
                    error = "No printable view is named '" + _viewName + "'";
                return named;
            }

            if (!string.IsNullOrWhiteSpace(_levelName))
            {
                View plan = views.OfType<ViewPlan>().FirstOrDefault(v =>
                    v.ViewType == ViewType.FloorPlan && v.GenLevel != null && v.GenLevel.Name == _levelName);
                if (plan == null)
                    error = "Level '" + _levelName + "' has no floor plan";
                return plan;
            }

            if (_threeD)
            {
                View3D existing = views.OfType<View3D>().Where(v => !v.IsPerspective)
                    .OrderBy(v => v.Name == Created3DViewName ? 0 : v.Name == "{3D}" ? 1 : 2)
                    .FirstOrDefault();
                if (existing != null)
                    return existing;

                ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                if (type == null)
                {
                    error = "The document has no 3D view and no 3D view type to create one";
                    return null;
                }

                using (var transaction = new Transaction(doc, "Create 3D view for export"))
                {
                    var failures = new TransactionFailureHandler();
                    transaction.Start();
                    failures.Attach(transaction);
                    View3D created = View3D.CreateIsometric(doc, type.Id);
                    created.Name = Created3DViewName;
                    transaction.Commit();
                    return created;
                }
            }

            error = "Pass one of view, level or threeD";
            return null;
        }

        private static string Sanitize(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name;
        }

        private static AIResult<Dictionary<string, object>> Fail(string message)
        {
            return new AIResult<Dictionary<string, object>> { Success = false, Message = message };
        }
    }
}
