using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// Builds a model spec into the target document as a single undo step.
    ///
    /// The whole build runs inside a transaction group. A dry run rolls the group back at the
    /// end, which validates the spec against the real document — types, levels, and every
    /// failure Revit would raise — without leaving anything behind.
    /// </summary>
    public class BuildModelFromSpecEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private string _documentTitle;
        private ModelSpec _spec;
        private string _specName;
        private List<string> _stages;
        private bool _dryRun;
        private bool _deleteMissing;
        private string _reportPath;

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(string documentTitle, ModelSpec spec, string specName, List<string> stages,
            bool dryRun, bool deleteMissing, string reportPath)
        {
            _documentTitle = documentTitle;
            _spec = spec;
            _specName = specName;
            _stages = stages;
            _dryRun = dryRun;
            _deleteMissing = deleteMissing;
            _reportPath = reportPath;
            Result = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 600000)
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

                var clock = Stopwatch.StartNew();
                var builder = new ModelBuilder(doc, _spec.Types, _specName);

                using (var group = new TransactionGroup(doc, "Build model from spec '" + _specName + "'"))
                {
                    group.Start();
                    builder.Build(_spec, _stages, _deleteMissing);
                    if (_dryRun)
                        group.RollBack();
                    else
                        group.Assimilate();
                }

                BuildReport report = builder.Report;
                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "spec", _specName },
                    { "dryRun", _dryRun },
                    { "stages", _stages },
                    { "seconds", Math.Round(clock.Elapsed.TotalSeconds, 1) }
                };
                foreach (var pair in report.ToDictionary(false))
                    payload[pair.Key] = pair.Value;

                if (!string.IsNullOrWhiteSpace(_reportPath))
                {
                    var full = new Dictionary<string, object>(payload);
                    foreach (var pair in report.ToDictionary(true))
                        full[pair.Key] = pair.Value;
                    full["ids"] = report.Ids;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_reportPath)));
                    File.WriteAllText(_reportPath, JsonConvert.SerializeObject(full, Formatting.Indented));
                    payload["reportPath"] = Path.GetFullPath(_reportPath);
                }

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = (_dryRun ? "Dry run of" : "Built") + " spec '" + _specName + "' in " + doc.Title +
                              ": " + report.Problems.Count + " problem(s)" +
                              (_dryRun ? "; nothing was kept" : ""),
                    Response = payload
                };
            }
            catch (Exception ex)
            {
                Result = Fail("Build failed: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Build model from spec";
        }

        private static AIResult<Dictionary<string, object>> Fail(string message)
        {
            return new AIResult<Dictionary<string, object>> { Success = false, Message = message };
        }
    }
}
