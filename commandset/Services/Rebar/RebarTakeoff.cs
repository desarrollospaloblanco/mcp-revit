using System.IO;
using System.Text;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// Steel take-off of the rebar and fabric areas in a model: weight of every set in quintals of
    /// 100 lb from Revit's own bar lengths, split by a dividing line (e.g. the party wall of a duplex,
    /// shared bars half and half) and by level band (below the first level or hosted by a foundation
    /// = foundation), piece marks for identical bars, and the commercial bars needed per size by
    /// first-fit cutting of 6, 9 or 12 m stock. Writes it to parameters, builds the ACERO schedules
    /// and 3D views, and exports JSON and CSV for the Excel and report generators.
    /// </summary>
    public class RebarTakeoff
    {
        public class Split
        {
            public XYZ P0, P1;
            public string Left = "casa 1", Right = "casa 2";
            public double Band = 0.24;

            public static Split From(JToken t)
            {
                if (t == null || t.Type == JTokenType.Null) return null;
                var line = t["line"] as JArray ?? throw new ArgumentException("split.line must be [[x0,y0],[x1,y1]] in metres");
                return new Split
                {
                    P0 = new XYZ(line[0][0].Value<double>(), line[0][1].Value<double>(), 0),
                    P1 = new XYZ(line[1][0].Value<double>(), line[1][1].Value<double>(), 0),
                    Left = t["left"]?.Value<string>() ?? "casa 1",
                    Right = t["right"]?.Value<string>() ?? "casa 2",
                    Band = t["sharedBand"]?.Value<double>() ?? 0.24
                };
            }

            /// <summary>Left / right share of a point in plan; on the band it counts half for each.</summary>
            public (double left, double right) Share(XYZ p)
            {
                XYZ d = (P1 - P0).Normalize();
                double side = d.X * (p.Y - P0.Y) - d.Y * (p.X - P0.X);
                if (Math.Abs(side) <= Band / 2) return (0.5, 0.5);
                return side > 0 ? (1, 0) : (0, 1);
            }
        }

        public class Row
        {
            public long Id;
            public string Kind, Element, Group, Description, Size, PieceMark, Laps, Shape;
            public int Count;
            public double CutLength, TotalLength, Kg, Qq, Area, AreaWithLaps;
            public Dictionary<string, double> Zones = new Dictionary<string, double>();
            public Dictionary<string, double> Levels = new Dictionary<string, double>();
        }

        private readonly Document _doc;
        private readonly RebarCatalog _cat;
        private readonly Split _split;
        private readonly string _foundation;
        private readonly bool _meshByWires;
        private readonly List<(string name, double elevation)> _levels;

        public List<Row> Rows { get; } = new List<Row>();
        public List<string> Notes { get; } = new List<string>();

        public RebarTakeoff(Document doc, RebarCatalog catalog, Split split, string foundationName, IList<string> levelNames, bool meshByWires)
        {
            _doc = doc;
            _cat = catalog;
            _split = split;
            _foundation = string.IsNullOrWhiteSpace(foundationName) ? "Cimentación" : foundationName;
            _meshByWires = meshByWires;
            var all = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => (name: l.Name, elevation: l.Elevation * 0.3048)).OrderBy(l => l.elevation).ToList();
            _levels = levelNames == null || levelNames.Count == 0 ? all : all.Where(l => levelNames.Contains(l.name)).ToList();
            if (_levels.Count == 0) throw new ArgumentException("none of the levels given is in the model");
        }

        public IEnumerable<string> ZoneNames => _split == null ? new string[0] : new[] { _split.Left, _split.Right };
        public IEnumerable<string> LevelNames => new[] { _foundation }.Concat(_levels.Select(l => l.name));

        // ------------------------------------------------------------------ measuring

        public void Measure(IEnumerable<Element> rebars, IEnumerable<FabricArea> meshes)
        {
            foreach (Element e in rebars)
            {
                if (!(e is Autodesk.Revit.DB.Structure.Rebar r)) continue;
                var type = _doc.GetElement(r.GetTypeId()) as RebarBarType;
                if (type == null) continue;
                string size = RebarCatalog.KeyOf(type);
                double total = (r.get_Parameter(BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH)?.AsDouble() ?? 0) * 0.3048;
                double each = (r.get_Parameter(BuiltInParameter.REBAR_ELEM_LENGTH)?.AsDouble() ?? 0) * 0.3048;
                var row = new Row
                {
                    Id = r.Id.GetValue(),
                    Kind = "bar",
                    Element = Text(r, RebarParameters.Element),
                    Group = Text(r, RebarParameters.Group),
                    Description = Text(r, RebarParameters.Description),
                    Laps = Text(r, RebarParameters.Lap),
                    Size = size,
                    Shape = _doc.GetElement(r.get_Parameter(BuiltInParameter.REBAR_SHAPE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name ?? "",
                    Count = r.Quantity,
                    CutLength = Math.Round(each, 3),
                    TotalLength = total,
                    Kg = total * RebarCatalog.KgPerMeter(size)
                };
                row.Qq = row.Kg / RebarCatalog.KgPerQuintal;
                if (string.IsNullOrEmpty(row.Group)) row.Group = HostCategory(r.GetHostId());
                if (string.IsNullOrEmpty(row.Element)) row.Element = row.Group + " " + r.GetHostId().GetValue();
                Distribute(row, BarSamples(r), IsFoundation(r.GetHostId()));
                Rows.Add(row);
            }
            foreach (FabricArea fa in meshes)
            {
                var row = MeasureMesh(fa);
                if (row != null) Rows.Add(row);
            }
            AssignPieceMarks();
        }

        private static string Text(Element e, string name) => e.LookupParameter(name)?.AsString() ?? "";

        private string HostCategory(ElementId host)
        {
            Element h = _doc.GetElement(host);
            return h?.Category?.Name ?? "";
        }

        private bool IsFoundation(ElementId host) =>
            _doc.GetElement(host)?.Category?.Id.GetValue() == (long)BuiltInCategory.OST_StructuralFoundation;

        /// <summary>Points along the bars (metres) with the length each one stands for.</summary>
        private static List<(XYZ p, double w)> BarSamples(Autodesk.Revit.DB.Structure.Rebar r)
        {
            var samples = new List<(XYZ, double)>();
            int positions = r.NumberOfBarPositions;
            int step = Math.Max(1, positions / 20);
            bool layout = r.LayoutRule != RebarLayoutRule.Single;
            for (int i = 0; i < positions; i += step)
            {
                if (layout && ((i == 0 && !r.IncludeFirstBar) || (i == positions - 1 && !r.IncludeLastBar))) continue;
                IList<Curve> curves;
                try { curves = r.GetTransformedCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, i); }
                catch (Exception) { continue; }
                foreach (Curve c in curves)
                {
                    double len = c.Length * 0.3048;
                    for (int k = 0; k < 10; k++)
                        samples.Add((c.Evaluate((k + 0.5) / 10, true) * 0.3048, len / 10));
                }
            }
            return samples;
        }

        private void Distribute(Row row, List<(XYZ p, double w)> samples, bool foundation)
        {
            double total = samples.Sum(s => s.w);
            if (total <= 0) return;
            foreach (var (p, w) in samples)
            {
                double f = w / total;
                if (_split != null)
                {
                    var (l, rr) = _split.Share(p);
                    Add(row.Zones, _split.Left, l * f);
                    Add(row.Zones, _split.Right, rr * f);
                }
                Add(row.Levels, foundation ? _foundation : LevelAt(p.Z - 0.02), f);
            }
        }

        private static void Add(Dictionary<string, double> d, string key, double v)
        {
            d.TryGetValue(key, out double old);
            d[key] = old + v;
        }

        private string LevelAt(double z)
        {
            if (z < _levels[0].elevation) return _foundation;
            string name = _levels[0].name;
            foreach (var l in _levels)
                if (z >= l.elevation) name = l.name;
            return name;
        }

        private Row MeasureMesh(FabricArea fa)
        {
            var sheetType = _doc.GetElement(fa.FabricSheetTypeId) as FabricSheetType;
            if (sheetType == null) return null;
            var wire = _doc.GetElement(sheetType.MajorDirectionWireType) as FabricWireType;
            var minorWire = _doc.GetElement(sheetType.MinorDirectionWireType) as FabricWireType;
            double kgMajor = WireKg(wire), kgMinor = WireKg(minorWire);
            var sheets = fa.GetFabricSheetElementIds().Select(id => _doc.GetElement(id) as FabricSheet).Where(s => s != null).ToList();
            var row = new Row
            {
                Id = fa.Id.GetValue(),
                Kind = "mesh",
                Element = Text(fa, RebarParameters.Element),
                Group = Text(fa, RebarParameters.Group),
                Description = Text(fa, RebarParameters.Description),
                Size = sheetType.Name,
                Count = sheets.Count,
                Area = fa.LookupParameter(RebarParameters.MeshArea)?.AsDouble() ?? 0
            };
            Element host = _doc.GetElement(fa.HostId);
            if (string.IsNullOrEmpty(row.Group)) row.Group = host?.Category?.Name ?? "";
            if (string.IsNullOrEmpty(row.Element)) row.Element = row.Group + " " + fa.HostId.GetValue();

            double wiresKg = 0;
            var samples = new List<(XYZ, double)>();
            foreach (FabricSheet s in sheets)
            {
                // points along every wire, so a sheet that crosses the dividing line or a level is split by its wire length
                foreach (var (direction, kgm) in new[] { (WireDistributionDirection.Major, kgMajor), (WireDistributionDirection.Minor, kgMinor) })
                    foreach (Curve c in s.GetWireCenterlines(direction))
                    {
                        double kg = c.Length * 0.3048 * kgm;
                        wiresKg += kg;
                        for (int k = 0; k < 5; k++)
                            samples.Add((c.Evaluate((k + 0.5) / 5, true) * 0.3048, kg / 5));
                    }
            }
            if (samples.Count == 0)
            {
                BoundingBoxXYZ b = fa.get_BoundingBox(null) ?? host?.get_BoundingBox(null);
                if (b != null) samples.Add(((b.Min + b.Max) / 2 * 0.3048, 1));
            }

            double kgPerM2 = kgMajor / (sheetType.MajorSpacing * 0.3048) + kgMinor / (sheetType.MinorSpacing * 0.3048);
            double w = sheetType.OverallWidth * 0.3048, l = sheetType.OverallLength * 0.3048, lap = fa.MajorLapSpliceLength * 0.3048;
            double lapFactor = w > lap && l > lap ? w * l / ((w - lap) * (l - lap)) : 1;
            if (_meshByWires || row.Area <= 0)
            {
                if (!_meshByWires) Notes.Add($"{row.Element}: no '{RebarParameters.MeshArea}', weighed by its wires");
                row.Kg = wiresKg;
                row.AreaWithLaps = kgPerM2 > 0 ? wiresKg / kgPerM2 : 0;
            }
            else
            {
                row.AreaWithLaps = row.Area * lapFactor;
                row.Kg = row.AreaWithLaps * kgPerM2;
            }
            row.Qq = row.Kg / RebarCatalog.KgPerQuintal;
            row.TotalLength = kgMajor > 0 ? row.Kg / ((kgMajor + kgMinor) / 2) : 0;
            Distribute(row, samples, host?.Category?.Id.GetValue() == (long)BuiltInCategory.OST_StructuralFoundation);
            return row;
        }

        private static double WireKg(FabricWireType w)
        {
            if (w == null) return 0;
            double d = w.WireDiameter * 0.3048;
            return 7850 * Math.PI * d * d / 4;
        }

        /// <summary>Same size, same cut length (cm) and same Revit shape = same piece mark ("#4-03").</summary>
        private void AssignPieceMarks()
        {
            foreach (var bySize in Rows.Where(r => r.Kind == "bar").GroupBy(r => r.Size))
            {
                int n = 0;
                foreach (var piece in bySize.GroupBy(r => (Math.Round(r.CutLength, 2), r.Shape)).OrderByDescending(g => g.Key.Item1))
                {
                    n++;
                    foreach (Row r in piece) r.PieceMark = $"{bySize.Key}-{n:00}";
                }
            }
        }

        // ------------------------------------------------------------------ parameters

        public List<string> EnsureParameters()
        {
            var numbers = RebarParameters.Numbers.Concat(ZoneNames.Select(RebarParameters.WeightIn)).Concat(LevelNames.Select(RebarParameters.WeightIn));
            return RebarParameters.Ensure(_doc, RebarParameters.Texts, numbers);
        }

        public int WriteParameters()
        {
            int written = 0;
            foreach (Row row in Rows)
            {
                Element e = _doc.GetElement(Utils.ElementIdExtensions.ToElementId(row.Id));
                if (e == null) continue;
                RebarParameters.Set(e, RebarParameters.Weight, Math.Round(row.Qq, 4));
                if (row.Kind == "bar")
                {
                    RebarParameters.Set(e, RebarParameters.CutLength, row.CutLength);
                    RebarParameters.Set(e, RebarParameters.PieceMark, row.PieceMark);
                    // "Ø 6.2 mm" written by earlier versions becomes the key the take-off and its tables use ("ø6.2")
                    string size = Text(e, RebarParameters.Size);
                    if (string.IsNullOrEmpty(size) || (size != row.Size && RebarCatalog.TryKey(size, out string key) && key == row.Size))
                        RebarParameters.Set(e, RebarParameters.Size, row.Size);
                }
                else
                {
                    RebarParameters.Set(e, RebarParameters.Size, row.Size);
                }
                if (string.IsNullOrEmpty(Text(e, RebarParameters.Group))) RebarParameters.Set(e, RebarParameters.Group, row.Group);
                if (string.IsNullOrEmpty(Text(e, RebarParameters.Element))) RebarParameters.Set(e, RebarParameters.Element, row.Element);
                foreach (string z in ZoneNames)
                    RebarParameters.Set(e, RebarParameters.WeightIn(z), Math.Round(row.Qq * (row.Zones.TryGetValue(z, out double f) ? f : 0), 4));
                foreach (string l in LevelNames)
                    RebarParameters.Set(e, RebarParameters.WeightIn(l), Math.Round(row.Qq * (row.Levels.TryGetValue(l, out double f) ? f : 0), 4));
                written++;
            }
            return written;
        }

        // ------------------------------------------------------------------ summary and export

        /// <summary>Commercial bars per size by first-fit decreasing on the stock length.</summary>
        public object Cutting()
        {
            double stock = _cat.StockLength;
            var result = new List<object>();
            foreach (var bySize in Rows.Where(r => r.Kind == "bar").GroupBy(r => r.Size).OrderBy(g => RebarCatalog.DiameterMm(g.Key)))
            {
                var lengths = new List<double>();
                int over = 0;
                foreach (Row r in bySize)
                    for (int i = 0; i < r.Count; i++)
                    {
                        if (r.CutLength > stock + 1e-6) { over++; lengths.Add(stock); }
                        else lengths.Add(r.CutLength);
                    }
                lengths.Sort((a, b) => b.CompareTo(a));
                var bins = new List<double>();
                foreach (double l in lengths)
                {
                    int k = bins.FindIndex(free => free >= l - 1e-9);
                    if (k >= 0) bins[k] -= l;
                    else bins.Add(stock - l);
                }
                double used = lengths.Sum(), bought = bins.Count * stock;
                result.Add(new
                {
                    bar = bySize.Key,
                    bars = lengths.Count,
                    cutLength = Math.Round(used, 2),
                    stockBars = bins.Count,
                    stockLength = stock,
                    cuttingWaste = bought > 0 ? Math.Round((bought - used) / bought, 4) : 0,
                    longerThanStock = over
                });
            }
            return result;
        }

        public JObject Summary(double waste)
        {
            var bars = Rows.Where(r => r.Kind == "bar").ToList();
            var meshes = Rows.Where(r => r.Kind == "mesh").ToList();
            double barQq = bars.Sum(r => r.Qq), meshQq = meshes.Sum(r => r.Qq);
            JObject Totals(Func<Row, Dictionary<string, double>> pick, IEnumerable<string> names) =>
                new JObject(names.Select(n => new JProperty(n, new JObject
                {
                    ["bar"] = Math.Round(bars.Sum(r => r.Qq * Get(pick(r), n)), 4),
                    ["mesh"] = Math.Round(meshes.Sum(r => r.Qq * Get(pick(r), n)), 4)
                })));
            return new JObject
            {
                ["unit"] = "qq de 100 lb",
                ["waste"] = waste,
                ["barSets"] = bars.Count,
                ["bars"] = bars.Sum(r => r.Count),
                ["meshAreas"] = meshes.Count,
                ["bar"] = Exact(barQq, waste),
                ["mesh"] = Exact(meshQq, waste),
                ["total"] = Exact(barQq + meshQq, waste),
                ["bySize"] = new JArray(bars.GroupBy(r => r.Size).OrderBy(g => RebarCatalog.DiameterMm(g.Key)).Select(g => new JObject
                {
                    ["bar"] = g.Key,
                    ["bars"] = g.Sum(r => r.Count),
                    ["length"] = Math.Round(g.Sum(r => r.TotalLength), 2),
                    ["qq"] = Math.Round(g.Sum(r => r.Qq), 4),
                    ["qqRoundedUp"] = Up(g.Sum(r => r.Qq))
                })),
                ["byMesh"] = new JArray(meshes.GroupBy(r => r.Size).Select(g => new JObject
                {
                    ["mesh"] = g.Key,
                    ["area"] = Math.Round(g.Sum(r => r.Area), 2),
                    ["areaWithLaps"] = Math.Round(g.Sum(r => r.AreaWithLaps), 2),
                    ["qq"] = Math.Round(g.Sum(r => r.Qq), 4),
                    ["qqRoundedUp"] = Up(g.Sum(r => r.Qq))
                })),
                ["byZone"] = Totals(r => r.Zones, ZoneNames),
                ["byLevel"] = Totals(r => r.Levels, LevelNames),
                ["longestBar"] = bars.Count == 0 ? 0 : bars.Max(r => r.CutLength)
            };
        }

        private static double Get(Dictionary<string, double> d, string k) => d.TryGetValue(k, out double v) ? v : 0;

        /// <summary>Quintals are reported rounded up to the next whole one, each from its exact value.</summary>
        public static int Up(double qq) => (int)Math.Ceiling(qq - 1e-9);

        private static JObject Exact(double qq, double waste) => new JObject
        {
            ["net"] = Math.Round(qq, 4),
            ["withWaste"] = Math.Round(qq * (1 + waste), 4),
            ["netRoundedUp"] = Up(qq),
            ["withWasteRoundedUp"] = Up(qq * (1 + waste))
        };

        public string Export(string path, JObject summary, object cutting, object laps)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = new JObject
            {
                ["document"] = _doc.Title,
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                ["stockLength"] = _cat.StockLength,
                ["lapFactor"] = _cat.LapFactor,
                ["laps"] = new JObject(Rows.Where(r => r.Kind == "bar").Select(r => r.Size).Distinct()
                    .OrderBy(RebarCatalog.DiameterMm).Select(s => new JProperty(s, _cat.Lap(s)))),
                ["kgPerMeter"] = new JObject(Rows.Where(r => r.Kind == "bar").Select(r => r.Size).Distinct()
                    .OrderBy(RebarCatalog.DiameterMm).Select(s => new JProperty(s, RebarCatalog.KgPerMeter(s)))),
                ["zones"] = new JArray(ZoneNames),
                ["levels"] = new JArray(LevelNames),
                ["summary"] = summary,
                ["cutting"] = JToken.FromObject(cutting),
                ["lapSplices"] = JToken.FromObject(laps),
                ["rows"] = JToken.FromObject(Rows.Select(r => new
                {
                    id = r.Id, kind = r.Kind, element = r.Element, group = r.Group, description = r.Description, size = r.Size,
                    pieceMark = r.PieceMark, shape = r.Shape, laps = r.Laps, count = r.Count, cutLength = r.CutLength,
                    totalLength = Math.Round(r.TotalLength, 3), kg = Math.Round(r.Kg, 3), qq = Math.Round(r.Qq, 5),
                    area = Math.Round(r.Area, 3), areaWithLaps = Math.Round(r.AreaWithLaps, 3),
                    zones = r.Zones.ToDictionary(k => k.Key, k => Math.Round(k.Value, 4)),
                    levels = r.Levels.ToDictionary(k => k.Key, k => Math.Round(k.Value, 4))
                }))
            };
            File.WriteAllText(path, json.ToString(Formatting.Indented), new UTF8Encoding(false));

            string csv = Path.ChangeExtension(path, ".csv");
            var sb = new StringBuilder();
            var zones = ZoneNames.ToList();
            var levels = LevelNames.ToList();
            sb.AppendLine(string.Join(";", new[] { "Id", "Tipo", "Elemento", "Grupo", "Descripción", "Diámetro o malla", "Marca pieza", "Traslape", "Cantidad",
                "Largo de corte m", "Longitud total m", "Área m2", "Área con traslapes m2", "kg", "qq" }
                .Concat(zones.Select(z => "qq " + z)).Concat(levels.Select(l => "qq " + l))));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (Row r in Rows)
            {
                var cells = new List<string>
                {
                    r.Id.ToString(), r.Kind, Csv(r.Element), Csv(r.Group), Csv(r.Description), Csv(r.Size),
                    r.PieceMark ?? "", r.Laps ?? "", r.Count.ToString(), r.CutLength.ToString("0.###", inv), r.TotalLength.ToString("0.###", inv),
                    r.Area.ToString("0.###", inv), r.AreaWithLaps.ToString("0.###", inv), r.Kg.ToString("0.###", inv), r.Qq.ToString("0.#####", inv)
                };
                cells.AddRange(zones.Select(z => (r.Qq * Get(r.Zones, z)).ToString("0.#####", inv)));
                cells.AddRange(levels.Select(l => (r.Qq * Get(r.Levels, l)).ToString("0.#####", inv)));
                sb.AppendLine(string.Join(";", cells));
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
            return csv;
        }

        private static string Csv(string s) => s != null && (s.Contains(";") || s.Contains("\"")) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s ?? "";

        /// <summary>Lap ids read back from the rebar, with what they join.</summary>
        public object LapsFromModel() =>
            Rows.Where(r => r.Kind == "bar" && !string.IsNullOrEmpty(r.Laps))
                .SelectMany(r => r.Laps.Split(',').Select(id => new { id = id.Trim(), r }))
                .GroupBy(x => x.id).OrderBy(g => g.Key)
                .Select(g => new
                {
                    id = g.Key,
                    element = g.First().r.Element,
                    description = System.Text.RegularExpressions.Regex.Replace(g.First().r.Description ?? "", @"\s*\(pieza \d+/\d+\)$", ""),
                    bar = g.First().r.Size,
                    lap = _cat.Lap(g.First().r.Size),
                    count = g.First().r.Count,
                    pieces = g.Select(x => x.r.CutLength).ToList()
                }).ToList();
    }
}
