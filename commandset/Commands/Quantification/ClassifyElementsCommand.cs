using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Quantification;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Quantification
{
    /// <summary>
    /// Assigns Assembly Code to element types and derives a missing Type Mark.
    /// </summary>
    public class ClassifyElementsCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private ClassifyElementsEventHandler _handler => (ClassifyElementsEventHandler)Handler;

        public override string CommandName => "classify_elements";

        public ClassifyElementsCommand(UIApplication uiApp)
            : base(new ClassifyElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    string documentTitle = parameters?["documentTitle"]?.Value<string>();
                    bool deriveTypeMark = parameters?["deriveTypeMark"]?.Value<bool>() ?? true;

                    var stripPrefixes = CommandParsing.ParseStrings(parameters?["stripPrefixes"] as JArray);
                    if (stripPrefixes.Count == 0)
                    {
                        stripPrefixes.Add("WAD_EXP+");
                        stripPrefixes.Add("WAD_");
                    }

                    var rules = new List<ClassificationRule>();
                    var raw = parameters?["rules"] as JArray;
                    if (raw != null)
                    {
                        foreach (JToken token in raw)
                        {
                            var entry = token as JObject;
                            if (entry == null)
                                continue;

                            string code = entry["assemblyCode"]?.Value<string>();
                            if (string.IsNullOrWhiteSpace(code))
                                continue;

                            rules.Add(new ClassificationRule
                            {
                                Categories = CommandParsing.ParseCategories(
                                    entry["categories"] as JArray,
                                    new List<BuiltInCategory>()),
                                TypeNameContains = entry["typeNameContains"]?.Value<string>(),
                                TypeNameStartsWith = entry["typeNameStartsWith"]?.Value<string>(),
                                AssemblyCode = code
                            });
                        }
                    }

                    if (rules.Count == 0)
                        throw new ArgumentException("At least one rule with an assemblyCode is required");

                    _handler.SetParameters(documentTitle, rules, deriveTypeMark, stripPrefixes);

                    if (!RaiseAndWaitForCompletion(120000))
                        throw new TimeoutException("Timed out while classifying elements");

                    return _handler.Result;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to classify elements: " + ex.Message);
                }
            }
        }
    }
}
