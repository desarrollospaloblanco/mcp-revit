using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Sheets
{
    /// <summary>
    /// Aligns viewports on sheets. topLeft puts each sheet's first (or named) viewport in the
    /// top-left corner of the title block. modelPoint makes a model point (e.g. a grid crossing) land
    /// on the same paper position on every sheet, so plans of different levels line up; the
    /// position is given in millimetres or read from a reference sheet.
    /// </summary>
    public class ArrangeViewportsCommand : DelegateCommand
    {
        public override string CommandName => "arrange_viewports";

        public ArrangeViewportsCommand(UIApplication uiApp) : base(uiApp, "MCP: alinear viewports")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var numbers = (parameters?["sheets"] as JArray)?.Select(t => t.Value<string>()).ToList();
            string mode = (parameters?["mode"]?.Value<string>() ?? "topLeft").ToLowerInvariant();
            string viewContains = parameters?["viewNameContains"]?.Value<string>();
            double left = parameters?["marginLeftMm"]?.Value<double>() ?? 25;
            double top = parameters?["marginTopMm"]?.Value<double>() ?? 10;
            string reference = parameters?["referenceSheet"]?.Value<string>();

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var notes = new List<string>();
                var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder && (numbers == null || numbers.Contains(s.SheetNumber)))
                    .OrderBy(s => s.SheetNumber).ToList();
                if (sheets.Count == 0) throw new InvalidOperationException("No sheets match");

                Viewport Pick(ViewSheet sheet) => sheet.GetAllViewports().Select(id => (Viewport)doc.GetElement(id))
                    .Where(vp => !(doc.GetElement(vp.ViewId) is View v) || v.ViewType != ViewType.Legend)
                    .FirstOrDefault(vp => viewContains == null || ((View)doc.GetElement(vp.ViewId)).Name.Contains(viewContains));

                XYZ modelPoint = null, sheetPoint = null;
                if (mode == "modelpoint")
                {
                    modelPoint = Metric.Point(parameters?["modelPoint"] ?? throw new ArgumentException("modelPoint [x, y] in metres is required"));
                    if (parameters["sheetPointMm"] is JArray sp)
                        sheetPoint = SheetLayout.Paper(sp[0].Value<double>(), sp[1].Value<double>());
                    else if (reference != null)
                    {
                        ViewSheet refSheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                            .FirstOrDefault(s => s.SheetNumber == reference) ?? throw new InvalidOperationException("No sheet " + reference);
                        Viewport refVp = Pick(refSheet) ?? throw new InvalidOperationException("Sheet " + reference + " has no matching viewport");
                        sheetPoint = SheetLayout.SheetPointOf(refVp, (View)doc.GetElement(refVp.ViewId), modelPoint);
                        sheets.RemoveAll(s => s.Id == refSheet.Id);
                    }
                    else throw new ArgumentException("modelPoint mode needs sheetPointMm or referenceSheet");
                }
                else if (mode != "topleft") throw new ArgumentException("mode must be topLeft or modelPoint");

                var moved = new List<object>();
                ToolTransaction.Run(doc, "MCP: alinear viewports", options, warnings, () =>
                {
                    foreach (ViewSheet sheet in sheets)
                    {
                        Viewport vp = Pick(sheet);
                        if (vp == null) { notes.Add(sheet.SheetNumber + ": no matching viewport"); continue; }
                        if (mode == "topleft")
                        {
                            BoundingBoxXYZ frame = SheetLayout.Frame(sheet);
                            SheetLayout.TopLeftTo(vp, new XYZ(frame.Min.X + left * SheetLayout.FeetPerMm, frame.Max.Y - top * SheetLayout.FeetPerMm, 0));
                        }
                        else
                        {
                            SheetLayout.ModelPointTo(vp, modelPoint, sheetPoint);
                        }
                        XYZ landed = modelPoint == null ? null : SheetLayout.SheetPointOf(vp, (View)doc.GetElement(vp.ViewId), modelPoint);
                        moved.Add(new
                        {
                            sheet = sheet.SheetNumber,
                            viewport = SheetLayout.Describe(vp),
                            modelPointOnSheetMm = landed == null ? null : new[] { SheetLayout.Mm(landed.X), SheetLayout.Mm(landed.Y) }
                        });
                    }
                });
                return new
                {
                    document = doc.Title,
                    mode,
                    sheetPointMm = sheetPoint == null ? null : new[] { SheetLayout.Mm(sheetPoint.X), SheetLayout.Mm(sheetPoint.Y) },
                    moved,
                    notes,
                    warnings
                };
            }, 600);
        }
    }
}
