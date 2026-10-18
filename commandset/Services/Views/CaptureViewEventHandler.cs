using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Execution;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    /// Renders a view to PNG under %APPDATA%\RevitMCP\captures so the agent can look at the model.
    /// view = "3d" builds a temporary isometric view (optionally framed by a section box around some
    /// elements) inside a transaction group that is rolled back after the export, so nothing is left
    /// in the model. Any other value exports an existing view by name or id.
    /// </summary>
    public class CaptureViewEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const int RetentionDays = 14;
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private string _documentTitle;
        private string _view;
        private List<long> _elementIds;
        private string _direction;
        private int _width;
        private string _style;
        private double _paddingMeters;

        public object Result { get; private set; }
        public string Error { get; private set; }

        public void SetParameters(string documentTitle, string view, List<long> elementIds, string direction,
            int width, string style, double paddingMeters)
        {
            _documentTitle = documentTitle;
            _view = string.IsNullOrWhiteSpace(view) ? "3d" : view.Trim();
            _elementIds = elementIds ?? new List<long>();
            _direction = (direction ?? "southwest").Trim().ToLowerInvariant();
            _width = width;
            _style = (style ?? "shaded").Trim().ToLowerInvariant();
            _paddingMeters = paddingMeters;
            Result = null;
            Error = null;
            _resetEvent.Reset();
        }

        public string GetName() => "MCP: captura de vista";

        public bool WaitForCompletion(int timeoutMilliseconds = 120000) => _resetEvent.WaitOne(timeoutMilliseconds);

        public void Execute(UIApplication app)
        {
            try
            {
                Document doc = QuantificationUtils.ResolveDocument(app, _documentTitle, out string error);
                if (doc == null)
                    throw new InvalidOperationException(error);

                PurgeOldCaptures();
                string prefix = "cap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string viewName;

                if (string.Equals(_view, "3d", StringComparison.OrdinalIgnoreCase))
                    viewName = CaptureTemporary3D(doc, prefix);
                else
                {
                    View view = FindView(doc, _view)
                                ?? throw new InvalidOperationException("No view named or numbered '" + _view + "' in " + doc.Title);
                    Export(doc, view, prefix);
                    viewName = view.Name;
                }

                string file = Directory.GetFiles(McpPaths.Captures, prefix + "*")
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                    ?? throw new InvalidOperationException("Revit did not write the image");

                var (w, h) = PngSize(file);
                Result = new { imagePath = file, document = doc.Title, view = viewName, width = w, height = h };
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private string CaptureTemporary3D(Document doc, string prefix)
        {
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);

            using (var group = new TransactionGroup(doc, "MCP: captura temporal"))
            {
                group.Start();
                try
                {
                    View3D view;
                    using (var t = new Transaction(doc, "MCP: vista temporal"))
                    {
                        t.Start();
                        view = View3D.CreateIsometric(doc, vft.Id);
                        view.Name = "MCP captura " + prefix;

                        BoundingBoxXYZ box = ElementsBox(doc);
                        XYZ center = box != null ? (box.Min + box.Max) / 2 : XYZ.Zero;
                        if (box != null)
                            view.SetSectionBox(box);

                        (XYZ forward, XYZ up) = Orientation(_direction);
                        view.SetOrientation(new ViewOrientation3D(center - forward * 500, up, forward));

                        foreach (var bic in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox,
                                     BuiltInCategory.OST_RoomSeparationLines, BuiltInCategory.OST_Lines, BuiltInCategory.OST_Cameras,
                                     BuiltInCategory.OST_Areas, BuiltInCategory.OST_AreaSchemeLines })
                        {
                            try { view.SetCategoryHidden(new ElementId(bic), true); } catch (Exception) { }
                        }
                        var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                            .Where(i => i.CanBeHidden(view)).Select(i => i.Id).ToList();
                        if (imports.Count > 0) view.HideElements(imports);

                        view.DetailLevel = ViewDetailLevel.Fine;
                        view.DisplayStyle = Style(_style);
                        t.Commit();
                    }

                    Export(doc, view, prefix);
                    return view.Name;
                }
                finally
                {
                    // Nothing created for the capture stays in the model.
                    group.RollBack();
                }
            }
        }

        private BoundingBoxXYZ ElementsBox(Document doc)
        {
            if (_elementIds.Count == 0) return null;
            XYZ min = null, max = null;
            foreach (long id in _elementIds)
            {
                BoundingBoxXYZ b = doc.GetElement(Utils.ElementIdExtensions.ToElementId(id))?.get_BoundingBox(null);
                if (b == null) continue;
                min = min == null ? b.Min : new XYZ(Math.Min(min.X, b.Min.X), Math.Min(min.Y, b.Min.Y), Math.Min(min.Z, b.Min.Z));
                max = max == null ? b.Max : new XYZ(Math.Max(max.X, b.Max.X), Math.Max(max.Y, b.Max.Y), Math.Max(max.Z, b.Max.Z));
            }
            if (min == null) return null;
            double pad = _paddingMeters / 0.3048;
            return new BoundingBoxXYZ { Min = min - new XYZ(pad, pad, pad), Max = max + new XYZ(pad, pad, pad) };
        }

        /// <summary>Direction is where the camera stands, for example "southwest" looks towards the northeast.</summary>
        private static (XYZ forward, XYZ up) Orientation(string direction)
        {
            XYZ f;
            switch (direction)
            {
                case "top": return (new XYZ(0, 0, -1), XYZ.BasisY);
                case "southeast": f = new XYZ(-1, 1, -0.8); break;
                case "northwest": f = new XYZ(1, -1, -0.8); break;
                case "northeast": f = new XYZ(-1, -1, -0.8); break;
                case "north": f = new XYZ(0, -1, -0.35); break;
                case "south": f = new XYZ(0, 1, -0.35); break;
                case "east": f = new XYZ(-1, 0, -0.35); break;
                case "west": f = new XYZ(1, 0, -0.35); break;
                default: f = new XYZ(1, 1, -0.8); break;
            }
            f = f.Normalize();
            XYZ up = (XYZ.BasisZ - f * XYZ.BasisZ.DotProduct(f)).Normalize();
            return (f, up);
        }

        private static DisplayStyle Style(string style)
        {
            switch (style)
            {
                case "realistic": return DisplayStyle.Realistic;
                case "hidden": return DisplayStyle.HLR;
                case "consistent": return DisplayStyle.FlatColors;
                case "wireframe": return DisplayStyle.Wireframe;
                default: return DisplayStyle.ShadingWithEdges;
            }
        }

        private void Export(Document doc, View view, string prefix)
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = _width,
                FilePath = Path.Combine(McpPaths.Captures, prefix),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);
        }

        private static View FindView(Document doc, string nameOrId)
        {
            if (long.TryParse(nameOrId, out long id) &&
                doc.GetElement(Utils.ElementIdExtensions.ToElementId(id)) is View byId)
                return byId;
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        }

        private static (int, int) PngSize(string file)
        {
            try
            {
                using (var fs = File.OpenRead(file))
                {
                    var header = new byte[24];
                    if (fs.Read(header, 0, 24) < 24) return (0, 0);
                    int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return (w, h);
                }
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }

        private static void PurgeOldCaptures()
        {
            DateTime limit = DateTime.Now.AddDays(-RetentionDays);
            foreach (string f in Directory.GetFiles(McpPaths.Captures))
            {
                try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); }
                catch (Exception) { /* a locked file is retried on a later capture */ }
            }
        }
    }
}
