using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Quantification
{
    /// <summary>
    /// Writes a quantification level onto elements.
    ///
    /// A slab is poured at the elevation of the level above the storey it roofs, so for
    /// take-off the slab and its beams belong to the storey underneath. This handler writes
    /// the level immediately below the one Revit assigns, which is the level being roofed.
    /// Anything classified as substructure gets a fixed text instead.
    /// </summary>
    public class AssignBimLevelEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DocumentTitle { get; private set; }
        public string ParameterName { get; private set; }
        public List<BuiltInCategory> Categories { get; private set; }
        public string FoundationText { get; private set; }
        public string FoundationCodePrefix { get; private set; }
        public List<string> MergeLevels { get; private set; }
        public bool NumberPrefix { get; private set; }

        public AIResult<Dictionary<string, object>> Result { get; private set; }

        public void SetParameters(
            string documentTitle,
            string parameterName,
            List<BuiltInCategory> categories,
            string foundationText,
            string foundationCodePrefix,
            List<string> mergeLevels,
            bool numberPrefix)
        {
            DocumentTitle = documentTitle;
            ParameterName = string.IsNullOrWhiteSpace(parameterName) ? "BIM_Level" : parameterName;
            Categories = categories;
            FoundationText = string.IsNullOrWhiteSpace(foundationText) ? "CIMENTACION" : foundationText;
            FoundationCodePrefix = foundationCodePrefix;
            MergeLevels = mergeLevels ?? new List<string>();
            NumberPrefix = numberPrefix;
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

                // Levels that only mark a reference height, such as a pavement datum between
                // two storeys, would otherwise become the "level below" and shift a whole floor.
                var chain = new List<Level>();
                var merged = new Dictionary<long, Level>();
                Level previous = null;
                foreach (Level level in QuantificationUtils.GetLevelsByElevation(doc))
                {
                    bool skip = false;
                    foreach (string name in MergeLevels)
                    {
                        if (string.Equals(level.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            skip = true;
                            break;
                        }
                    }

                    if (skip)
                    {
                        if (previous != null)
                            merged[level.Id.GetValue()] = previous;
                        continue;
                    }

                    chain.Add(level);
                    previous = level;
                }

                // A merged level maps onto the closest kept level below it; when it is the
                // lowest of all, map it onto the first kept level above instead.
                foreach (Level level in QuantificationUtils.GetLevelsByElevation(doc))
                {
                    if (merged.ContainsKey(level.Id.GetValue()) || chain.Contains(level))
                        continue;
                    if (chain.Count > 0)
                        merged[level.Id.GetValue()] = chain[0];
                }

                var index = new Dictionary<long, int>();
                for (int i = 0; i < chain.Count; i++)
                    index[chain[i].Id.GetValue()] = i;

                var counts = new Dictionary<string, int>();
                int written = 0, foundation = 0, lowest = 0, noLevel = 0, noParameter = 0;

                var failures = new TransactionFailureHandler();
                using (var transaction = new Transaction(doc, "Assign quantification level"))
                {
                    transaction.Start();
                    failures.Attach(transaction);

                    foreach (BuiltInCategory category in Categories)
                    {
                        var elements = new FilteredElementCollector(doc)
                            .OfCategory(category)
                            .WhereElementIsNotElementType()
                            .ToElements();

                        foreach (Element element in elements)
                        {
                            Parameter target = element.LookupParameter(ParameterName);
                            if (target == null || target.IsReadOnly)
                            {
                                noParameter++;
                                continue;
                            }

                            string value = null;

                            if (!string.IsNullOrWhiteSpace(FoundationCodePrefix))
                            {
                                string code = QuantificationUtils.GetTypeText(doc, element, "Assembly Code");
                                if (code != null && code.StartsWith(FoundationCodePrefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    value = FoundationText;
                                    foundation++;
                                }
                            }

                            if (value == null)
                            {
                                Level own = GetLevel(doc, element);
                                if (own == null)
                                {
                                    noLevel++;
                                    continue;
                                }

                                if (merged.ContainsKey(own.Id.GetValue()))
                                    own = merged[own.Id.GetValue()];

                                if (own == null || !index.ContainsKey(own.Id.GetValue()))
                                {
                                    noLevel++;
                                    continue;
                                }

                                int position = index[own.Id.GetValue()];
                                if (position == 0)
                                {
                                    // Nothing below the lowest level, so it keeps its own name.
                                    value = own.Name;
                                    lowest++;
                                }
                                else
                                {
                                    value = chain[position - 1].Name;
                                }
                            }

                            target.Set(value);
                            written++;

                            if (!counts.ContainsKey(value))
                                counts[value] = 0;
                            counts[value]++;
                        }
                    }

                    if (NumberPrefix)
                        ApplyNumberPrefix(doc, chain, counts);

                    transaction.Commit();
                }

                var payload = new Dictionary<string, object>
                {
                    { "document", doc.Title },
                    { "parameter", ParameterName },
                    { "written", written },
                    { "foundation", foundation },
                    { "lowestLevelKeptOwnName", lowest },
                    { "withoutLevel", noLevel },
                    { "withoutParameter", noParameter },
                    { "byValue", counts }
                };

                Result = new AIResult<Dictionary<string, object>>
                {
                    Success = true,
                    Message = "Wrote " + ParameterName + " on " + written + " elements in " + doc.Title +
                              failures.Summarize(),
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

        /// <summary>
        /// Renames the written values so they sort from the lowest level up, because a
        /// schedule grouped by a text parameter sorts alphabetically otherwise.
        /// </summary>
        private void ApplyNumberPrefix(Document doc, List<Level> chain, Dictionary<string, int> counts)
        {
            var order = new List<string>();
            order.Add(FoundationText);
            foreach (Level level in chain)
            {
                if (counts.ContainsKey(level.Name) && !order.Contains(level.Name))
                    order.Add(level.Name);
            }

            var prefixes = new Dictionary<string, string>();
            int n = 0;
            foreach (string name in order)
            {
                if (!counts.ContainsKey(name))
                    continue;
                n++;
                prefixes[name] = n.ToString("00") + ". " + name;
            }

            foreach (BuiltInCategory category in Categories)
            {
                var elements = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToElements();

                foreach (Element element in elements)
                {
                    Parameter target = element.LookupParameter(ParameterName);
                    if (target == null || target.IsReadOnly)
                        continue;

                    string current = StripPrefix(target.AsString());
                    if (current != null && prefixes.ContainsKey(current))
                        target.Set(prefixes[current]);
                }
            }

            var renamed = new Dictionary<string, int>();
            foreach (var pair in prefixes)
                renamed[pair.Value] = counts[pair.Key];

            counts.Clear();
            foreach (var pair in renamed)
                counts[pair.Key] = pair.Value;
        }

        /// <summary>Removes an existing "01. " prefix so the command stays idempotent.</summary>
        public static string StripPrefix(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            int i = 0;
            while (i < value.Length && char.IsDigit(value[i]))
                i++;

            if (i > 0 && i < value.Length && value[i] == '.')
            {
                int j = i + 1;
                while (j < value.Length && value[j] == ' ')
                    j++;
                return value.Substring(j);
            }

            return value;
        }

        private static Level GetLevel(Document doc, Element element)
        {
            var candidates = new BuiltInParameter[]
            {
                BuiltInParameter.LEVEL_PARAM,
                BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.FAMILY_BASE_LEVEL_PARAM
            };

            foreach (BuiltInParameter bip in candidates)
            {
                Parameter param = element.get_Parameter(bip);
                if (param == null || param.StorageType != StorageType.ElementId)
                    continue;

                ElementId id = param.AsElementId();
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                var level = doc.GetElement(id) as Level;
                if (level != null)
                    return level;
            }

            return null;
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
            return "Assign quantification level";
        }
    }
}
