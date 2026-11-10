using System.Text.RegularExpressions;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Common;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Services.Modeling;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>
    /// Moves a set of levels by one offset and renames levels together with their plan views: what a
    /// floor that grows taller or a renumbering of the stack needs. Everything is checked before
    /// anything changes, and the result lists the walls and columns that will not follow the levels.
    /// </summary>
    public class ShiftLevelsCommand : DelegateCommand
    {
        private const double SelectToleranceMeters = 0.005;
        private const double ClashToleranceMeters = 0.01;
        private const int MaxIds = 50;
        private const string TempPrefix = "zz_mcp_shift_";
        private static readonly char[] ForbiddenNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
        // Schema of SpecTagStorage (build_model_from_spec); its GUID is private there.
        private static readonly Guid SpecSchemaGuid = new Guid("6b1d2f9e-4c1a-4f0e-9a57-3f8e2c7d5b10");

        public override string CommandName => "shift_levels";

        public ShiftLevelsCommand(UIApplication uiApp) : base(uiApp, "MCP: desplazar niveles")
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var options = ToolOptions.From(parameters);
            double? from = parameters?["fromElevationMeters"]?.Value<double?>();
            var names = (parameters?["levels"] as JArray)?.Select(t => t.Value<string>()).ToList();
            double offset = parameters?["offsetMeters"]?.Value<double>() ?? 0;
            var renames = (parameters?["renames"] as JObject)?.Properties()
                              .ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal)
                          ?? new Dictionary<string, string>(StringComparer.Ordinal);
            bool renameViews = parameters?["renameViews"]?.Value<bool>() ?? true;
            bool dryRun = parameters?["dryRun"]?.Value<bool>() ?? false;
            if (from != null && names != null)
                throw new ArgumentException("give fromElevationMeters or levels, not both");

            return RunInRevit(app =>
            {
                Document doc = options.Document(app);
                var all = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.ProjectElevation).ToList();
                var byName = all.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var problems = new List<string>();

                List<Level> moved = SelectLevels(all, byName, from, names, problems);
                if (Math.Abs(offset) <= 1e-9) moved.Clear();
                var movedIds = new HashSet<ElementId>(moved.Select(l => l.Id));
                var newNames = PlanRenames(all, byName, renames, problems);
                if (Math.Abs(offset) > 1e-9 && moved.Count == 0 && problems.Count == 0)
                    problems.Add("offsetMeters is not 0 but no level was selected to move");
                if (Math.Abs(offset) <= 1e-9 && newNames.Count == 0 && problems.Count == 0)
                    problems.Add("nothing to do: offsetMeters is 0 and no rename changes a name");
                if (Math.Abs(offset) > 1e-9)
                    CheckClashes(all, movedIds, Metric.Ft(offset), problems);
                var viewRenames = renameViews ? PlanViewRenames(doc, newNames, problems) : new List<ViewRename>();
                if (problems.Count > 0)
                    throw new InvalidOperationException("nothing was changed: " + string.Join(" | ", problems));

                var before = all.ToDictionary(l => l.Id, l => (name: l.Name, elevation: Metric.M(l.Elevation)));
                var report = Math.Abs(offset) > 1e-9 ? NotFollowing(doc, all, movedIds, Metric.Ft(offset)) : null;
                var warnings = new List<string>();
                if (newNames.Count > 0) SpecKeyWarning(doc, newNames, before, warnings);

                ToolTransaction.Run(doc, "MCP: desplazar niveles", options, warnings, () =>
                {
                    // Temporary names first, so renumbering N2->N4 while N4->N6 never collides.
                    foreach (Level l in all.Where(l => newNames.ContainsKey(l.Id))) l.Name = TempPrefix + ElementSelection.Id(l);
                    foreach (ViewRename v in viewRenames) v.View.Name = TempPrefix + ElementSelection.Id(v.View);

                    // Top-down when raising, bottom-up when lowering, so no level passes through another on the way.
                    double d = Metric.Ft(offset);
                    var order = d > 0 ? moved.OrderByDescending(l => l.ProjectElevation) : moved.OrderBy(l => l.ProjectElevation);
                    foreach (Level l in order.ToList()) l.Elevation = l.Elevation + d;

                    foreach (Level l in all.Where(l => newNames.ContainsKey(l.Id))) l.Name = newNames[l.Id];
                    foreach (ViewRename v in viewRenames) v.View.Name = v.After;
                }, dryRun);

                var levels = all.OrderBy(l => l.ProjectElevation).Select(l => new
                {
                    id = ElementSelection.Id(l),
                    nameBefore = before[l.Id].name,
                    nameAfter = newNames.TryGetValue(l.Id, out string n) ? n : before[l.Id].name,
                    elevationBefore = before[l.Id].elevation,
                    elevationAfter = movedIds.Contains(l.Id) ? Math.Round(before[l.Id].elevation + offset, 4) : before[l.Id].elevation,
                    moved = movedIds.Contains(l.Id),
                    renamed = newNames.ContainsKey(l.Id)
                }).ToList();
                return new
                {
                    document = doc.Title,
                    dryRun,
                    offsetMeters = offset,
                    movedCount = moved.Count,
                    renamedCount = newNames.Count,
                    levels,
                    viewsRenamed = viewRenames.Select(v => new { id = ElementSelection.Id(v.View), viewType = v.View.ViewType.ToString(), before = v.Before, after = v.After }).ToList(),
                    notFollowing = report,
                    warnings
                };
            }, 900);
        }

        private static List<Level> SelectLevels(List<Level> all, Dictionary<string, Level> byName, double? from, List<string> names, List<string> problems)
        {
            if (from != null)
            {
                double min = from.Value - SelectToleranceMeters;
                return all.Where(l => l.Elevation * 0.3048 >= min).ToList();
            }
            if (names == null) return new List<Level>();
            var unknown = names.Where(n => n == null || !byName.ContainsKey(n)).ToList();
            if (unknown.Count > 0) problems.Add("unknown levels: " + string.Join(", ", unknown.Select(n => n ?? "(null)")));
            return names.Where(n => n != null && byName.ContainsKey(n)).Distinct().Select(n => byName[n]).ToList();
        }

        /// <summary>New name per level id, only for renames that actually change a name.</summary>
        private static Dictionary<ElementId, string> PlanRenames(List<Level> all, Dictionary<string, Level> byName, Dictionary<string, string> renames, List<string> problems)
        {
            var result = new Dictionary<ElementId, string>();
            foreach (var kv in renames)
            {
                if (!byName.TryGetValue(kv.Key, out Level level)) { problems.Add($"rename of unknown level '{kv.Key}'"); continue; }
                string target = kv.Value?.Trim();
                if (string.IsNullOrEmpty(target)) { problems.Add($"rename of '{kv.Key}' has an empty name"); continue; }
                if (target.IndexOfAny(ForbiddenNameChars) >= 0) { problems.Add($"'{target}' has a character Revit does not allow in names (\\ : {{ }} [ ] | ; < > ? ` ~)"); continue; }
                if (target != kv.Key) result[level.Id] = target;
            }
            var duplicates = all.Select(l => result.TryGetValue(l.Id, out string n) ? n : l.Name)
                .GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0) problems.Add("two levels would share a name: " + string.Join(", ", duplicates));
            return result;
        }

        /// <summary>A moved level may not land on, or pass through, a level that stays.</summary>
        private static void CheckClashes(List<Level> all, HashSet<ElementId> movedIds, double offsetFeet, List<string> problems)
        {
            double tol = Metric.Ft(ClashToleranceMeters);
            var fixedLevels = all.Where(l => !movedIds.Contains(l.Id)).ToList();
            foreach (Level m in all.Where(l => movedIds.Contains(l.Id)))
            {
                double z = m.ProjectElevation, nz = z + offsetFeet;
                foreach (Level f in fixedLevels)
                {
                    double fz = f.ProjectElevation;
                    if (Math.Abs(nz - fz) < tol)
                        problems.Add($"'{m.Name}' would land {Metric.M(Math.Abs(nz - fz)):0.###} m from '{f.Name}', which does not move");
                    else if (Math.Abs(z - fz) >= tol && Math.Sign(z - fz) != Math.Sign(nz - fz))
                        problems.Add($"'{m.Name}' would cross '{f.Name}', which does not move");
                }
            }
        }

        private class ViewRename
        {
            public ViewPlan View;
            public string Before;
            public string After;
        }

        /// <summary>
        /// Plan views of a renamed level whose name is the level name, or starts with it followed by
        /// a non-alphanumeric character ("N2 - Oficinas"), so "N20" is never taken for "N2".
        /// </summary>
        private static List<ViewRename> PlanViewRenames(Document doc, Dictionary<ElementId, string> newNames, List<string> problems)
        {
            var result = new List<ViewRename>();
            if (newNames.Count == 0) return result;
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).ToList();
            foreach (ViewPlan v in plans)
            {
                Level level = v.GenLevel;
                if (level == null || !newNames.TryGetValue(level.Id, out string target)) continue;
                string old = level.Name, name = v.Name;
                if (!name.StartsWith(old, StringComparison.Ordinal)) continue;
                if (name.Length > old.Length && char.IsLetterOrDigit(name[old.Length])) continue;
                result.Add(new ViewRename { View = v, Before = name, After = target + name.Substring(old.Length) });
            }

            // Revit keeps view names unique within a view type.
            var renamedIds = new HashSet<ElementId>(result.Select(r => r.View.Id));
            foreach (var group in result.GroupBy(r => r.View.ViewType))
            {
                var taken = new HashSet<string>(plans.Where(v => v.ViewType == group.Key && !renamedIds.Contains(v.Id)).Select(v => v.Name), StringComparer.Ordinal);
                foreach (var clash in group.GroupBy(r => r.After, StringComparer.Ordinal))
                {
                    if (taken.Contains(clash.Key))
                        problems.Add($"{group.Key} view '{clash.First().Before}' would be renamed '{clash.Key}', which another view already uses (rename it first or pass renameViews: false)");
                    else if (clash.Count() > 1)
                        problems.Add($"{clash.Count()} {group.Key} views would all be renamed '{clash.Key}'");
                }
            }
            return result;
        }

        private class Group
        {
            public string Category;
            public string Type;
            public string BaseLevel;
            public string TopLevel;
            public int Count;
        }

        private class Bucket
        {
            public readonly Dictionary<string, Group> Groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            public readonly List<long> Ids = new List<long>();
            public int Count;

            public void Add(Element e, string category, string type, string baseLevel, string topLevel)
            {
                string key = category + "|" + type + "|" + baseLevel + "|" + topLevel;
                if (!Groups.TryGetValue(key, out Group g))
                    Groups[key] = g = new Group { Category = category, Type = type, BaseLevel = baseLevel, TopLevel = topLevel };
                g.Count++;
                Count++;
                if (Ids.Count < MaxIds) Ids.Add(ElementSelection.Id(e));
            }

            public object Report(string note) => new
            {
                count = Count,
                note,
                groups = Groups.Values.OrderByDescending(g => g.Count).Select(g => new { category = g.Category, type = g.Type, baseLevel = g.BaseLevel, topLevel = g.TopLevel, count = g.Count }).ToList(),
                ids = Ids
            };
        }

        /// <summary>
        /// Walls and columns whose height is not carried by the move: unconnected walls based on a level
        /// that stays and reaching a moved level, and walls or columns with one end on a moved level and
        /// the other on a level that stays.
        /// </summary>
        private static object NotFollowing(Document doc, List<Level> all, HashSet<ElementId> movedIds, double offsetFeet)
        {
            double tol = Metric.Ft(ClashToleranceMeters);
            var byId = all.ToDictionary(l => l.Id);
            var movedZ = all.Where(l => movedIds.Contains(l.Id)).Select(l => l.ProjectElevation).ToList();
            var fixedHeight = new Bucket();
            var spanning = new Bucket();
            string LevelName(ElementId id) => id != null && byId.TryGetValue(id, out Level l) ? l.Name : null;

            foreach (Wall w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (w.IsStackedWallMember) continue;
                ElementId baseId = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? w.LevelId;
                if (baseId == null || !byId.TryGetValue(baseId, out Level baseLevel)) continue;
                ElementId topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                string type = w.WallType?.Name ?? "?";
                if (topId == ElementId.InvalidElementId || !byId.ContainsKey(topId))
                {
                    if (movedIds.Contains(baseId)) continue;
                    double baseZ = baseLevel.ProjectElevation + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
                    double topZ = baseZ + (w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0);
                    // Reaching the level where it is, or where it will be when lowering.
                    if (movedZ.Any(z => baseZ < z - tol && topZ > Math.Min(z, z + offsetFeet) - tol))
                        fixedHeight.Add(w, "Walls", type, baseLevel.Name, null);
                }
                else if (movedIds.Contains(baseId) != movedIds.Contains(topId))
                    spanning.Add(w, "Walls", type, baseLevel.Name, LevelName(topId));
            }

            var columnFilter = new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns });
            foreach (FamilyInstance c in new FilteredElementCollector(doc).WherePasses(columnFilter).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                ElementId baseId = c.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
                ElementId topId = c.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId();
                if (baseId == null || topId == null || !byId.ContainsKey(baseId) || !byId.ContainsKey(topId)) continue;
                if (movedIds.Contains(baseId) == movedIds.Contains(topId)) continue;
                string category = c.Category?.Name ?? "Columns";
                string type = c.Symbol != null ? c.Symbol.FamilyName + ": " + c.Symbol.Name : "?";
                spanning.Add(c, category, type, LevelName(baseId), LevelName(topId));
            }

            return new
            {
                fixedHeight = fixedHeight.Report("Walls with unconnected height, based on a level that stays, reaching a moved level: they keep their height and will not follow. Give them a top constraint or adjust the height."),
                heightChanges = spanning.Report("Walls and columns with one end on a moved level and the other on a level that stays: they follow, but their height changes by the offset. After a renumbering check that their top level is still the one they should stop at.")
            };
        }

        /// <summary>build_model_from_spec stamps keep level names in their keys ('level:N2', 'wall@N2'); they are not rewritten.</summary>
        private static void SpecKeyWarning(Document doc, Dictionary<ElementId, string> newNames, Dictionary<ElementId, (string name, double elevation)> before, List<string> warnings)
        {
            var oldNames = newNames.Keys.Select(id => before[id].name).ToList();
            var patterns = oldNames.ToDictionary(n => n, n => new Regex("(?<=[:@])" + Regex.Escape(n) + "(?![A-Za-z0-9])"), StringComparer.Ordinal);
            var counts = oldNames.ToDictionary(n => n, n => 0, StringComparer.Ordinal);
            var stamped = new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SpecSchemaGuid)).WhereElementIsNotElementType().ToElements()
                .Concat(new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SpecSchemaGuid)).WhereElementIsElementType().ToElements());
            foreach (Element e in stamped)
            {
                string key = SpecTagStorage.Read(e)?.Key;
                if (string.IsNullOrEmpty(key)) continue;
                foreach (var p in patterns)
                    if (p.Value.IsMatch(key)) counts[p.Key]++;
            }
            var hits = counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} ({kv.Value})").ToList();
            if (hits.Count > 0)
                warnings.Add("build_model_from_spec keys still use the old level names and were not rewritten: " + string.Join(", ", hits) +
                             ". A later build_model_from_spec run with the new names will not match these elements.");
        }
    }
}
