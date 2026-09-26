using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Quantification;
using RevitMCPCommandSet.Services.Modeling;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Modeling
{
    /// <summary>
    /// Builds levels, grids, columns, walls, beams and floors from a spec in metres.
    /// </summary>
    public class BuildModelFromSpecCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private BuildModelFromSpecEventHandler _handler => (BuildModelFromSpecEventHandler)Handler;

        public override string CommandName => "build_model_from_spec";

        public BuildModelFromSpecCommand(UIApplication uiApp)
            : base(new BuildModelFromSpecEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    ModelSpec spec = ReadSpec(parameters);

                    string specName = parameters?["specName"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(specName))
                        specName = "spec";

                    List<string> stages = CommandParsing.ParseStrings(parameters?["stages"] as JArray)
                        .Select(s => s.Trim().ToLowerInvariant()).ToList();
                    if (stages.Count == 0)
                        stages = ModelBuilder.AllStages.ToList();
                    string unknown = string.Join(", ", stages.Where(s => !ModelBuilder.AllStages.Contains(s)));
                    if (unknown.Length > 0)
                        throw new ArgumentException("unknown stage(s): " + unknown + ". Valid: " + string.Join(", ", ModelBuilder.AllStages));

                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;
                    bool deleteMissing = parameters?["deleteMissing"]?.Value<bool>() ?? false;
                    string reportPath = parameters?["reportPath"]?.Value<string>();

                    _handler.SetParameters(documentTitle, spec, specName, stages, dryRun, deleteMissing, reportPath);

                    if (!RaiseAndWaitForCompletion(600000))
                        throw new TimeoutException("Timed out while building the model; the build may still be running in Revit");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to build model from spec: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The spec comes inline or from a JSON file. A file keeps a large building out of the
        /// request, and lets the same reviewed file be rebuilt without resending it.
        /// </summary>
        private static ModelSpec ReadSpec(JObject parameters)
        {
            JToken inline = parameters?["spec"];
            string path = parameters?["specPath"]?.Value<string>();

            if (inline != null && inline.Type == JTokenType.Object)
                return inline.ToObject<ModelSpec>() ?? new ModelSpec();

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("pass either spec or specPath");
            if (!File.Exists(path))
                throw new ArgumentException("specPath does not exist: " + path);

            return JsonConvert.DeserializeObject<ModelSpec>(File.ReadAllText(path)) ?? new ModelSpec();
        }
    }
}
