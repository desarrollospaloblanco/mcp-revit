using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    /// <summary>
    /// Creates or updates a presentation perspective with the same camera and the same crop in one
    /// or several documents, framed on the model by a calibration export, and optionally exports it.
    /// </summary>
    public class SetPerspectiveViewCommand : DelegateCommand
    {
        private const int MaxCalibrationPasses = 4;
        private const int CalibrationWidth = 1000;
        private const double EdgeTolerance = 0.003;

        public override string CommandName => "set_perspective_view";

        public SetPerspectiveViewCommand(UIApplication uiApp) : base(uiApp, "MCP: vista de presentación")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string viewName = parameters?["viewName"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(viewName))
                throw new ArgumentException("viewName is required");
            double[] eye = ReadPoint(parameters, "eye") ?? throw new ArgumentException("eye [x,y,z] in metres is required");
            double[] target = ReadPoint(parameters, "target") ?? throw new ArgumentException("target [x,y,z] in metres is required");

            var titles = (parameters["documents"] as JArray)?.Select(t => t.Value<string>()).ToList() ?? new List<string>();
            bool twoPoint = parameters["twoPoint"]?.Value<bool>() ?? true;
            bool perspective = parameters["perspective"]?.Value<bool>() ?? true;
            bool hideImports = parameters["hideImports"]?.Value<bool>() ?? true;
            string style = parameters["style"]?.Value<string>();
            string frame = (parameters["frame"]?.Value<string>() ?? "fit").Trim().ToLowerInvariant();
            double margin = parameters["marginPercent"]?.Value<double>() ?? 4;
            double aspect = parameters["aspectRatio"]?.Value<double>() ?? 0;
            int exportWidth = parameters["exportWidth"]?.Value<int>() ?? 0;
            string outputFolder = parameters["outputFolder"]?.Value<string>();
            double[] boxMin = ReadPoint(parameters["sectionBox"] as JObject, "min");
            double[] boxMax = ReadPoint(parameters["sectionBox"] as JObject, "max");

            return RunInRevit(app =>
            {
                var docs = ResolveDocuments(app, titles);
                BoundingBoxXYZ sectionBox = boxMin != null && boxMax != null
                    ? new BoundingBoxXYZ { Min = PresentationViewService.Point(boxMin), Max = PresentationViewService.Point(boxMax) }
                    : null;

                var views = new List<View3D>();
                var created = new List<bool>();
                var warnings = new List<string>();
                foreach (Document doc in docs)
                {
                    ToolTransaction.Run(doc, "MCP: vista de presentación " + viewName, options, warnings, () =>
                    {
                        View3D view = PresentationViewService.GetOrCreatePerspective(doc, viewName, perspective, out bool isNew);
                        PresentationViewService.ApplyCamera(view, PresentationViewService.Point(eye), PresentationViewService.Point(target),
                            twoPoint, sectionBox, PresentationViewService.ParseStyle(style), hideImports);
                        RunMarks.Set(view, options.RunMark);
                        views.Add(view);
                        created.Add(isNew);
                    });
                }

                if (frame != "keep")
                    Frame(docs, views, frame == "calibrate" || frame == "fit", margin, aspect);

                var result = new List<object>();
                for (int i = 0; i < docs.Count; i++)
                {
                    string image = exportWidth > 0
                        ? PresentationViewService.Export(docs[i], views[i], exportWidth,
                            string.IsNullOrWhiteSpace(outputFolder) ? PresentationViewService.ExportFolder : outputFolder,
                            Sanitize(docs[i].Title + "_" + viewName))
                        : null;
                    double[] crop = PresentationViewService.CropRect(views[i]);
                    result.Add(new
                    {
                        document = docs[i].Title,
                        view = views[i].Name,
                        viewId = Utils.ElementIdExtensions.GetValue(views[i].Id),
                        created = created[i],
                        crop = crop.Select(c => Math.Round(c, 6)).ToArray(),
                        image
                    });
                }
                return new { views = result, warnings };
            }, 600);
        }

        /// <summary>
        /// Starts from the model box projected onto the crop, then exports each view, measures where
        /// the model lands in the image and maps that back onto the crop. A side touching the edge of
        /// the image means the model was cut, so that side is widened and the pass repeated. The
        /// union over all documents is applied to every view so the framing is identical.
        /// </summary>
        private static void Frame(List<Document> docs, List<View3D> views, bool calibrate, double margin, double aspect)
        {
            BoundingBoxXYZ zSource;
            using (var t = new Transaction(docs[0], "MCP: encuadre"))
            {
                t.Start();
                var boxes = views.Select(PresentationViewService.ModelBox).ToList();
                double[] start = PresentationViewService.Union(boxes.Select(b => PresentationViewService.ProjectedCrop(views[0], b)));
                PresentationViewService.SetCrop(views[0], PresentationViewService.Grow(start, calibrate ? 15 : margin));
                t.Commit();
            }
            zSource = views[0].CropBox;
            double[] rect = PresentationViewService.CropRect(views[0]);
            ApplyCrop(docs, views, rect, zSource, skipFirst: true);

            if (calibrate)
            {
                int cleanPasses = 0;
                for (int pass = 0; pass < MaxCalibrationPasses + 1; pass++)
                {
                    var measured = new List<double[]>();
                    bool cut = false;
                    double[] grow = { 0, 0, 0, 0 };
                    for (int i = 0; i < docs.Count; i++)
                    {
                        string file = PresentationViewService.Export(docs[i], views[i], CalibrationWidth,
                            PresentationViewService.CalibrationFolder, "cal_" + DateTime.Now.ToString("HHmmss_fff"));
                        double[] f = PresentationViewService.ContentFraction(file);
                        TryDelete(file);
                        measured.Add(PresentationViewService.FractionToCrop(rect, f));
                        // fraction order: left, top, right, bottom; crop order: minX, minY, maxX, maxY
                        if (f[0] < EdgeTolerance) { cut = true; grow[0] = 0.5; }
                        if (f[3] > 1 - EdgeTolerance) { cut = true; grow[1] = 0.5; }
                        if (f[2] > 1 - EdgeTolerance) { cut = true; grow[2] = 0.5; }
                        if (f[1] < EdgeTolerance) { cut = true; grow[3] = 0.5; }
                    }

                    double w = rect[2] - rect[0], h = rect[3] - rect[1];
                    rect = cut
                        ? new[] { rect[0] - grow[0] * w, rect[1] - grow[1] * h, rect[2] + grow[2] * w, rect[3] + grow[3] * h }
                        : PresentationViewService.Grow(PresentationViewService.Union(measured), margin);
                    if (!cut) rect = WithAspect(rect, aspect);
                    ApplyCrop(docs, views, rect, zSource, skipFirst: false);
                    // A second clean pass refines the framing when the first image showed the model small.
                    if (!cut && ++cleanPasses == 2) return;
                }
                throw new InvalidOperationException(
                    "The model still touches the edge of the image after widening the crop; move the eye further away or use a section box");
            }

            ApplyCrop(docs, views, WithAspect(rect, aspect), zSource, skipFirst: false);
        }

        private static double[] WithAspect(double[] rect, double aspect)
        {
            if (aspect <= 0) return rect;
            double w = rect[2] - rect[0], h = rect[3] - rect[1];
            double cx = (rect[0] + rect[2]) / 2, cy = (rect[1] + rect[3]) / 2;
            if (w / h < aspect) w = h * aspect; else h = w / aspect;
            return new[] { cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2 };
        }

        private static void ApplyCrop(List<Document> docs, List<View3D> views, double[] rect, BoundingBoxXYZ zSource, bool skipFirst)
        {
            for (int i = skipFirst ? 1 : 0; i < docs.Count; i++)
            {
                using (var t = new Transaction(docs[i], "MCP: encuadre"))
                {
                    t.Start();
                    PresentationViewService.SetCrop(views[i], rect, zSource);
                    t.Commit();
                }
            }
        }

        internal static List<Document> ResolveDocuments(UIApplication app, List<string> titles)
        {
            if (titles == null || titles.Count == 0)
                titles = new List<string> { null };
            var docs = new List<Document>();
            foreach (string title in titles)
            {
                Document doc = QuantificationUtils.ResolveDocument(app, title, out string error)
                               ?? throw new InvalidOperationException(error);
                if (doc.IsLinked || doc.IsFamilyDocument)
                    throw new InvalidOperationException(doc.Title + " is a linked or family document");
                docs.Add(doc);
            }
            return docs;
        }

        internal static double[] ReadPoint(JObject parameters, string name)
        {
            var array = parameters?[name] as JArray;
            if (array == null) return null;
            if (array.Count != 3) throw new ArgumentException(name + " must be [x, y, z] in metres");
            return array.Select(v => v.Value<double>()).ToArray();
        }

        internal static string Sanitize(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        private static void TryDelete(string file)
        {
            try { System.IO.File.Delete(file); }
            catch (System.IO.IOException) { /* a leftover calibration image is purged with the captures */ }
        }
    }
}
