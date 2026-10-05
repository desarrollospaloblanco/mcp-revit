using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>One classification rule: which types it matches and the code it writes.</summary>
    public class ClassificationRule
    {
        public List<BuiltInCategory> Categories = new List<BuiltInCategory>();
        public string TypeNameContains;
        public string TypeNameStartsWith;
        public string AssemblyCode;
    }

    /// <summary>
    /// Writes Assembly Code onto element types, and fills in a missing Type Mark from the
    /// type name.
    ///
    /// Rules are evaluated in order and the first match wins, so put the specific ones first.
    /// Only types that are actually placed in the model are touched, which keeps unused
    /// library types out of the take-off.
    /// </summary>
    public class ClassifyElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public List<ClassificationRule> Rules { get; private set; }
        public bool DeriveTypeMark { get; private set; }
        public List<string> StripPrefixes { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(
            string documentTitle,
            List<ClassificationRule> rules,
            bool deriveTypeMark,
            List<string> stripPrefixes)
        {
            DocumentTitle = documentTitle;
            Rules = rules ?? new List<ClassificationRule>();
            DeriveTypeMark = deriveTypeMark;
            StripPrefixes = stripPrefixes;
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

                // Only types with instances in the model.
                var usedTypes = new Dictionary<long, ElementType>();
                foreach (ClassificationRule rule in Rules)
                {
                    foreach (BuiltInCategory category in rule.Categories)
                    {
                        var elements = new FilteredElementCollector(doc)
                            .OfCategory(category)
                            .WhereElementIsNotElementType()
                            .ToElements();

                        foreach (Element element in elements)
                        {
                            var type = doc.GetElement(element.GetTypeId()) as ElementType;
                            if (type == null)
                                continue;
                            long key = Utils.ElementIdExtensions.GetValue(type.Id);
                            if (!usedTypes.ContainsKey(key))
                                usedTypes[key] = type;
                        }
                    }
                }

                int coded = 0, marked = 0, notDerivable = 0, unmatched = 0;
                var detail = new List<string>();
                var unresolved = new List<string>();

                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Classify elements"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    foreach (var pair in usedTypes)
                    {
                        ElementType type = pair.Value;
                        ClassificationRule rule = FindRule(type);
                        if (rule == null)
                        {
                            unmatched++;
                            continue;
                        }

                        Parameter code = type.LookupParameter("Assembly Code");
                        if (code != null && !code.IsReadOnly)
                        {
                            code.Set(rule.AssemblyCode);
                            coded++;
                        }

                        if (DeriveTypeMark)
                        {
                            Parameter mark = type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                            if (mark != null && !mark.IsReadOnly && string.IsNullOrWhiteSpace(mark.AsString()))
                            {
                                string derived = DeriveMark(type.Name, StripPrefixes);
                                if (string.IsNullOrEmpty(derived))
                                {
                                    notDerivable++;
                                    unresolved.Add(type.Name);
                                }
                                else
                                {
                                    mark.Set(derived);
                                    marked++;
                                }
                            }
                        }

                        // Assembly Description is filled by Revit from the loaded classification
                        // file. An empty one means the code is not in that file.
                        Parameter description = type.LookupParameter("Assembly Description");
                        string resolved = description == null ? null : description.AsString();
                        if (string.IsNullOrWhiteSpace(resolved))
                            unresolved.Add(type.Name + " -> " + rule.AssemblyCode + " (code not in the classification file)");
                        else if (detail.Count < 60)
                            detail.Add(type.Name + " -> " + rule.AssemblyCode + " = " + resolved);
                    }

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "typesInUse", usedTypes.Count },
                    { "codesWritten", coded },
                    { "typeMarksDerived", marked },
                    { "typeMarkNotDerivable", notDerivable },
                    { "typesWithoutRule", unmatched },
                    { "assignments", detail },
                    { "warnings", unresolved }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Classified " + coded + " types in " + doc.Title + failures.Summarize(),
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

        private ClassificationRule FindRule(ElementType type)
        {
            if (type.Category == null)
                return null;

            long categoryId = Utils.ElementIdExtensions.GetValue(type.Category.Id);

            foreach (ClassificationRule rule in Rules)
            {
                bool categoryMatches = false;
                foreach (BuiltInCategory category in rule.Categories)
                {
                    if ((long)category == categoryId)
                    {
                        categoryMatches = true;
                        break;
                    }
                }

                if (!categoryMatches)
                    continue;

                if (!string.IsNullOrEmpty(rule.TypeNameStartsWith) &&
                    !type.Name.StartsWith(rule.TypeNameStartsWith, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(rule.TypeNameContains) &&
                    type.Name.IndexOf(rule.TypeNameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                return rule;
            }

            return null;
        }

        /// <summary>
        /// Pulls the mark out of a type name: strips a known prefix, then takes everything up
        /// to the first underscore. "WAD_EXP+MI-11_0.15m" becomes "MI-11".
        /// </summary>
        public static string DeriveMark(string typeName, List<string> stripPrefixes)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            string name = typeName;
            if (stripPrefixes != null)
            {
                foreach (string prefix in stripPrefixes)
                {
                    if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring(prefix.Length);
                        break;
                    }
                }
            }

            int separator = name.IndexOf('_');
            if (separator <= 0)
                return null;

            string mark = name.Substring(0, separator).Trim();
            return string.IsNullOrEmpty(mark) ? null : mark;
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
            return "Classify elements";
        }
    }
}
