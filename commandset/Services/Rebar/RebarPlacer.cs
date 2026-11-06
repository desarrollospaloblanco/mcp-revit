using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// Places bar sets and fabric areas: splits bars longer than the commercial length with lap
    /// splices away from joints, finds hosts when none is given, writes the "MCP Acero" parameters
    /// and draws a marker (magenta rectangle, grouped as "Traslape T001 - ...") on every splice.
    /// Sets go in chunks; a chunk Revit refuses is retried one set at a time so one bad bar does
    /// not lose the rest.
    /// </summary>
    public class RebarPlacer
    {
        private const int Chunk = 120;
        private readonly Document _doc;
        private readonly RebarCatalog _catalog;
        private readonly ToolOptions _options;
        private readonly Dictionary<string, RebarBarType> _barTypes = new Dictionary<string, RebarBarType>();
        private Dictionary<string, RebarHookType> _hookTypes;
        private RebarHosts _hosts;

        public List<string> Notes { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<LapSplice> Laps { get; } = new List<LapSplice>();
        public int BarSetsCreated { get; private set; }
        public int MeshesCreated { get; private set; }
        public int MarkersCreated { get; private set; }
        public double LongestPiece { get; private set; }

        public RebarPlacer(Document doc, RebarCatalog catalog, ToolOptions options)
        {
            _doc = doc;
            _catalog = catalog;
            _options = options;
        }

        private RebarHosts Hosts => _hosts ?? (_hosts = new RebarHosts(_doc));

        /// <summary>Highest lap number already in the model, so new laps continue the series.</summary>
        public static int NextLapNumber(Document doc)
        {
            int max = 0;
            foreach (GroupType g in new FilteredElementCollector(doc).OfClass(typeof(GroupType)))
                max = Math.Max(max, LapNumber(g.Name.StartsWith("Traslape ") ? g.Name.Substring(9) : null));
            foreach (Element r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType())
            {
                string laps = r.LookupParameter(RebarParameters.Lap)?.AsString();
                if (laps != null)
                    foreach (string id in laps.Split(','))
                        max = Math.Max(max, LapNumber(id));
            }
            return max + 1;
        }

        private static int LapNumber(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != 'T') return 0;
            string digits = new string(text.Skip(1).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out int n) ? n : 0;
        }

        /// <summary>Splits every set into commercial pieces; auto zones come from the joints the bar crosses.</summary>
        public List<BarSpec> Split(IEnumerable<BarSpec> specs, bool autoZones, bool autoHosts)
        {
            var splicer = new RebarSplicer(_catalog, NextLapNumber(_doc));
            var pieces = new List<BarSpec>();
            foreach (BarSpec spec in specs)
            {
                bool findHost = spec.HostId == ElementId.InvalidElementId && autoHosts;
                if (findHost)
                {
                    Element host = Hosts.HostFor(spec);
                    if (host != null) spec.HostId = host.Id;
                }
                var zones = spec.SpliceZones;
                if (zones == null && autoZones && !spec.Stirrup && spec.Length > _catalog.StockLength)
                    zones = Hosts.Zones(spec, _doc.GetElement(spec.HostId));
                var split = splicer.Split(spec, zones);
                if (findHost && split.Count > 1)
                    foreach (BarSpec piece in split)
                    {
                        // a bar running through several walls or slabs: each piece takes the element it sits in
                        Element own = Hosts.HostFor(piece);
                        if (own != null) piece.HostId = own.Id;
                    }
                pieces.AddRange(split);
            }
            Laps.AddRange(splicer.Laps);
            Notes.AddRange(splicer.Notes);
            foreach (BarSpec p in pieces.Where(p => !p.Stirrup))
                LongestPiece = Math.Max(LongestPiece, p.Length);
            return pieces;
        }

        /// <summary>Creates the pieces in Revit. With dryRun every chunk is rolled back.</summary>
        public void PlaceBars(List<BarSpec> pieces, bool dryRun)
        {
            _hookTypes = new FilteredElementCollector(_doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>()
                .GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.First());
            // A dry run rolls every transaction back, so the bar types it needs are made again inside each one.
            void EnsureTypes(IEnumerable<BarSpec> specs)
            {
                if (dryRun) _barTypes.Clear();
                foreach (string size in specs.Select(p => p.Size).Distinct())
                    RebarCatalog.BarType(_doc, size, _barTypes, Notes);
            }
            if (!dryRun) Run("MCP: tipos de varilla", false, () => EnsureTypes(pieces));
            for (int start = 0; start < pieces.Count; start += Chunk)
            {
                var part = pieces.Skip(start).Take(Chunk).ToList();
                var failed = new List<string>();
                bool committed = Run("MCP: acero " + (start / Chunk + 1), dryRun, () =>
                {
                    if (dryRun) EnsureTypes(part);
                    foreach (BarSpec p in part)
                    {
                        string error = CreateBar(p);
                        if (error != null) failed.Add(Name(p) + ": " + error);
                    }
                });
                if (committed)
                {
                    BarSetsCreated += part.Count - failed.Count;
                    Errors.AddRange(failed);
                    continue;
                }
                foreach (BarSpec p in part)
                {
                    string error = null;
                    bool ok = Run("MCP: acero " + p.Element, dryRun, () =>
                    {
                        if (dryRun) EnsureTypes(new[] { p });
                        error = CreateBar(p);
                        if (error != null) throw new InvalidOperationException(error);
                    });
                    if (ok) BarSetsCreated++;
                    else Errors.Add(Name(p) + ": " + (error ?? Warnings.LastOrDefault() ?? "Revit refused it"));
                }
            }
        }

        private static string Name(BarSpec p) => (p.Element + " " + p.Description).Trim();

        /// <summary>Runs work in its own transaction; false when Revit rolled it back.</summary>
        private bool Run(string name, bool dryRun, Action work)
        {
            using (var t = new Transaction(_doc, name))
            {
                t.Start();
                var collector = new WarningCollector(true);
                var fo = t.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(collector);
                fo.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(fo);
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    Warnings.Add(name + ": " + ex.Message);
                    return false;
                }
                if (dryRun)
                {
                    _doc.Regenerate();
                    t.RollBack();
                    return true;
                }
                TransactionStatus status = t.Commit();
                foreach (string w in collector.Report()) if (!Warnings.Contains(w)) Warnings.Add(w);
                if (status == TransactionStatus.Committed) return true;
                Warnings.Add(name + ": " + string.Join(" | ", collector.BlockingErrors));
                return false;
            }
        }

        private string CreateBar(BarSpec p)
        {
            try
            {
                Element host = _doc.GetElement(p.HostId);
                if (host == null) return "no host (give hostId or put the bar inside a column, beam, footing, wall or floor)";
                if (p.Points.Count < 2) return "a bar needs at least two points";
                var curves = new List<Curve>();
                for (int i = 0; i + 1 < p.Points.Count; i++)
                    curves.Add(Line.CreateBound(p.Points[i] * Metric.FeetPerMeter, p.Points[i + 1] * Metric.FeetPerMeter));
                RebarHookType h0 = Hook(p.StartHook), h1 = Hook(p.EndHook);
                var rebar = Autodesk.Revit.DB.Structure.Rebar.CreateFromCurves(_doc,
                    p.Stirrup ? RebarStyle.StirrupTie : RebarStyle.Standard, _barTypes[p.Size], h0, h1, host, p.Normal, curves,
                    RebarHookOrientation.Left, RebarHookOrientation.Left, true, true);
                if (rebar == null) return "Revit could not build a bar from these points";
                if (p.Count > 1)
                    rebar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(p.Count, Metric.Ft(p.Spacing), true, true, true);
                RebarParameters.Set(rebar, RebarParameters.Element, p.Element);
                RebarParameters.Set(rebar, RebarParameters.Group, p.Group);
                RebarParameters.Set(rebar, RebarParameters.Size, RebarCatalog.Display(p.Size));
                RebarParameters.Set(rebar, RebarParameters.Description, p.Description);
                RebarParameters.Set(rebar, RebarParameters.Lap, p.Laps);
                if (p.Parameters != null)
                    foreach (var kv in p.Parameters.Properties())
                        Metric.Set(rebar.LookupParameter(kv.Name), kv.Value);
                RunMarks.Set(rebar, _options.RunMark);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private RebarHookType Hook(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _hookTypes.TryGetValue(name, out RebarHookType h) ? h : throw new ArgumentException("No rebar hook type named '" + name + "'");
        }

        // ------------------------------------------------------------------ welded wire mesh

        public void PlaceMeshes(List<MeshSpec> meshes, bool dryRun)
        {
            if (meshes.Count == 0) return;
            var sheetTypes = new Dictionary<string, ElementId>();
            ElementId areaType = ElementId.InvalidElementId;
            // As with bars, a dry run makes the types again inside each rolled-back transaction.
            void EnsureTypes()
            {
                sheetTypes.Clear();
                areaType = new FilteredElementCollector(_doc).OfClass(typeof(FabricAreaType)).FirstElementId();
                if (areaType == ElementId.InvalidElementId) areaType = FabricAreaType.CreateDefaultFabricAreaType(_doc);
                foreach (SheetSpec s in meshes.Select(m => m.Sheet).GroupBy(s => s.Name).Select(g => g.First()))
                    sheetTypes[s.Name] = SheetType(s);
            }
            if (!dryRun) Run("MCP: tipos de electromalla", false, EnsureTypes);
            foreach (MeshSpec m in meshes)
            {
                string error = null;
                bool ok = Run("MCP: electromalla " + m.Element, dryRun, () =>
                {
                    if (dryRun) EnsureTypes();
                    error = CreateMesh(m, areaType, sheetTypes);
                    if (error != null) throw new InvalidOperationException(error);
                });
                if (ok) MeshesCreated++;
                else Errors.Add((m.Element + " " + m.Description).Trim() + ": " + (error ?? Warnings.LastOrDefault()));
            }
        }

        private ElementId SheetType(SheetSpec s)
        {
            var existing = new FilteredElementCollector(_doc).OfClass(typeof(FabricSheetType)).Cast<FabricSheetType>().FirstOrDefault(t => t.Name == s.Name);
            if (existing != null) return existing.Id;
            string wireName = "Alambre " + s.WireMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm";
            var wire = new FilteredElementCollector(_doc).OfClass(typeof(FabricWireType)).Cast<FabricWireType>().FirstOrDefault(w => w.Name == wireName);
            if (wire == null)
            {
                wire = (FabricWireType)_doc.GetElement(FabricWireType.CreateDefaultFabricWireType(_doc));
                wire.Name = wireName;
                wire.WireDiameter = s.WireMm / 304.8;
            }
            var sheet = (FabricSheetType)_doc.GetElement(FabricSheetType.CreateDefaultFabricSheetType(_doc));
            sheet.Name = s.Name;
            sheet.MajorDirectionWireType = wire.Id;
            sheet.MinorDirectionWireType = wire.Id;
            double sp = Metric.Ft(s.Spacing);
            sheet.SetMajorLayoutAsActualSpacing(Metric.Ft(s.Width), sp / 2, sp);
            sheet.SetMinorLayoutAsActualSpacing(Metric.Ft(s.Length), sp / 2, sp);
            Notes.Add("created fabric sheet type " + s.Name);
            return sheet.Id;
        }

        private string CreateMesh(MeshSpec m, ElementId areaType, Dictionary<string, ElementId> sheetTypes)
        {
            try
            {
                if (m.Loops.Count == 0 || m.Loops[0].Count < 3) return "a mesh needs an outline of at least three points";
                Element host = _doc.GetElement(m.HostId);
                if (host == null)
                {
                    var o = m.Loops[0];
                    XYZ centre = o.Aggregate(XYZ.Zero, (s, q) => s + q) / o.Count;
                    host = Hosts.HostAt(centre);
                }
                if (host == null) return "no host for the mesh (give hostId)";
                var loops = m.Loops.Select(l =>
                {
                    var loop = new CurveLoop();
                    for (int i = 0; i < l.Count; i++)
                        loop.Append(Line.CreateBound(l[i] * Metric.FeetPerMeter, l[(i + 1) % l.Count] * Metric.FeetPerMeter));
                    return loop;
                }).ToList();
                XYZ origin = (m.Origin ?? m.Loops[0][0]) * Metric.FeetPerMeter;
                var area = FabricArea.Create(_doc, host, loops, m.MajorDirection, origin, areaType, sheetTypes[m.Sheet.Name]);
                area.FabricLocation = m.Top ? FabricLocation.TopOrExternal : FabricLocation.BottomOrInternal;
                area.MajorLapSpliceLength = Metric.Ft(m.Lap);
                area.MinorLapSpliceLength = Metric.Ft(m.Lap);
                RebarParameters.Set(area, RebarParameters.Element, m.Element);
                RebarParameters.Set(area, RebarParameters.Group, m.Group);
                RebarParameters.Set(area, RebarParameters.Size, m.Sheet.Name);
                RebarParameters.Set(area, RebarParameters.Description, m.Description);
                RebarParameters.Set(area, RebarParameters.MeshArea, Math.Round(RebarFrame.LoopArea(m.Loops), 3));
                if (m.Parameters != null)
                    foreach (var kv in m.Parameters.Properties())
                        Metric.Set(area.LookupParameter(kv.Name), kv.Value);
                RunMarks.Set(area, _options.RunMark);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ------------------------------------------------------------------ lap markers

        /// <summary>A rectangle with a diagonal on the plane of the spliced bars, grouped and named after the lap.</summary>
        public void PlaceMarkers(bool dryRun)
        {
            if (Laps.Count == 0) return;
            Run("MCP: marcadores de traslape", dryRun, () =>
            {
                GraphicsStyle style = RebarParameters.LapStyle(_doc);
                foreach (LapSplice lap in Laps)
                {
                    try
                    {
                        XYZ p0 = lap.Start * Metric.FeetPerMeter, p1 = lap.End * Metric.FeetPerMeter;
                        XYZ u = (p1 - p0).Normalize();
                        XYZ n = lap.Normal;
                        if (Math.Abs(u.DotProduct(n)) > 0.01)
                            n = u.CrossProduct(XYZ.BasisZ).GetLength() > 0.1 ? u.CrossProduct(XYZ.BasisZ).Normalize() : XYZ.BasisX;
                        double width = Metric.Ft(lap.Spacing * Math.Max(0, lap.Count - 1)), margin = Metric.Ft(0.035), end = Metric.Ft(0.02);
                        XYZ a = p0 - u * end - n * margin, b = p1 + u * end - n * margin;
                        XYZ c = b + n * (width + 2 * margin), e = a + n * (width + 2 * margin);
                        var plane = SketchPlane.Create(_doc, Plane.CreateByThreePoints(a, b, c));
                        var ids = new List<ElementId>();
                        XYZ[] corners = { a, b, c, e };
                        for (int i = 0; i < 4; i++)
                        {
                            ModelCurve mc = _doc.Create.NewModelCurve(Line.CreateBound(corners[i], corners[(i + 1) % 4]), plane);
                            mc.LineStyle = style;
                            ids.Add(mc.Id);
                        }
                        ModelCurve diagonal = _doc.Create.NewModelCurve(Line.CreateBound(a, c), plane);
                        diagonal.LineStyle = style;
                        ids.Add(diagonal.Id);
                        Group group = _doc.Create.NewGroup(ids);
                        string label = new string($"{lap.Id} - {RebarCatalog.Display(lap.Size)} {lap.Length:0.00} m - {lap.Element}"
                            .Where(ch => "{}[]|;<>?`~:\\".IndexOf(ch) < 0).ToArray());
                        group.GroupType.Name = "Traslape " + label;
                        RunMarks.Set(group, _options.RunMark);
                        MarkersCreated++;
                    }
                    catch (Exception ex)
                    {
                        Errors.Add("marker " + lap.Id + ": " + ex.Message);
                    }
                }
            });
        }
    }
}
