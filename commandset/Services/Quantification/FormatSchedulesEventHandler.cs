using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>
    /// Renames schedule column headings and applies a consistent look.
    ///
    /// Two things about the schedule table API are easy to get wrong:
    ///
    /// - SectionType.Header is the schedule *title*, one cell. The column headings live in
    ///   row 0 of SectionType.Body.
    /// - TableSectionData.SetCellStyle(int, style) takes a *column* index, not a row. Styling
    ///   a whole row means calling the two-index overload cell by cell. Data rows reject
    ///   per-cell overrides, which is why the first column is styled as a column.
    /// </summary>
    public class FormatSchedulesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public Dictionary<string, string> Headings { get; private set; }
        public List<string> ScheduleNames { get; private set; }
        public string FontName { get; private set; }
        public int[] HeaderColor { get; private set; }
        public int[] TitleColor { get; private set; }
        public bool HighlightFirstColumn { get; private set; }
        public bool AlignNumbersRight { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        private static readonly HashSet<string> NumericFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Area", "Volume", "Length", "Cut Length", "Perimeter", "Thickness", "Width", "Height", "Pendiente"
        };

        public void SetParameters(
            string documentTitle,
            Dictionary<string, string> headings,
            List<string> scheduleNames,
            string fontName,
            int[] headerColor,
            int[] titleColor,
            bool highlightFirstColumn,
            bool alignNumbersRight)
        {
            DocumentTitle = documentTitle;
            Headings = headings ?? new Dictionary<string, string>();
            ScheduleNames = scheduleNames;
            FontName = string.IsNullOrWhiteSpace(fontName) ? "Arial" : fontName;
            HeaderColor = headerColor ?? new int[] { 47, 84, 150 };
            TitleColor = titleColor ?? new int[] { 31, 56, 100 };
            HighlightFirstColumn = highlightFirstColumn;
            AlignNumbersRight = alignNumbersRight;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 60000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                string error;
                Document doc = QuantificationUtils.ResolveDocument(app, DocumentTitle, out error);
                if (doc == null)
                {
                    Result = Fail(error);
                    return;
                }

                var schedules = new List<ViewSchedule>();
                foreach (ViewSchedule schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
                {
                    if (schedule.IsTemplate)
                        continue;
                    if (ScheduleNames != null && ScheduleNames.Count > 0 && !ScheduleNames.Contains(schedule.Name))
                        continue;
                    schedules.Add(schedule);
                }

                int renamed = 0, styled = 0;
                var touched = new List<string>();

                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Format schedules"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    foreach (ViewSchedule schedule in schedules)
                    {
                        ScheduleDefinition definition = schedule.Definition;

                        for (int i = 0; i < definition.GetFieldCount(); i++)
                        {
                            ScheduleField field = definition.GetField(i);
                            string name = field.GetName();

                            string heading;
                            if (Headings.TryGetValue(name, out heading) && !string.IsNullOrWhiteSpace(heading))
                            {
                                try
                                {
                                    field.ColumnHeading = heading;
                                    renamed++;
                                }
                                catch
                                {
                                    // Some fields refuse a custom heading; leave them as they are.
                                }
                            }

                            if (AlignNumbersRight)
                            {
                                try
                                {
                                    field.HorizontalAlignment = NumericFields.Contains(name)
                                        ? ScheduleHorizontalAlignment.Right
                                        : ScheduleHorizontalAlignment.Left;
                                }
                                catch
                                {
                                }
                            }
                        }

                        if (ApplyLook(schedule))
                        {
                            styled++;
                            touched.Add(schedule.Name);
                        }
                    }

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "schedules", schedules.Count },
                    { "headingsRenamed", renamed },
                    { "schedulesStyled", styled },
                    { "names", touched }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Formatted " + styled + " schedules in " + doc.Title + failures.Summarize(),
                    Response = payload
                };
            }
            catch (Exception ex)
            {
                Result = Fail(ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private bool ApplyLook(ViewSchedule schedule)
        {
            try
            {
                TableData table = schedule.GetTableData();

                TableSectionData title = table.GetSectionData(SectionType.Header);
                if (title != null && title.NumberOfRows > 0)
                {
                    TableCellStyle style = BuildStyle(title.GetTableCellStyle(0, 0), true, TitleColor);
                    title.SetCellStyle(style);
                }

                TableSectionData body = table.GetSectionData(SectionType.Body);
                if (body == null || body.NumberOfRows == 0)
                    return false;

                // Plain look for the data first, so nothing from a previous run survives.
                TableCellStyle plain = BuildStyle(
                    body.GetTableCellStyle(body.NumberOfRows > 1 ? 1 : 0, 0),
                    false,
                    new int[] { 255, 255, 255 });
                body.SetCellStyle(plain);

                TableCellStyle accent = BuildStyle(body.GetTableCellStyle(0, 0), true, HeaderColor);

                // Single-index overload styles a whole column.
                if (HighlightFirstColumn)
                    body.SetCellStyle(0, accent);

                // Column headings are row 0 of the body, one cell at a time.
                for (int column = 0; column < body.NumberOfColumns; column++)
                {
                    try
                    {
                        body.SetCellStyle(0, column, accent);
                    }
                    catch
                    {
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private TableCellStyle BuildStyle(TableCellStyle source, bool accent, int[] background)
        {
            TableCellStyleOverrideOptions overrides = source.GetCellStyleOverrideOptions();
            overrides.Bold = true;
            overrides.Font = true;
            overrides.FontColor = true;
            overrides.BackgroundColor = true;
            overrides.HorizontalAlignment = true;
            source.SetCellStyleOverrideOptions(overrides);

            source.FontName = FontName;
            source.IsFontBold = accent;
            source.BackgroundColor = new Color((byte)background[0], (byte)background[1], (byte)background[2]);
            source.TextColor = accent ? new Color(255, 255, 255) : new Color(0, 0, 0);
            if (accent)
                source.FontHorizontalAlignment = HorizontalAlignmentStyle.Center;

            return source;
        }

        private static AIResult<Dictionary<string, object>> Fail(string message)
        {
            return new AIResult<Dictionary<string, object>>
            {
                Success = false,
                Message = message
            };
        }

        public string GetName()
        {
            return "Format schedules";
        }
    }
}
