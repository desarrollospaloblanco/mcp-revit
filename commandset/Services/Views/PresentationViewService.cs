using System.IO;
using PixelFormats = System.Windows.Media.PixelFormats;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Services.Execution;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    /// Perspective views for presentations: camera from an eye and a target (two-point by default,
    /// so vertical edges stay vertical), framing measured on a calibration export, identical camera
    /// and crop across several documents, and batch PNG export.
    ///
    /// The crop of a perspective view is not expressed at the near clip plane in the units the API
    /// suggests, so the framing is calibrated: the view is exported once, the extent of the model in
    /// the image is measured, and that rectangle is mapped back onto the crop (the image maps
    /// linearly onto the crop region).
    /// </summary>
    public static class PresentationViewService
    {
        private const double FeetPerMeter = 1 / 0.3048;

        public static readonly BuiltInCategory[] DefaultHiddenCategories =
        {
            BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox,
            BuiltInCategory.OST_RoomSeparationLines, BuiltInCategory.OST_Lines, BuiltInCategory.OST_Cameras,
            BuiltInCategory.OST_Areas, BuiltInCategory.OST_AreaSchemeLines
        };

        public static XYZ Point(double[] metres) => new XYZ(metres[0] * FeetPerMeter, metres[1] * FeetPerMeter, metres[2] * FeetPerMeter);

        public static View3D GetOrCreatePerspective(Document doc, string name, bool perspective, out bool created)
        {
            View3D view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            created = view == null;
            if (view != null) return view;

            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            view = perspective ? View3D.CreatePerspective(doc, type.Id) : View3D.CreateIsometric(doc, type.Id);
            view.Name = name;
            return view;
        }

        /// <summary>Camera, section box, display and the categories a presentation view hides.</summary>
        public static void ApplyCamera(View3D view, XYZ eye, XYZ target, bool twoPoint, BoundingBoxXYZ sectionBox,
            DisplayStyle style, bool hideImports)
        {
            Document doc = view.Document;
            view.ViewTemplateId = ElementId.InvalidElementId;

            XYZ forward = twoPoint ? new XYZ(target.X - eye.X, target.Y - eye.Y, 0) : target - eye;
            if (forward.GetLength() < 1e-6)
                throw new ArgumentException("The eye and the target are at the same place (or straight above each other with twoPoint)");
            forward = forward.Normalize();
            XYZ up = (XYZ.BasisZ - forward * XYZ.BasisZ.DotProduct(forward)).Normalize();
            view.SetOrientation(new ViewOrientation3D(eye, up, forward));

            if (sectionBox != null)
                view.SetSectionBox(sectionBox);

            Parameter far = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
            if (far != null && !far.IsReadOnly) far.Set(0);

            foreach (var bic in DefaultHiddenCategories)
            {
                try { view.SetCategoryHidden(new ElementId(bic), true); }
                catch (Exception) { /* category not available in this view */ }
            }

            if (hideImports)
            {
                var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                    .Where(i => i.CanBeHidden(view)).Select(i => i.Id).ToList();
                if (imports.Count > 0) view.HideElements(imports);
            }

            view.DisplayStyle = style;
            view.DetailLevel = ViewDetailLevel.Fine;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        }

        public static DisplayStyle ParseStyle(string style)
        {
            switch ((style ?? "realistic").Trim().ToLowerInvariant())
            {
                case "shaded": return DisplayStyle.ShadingWithEdges;
                case "hidden": return DisplayStyle.HLR;
                case "consistent": return DisplayStyle.FlatColors;
                case "wireframe": return DisplayStyle.Wireframe;
                default: return DisplayStyle.Realistic;
            }
        }

        /// <summary>Starting crop: the eight corners of a box projected onto the crop plane.</summary>
        public static double[] ProjectedCrop(View3D view, BoundingBoxXYZ box)
        {
            BoundingBoxXYZ crop = view.CropBox;
            Transform inverse = crop.Transform.Inverse;
            double near = crop.Max.Z;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                XYZ corner = new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                XYZ local = inverse.OfPoint(corner);
                if (local.Z >= 0) continue; // behind the camera
                double s = near / local.Z;
                x0 = Math.Min(x0, local.X * s); x1 = Math.Max(x1, local.X * s);
                y0 = Math.Min(y0, local.Y * s); y1 = Math.Max(y1, local.Y * s);
            }
            if (x0 == double.MaxValue)
                throw new InvalidOperationException("The model is behind the camera; check the eye and the target");
            return new[] { x0, y0, x1, y1 };
        }

        public static void SetCrop(View3D view, double[] rect, BoundingBoxXYZ zFrom = null)
        {
            BoundingBoxXYZ crop = view.CropBox;
            double minZ = zFrom?.Min.Z ?? crop.Min.Z, maxZ = zFrom?.Max.Z ?? crop.Max.Z;
            crop.Min = new XYZ(rect[0], rect[1], minZ);
            crop.Max = new XYZ(rect[2], rect[3], maxZ);
            view.CropBox = crop;
        }

        public static double[] CropRect(View3D view)
        {
            BoundingBoxXYZ c = view.CropBox;
            return new[] { c.Min.X, c.Min.Y, c.Max.X, c.Max.Y };
        }

        /// <summary>Box of the model elements visible in the view (or of the section box when one is active).</summary>
        public static BoundingBoxXYZ ModelBox(View3D view)
        {
            if (view.IsSectionBoxActive)
            {
                BoundingBoxXYZ sb = view.GetSectionBox();
                return new BoundingBoxXYZ { Min = sb.Transform.OfPoint(sb.Min), Max = sb.Transform.OfPoint(sb.Max) };
            }

            XYZ min = null, max = null;
            foreach (Element e in new FilteredElementCollector(view.Document, view.Id).WhereElementIsNotElementType())
            {
                if (e.Category == null || e.Category.CategoryType != CategoryType.Model || e is ImportInstance) continue;
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b == null) continue;
                min = min == null ? b.Min : new XYZ(Math.Min(min.X, b.Min.X), Math.Min(min.Y, b.Min.Y), Math.Min(min.Z, b.Min.Z));
                max = max == null ? b.Max : new XYZ(Math.Max(max.X, b.Max.X), Math.Max(max.Y, b.Max.Y), Math.Max(max.Z, b.Max.Z));
            }
            if (min == null)
                throw new InvalidOperationException("The view shows no model elements to frame");
            return new BoundingBoxXYZ { Min = min, Max = max };
        }

        /// <summary>Exports one view and returns the PNG path. Revit appends the view type and name to the prefix.</summary>
        public static string Export(Document doc, View view, int width, string folder, string prefix)
        {
            Directory.CreateDirectory(folder);
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = width,
                FilePath = Path.Combine(folder, prefix),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);
            return Directory.GetFiles(folder, prefix + "*").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                   ?? throw new InvalidOperationException("Revit did not write the image of " + view.Name);
        }

        /// <summary>Extent of non-white pixels as fractions of the image: left, top, right, bottom.</summary>
        public static double[] ContentFraction(string pngFile)
        {
            BitmapSource source;
            using (var stream = File.OpenRead(pngFile))
            {
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            }
            int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
            var pixels = new byte[h * stride];
            source.CopyPixels(pixels, stride, 0);

            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    if (Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) >= 235) continue;
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            }
            if (x1 < 0)
                throw new InvalidOperationException("The calibration image is empty: the model is outside the view");
            return new[] { x0 / (double)w, y0 / (double)h, (x1 + 1) / (double)w, (y1 + 1) / (double)h };
        }

        /// <summary>Maps an image rectangle (fractions, origin top-left) onto crop coordinates.</summary>
        public static double[] FractionToCrop(double[] crop, double[] fraction)
        {
            double w = crop[2] - crop[0], h = crop[3] - crop[1];
            return new[]
            {
                crop[0] + fraction[0] * w, crop[3] - fraction[3] * h,
                crop[0] + fraction[2] * w, crop[3] - fraction[1] * h
            };
        }

        public static double[] Union(IEnumerable<double[]> rects)
        {
            var list = rects.ToList();
            return new[] { list.Min(r => r[0]), list.Min(r => r[1]), list.Max(r => r[2]), list.Max(r => r[3]) };
        }

        public static double[] Grow(double[] rect, double percent)
        {
            double mx = (rect[2] - rect[0]) * percent / 100, my = (rect[3] - rect[1]) * percent / 100;
            return new[] { rect[0] - mx, rect[1] - my, rect[2] + mx, rect[3] + my };
        }

        public static string CalibrationFolder => Path.Combine(McpPaths.Captures, "calibration");
        public static string ExportFolder => Path.Combine(McpPaths.Root, "exports");
    }
}
