namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    /// Outlines of groups of rooms (apartments) from their boundary segments, in plan coordinates.
    ///
    /// Every segment is split at every vertex lying on it, so rooms of the same group share identical
    /// elementary pieces. A piece used by two rooms of the same group is interior and dropped; what is
    /// left is the outline of the group. Outlines of all groups are merged (a wall between two
    /// apartments is kept once) and collinear pieces are joined back into longer lines.
    /// </summary>
    public class AreaOutlineBuilder
    {
        private readonly double _tolerance;
        private readonly List<Segment> _segments = new List<Segment>();

        public AreaOutlineBuilder(double tolerance)
        {
            _tolerance = tolerance;
        }

        private struct Segment
        {
            public string Group;
            public long Room;
            public bool Outer;
            public (long, long) A, B;
        }

        public void Add(string group, long room, bool outerLoop, double x0, double y0, double x1, double y1)
        {
            var a = Key(x0, y0);
            var b = Key(x1, y1);
            if (a != b)
                _segments.Add(new Segment { Group = group, Room = room, Outer = outerLoop, A = a, B = b });
        }

        /// <summary>Lines (x0, y0, x1, y1) of the merged outlines.</summary>
        public List<double[]> Build(bool includeHoles)
        {
            var vertices = new HashSet<(long, long)>();
            foreach (var s in _segments) { vertices.Add(s.A); vertices.Add(s.B); }
            var vertexList = vertices.ToList();

            // (group, piece) -> rooms using it, and whether any of them uses it on its outer loop
            var rooms = new Dictionary<(string, (long, long), (long, long)), HashSet<long>>();
            var outer = new HashSet<(string, (long, long), (long, long))>();
            foreach (var s in _segments)
            {
                var points = SplitPoints(s, vertexList);
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    if (points[i] == points[i + 1]) continue;
                    var piece = Ordered(points[i], points[i + 1]);
                    var key = (s.Group, piece.Item1, piece.Item2);
                    if (!rooms.TryGetValue(key, out var set)) rooms[key] = set = new HashSet<long>();
                    set.Add(s.Room);
                    if (s.Outer) outer.Add(key);
                }
            }

            // Inner loops (columns, shafts) are holes; a piece that only an inner loop uses is left out
            // unless holes are wanted. A closet inside a bedroom still cancels: the closet's outer loop
            // shares the piece with the bedroom's inner loop.
            // Rooms of a group sharing a piece belong to the same apartment.
            _parent.Clear();
            foreach (var kv in rooms)
            {
                long first = kv.Value.First();
                foreach (long other in kv.Value) Union(first, other);
            }

            var outline = new HashSet<((long, long), (long, long))>();
            foreach (var kv in rooms)
            {
                if (kv.Value.Count == 1 && (includeHoles || outer.Contains(kv.Key)))
                    outline.Add((kv.Key.Item2, kv.Key.Item3));
            }
            return MergeCollinear(outline);
        }

        public int SegmentCount => _segments.Count;

        /// <summary>After Build: a representative room of the connected set of rooms the given room belongs to.</summary>
        public long Component(long room) => Find(room);

        private readonly Dictionary<long, long> _parent = new Dictionary<long, long>();

        private long Find(long room)
        {
            if (!_parent.TryGetValue(room, out long parent)) return room;
            if (parent == room) return room;
            long root = Find(parent);
            _parent[room] = root;
            return root;
        }

        private void Union(long a, long b)
        {
            if (!_parent.ContainsKey(a)) _parent[a] = a;
            if (!_parent.ContainsKey(b)) _parent[b] = b;
            long ra = Find(a), rb = Find(b);
            if (ra != rb) _parent[ra] = rb;
        }

        private (long, long) Key(double x, double y) => ((long)Math.Round(x / _tolerance), (long)Math.Round(y / _tolerance));

        private static ((long, long), (long, long)) Ordered((long, long) p, (long, long) q) =>
            p.CompareTo(q) <= 0 ? (p, q) : (q, p);

        private List<(long, long)> SplitPoints(Segment s, List<(long, long)> vertices)
        {
            double x0 = s.A.Item1, y0 = s.A.Item2, x1 = s.B.Item1, y1 = s.B.Item2;
            double dx = x1 - x0, dy = y1 - y0, length = Math.Sqrt(dx * dx + dy * dy);
            long minX = Math.Min(s.A.Item1, s.B.Item1) - 1, maxX = Math.Max(s.A.Item1, s.B.Item1) + 1;
            long minY = Math.Min(s.A.Item2, s.B.Item2) - 1, maxY = Math.Max(s.A.Item2, s.B.Item2) + 1;

            var cuts = new List<double> { 0, 1 };
            foreach (var v in vertices)
            {
                if (v.Item1 < minX || v.Item1 > maxX || v.Item2 < minY || v.Item2 > maxY) continue;
                double cross = dx * (v.Item2 - y0) - dy * (v.Item1 - x0);
                if (Math.Abs(cross) / length > 1) continue; // more than one grid step off the line
                double t = ((v.Item1 - x0) * dx + (v.Item2 - y0) * dy) / (length * length);
                if (t > 1 / length && t < 1 - 1 / length) cuts.Add(t);
            }
            cuts.Sort();
            return cuts.Select(t => ((long)Math.Round(x0 + dx * t), (long)Math.Round(y0 + dy * t))).ToList();
        }

        private List<double[]> MergeCollinear(HashSet<((long, long), (long, long))> pieces)
        {
            var adjacent = new Dictionary<(long, long), List<(long, long)>>();
            foreach (var (p, q) in pieces)
            {
                if (!adjacent.TryGetValue(p, out var lp)) adjacent[p] = lp = new List<(long, long)>();
                if (!adjacent.TryGetValue(q, out var lq)) adjacent[q] = lq = new List<(long, long)>();
                lp.Add(q);
                lq.Add(p);
            }

            var used = new HashSet<((long, long), (long, long))>();
            var lines = new List<double[]>();
            foreach (var piece in pieces.OrderBy(p => p.Item1).ThenBy(p => p.Item2))
            {
                if (!used.Add(piece)) continue;
                var d = Direction(piece.Item1, piece.Item2);
                var start = Extend(piece.Item1, Negate(d), adjacent, used);
                var end = Extend(piece.Item2, d, adjacent, used);
                lines.Add(new[] { start.Item1 * _tolerance, start.Item2 * _tolerance, end.Item1 * _tolerance, end.Item2 * _tolerance });
            }
            return lines;
        }

        /// <summary>Walks from a point while the next unused piece keeps the direction.</summary>
        private static (long, long) Extend((long, long) from, (double, double) d,
            Dictionary<(long, long), List<(long, long)>> adjacent, HashSet<((long, long), (long, long))> used)
        {
            var current = from;
            while (true)
            {
                (long, long)? next = null;
                foreach (var q in adjacent[current])
                {
                    var piece = Ordered(current, q);
                    if (used.Contains(piece)) continue;
                    var dq = Direction(current, q);
                    // about one degree: short pieces snapped to the grid lean slightly
                    bool sameDirection = dq.Item1 * d.Item1 + dq.Item2 * d.Item2 > 0 &&
                                         Math.Abs(dq.Item1 * d.Item2 - dq.Item2 * d.Item1) < 0.02;
                    if (sameDirection)
                    {
                        used.Add(piece);
                        next = q;
                        break;
                    }
                }
                if (next == null) return current;
                current = next.Value;
            }
        }

        private static (double, double) Direction((long, long) p, (long, long) q)
        {
            double dx = q.Item1 - p.Item1, dy = q.Item2 - p.Item2, n = Math.Sqrt(dx * dx + dy * dy);
            return (dx / n, dy / n);
        }

        private static (double, double) Negate((double, double) d) => (-d.Item1, -d.Item2);
    }
}
