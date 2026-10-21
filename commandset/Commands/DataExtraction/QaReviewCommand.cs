using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    /// Model QA across open documents: orphan tags (tags whose element is gone) and the model
    /// warnings grouped by text. Orphan tags can be reported, deleted, or re-hosted on the nearest
    /// element of the tagged category in the same view.
    /// </summary>
    public class QaReviewCommand : DelegateCommand
    {
        private static readonly Dictionary<string, BuiltInCategory> TagToElement = new Dictionary<string, BuiltInCategory>
        {
            { "OST_DuctTags", BuiltInCategory.OST_DuctCurves },
            { "OST_PipeTags", BuiltInCategory.OST_PipeCurves },
            { "OST_DuctFittingTags", BuiltInCategory.OST_DuctFitting },
            { "OST_PipeFittingTags", BuiltInCategory.OST_PipeFitting },
            { "OST_ConduitTags", BuiltInCategory.OST_Conduit },
            { "OST_CableTrayTags", BuiltInCategory.OST_CableTray },
            { "OST_DuctTerminalTags", BuiltInCategory.OST_DuctTerminal },
        };

        public override string CommandName => "qa_review";

        public QaReviewCommand(UIApplication uiApp) : base(uiApp, "MCP: control de calidad")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            var titles = (parameters?["documents"] as JArray)?.Select(t => t.Value<string>()).ToList();
            var checks = (parameters?["checks"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                         ?? new List<string> { "orphan_tags", "warnings" };
            string fix = (parameters?["fixOrphans"]?.Value<string>() ?? "none").ToLowerInvariant();
            double reach = Metric.Ft(parameters?["reassignWithinMeters"]?.Value<double>() ?? 1.5);
            int limit = Math.Max(1, parameters?["limit"]?.Value<int>() ?? 200);

            return RunInRevit(app =>
            {
                var docs = titles == null || titles.Count == 0 ? ToolDocuments.AllProjects(app) : ToolDocuments.ResolveMany(app, titles);
                var reports = new List<object>();
                foreach (Document doc in docs)
                {
                    var report = new JObject { ["document"] = doc.Title };
                    if (checks.Contains("orphan_tags"))
                        report["orphanTags"] = JToken.FromObject(OrphanTags(doc, fix, reach, limit, options));
                    if (checks.Contains("warnings"))
                        report["warnings"] = JToken.FromObject(Warnings(doc, limit));
                    reports.Add(report);
                }
                return new { documents = reports };
            }, 900);
        }

        private class Orphan
        {
            public Element Tag;
            public View View;
            public string Kind;
        }

        private static object OrphanTags(Document doc, string fix, double reach, int limit, ToolOptions options)
        {
            var orphans = new List<Orphan>();
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(SpatialElementTag)).WhereElementIsNotElementType())
            {
                bool lost = e is RoomTag rt ? rt.Room == null
                    : e is Autodesk.Revit.DB.AreaTag at ? at.Area == null
                    : e is Autodesk.Revit.DB.Mechanical.SpaceTag st && st.Space == null;
                if (lost) orphans.Add(new Orphan { Tag = e, View = doc.GetElement(e.OwnerViewId) as View, Kind = "spatial" });
            }
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                if (TaggedIds(tag).Count == 0)
                    orphans.Add(new Orphan { Tag = tag, View = doc.GetElement(tag.OwnerViewId) as View, Kind = "element" });
            }

            int deleted = 0, reassigned = 0;
            var notReassigned = new List<long>();
            var warnings = new List<string>();
            if (orphans.Count > 0 && (fix == "delete" || fix == "reassign"))
            {
                ToolTransaction.Run(doc, "MCP: etiquetas huérfanas", options, warnings, () =>
                {
                    foreach (Orphan o in orphans)
                    {
                        if (fix == "reassign" && o.Kind == "element" && Reassign(doc, (IndependentTag)o.Tag, o.View, reach))
                        {
                            reassigned++;
                            continue;
                        }
                        if (fix == "reassign") notReassigned.Add(Utils.ElementIdExtensions.GetValue(o.Tag.Id));
                        if (fix == "delete") { doc.Delete(o.Tag.Id); deleted++; }
                    }
                });
            }

            return new
            {
                count = orphans.Count,
                byCategory = orphans.GroupBy(o => o.Tag.Category?.Name ?? "?").ToDictionary(g => g.Key, g => g.Count()),
                byView = orphans.GroupBy(o => o.View?.Name ?? "?").OrderByDescending(g => g.Count()).Take(30).ToDictionary(g => g.Key, g => g.Count()),
                tags = fix == "none" ? orphans.Take(limit).Select(o => new
                {
                    id = Utils.ElementIdExtensions.GetValue(o.Tag.Id),
                    category = o.Tag.Category?.Name,
                    view = o.View?.Name
                }) : null,
                deleted,
                reassigned,
                notReassigned = notReassigned.Take(limit),
                warnings
            };
        }

        private static ICollection<ElementId> TaggedIds(IndependentTag tag)
        {
#if REVIT2022_OR_GREATER
            return tag.GetTaggedLocalElementIds().Where(id => id != ElementId.InvalidElementId).ToList();
#else
            ElementId id = tag.TaggedLocalElementId;
            return id == ElementId.InvalidElementId ? new List<ElementId>() : new List<ElementId> { id };
#endif
        }

        /// <summary>
        /// Replaces the tag with a new one of the same type on the nearest element of the tagged
        /// category visible in its view, keeping the head position.
        /// </summary>
        private static bool Reassign(Document doc, IndependentTag tag, View view, double reach)
        {
            if (view == null || tag.Category == null) return false;
            string tagCategory = ((BuiltInCategory)Utils.ElementIdExtensions.GetValue(tag.Category.Id)).ToString();
            if (!TagToElement.TryGetValue(tagCategory, out BuiltInCategory target) &&
                !Enum.TryParse(tagCategory.EndsWith("Tags") ? tagCategory.Substring(0, tagCategory.Length - 4) : "", out target))
                return false;

            XYZ head = tag.TagHeadPosition;
            Element nearest = null;
            double best = reach;
            foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategory(target).WhereElementIsNotElementType())
            {
                XYZ p = (e.Location as LocationPoint)?.Point
                        ?? ((e.Location as LocationCurve)?.Curve.Project(head)?.XYZPoint);
                if (p == null) continue;
                double d = new XYZ(p.X - head.X, p.Y - head.Y, 0).GetLength();
                if (d < best) { best = d; nearest = e; }
            }
            if (nearest == null) return false;

            ElementId typeId = tag.GetTypeId();
            bool leader = tag.HasLeader;
            TagOrientation orientation = tag.TagOrientation;
            doc.Delete(tag.Id);
            IndependentTag created = IndependentTag.Create(doc, typeId, view.Id, new Reference(nearest), leader, orientation, head);
            return created != null;
        }

        private static object Warnings(Document doc, int limit)
        {
            IList<FailureMessage> all = doc.GetWarnings();
            return new
            {
                count = all.Count,
                groups = all.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(limit).Select(g => new
                {
                    text = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    elementIds = g.SelectMany(w => w.GetFailingElements()).Distinct().Take(50)
                        .Select(id => Utils.ElementIdExtensions.GetValue(id))
                })
            };
        }
    }
}
