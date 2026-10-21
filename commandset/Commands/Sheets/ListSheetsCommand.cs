using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Sheets
{
    /// <summary>Read-only inventory of sheets: number, name, title block, viewports and schedules.</summary>
    public class ListSheetsCommand : DelegateCommand
    {
        public override string CommandName => "list_sheets";

        public ListSheetsCommand(UIApplication uiApp) : base(uiApp, "MCP: listar hojas")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            string contains = parameters?["numberContains"]?.Value<string>();
            var extra = (parameters?["parameters"] as JArray)?.Select(t => t.Value<string>()).ToList() ?? new List<string>();

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => contains == null || s.SheetNumber.Contains(contains)).OrderBy(s => s.SheetNumber).ToList();
                return new
                {
                    document = doc.Title,
                    count = sheets.Count,
                    sheets = sheets.Select(s =>
                    {
                        Element tb = new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
                        BoundingBoxXYZ frame = SheetLayout.Frame(s);
                        return new
                        {
                            number = s.SheetNumber,
                            name = s.Name,
                            id = Utils.ElementIdExtensions.GetValue(s.Id),
                            placeholder = s.IsPlaceholder,
                            titleBlock = tb == null ? null : (doc.GetElement(tb.GetTypeId()) as FamilySymbol)?.FamilyName + " : " + doc.GetElement(tb.GetTypeId())?.Name,
                            frameMm = new[] { SheetLayout.Mm(frame.Min.X), SheetLayout.Mm(frame.Min.Y), SheetLayout.Mm(frame.Max.X), SheetLayout.Mm(frame.Max.Y) },
                            parameters = extra.ToDictionary(n => n, n => s.LookupParameter(n)?.AsValueString() ?? s.LookupParameter(n)?.AsString()),
                            viewports = s.GetAllViewports().Select(id => SheetLayout.Describe((Viewport)doc.GetElement(id))),
                            schedules = new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                                .Where(si => !si.IsTitleblockRevisionSchedule).Select(si => doc.GetElement(si.ScheduleId)?.Name)
                        };
                    })
                };
            }, 300);
        }
    }
}
