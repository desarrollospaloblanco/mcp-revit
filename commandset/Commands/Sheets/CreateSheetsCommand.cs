using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Sheets
{
    /// <summary>
    /// Creates sheets (or reuses one with the same number) with a title block, sheet parameters and
    /// views or schedules. Views go to an explicit point on the paper in millimetres, or to the
    /// top-left corner of the title block (DPB standard), the following ones in a row to its right.
    /// </summary>
    public class CreateSheetsCommand : DelegateCommand
    {
        public override string CommandName => "create_sheets";

        public CreateSheetsCommand(UIApplication uiApp) : base(uiApp, "MCP: crear hojas")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var specs = (parameters?["sheets"] as JArray)?.OfType<JObject>().ToList();
            if (specs == null || specs.Count == 0) throw new ArgumentException("sheets is required: [{ number, name, views }]");
            string defaultTitleBlock = parameters?["titleBlock"]?.Value<string>();
            double left = parameters?["marginLeftMm"]?.Value<double>() ?? 25;
            double top = parameters?["marginTopMm"]?.Value<double>() ?? 10;
            double gap = parameters?["gapMm"]?.Value<double>() ?? 10;

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var warnings = new List<string>();
                var results = new List<object>();
                var notes = new List<string>();
                var created = new List<(ViewSheet sheet, List<Element> placed, List<JObject> specs)>();

                ToolTransaction.Run(doc, "MCP: crear hojas", options, warnings, () =>
                {
                    foreach (JObject spec in specs)
                    {
                        string number = spec["number"]?.Value<string>() ?? throw new ArgumentException("every sheet needs a number");
                        ViewSheet sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                            .FirstOrDefault(s => s.SheetNumber == number);
                        bool isNew = sheet == null;
                        if (isNew)
                        {
                            FamilySymbol tb = SheetLayout.TitleBlock(doc, spec["titleBlock"]?.Value<string>() ?? defaultTitleBlock);
                            if (tb != null && !tb.IsActive) tb.Activate();
                            sheet = ViewSheet.Create(doc, tb?.Id ?? ElementId.InvalidElementId);
                            sheet.SheetNumber = number;
                            RunMarks.Set(sheet, options.RunMark);
                        }
                        if (spec["name"] != null) sheet.Name = spec["name"].Value<string>();
                        foreach (var prop in (spec["parameters"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                        {
                            Parameter prm = sheet.LookupParameter(prop.Name);
                            if (prm == null || prm.IsReadOnly) { notes.Add($"{number}: parameter '{prop.Name}' missing or read-only"); continue; }
                            if (prm.StorageType == StorageType.String) prm.Set(prop.Value.Value<string>());
                            else if (prm.StorageType == StorageType.Integer) prm.Set(prop.Value.Value<int>());
                            else if (prm.StorageType == StorageType.Double) prm.Set(prop.Value.Value<double>());
                        }

                        var placed = new List<Element>();
                        var viewSpecs = (spec["views"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                        foreach (JObject vs in viewSpecs)
                        {
                            string key = vs["view"]?.Value<string>();
                            View view = Views.ExportViewsCommand.FindView(doc, key);
                            if (view == null) { notes.Add($"{number}: no view '{key}'"); placed.Add(null); continue; }
                            XYZ at = vs["positionMm"] is JArray pos ? SheetLayout.Paper(pos[0].Value<double>(), pos[1].Value<double>()) : XYZ.Zero;
                            if (view is ViewSchedule schedule)
                            {
                                var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, at);
                                RunMarks.Set(instance, options.RunMark);
                                placed.Add(instance);
                                continue;
                            }
                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                            {
                                notes.Add($"{number}: '{view.Name}' is already on a sheet or cannot be placed");
                                placed.Add(null);
                                continue;
                            }
                            Viewport vp = Viewport.Create(doc, sheet.Id, view.Id, at);
                            RunMarks.Set(vp, options.RunMark);
                            placed.Add(vp);
                        }
                        created.Add((sheet, placed, viewSpecs));
                        results.Add(new { number, name = sheet.Name, id = Utils.ElementIdExtensions.GetValue(sheet.Id), created = isNew });
                    }

                    // Outlines are only right after a regeneration, so the layout is a second pass.
                    doc.Regenerate();
                    foreach (var (sheet, placed, viewSpecs) in created)
                    {
                        BoundingBoxXYZ frame = SheetLayout.Frame(sheet);
                        XYZ cursor = new XYZ(frame.Min.X + left * SheetLayout.FeetPerMm, frame.Max.Y - top * SheetLayout.FeetPerMm, 0);
                        for (int i = 0; i < placed.Count; i++)
                        {
                            if (placed[i] == null || viewSpecs[i]["positionMm"] != null) continue;
                            if (placed[i] is Viewport vp)
                            {
                                SheetLayout.TopLeftTo(vp, cursor);
                                cursor = new XYZ(vp.GetBoxOutline().MaximumPoint.X + gap * SheetLayout.FeetPerMm, cursor.Y, 0);
                            }
                            else if (placed[i] is ScheduleSheetInstance si)
                            {
                                SheetLayout.TopLeftTo(si, cursor);
                                doc.Regenerate();
                                BoundingBoxXYZ b = si.get_BoundingBox(sheet);
                                if (b != null) cursor = new XYZ(b.Max.X + gap * SheetLayout.FeetPerMm, cursor.Y, 0);
                            }
                        }
                    }
                });

                return new
                {
                    document = doc.Title,
                    sheets = results,
                    layout = created.Select(c => new
                    {
                        number = c.sheet.SheetNumber,
                        viewports = c.placed.OfType<Viewport>().Select(SheetLayout.Describe)
                    }),
                    notes,
                    warnings
                };
            }, 600);
        }
    }
}
