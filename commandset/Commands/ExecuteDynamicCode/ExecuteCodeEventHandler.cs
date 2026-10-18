using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using RevitMCPCommandSet.Services.DialogGuard;
using RevitMCPCommandSet.Services.Execution;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// Compiles and runs agent-written C# inside Revit.
    ///
    /// By default the run is wrapped in a transaction group on every open document, so whatever the
    /// script changes (in the active document or in any other open one) becomes a single undo step,
    /// and the group can be rolled back for a dry run, a discarded approval or a failure. Scripts that
    /// save, synchronize, open or close documents cannot run inside a group: they pass group=false.
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;
        private bool _dryRun;
        private bool _group = true;
        private string _title;

        public ExecutionResultInfo ResultInfo { get; private set; }

        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetExecutionParameters(string code, object[] parameters, string transactionMode,
            bool dryRun, bool group, string title)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = transactionMode == TransactionModeNone ? TransactionModeNone : TransactionModeAuto;
            _dryRun = dryRun;
            _group = group;
            _title = string.IsNullOrWhiteSpace(title) ? "MCP: código C#" : "MCP: " + title.Trim();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // The event was reset when the parameters were set; resetting it here could swallow a
            // completion that happened before the wait started.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            var watch = Stopwatch.StartNew();
            string policy = ExecutionPolicy.Current;
            bool dryRun = _dryRun || policy == ExecutionPolicy.ReadOnly;
            // A dry run is only possible inside a group, so it overrides group=false.
            bool grouped = _group || dryRun;

            var record = new RunRecord
            {
                Id = RunHistory.NewRunId(),
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Title = _title,
                Policy = policy,
                DryRun = dryRun,
                Grouped = grouped,
                TransactionMode = _transactionMode,
                Code = _generatedCode,
                Parameters = _executionParameters
            };
            ResultInfo = new ExecutionResultInfo { RunId = record.Id, Policy = policy, DryRun = dryRun, Grouped = grouped };

            var groups = new List<TransactionGroup>();
            try
            {
                Document activeDoc = app.ActiveUIDocument?.Document;
                record.ActiveDocument = activeDoc?.Title;

                using (var tracker = new ChangeTracker(app.Application))
                {
                    if (grouped)
                        groups = StartGroups(app, _title);

                    object result = Run(activeDoc);
                    ResultInfo.Result = JsonConvert.SerializeObject(result);
                    ResultInfo.Changes = tracker.Summarize();

                    if (!grouped)
                        ResultInfo.Outcome = "applied_ungrouped";
                    else if (!tracker.HasChanges)
                    {
                        Finish(groups, keep: true);
                        ResultInfo.Outcome = "no_changes";
                    }
                    else if (dryRun)
                    {
                        Finish(groups, keep: false);
                        ResultInfo.Outcome = "dry_run_rolled_back";
                    }
                    else if (policy == ExecutionPolicy.Ask)
                    {
                        bool keep = AskUser(app, tracker, ResultInfo.Changes);
                        Finish(groups, keep);
                        ResultInfo.Outcome = keep ? "applied_after_approval" : "discarded_by_user";
                    }
                    else
                    {
                        Finish(groups, keep: true);
                        ResultInfo.Outcome = "applied";
                    }
                }

                ResultInfo.Success = true;
            }
            catch (Exception ex)
            {
                Finish(groups, keep: false);
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                ResultInfo.Success = false;
                ResultInfo.Outcome = "failed_rolled_back";
                ResultInfo.ErrorMessage = $"执行失败: {inner.Message}" + Hint(inner, grouped);
            }
            finally
            {
                foreach (var g in groups) g.Dispose();
                watch.Stop();
                record.DurationMs = watch.ElapsedMilliseconds;
                record.Success = ResultInfo.Success;
                record.Outcome = ResultInfo.Outcome;
                record.Result = ResultInfo.Result;
                record.ErrorMessage = ResultInfo.ErrorMessage;
                record.Changes = ResultInfo.Changes;
                ResultInfo.DurationMs = record.DurationMs;
                RunHistory.Save(record);

                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private object Run(Document activeDoc)
        {
            if (_transactionMode == TransactionModeNone)
                return CompileAndExecuteCode(_generatedCode, activeDoc, _executionParameters);

            if (activeDoc == null)
                throw new InvalidOperationException("No active document; use transactionMode 'none' and pick the document by title");

            using (var transaction = new Transaction(activeDoc, "执行AI代码"))
            {
                transaction.Start();
                object result = CompileAndExecuteCode(_generatedCode, activeDoc, _executionParameters);
                transaction.Commit();
                return result;
            }
        }

        /// <summary>One group per open, editable document; documents that refuse a group are skipped.</summary>
        private static List<TransactionGroup> StartGroups(UIApplication app, string title)
        {
            var groups = new List<TransactionGroup>();
            foreach (Document doc in app.Application.Documents)
            {
                if (doc.IsLinked || doc.IsReadOnly)
                    continue;
                var group = new TransactionGroup(doc, title);
                try
                {
                    group.Start();
                    groups.Add(group);
                }
                catch (Exception)
                {
                    group.Dispose();
                }
            }
            return groups;
        }

        private static void Finish(List<TransactionGroup> groups, bool keep)
        {
            foreach (var group in groups)
            {
                try
                {
                    if (group.GetStatus() != TransactionStatus.Started)
                        continue;
                    if (keep) group.Assimilate();
                    else group.RollBack();
                }
                catch (Exception)
                {
                    // A group closed by the script itself cannot be finished here; the other
                    // documents must still be handled.
                }
            }
        }

        /// <summary>
        /// Isolates the changed elements in the active view and asks the user to apply or discard
        /// them. The isolation is temporary view state and is cleared before the group closes.
        /// </summary>
        private static bool AskUser(UIApplication app, ChangeTracker tracker, List<DocumentChangeSummary> changes)
        {
            View view = app.ActiveUIDocument?.ActiveView;
            Document doc = app.ActiveUIDocument?.Document;
            bool isolated = false;

            if (view != null && doc != null && view.CanUseTemporaryVisibilityModes())
            {
                var ids = tracker.ChangedElementIds(doc);
                if (ids.Count > 0)
                {
                    try
                    {
                        using (var t = new Transaction(doc, "MCP: vista previa"))
                        {
                            t.Start();
                            view.IsolateElementsTemporary(ids);
                            t.Commit();
                        }
                        app.ActiveUIDocument.RefreshActiveView();
                        isolated = true;
                    }
                    catch (Exception)
                    {
                        // The prompt still works without the preview.
                    }
                }
            }

            var dialog = new TaskDialog("Revit MCP")
            {
                MainInstruction = "¿Aplicar los cambios propuestos?",
                MainContent = string.Join("\n", changes.Select(DescribeChange)),
                FooterText = isolated ? "Los elementos creados o modificados están aislados en la vista activa." : null,
                AllowCancellation = true
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Aplicar", "Se guardan como un solo paso de deshacer");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Descartar", "El modelo queda como estaba");

            TaskDialogResult answer;
            RevitDialogGuard.Suspended = true;
            try
            {
                answer = dialog.Show();
            }
            finally
            {
                RevitDialogGuard.Suspended = false;
            }

            if (isolated)
            {
                try
                {
                    using (var t = new Transaction(doc, "MCP: quitar vista previa"))
                    {
                        t.Start();
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        t.Commit();
                    }
                }
                catch (Exception)
                {
                    // Leaving a temporary isolation behind is harmless; it is not saved with the model.
                }
            }

            return answer == TaskDialogResult.CommandLink1;
        }

        private static string DescribeChange(DocumentChangeSummary c)
        {
            var categories = c.AddedByCategory.Concat(c.ModifiedByCategory)
                .GroupBy(kv => kv.Key)
                .Select(g => g.Key + " " + g.Sum(kv => kv.Value))
                .Take(6);
            return c + "\n   " + string.Join(", ", categories);
        }

        private static string Hint(Exception ex, bool grouped)
        {
            string message = ex.Message ?? "";
            if (grouped && (message.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            message.IndexOf("synchroniz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            message.IndexOf("transaction group", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            message.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0))
                return " (los scripts que guardan, sincronizan, abren o cierran documentos deben usar group=false)";
            return "";
        }

        private object CompileAndExecuteCode(string code, Document doc, object[] parameters)
        {
            // 包装代码以规范入口点
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
            // 用户代码入口
            {code}
        }}
    }}
}}";

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);

            // 添加必要的程序集引用（引用所有已加载的程序集）
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            // 编译代码
            var compilation = CSharpCompilation.Create(
                "AIGeneratedCode",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);

                // 处理编译结果
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {d.Location.GetLineSpan().StartLinePosition.Line}: {d.GetMessage()}"));
                    throw new Exception($"代码编译错误:\n{errors}");
                }

                // 反射调用执行方法
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());
                var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
                var executeMethod = executorType.GetMethod("Execute");

                return executeMethod.Invoke(null, new object[] { doc, parameters });
            }
        }

        public string GetName()
        {
            return "执行AI代码";
        }
    }

    // 执行结果数据结构
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("policy")]
        public string Policy { get; set; }

        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }

        [JsonProperty("grouped")]
        public bool Grouped { get; set; }

        [JsonProperty("durationMs")]
        public long DurationMs { get; set; }

        [JsonProperty("changes")]
        public List<DocumentChangeSummary> Changes { get; set; }
    }
}
