using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;

namespace RevitMCPCommandSet.Services.Common
{
    /// <summary>
    /// Options every tool accepts: the document to act on (by title, the active one by default),
    /// whether warnings are deleted instead of stopping Revit with a dialog, and a run mark that tags
    /// what the tool creates so a later run can find, replace or delete it.
    /// </summary>
    public class ToolOptions
    {
        public string DocumentTitle { get; private set; }
        public string RunMark { get; private set; }
        public bool SwallowWarnings { get; private set; } = true;

        public static ToolOptions From(JObject parameters) => new ToolOptions
        {
            DocumentTitle = parameters?["documentTitle"]?.Value<string>(),
            RunMark = parameters?["runMark"]?.Value<string>(),
            SwallowWarnings = parameters?["swallowWarnings"]?.Value<bool>() ?? true
        };

        public Document Document(UIApplication app) => ToolDocuments.Resolve(app, DocumentTitle);
    }

    public static class ToolDocuments
    {
        public static Document Resolve(UIApplication app, string title)
        {
            Document doc = QuantificationUtils.ResolveDocument(app, title, out string error)
                           ?? throw new InvalidOperationException(error);
            return doc;
        }

        public static List<Document> ResolveMany(UIApplication app, IList<string> titles)
        {
            if (titles == null || titles.Count == 0)
                return new List<Document> { Resolve(app, null) };
            return titles.Select(t => Resolve(app, t)).ToList();
        }

        /// <summary>Every open project document, without links and families.</summary>
        public static List<Document> AllProjects(UIApplication app) =>
            app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !d.IsFamilyDocument).ToList();
    }

    /// <summary>Metres in, feet out, and back. Tools speak metres; the API speaks feet.</summary>
    public static class Metric
    {
        public const double FeetPerMeter = 1 / 0.3048;
        public const double SquareMetersPerSquareFoot = 0.09290304;

        public static double Ft(double meters) => meters * FeetPerMeter;
        public static double M(double feet) => Math.Round(feet * 0.3048, 4);
        public static XYZ Point(double x, double y, double z = 0) => new XYZ(Ft(x), Ft(y), Ft(z));

        public static XYZ Point(JToken token, double defaultZ = 0)
        {
            var a = token as JArray ?? throw new ArgumentException("a point must be [x, y] or [x, y, z] in metres");
            if (a.Count < 2) throw new ArgumentException("a point must be [x, y] or [x, y, z] in metres");
            return Point(a[0].Value<double>(), a[1].Value<double>(), a.Count > 2 ? a[2].Value<double>() : defaultZ);
        }

        public static double[] Out(XYZ p) => new[] { M(p.X), M(p.Y), M(p.Z) };

        public static bool IsLength(Parameter p)
        {
#if REVIT2022_OR_GREATER
            return p.Definition.GetDataType() == SpecTypeId.Length;
#else
            return p.Definition.ParameterType == ParameterType.Length;
#endif
        }

        /// <summary>Writes a JSON value to a parameter; lengths are taken in metres.</summary>
        public static bool Set(Parameter p, JToken value)
        {
            if (p == null || p.IsReadOnly || value == null) return false;
            switch (p.StorageType)
            {
                case StorageType.String: return p.Set(value.Value<string>());
                case StorageType.Integer: return p.Set(value.Type == JTokenType.Boolean ? (value.Value<bool>() ? 1 : 0) : value.Value<int>());
                case StorageType.Double: return p.Set(IsLength(p) ? Ft(value.Value<double>()) : value.Value<double>());
                default: return false;
            }
        }

        public static double[] Out2(XYZ p) => new[] { M(p.X), M(p.Y) };

        /// <summary>A closed loop of points in metres at elevation z (feet).</summary>
        public static CurveLoop Loop(JToken points, double zFeet)
        {
            var list = (points as JArray ?? throw new ArgumentException("a loop is an array of [x, y] points in metres"))
                .Select(p => { XYZ q = Point(p); return new XYZ(q.X, q.Y, zFeet); }).ToList();
            if (list.Count > 1 && list[0].IsAlmostEqualTo(list[list.Count - 1])) list.RemoveAt(list.Count - 1);
            if (list.Count < 3) throw new ArgumentException("a loop needs at least three points");
            var loop = new CurveLoop();
            for (int i = 0; i < list.Count; i++)
                loop.Append(Line.CreateBound(list[i], list[(i + 1) % list.Count]));
            return loop;
        }
    }

    /// <summary>
    /// Keeps Revit from stopping a tool with a dialog. Warnings are deleted and kept for the result.
    /// Errors that have a resolution (e.g. "Can't keep elements joined" -> unjoin) are resolved with
    /// it; errors without one roll the transaction back, and their text comes back in the error.
    /// </summary>
    public class WarningCollector : IFailuresPreprocessor
    {
        private readonly bool _delete;
        public List<string> Warnings { get; } = new List<string>();
        public List<string> ResolvedErrors { get; } = new List<string>();
        public List<string> BlockingErrors { get; } = new List<string>();

        public WarningCollector(bool deleteWarnings)
        {
            _delete = deleteWarnings;
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool resolved = false;
            foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
            {
                string text = message.GetDescriptionText();
                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    if (!Warnings.Contains(text)) Warnings.Add(text);
                    if (_delete) accessor.DeleteWarning(message);
                    continue;
                }
                if (message.HasResolutions())
                {
                    accessor.ResolveFailure(message);
                    if (!ResolvedErrors.Contains(text)) ResolvedErrors.Add(text);
                    resolved = true;
                }
                else
                {
                    if (!BlockingErrors.Contains(text)) BlockingErrors.Add(text);
                    return FailureProcessingResult.ProceedWithRollBack;
                }
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }

        public IEnumerable<string> Report() =>
            Warnings.Concat(ResolvedErrors.Select(e => "resolved: " + e));
    }

    public static class ToolTransaction
    {
        /// <summary>Runs work in a transaction with the warning collector attached.</summary>
        public static T Run<T>(Document doc, string name, ToolOptions options, List<string> warnings, Func<T> work) =>
            Run(doc, name, options, warnings, work, false);

        /// <summary>With dryRun the work runs and is rolled back, so the caller can report what would change.</summary>
        public static T Run<T>(Document doc, string name, ToolOptions options, List<string> warnings, Func<T> work, bool dryRun)
        {
            using (var t = new Transaction(doc, name))
            {
                t.Start();
                var collector = new WarningCollector(options?.SwallowWarnings ?? true);
                var failureOptions = t.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(collector);
                t.SetFailureHandlingOptions(failureOptions);
                T result = work();
                if (dryRun)
                {
                    doc.Regenerate();
                    t.RollBack();
                    warnings?.AddRange(collector.Report().Where(w => !warnings.Contains(w)));
                    return result;
                }
                TransactionStatus status = t.Commit();
                warnings?.AddRange(collector.Report().Where(w => !warnings.Contains(w)));
                if (status != TransactionStatus.Committed)
                    throw new InvalidOperationException($"Revit rolled back '{name}' ({status}). " +
                        (collector.BlockingErrors.Count > 0
                            ? "Errors: " + string.Join(" | ", collector.BlockingErrors)
                            : "Elements owned by another user or updated in central make Revit discard the whole transaction."));
                return result;
            }
        }

        public static void Run(Document doc, string name, ToolOptions options, List<string> warnings, Action work) =>
            Run<object>(doc, name, options, warnings, () => { work(); return null; });

        public static void Run(Document doc, string name, ToolOptions options, List<string> warnings, Action work, bool dryRun) =>
            Run<object>(doc, name, options, warnings, () => { work(); return null; }, dryRun);
    }

    /// <summary>
    /// A hidden label on elements created by a tool run, stored with extensible storage so it never
    /// touches Comments or any visible parameter. run_marks counts, selects or deletes by label.
    /// </summary>
    public static class RunMarks
    {
        private static readonly Guid SchemaGuid = new Guid("6C1E7A52-3F0B-4B8E-9D5A-2E7F1C9B4A10");
        private const string Field = "Mark";

        public static Schema Schema
        {
            get
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema != null) return schema;
                var builder = new SchemaBuilder(SchemaGuid);
                builder.SetSchemaName("RevitMcpRunMark");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField(Field, typeof(string));
                return builder.Finish();
            }
        }

        /// <summary>Labels the element when a mark was given; must run inside a transaction.</summary>
        public static void Set(Element element, string mark)
        {
            if (string.IsNullOrWhiteSpace(mark) || element == null) return;
            var entity = new Entity(Schema);
            entity.Set(Field, mark);
            element.SetEntity(entity);
        }

        public static string Get(Element element)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return null;
            Entity entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(Field) : null;
        }

        /// <summary>Elements carrying the mark (any mark when null).</summary>
        public static List<Element> Find(Document doc, string mark)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new List<Element>();
            return new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                .Where(e => mark == null || Get(e) == mark).ToList();
        }

        /// <summary>Deletes what an earlier run with this mark created, so a re-run replaces it.</summary>
        public static int DeletePrevious(Document doc, string mark)
        {
            if (string.IsNullOrWhiteSpace(mark)) return 0;
            var ids = Find(doc, mark).Select(e => e.Id).ToList();
            if (ids.Count == 0) return 0;
            return doc.Delete(ids).Count;
        }
    }
}
