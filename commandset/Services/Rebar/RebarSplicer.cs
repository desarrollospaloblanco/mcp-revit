namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// Cuts bars longer than the commercial length into pieces joined by lap splices, with every splice
    /// inside an allowed zone (away from joints) when there is one. Successive sets of the same element
    /// splice from alternate ends so their laps do not line up. Works in metres and needs no document.
    /// </summary>
    public class RebarSplicer
    {
        private readonly RebarCatalog _catalog;
        private readonly Dictionary<string, int> _alternate = new Dictionary<string, int>();
        private int _nextLap;

        public List<LapSplice> Laps { get; } = new List<LapSplice>();
        public List<string> Notes { get; } = new List<string>();

        public RebarSplicer(RebarCatalog catalog, int firstLapNumber)
        {
            _catalog = catalog;
            _nextLap = Math.Max(1, firstLapNumber);
        }

        /// <summary>
        /// Free-span splice zones on a straight run of length L: the middle part of each clear span
        /// between joints (each joint an interval along the run), keeping frac of the span and 2h
        /// clear of each face (ACI 318 18.6.3.3 for beams, the central half for columns).
        /// </summary>
        public static List<double[]> ZonesBetween(IEnumerable<double[]> joints, double length, double depth = 0, double frac = 0.25)
        {
            var faces = new List<double[]> { new[] { -1e9, 0.0 } };
            faces.AddRange(joints.OrderBy(j => j[0]));
            faces.Add(new[] { length, 1e9 });
            var zones = new List<double[]>();
            for (int i = 0; i + 1 < faces.Count; i++)
            {
                double f0 = Math.Max(faces[i][1], 0), f1 = Math.Min(faces[i + 1][0], length), span = f1 - f0;
                if (span <= 0) continue;
                double margin = Math.Max(frac * span, 2 * depth);
                if (f1 - margin > f0 + margin) zones.Add(new[] { f0 + margin, f1 - margin });
            }
            return zones;
        }

        /// <summary>The set itself when it fits, or its pieces with lap ids; laps go to Laps.</summary>
        public List<BarSpec> Split(BarSpec spec, List<double[]> zones)
        {
            if (spec.Stirrup || spec.Length <= _catalog.StockLength + 1e-6)
                return new List<BarSpec> { spec };

            string key = spec.Element ?? "";
            _alternate.TryGetValue(key, out int turn);
            _alternate[key] = turn + 1;
            if (turn % 2 == 1)
            {
                // this set splices from the other end, so laps of neighbouring sets are staggered
                var reversed = spec.Copy();
                reversed.Points = Enumerable.Reverse(spec.Points).ToList();
                double main = LongestSegment(reversed.Points, out _);
                zones = zones?.Select(z => new[] { main - z[1], main - z[0] }).ToList();
                return Cut(reversed, zones);
            }
            return Cut(spec, zones);
        }

        private static double LongestSegment(List<XYZ> pts, out int index)
        {
            index = 0;
            double best = -1;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double l = pts[i].DistanceTo(pts[i + 1]);
                if (l > best) { best = l; index = i; }
            }
            return best;
        }

        private List<BarSpec> Cut(BarSpec spec, List<double[]> zones)
        {
            var pts = spec.Points;
            double mainLength = LongestSegment(pts, out int k);
            XYZ a = pts[k], b = pts[k + 1], u = (b - a).Normalize();
            var before = pts.Take(k).ToList();
            var after = pts.Skip(k + 2).ToList();
            double lead = 0, tail = 0;
            for (int i = 0; i < k; i++) lead += pts[i].DistanceTo(pts[i + 1]);
            for (int i = k + 1; i + 1 < pts.Count; i++) tail += pts[i].DistanceTo(pts[i + 1]);

            double lap = _catalog.Lap(spec.Size), stock = _catalog.StockLength;
            if (lap >= stock / 2) throw new ArgumentException($"lap {lap} m of {spec.Size} does not fit in {stock} m bars");
            var allowed = (zones ?? new List<double[]>())
                .Where(z => z[1] - z[0] >= lap)
                .Select(z => new[] { z[0] + lap / 2, z[1] - lap / 2 })
                .OrderBy(z => z[0]).ToList();
            if (allowed.Count == 0)
            {
                allowed.Add(new[] { 0.25 * mainLength, 0.75 * mainLength });
                Notes.Add($"{spec.Element} {spec.Description}: no splice zone given or found, splices go in the middle half");
            }

            var cuts = new List<(double at, bool ok)>();
            double start = -lead;
            for (int guard = 0; mainLength + tail - start > stock + 1e-6 && guard < 100; guard++)
            {
                double limit = start + stock - lap / 2;
                var candidates = allowed
                    .Select(z => new { z, c = Math.Min(z[1], limit) })
                    .Where(x => x.c >= x.z[0] && x.c - lap / 2 > start + 1.0)
                    .Select(x => x.c).ToList();
                double at;
                bool ok = candidates.Count > 0;
                if (ok) at = candidates.Max();
                else
                {
                    at = Math.Min(limit, mainLength - lap);
                    if (at - lap / 2 <= start + 0.5) at = limit;
                    Notes.Add($"{spec.Element} {spec.Description}: splice outside the allowed zones at {at:0.00} m");
                }
                cuts.Add((at, ok));
                start = at - lap / 2;
            }

            int first = _nextLap;
            _nextLap += cuts.Count;
            var pieces = new List<BarSpec>();
            for (int i = 0; i <= cuts.Count; i++)
            {
                var seg = new List<XYZ>();
                if (i == 0) { seg.AddRange(before); seg.Add(a); }
                else seg.Add(a + u * (cuts[i - 1].at - lap / 2));
                if (i == cuts.Count) { seg.Add(b); seg.AddRange(after); }
                else seg.Add(a + u * (cuts[i].at + lap / 2));
                var piece = spec.Copy();
                piece.Points = seg;
                piece.Simplify();
                piece.Description = $"{spec.Description} (pieza {i + 1}/{cuts.Count + 1})";
                piece.Laps = string.Join(",", new[] { i - 1, i }.Where(j => j >= 0 && j < cuts.Count).Select(j => LapId(first + j)));
                pieces.Add(piece);
            }
            for (int j = 0; j < cuts.Count; j++)
            {
                Laps.Add(new LapSplice
                {
                    Id = LapId(first + j),
                    Element = spec.Element,
                    Description = spec.Description,
                    Size = spec.Size,
                    Count = spec.Count,
                    Length = lap,
                    Spacing = spec.Spacing,
                    Start = a + u * (cuts[j].at - lap / 2),
                    End = a + u * (cuts[j].at + lap / 2),
                    Normal = spec.Normal,
                    InAllowedZone = cuts[j].ok
                });
            }
            return pieces;
        }

        public static string LapId(int n) => "T" + n.ToString("000");
    }
}
