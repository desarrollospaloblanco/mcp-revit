using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modeling
{
    /// <summary>
    /// Builds a <see cref="ModelSpec"/> into a document, one stage per element kind.
    ///
    /// Stages run in dependency order — levels, grids, columns, walls, beams, floors — each in
    /// its own transaction, so a stage Revit refuses does not undo the stages before it. The
    /// caller wraps the whole build in a transaction group to make it one undo step, or to roll
    /// it all back for a dry run.
    ///
    /// Each entry is matched to the element built from it earlier through its stamp. An entry
    /// whose text has not changed since the last build is skipped outright; this is what keeps
    /// re-sending a 5,000-element spec cheap, and it also means a hand edit made in Revit
    /// survives until the spec entry itself changes.
    /// </summary>
    public class ModelBuilder
    {
        public static readonly string[] AllStages = { "levels", "grids", "columns", "walls", "beams", "floors" };

        private const double FeetPerMetre = 1.0 / 0.3048;
        private const double SameTolerance = 0.001 * FeetPerMetre;

        private readonly Document _doc;
        private readonly string _specName;
        private readonly BuildReport _report = new BuildReport();
        private readonly TypeResolver _types;
        private ModelSpec _spec;

        private Dictionary<string, Element> _tagged;
        private readonly Dictionary<string, Level> _levels = new Dictionary<string, Level>(StringComparer.Ordinal);
        private List<string> _levelOrder = new List<string>();
        private readonly Dictionary<string, string> _levelIdByName = new Dictionary<string, string>(StringComparer.Ordinal);

        private class Pending
        {
            public string Kind;
            public string Key;
            public ElementId Id;
            public string Action;
        }

        /// <summary>An element entry after repeatOn expansion, with its key and signature.</summary>
        private class Entry<T>
        {
            public string Key;
            public string Signature;
            public T Spec;
        }

        public ModelBuilder(Document doc, TypesSpec types, string specName)
        {
            _doc = doc;
            _specName = specName;
            _types = new TypeResolver(doc, types, specName, _report);
        }

        public BuildReport Report => _report;

        public void Build(ModelSpec spec, ICollection<string> stages, bool deleteMissing)
        {
            _spec = spec;
            ReindexTagged();
            IndexLevelOrder();

            var columns = Expand("column", spec.Columns, c => c.BaseLevel, (c, l) => c.BaseLevel = l,
                c => c.TopLevel = ResolveRelative(c.TopLevel, c.BaseLevel));
            var walls = Expand("wall", spec.Walls, w => w.BaseLevel, (w, l) => w.BaseLevel = l,
                w => w.TopLevel = ResolveRelative(w.TopLevel, w.BaseLevel));
            var beams = Expand("beam", spec.Beams, b => b.Level, (b, l) => b.Level = l, b => { });
            var floors = Expand("floor", spec.Floors, f => f.Level, (f, l) => f.Level = l, f => { });

            if (stages.Contains("levels"))
                RunStage("levels", BuildLevels);
            MapLevels();

            if (stages.Contains("grids"))
                RunStage("grids", BuildGrids);
            if (stages.Contains("columns"))
                RunStage("columns", pending => { foreach (var e in columns) BuildColumn(e, pending); });
            if (stages.Contains("walls"))
                RunStage("walls", pending => { foreach (var e in walls) BuildWall(e, pending); });
            if (stages.Contains("beams"))
                RunStage("beams", pending => { foreach (var e in beams) BuildBeam(e, pending); });
            if (stages.Contains("floors"))
                RunStage("floors", pending => { foreach (var e in floors) BuildFloor(e, pending); });

            var specKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (LevelSpec l in spec.Levels) specKeys.Add("level:" + l.Id);
            foreach (GridSpec g in spec.Grids) specKeys.Add("grid:" + g.Id);
            foreach (var e in columns) specKeys.Add(e.Key);
            foreach (var e in walls) specKeys.Add(e.Key);
            foreach (var e in beams) specKeys.Add(e.Key);
            foreach (var e in floors) specKeys.Add(e.Key);

            HandleOrphans(specKeys, stages, deleteMissing);

            foreach (string key in specKeys)
            {
                if (_tagged.TryGetValue(key, out Element element) && element.IsValidObject)
                    _report.Ids[key] = Utils.ElementIdExtensions.GetValue(element.Id);
            }
        }

        // ------------------------------------------------------------------ stages

        private void RunStage(string label, Action<List<Pending>> body)
        {
            var pending = new List<Pending>();
            var failures = new BuildFailureHandler();
            TransactionStatus status = TransactionStatus.Uninitialized;

            using (var transaction = new Transaction(_doc, "Build from spec: " + label))
            {
                transaction.Start();
                failures.Attach(transaction);
                try
                {
                    body(pending);
                    status = transaction.Commit();
                }
                catch (Exception ex)
                {
                    _report.Problems.Add(label + ": stage aborted: " + ex.Message);
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                    status = TransactionStatus.RolledBack;
                }
            }

            _report.Warn(failures.Warnings);
            bool rolledBack = status != TransactionStatus.Committed || failures.RolledBack;
            string errors = failures.Errors.Count == 0 ? "" : ": " + string.Join(" / ", failures.Errors);

            foreach (Pending item in pending)
            {
                if (rolledBack)
                {
                    _report.Fail(item.Kind, item.Key, "the " + label + " stage was rolled back" + errors);
                    continue;
                }

                Element element = _doc.GetElement(item.Id);
                if (failures.Deleted.TryGetValue(item.Id, out string reason) || element == null)
                {
                    _report.Fail(item.Kind, item.Key, "Revit deleted it: " + (reason ?? "unknown reason"));
                    _tagged.Remove(item.Key);
                    continue;
                }

                KindCounts counts = _report.For(item.Kind);
                switch (item.Action)
                {
                    case "created": counts.Created++; break;
                    case "adopted": counts.Adopted++; break;
                    default: counts.Updated++; break;
                }
            }

            if (rolledBack)
            {
                // Whatever this stage created is gone, so the cached lookups are stale.
                _types.Reset();
                ReindexTagged();
                MapLevels();
            }
        }

        private void BuildLevels(List<Pending> pending)
        {
            var byName = DocLevels().GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());

            foreach (LevelSpec spec in _spec.Levels)
            {
                if (string.IsNullOrWhiteSpace(spec.Id))
                {
                    _report.Fail("level", "level:?", "a level has no id");
                    continue;
                }

                string key = "level:" + spec.Id;
                string name = string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name;
                string signature = JsonConvert.SerializeObject(spec);
                ElementId created = null;

                try
                {
                    Level level = Tagged<Level>(key);
                    string action;

                    if (level != null)
                    {
                        action = SignatureOf(level) == signature ? "unchanged" : "updated";
                    }
                    else if (byName.TryGetValue(name, out Level named) && SpecTagStorage.Read(named) == null)
                    {
                        level = named;
                        action = "adopted";
                    }
                    else if (byName.TryGetValue(name, out Level taken))
                    {
                        _report.Fail("level", key, "the name '" + name + "' is already used by " +
                                                   SpecTagStorage.Read(taken)?.Key);
                        continue;
                    }
                    else
                    {
                        level = Level.Create(_doc, spec.Elevation * FeetPerMetre);
                        created = level.Id;
                        action = "created";
                    }

                    if (action != "unchanged")
                    {
                        double elevation = spec.Elevation * FeetPerMetre;
                        if (Math.Abs(level.Elevation - elevation) > 1e-9)
                            level.Elevation = elevation;
                        if (level.Name != name)
                            level.Name = name;
                        Stamp(level, key, signature);
                        pending.Add(new Pending { Kind = "level", Key = key, Id = level.Id, Action = action });
                    }
                    else
                    {
                        _report.For("level").Unchanged++;
                    }

                    byName[name] = level;
                }
                catch (Exception ex)
                {
                    DeleteHalfBuilt(created);
                    _report.Fail("level", key, ex.Message);
                }
            }

            CreateMissingPlans();
        }

        private void CreateMissingPlans()
        {
            ViewFamilyType planType = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);
            if (planType == null)
            {
                _report.Notes.Add("The document has no floor plan view type, so no plans were created.");
                return;
            }

            var planned = new HashSet<ElementId>();
            var viewNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (View view in new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>())
            {
                viewNames.Add(view.Name);
                var plan = view as ViewPlan;
                if (plan != null && !plan.IsTemplate && plan.ViewType == ViewType.FloorPlan && plan.GenLevel != null)
                    planned.Add(plan.GenLevel.Id);
            }

            foreach (LevelSpec spec in _spec.Levels)
            {
                if (!spec.CreatePlan)
                    continue;

                Level level = Tagged<Level>("level:" + spec.Id);
                if (level == null || planned.Contains(level.Id))
                    continue;

                ViewPlan plan = ViewPlan.Create(_doc, planType.Id, level.Id);
                if (!viewNames.Contains(level.Name))
                {
                    plan.Name = level.Name;
                    viewNames.Add(level.Name);
                }
                planned.Add(level.Id);
                _report.ViewsCreated.Add(plan.Name);
            }
        }

        private void BuildGrids(List<Pending> pending)
        {
            var byName = new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>()
                .GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First());

            foreach (GridSpec spec in _spec.Grids)
            {
                if (string.IsNullOrWhiteSpace(spec.Id))
                {
                    _report.Fail("grid", "grid:?", "a grid has no id");
                    continue;
                }

                string key = "grid:" + spec.Id;
                string name = string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name;
                string signature = JsonConvert.SerializeObject(spec);
                ElementId created = null;

                try
                {
                    Curve curve = MakeCurve(spec.Start, spec.End, spec.Mid, 0, out string error);
                    if (curve == null)
                    {
                        _report.Fail("grid", key, error);
                        continue;
                    }

                    Grid grid = Tagged<Grid>(key);
                    string action;

                    if (grid != null)
                    {
                        if (SignatureOf(grid) == signature)
                        {
                            _report.For("grid").Unchanged++;
                            continue;
                        }

                        action = "updated";
                        if (!SameCurve(grid.Curve, curve))
                        {
                            byName.Remove(grid.Name);
                            _doc.Delete(grid.Id);
                            _tagged.Remove(key);
                            grid = CreateGrid(curve);
                            created = grid.Id;
                        }
                    }
                    else if (byName.TryGetValue(name, out Grid named))
                    {
                        SpecTag owner = SpecTagStorage.Read(named);
                        if (owner != null)
                        {
                            _report.Fail("grid", key, "the name '" + name + "' is already used by " + owner.Spec + " / " + owner.Key);
                            continue;
                        }
                        if (!SameCurve(named.Curve, curve))
                        {
                            _report.Fail("grid", key, "a grid named '" + name + "' already exists with different geometry " +
                                                      "and was not built from this spec; rename or delete it first");
                            continue;
                        }
                        grid = named;
                        action = "adopted";
                    }
                    else
                    {
                        grid = CreateGrid(curve);
                        created = grid.Id;
                        action = "created";
                    }

                    if (grid.Name != name)
                        grid.Name = name;
                    byName[name] = grid;
                    Stamp(grid, key, signature);
                    pending.Add(new Pending { Kind = "grid", Key = key, Id = grid.Id, Action = action });
                }
                catch (Exception ex)
                {
                    DeleteHalfBuilt(created);
                    _report.Fail("grid", key, ex.Message);
                }
            }
        }

        private Grid CreateGrid(Curve curve)
        {
            var arc = curve as Arc;
            return arc != null ? Grid.Create(_doc, arc) : Grid.Create(_doc, (Line)curve);
        }

        private void BuildColumn(Entry<ColumnSpec> entry, List<Pending> pending)
        {
            ColumnSpec spec = entry.Spec;
            ElementId created = null;
            try
            {
                FamilyInstance existing = Tagged<FamilyInstance>(entry.Key);
                if (existing != null && SignatureOf(existing) == entry.Signature)
                {
                    _report.For("column").Unchanged++;
                    return;
                }

                FamilySymbol symbol = _types.Column(spec.Type, out string error);
                Level baseLevel = symbol == null ? null : ResolveLevel(spec.BaseLevel, "base level", out error);
                Level topLevel = baseLevel == null ? null : ResolveLevel(spec.TopLevel, "top level", out error);
                XYZ point = topLevel == null ? null : MakePoint(spec.At, baseLevel.Elevation, "at", out error);
                if (point == null)
                {
                    _report.Fail("column", entry.Key, error);
                    return;
                }

                if (topLevel.Elevation + spec.TopOffset * FeetPerMetre <= baseLevel.Elevation + spec.BaseOffset * FeetPerMetre)
                {
                    _report.Fail("column", entry.Key, "its top is not above its base");
                    return;
                }

                string action = existing == null ? "created" : "updated";
                if (existing != null && existing.Category.Id != symbol.Category.Id)
                {
                    _doc.Delete(existing.Id);
                    _tagged.Remove(entry.Key);
                    existing = null;
                }

                if (!symbol.IsActive)
                    symbol.Activate();

                FamilyInstance column;
                if (existing == null)
                {
                    StructuralType structuralType = symbol.Category.Id.GetValue() == (long)BuiltInCategory.OST_StructuralColumns
                        ? StructuralType.Column
                        : StructuralType.NonStructural;
                    column = _doc.Create.NewFamilyInstance(point, symbol, baseLevel, structuralType);
                    created = column.Id;
                }
                else
                {
                    column = existing;
                    if (column.Symbol.Id != symbol.Id)
                        column.Symbol = symbol;
                    var location = (LocationPoint)column.Location;
                    location.Point = new XYZ(point.X, point.Y, location.Point.Z);
                }

                SetId(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id);
                SetId(column, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, baseLevel.Id);
                SetDouble(column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, spec.BaseOffset * FeetPerMetre);
                if (!SetDouble(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, spec.TopOffset * FeetPerMetre))
                    _report.Notes.Add(entry.Key + ": top offset is read-only, most likely because the top is attached");

                // The axis goes through the spec point, not through the location Revit reports:
                // on a column created in this transaction that location is not regenerated yet,
                // and rotating about it swung a column 2.6 m around the project origin.
                var placed = (LocationPoint)column.Location;
                double delta = spec.Rotation * Math.PI / 180.0 - placed.Rotation;
                delta = Math.IEEERemainder(delta, 2 * Math.PI);
                if (Math.Abs(delta) > 1e-9)
                {
                    XYZ axisBase = new XYZ(point.X, point.Y, baseLevel.Elevation);
                    ElementTransformUtils.RotateElement(_doc, column.Id,
                        Line.CreateBound(axisBase, axisBase + XYZ.BasisZ), delta);
                }

                Stamp(column, entry.Key, entry.Signature);
                pending.Add(new Pending { Kind = "column", Key = entry.Key, Id = column.Id, Action = action });
            }
            catch (Exception ex)
            {
                DeleteHalfBuilt(created);
                _report.Fail("column", entry.Key, ex.Message);
            }
        }

        private void BuildWall(Entry<WallSpec> entry, List<Pending> pending)
        {
            WallSpec spec = entry.Spec;
            ElementId created = null;
            try
            {
                Wall existing = Tagged<Wall>(entry.Key);
                if (existing != null && SignatureOf(existing) == entry.Signature)
                {
                    _report.For("wall").Unchanged++;
                    return;
                }

                WallType type = _types.Wall(spec.Type, out string error);
                Level baseLevel = type == null ? null : ResolveLevel(spec.BaseLevel, "base level", out error);
                if (baseLevel == null)
                {
                    _report.Fail("wall", entry.Key, error);
                    return;
                }

                Level topLevel = null;
                if (!string.IsNullOrWhiteSpace(spec.TopLevel))
                {
                    topLevel = ResolveLevel(spec.TopLevel, "top level", out error);
                    if (topLevel == null)
                    {
                        _report.Fail("wall", entry.Key, error);
                        return;
                    }
                }
                else if (spec.Height == null || spec.Height <= 0)
                {
                    _report.Fail("wall", entry.Key, "it needs either topLevel or a positive height");
                    return;
                }

                Curve curve = MakeCurve(spec.Start, spec.End, spec.Mid, baseLevel.Elevation, out error);
                if (curve == null)
                {
                    _report.Fail("wall", entry.Key, error);
                    return;
                }

                string alignment = (spec.Alignment ?? "center").Trim().ToLowerInvariant();
                if (alignment == "left" || alignment == "right")
                {
                    var line = curve as Line;
                    if (line == null)
                    {
                        _report.Fail("wall", entry.Key, "left or right alignment is only supported on straight walls");
                        return;
                    }
                    XYZ direction = line.Direction;
                    XYZ left = new XYZ(-direction.Y, direction.X, 0);
                    XYZ shift = left * (type.Width / 2.0) * (alignment == "left" ? -1 : 1);
                    curve = line.CreateTransformed(Transform.CreateTranslation(shift));
                }
                else if (alignment != "center")
                {
                    _report.Fail("wall", entry.Key, "alignment must be center, left or right");
                    return;
                }

                Wall wall;
                string action;
                if (existing == null)
                {
                    double height = (spec.Height ?? 3.0) * FeetPerMetre;
                    wall = Wall.Create(_doc, curve, type.Id, baseLevel.Id, height, spec.BaseOffset * FeetPerMetre, false, spec.Structural);
                    created = wall.Id;
                    SetInt(wall, BuiltInParameter.WALL_KEY_REF_PARAM, 0);
                    action = "created";
                }
                else
                {
                    wall = existing;
                    // Centreline first, so the curve set next means the centre of the wall.
                    SetInt(wall, BuiltInParameter.WALL_KEY_REF_PARAM, 0);
                    if (wall.WallType.Id != type.Id)
                        wall.WallType = type;
                    ((LocationCurve)wall.Location).Curve = curve;
                    SetId(wall, BuiltInParameter.WALL_BASE_CONSTRAINT, baseLevel.Id);
                    SetDouble(wall, BuiltInParameter.WALL_BASE_OFFSET, spec.BaseOffset * FeetPerMetre);
                    SetInt(wall, BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT, spec.Structural ? 1 : 0);
                    action = "updated";
                }

                if (topLevel != null)
                {
                    SetId(wall, BuiltInParameter.WALL_HEIGHT_TYPE, topLevel.Id);
                    SetDouble(wall, BuiltInParameter.WALL_TOP_OFFSET, spec.TopOffset * FeetPerMetre);
                }
                else
                {
                    SetId(wall, BuiltInParameter.WALL_HEIGHT_TYPE, ElementId.InvalidElementId);
                    SetDouble(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM, spec.Height.Value * FeetPerMetre);
                }

                Stamp(wall, entry.Key, entry.Signature);
                pending.Add(new Pending { Kind = "wall", Key = entry.Key, Id = wall.Id, Action = action });
            }
            catch (Exception ex)
            {
                DeleteHalfBuilt(created);
                _report.Fail("wall", entry.Key, ex.Message);
            }
        }

        private void BuildBeam(Entry<BeamSpec> entry, List<Pending> pending)
        {
            BeamSpec spec = entry.Spec;
            ElementId created = null;
            try
            {
                FamilyInstance existing = Tagged<FamilyInstance>(entry.Key);
                if (existing != null && SignatureOf(existing) == entry.Signature)
                {
                    _report.For("beam").Unchanged++;
                    return;
                }

                FamilySymbol symbol = _types.Beam(spec.Type, out string error);
                Level level = symbol == null ? null : ResolveLevel(spec.Level, "level", out error);
                Curve curve = level == null ? null
                    : MakeCurve(spec.Start, spec.End, spec.Mid, level.Elevation + spec.Offset * FeetPerMetre, out error);
                if (curve == null)
                {
                    _report.Fail("beam", entry.Key, error);
                    return;
                }

                string action = existing == null ? "created" : "updated";
                if (existing != null)
                {
                    ElementId reference = existing.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
                    if (reference != level.Id)
                    {
                        _doc.Delete(existing.Id);
                        _tagged.Remove(entry.Key);
                        existing = null;
                    }
                }

                if (!symbol.IsActive)
                    symbol.Activate();

                FamilyInstance beam;
                if (existing == null)
                {
                    beam = _doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam);
                    created = beam.Id;
                }
                else
                {
                    beam = existing;
                    if (beam.Symbol.Id != symbol.Id)
                        beam.Symbol = symbol;
                    ((LocationCurve)beam.Location).Curve = curve;
                }

                // Top justification, so the offset in the spec is the top of the beam.
                SetInt(beam, BuiltInParameter.Z_JUSTIFICATION, 0);

                Stamp(beam, entry.Key, entry.Signature);
                pending.Add(new Pending { Kind = "beam", Key = entry.Key, Id = beam.Id, Action = action });
            }
            catch (Exception ex)
            {
                DeleteHalfBuilt(created);
                _report.Fail("beam", entry.Key, ex.Message);
            }
        }

        private void BuildFloor(Entry<FloorSpec> entry, List<Pending> pending)
        {
            FloorSpec spec = entry.Spec;
            ElementId created = null;
            try
            {
                Floor existing = Tagged<Floor>(entry.Key);
                if (existing != null && SignatureOf(existing) == entry.Signature)
                {
                    _report.For("floor").Unchanged++;
                    return;
                }

#if REVIT2022_OR_GREATER
                FloorType type = _types.Floor(spec.Type, out string error);
                Level level = type == null ? null : ResolveLevel(spec.Level, "level", out error);
                if (level == null)
                {
                    _report.Fail("floor", entry.Key, error);
                    return;
                }

                double z = level.Elevation;
                var loops = new List<CurveLoop>();
                CurveLoop outline = MakeLoop(spec.Boundary, z, out error);
                if (outline == null)
                {
                    _report.Fail("floor", entry.Key, "boundary: " + error);
                    return;
                }
                loops.Add(outline);

                for (int i = 0; spec.Holes != null && i < spec.Holes.Count; i++)
                {
                    CurveLoop hole = MakeLoop(spec.Holes[i], z, out error);
                    if (hole == null)
                    {
                        _report.Fail("floor", entry.Key, "hole " + i + ": " + error);
                        return;
                    }
                    loops.Add(hole);
                }

                Line arrow = null;
                double slope = 0;
                if (spec.SlopeArrow != null)
                {
                    XYZ from = MakePoint(spec.SlopeArrow.From, z, "slopeArrow.from", out error);
                    XYZ to = from == null ? null : MakePoint(spec.SlopeArrow.To, z, "slopeArrow.to", out error);
                    if (to == null || from.DistanceTo(to) < SameTolerance)
                    {
                        _report.Fail("floor", entry.Key, error ?? "the slope arrow has no length");
                        return;
                    }
                    arrow = Line.CreateBound(from, to);
                    slope = spec.SlopeArrow.Percent / 100.0;
                }

                // A sketch cannot be edited in place without a sketch edit scope, so a changed
                // floor is rebuilt. Nothing is hosted on floors at this stage of a model.
                string action = existing == null ? "created" : "updated";
                if (existing != null)
                {
                    _doc.Delete(existing.Id);
                    _tagged.Remove(entry.Key);
                }

                Floor floor = Floor.Create(_doc, loops, type.Id, level.Id, spec.Structural, arrow, slope);
                created = floor.Id;
                SetDouble(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, spec.Offset * FeetPerMetre);

                Stamp(floor, entry.Key, entry.Signature);
                pending.Add(new Pending { Kind = "floor", Key = entry.Key, Id = floor.Id, Action = action });
#else
                _report.Fail("floor", entry.Key, "building floors needs Revit 2022 or newer");
#endif
            }
            catch (Exception ex)
            {
                DeleteHalfBuilt(created);
                _report.Fail("floor", entry.Key, ex.Message);
            }
        }

        private void HandleOrphans(HashSet<string> specKeys, ICollection<string> stages, bool deleteMissing)
        {
            var kindsInStages = new Dictionary<string, string>
            {
                { "level", "levels" }, { "grid", "grids" }, { "column", "columns" },
                { "wall", "walls" }, { "beam", "beams" }, { "floor", "floors" }
            };

            var orphans = new List<KeyValuePair<string, Element>>();
            foreach (var pair in _tagged)
            {
                string kind = pair.Key.Split(':')[0];
                if (kindsInStages.TryGetValue(kind, out string stage) && stages.Contains(stage) && !specKeys.Contains(pair.Key))
                    orphans.Add(pair);
            }

            if (orphans.Count == 0)
                return;

            if (!deleteMissing)
            {
                foreach (var orphan in orphans)
                    _report.Orphans.Add(orphan.Key);
                return;
            }

            // Deleting a level deletes everything hosted on it, so levels are only ever reported.
            RunStage("cleanup", pending =>
            {
                foreach (var orphan in orphans)
                {
                    string kind = orphan.Key.Split(':')[0];
                    if (kind == "level")
                    {
                        _report.Orphans.Add(orphan.Key + " (levels are never deleted automatically)");
                        continue;
                    }
                    _doc.Delete(orphan.Value.Id);
                    _tagged.Remove(orphan.Key);
                    _report.For(kind).Deleted++;
                }
            });
        }

        // ------------------------------------------------------------------ expansion and levels

        /// <summary>
        /// Turns each entry into the list of copies it stands for: itself on its own level, plus
        /// one per repeatOn level with the id "&lt;id&gt;@&lt;level&gt;". Relative level references
        /// are resolved per copy, and the signature is taken after that, so inserting a level
        /// into the spec changes the signature of every copy whose "+1" now means another level.
        /// </summary>
        private List<Entry<T>> Expand<T>(string kind, List<T> items, Func<T, string> getBase,
            Action<T, string> setBase, Action<T> resolveRelative) where T : PlacedSpec
        {
            var result = new List<Entry<T>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (items == null)
                return result;

            foreach (T item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    _report.Fail(kind, kind + ":?", "an entry has no id");
                    continue;
                }

                var copies = new List<T> { Clone(item) };
                if (item.RepeatOn != null)
                {
                    foreach (string level in item.RepeatOn)
                    {
                        if (string.IsNullOrWhiteSpace(level) || level == getBase(item))
                            continue;
                        T copy = Clone(item);
                        copy.Id = item.Id + "@" + level;
                        setBase(copy, level);
                        copies.Add(copy);
                    }
                }

                foreach (T copy in copies)
                {
                    copy.RepeatOn = null;
                    resolveRelative(copy);
                    string key = kind + ":" + copy.Id;
                    if (!seen.Add(key))
                    {
                        _report.Fail(kind, key, "the id is used more than once in the spec; only the first is built");
                        continue;
                    }
                    result.Add(new Entry<T> { Key = key, Signature = JsonConvert.SerializeObject(copy), Spec = copy });
                }
            }

            return result;
        }

        private static T Clone<T>(T item) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(item));

        /// <summary>
        /// The order relative references count in: the spec's levels by elevation, or the
        /// document's levels when the spec lists none.
        /// </summary>
        private void IndexLevelOrder()
        {
            _levelIdByName.Clear();
            if (_spec.Levels.Count > 0)
            {
                _levelOrder = _spec.Levels.Where(l => !string.IsNullOrWhiteSpace(l.Id))
                    .OrderBy(l => l.Elevation).Select(l => l.Id).ToList();
                foreach (LevelSpec level in _spec.Levels)
                {
                    if (!string.IsNullOrWhiteSpace(level.Name) && !string.IsNullOrWhiteSpace(level.Id))
                        _levelIdByName[level.Name] = level.Id;
                }
            }
            else
            {
                _levelOrder = DocLevels().OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
            }
        }

        private string ResolveRelative(string reference, string baseReference)
        {
            if (string.IsNullOrWhiteSpace(reference) || (reference[0] != '+' && reference[0] != '-'))
                return reference;
            if (!int.TryParse(reference, out int step))
                return reference;

            string baseId = baseReference != null && _levelIdByName.TryGetValue(baseReference, out string mapped)
                ? mapped
                : baseReference;
            int index = _levelOrder.IndexOf(baseId);
            int target = index + step;
            if (index < 0 || target < 0 || target >= _levelOrder.Count)
                return reference + " from " + baseReference;
            return _levelOrder[target];
        }

        /// <summary>Maps every spec level id and name to the level that stands for it.</summary>
        private void MapLevels()
        {
            _levels.Clear();
            var byName = DocLevels().GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (var pair in byName)
                _levels[pair.Key] = pair.Value;

            foreach (LevelSpec spec in _spec.Levels)
            {
                if (string.IsNullOrWhiteSpace(spec.Id))
                    continue;
                Level level = Tagged<Level>("level:" + spec.Id);
                string name = string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name;
                if (level == null)
                    byName.TryGetValue(name, out level);
                if (level != null)
                    _levels[spec.Id] = level;
            }
        }

        private Level ResolveLevel(string reference, string role, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(reference))
            {
                error = "no " + role + " given";
                return null;
            }
            if (_levels.TryGetValue(reference, out Level level))
                return level;
            error = role + " '" + reference + "' was not found";
            return null;
        }

        private List<Level> DocLevels()
        {
            return new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        }

        // ------------------------------------------------------------------ stamps

        /// <summary>
        /// Removes an element created for an entry that then failed, so it is not left behind
        /// unstamped: the next build would not recognise it and would build a second one.
        /// </summary>
        private void DeleteHalfBuilt(ElementId created)
        {
            if (created == null || _doc.GetElement(created) == null)
                return;
            try
            {
                _doc.Delete(created);
            }
            catch
            {
                // The report already carries the original error.
            }
        }

        private void ReindexTagged()
        {
            var duplicates = new List<Element>();
            _tagged = SpecTagStorage.Index(_doc, _specName, duplicates);
            foreach (Element duplicate in duplicates)
            {
                _report.Notes.Add("Element " + Utils.ElementIdExtensions.GetValue(duplicate.Id) + " carries the stamp of " +
                                  SpecTagStorage.Read(duplicate).Key + ", which another element already has; it was " +
                                  "probably copied by hand and is ignored.");
            }
        }

        private T Tagged<T>(string key) where T : Element
        {
            if (!_tagged.TryGetValue(key, out Element element))
                return null;
            if (!element.IsValidObject)
            {
                _tagged.Remove(key);
                return null;
            }
            return element as T;
        }

        private static string SignatureOf(Element element) => SpecTagStorage.Read(element)?.Signature;

        private void Stamp(Element element, string key, string signature)
        {
            SpecTagStorage.Write(element, new SpecTag { Spec = _specName, Key = key, Signature = signature });
            _tagged[key] = element;
        }

        // ------------------------------------------------------------------ geometry and parameters

        private static XYZ MakePoint(double[] xy, double z, string name, out string error)
        {
            error = null;
            if (xy == null || xy.Length < 2)
            {
                error = name + " must be [x, y]";
                return null;
            }
            return new XYZ(xy[0] * FeetPerMetre, xy[1] * FeetPerMetre, z);
        }

        private static Curve MakeCurve(double[] start, double[] end, double[] mid, double z, out string error)
        {
            XYZ a = MakePoint(start, z, "start", out error);
            XYZ b = a == null ? null : MakePoint(end, z, "end", out error);
            if (b == null)
                return null;
            if (a.DistanceTo(b) < SameTolerance)
            {
                error = "start and end are the same point";
                return null;
            }
            if (mid == null)
                return Line.CreateBound(a, b);

            XYZ m = MakePoint(mid, z, "mid", out error);
            if (m == null)
                return null;
            try
            {
                return Arc.Create(a, b, m);
            }
            catch (Exception ex)
            {
                error = "mid does not define an arc: " + ex.Message;
                return null;
            }
        }

        private static CurveLoop MakeLoop(List<double[]> points, double z, out string error)
        {
            error = null;
            var vertices = new List<XYZ>();
            if (points != null)
            {
                foreach (double[] point in points)
                {
                    XYZ vertex = MakePoint(point, z, "vertex", out error);
                    if (vertex == null)
                        return null;
                    if (vertices.Count == 0 || vertices[vertices.Count - 1].DistanceTo(vertex) >= SameTolerance)
                        vertices.Add(vertex);
                }
            }

            if (vertices.Count > 1 && vertices[0].DistanceTo(vertices[vertices.Count - 1]) < SameTolerance)
                vertices.RemoveAt(vertices.Count - 1);

            if (vertices.Count < 3)
            {
                error = "it needs at least three distinct vertices";
                return null;
            }

            var loop = new CurveLoop();
            for (int i = 0; i < vertices.Count; i++)
                loop.Append(Line.CreateBound(vertices[i], vertices[(i + 1) % vertices.Count]));
            return loop;
        }

        private static bool SameCurve(Curve a, Curve b)
        {
            XYZ a0 = a.GetEndPoint(0), a1 = a.GetEndPoint(1), b0 = b.GetEndPoint(0), b1 = b.GetEndPoint(1);
            bool ends = (Close(a0, b0) && Close(a1, b1)) || (Close(a0, b1) && Close(a1, b0));
            if (!ends || (a is Arc) != (b is Arc))
                return false;
            return !(a is Arc) || Close(a.Evaluate(0.5, true), b.Evaluate(0.5, true));
        }

        private static bool Close(XYZ a, XYZ b)
        {
            return new XYZ(a.X - b.X, a.Y - b.Y, 0).GetLength() < SameTolerance;
        }

        private static bool SetDouble(Element element, BuiltInParameter id, double value)
        {
            Parameter parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
                return false;
            if (Math.Abs(parameter.AsDouble() - value) > 1e-9)
                parameter.Set(value);
            return true;
        }

        private static bool SetInt(Element element, BuiltInParameter id, int value)
        {
            Parameter parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
                return false;
            if (parameter.AsInteger() != value)
                parameter.Set(value);
            return true;
        }

        private static bool SetId(Element element, BuiltInParameter id, ElementId value)
        {
            Parameter parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
                return false;
            if (parameter.AsElementId() != value)
                parameter.Set(value);
            return true;
        }
    }
}
