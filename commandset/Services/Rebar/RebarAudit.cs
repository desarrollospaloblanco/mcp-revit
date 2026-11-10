using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// Checks that the steel stays inside the concrete: the polyline of every bar of every set (hook legs
    /// drawn as segments included) is intersected with the solids of the walls, slabs, beams, columns,
    /// foundations and stairs, and the sets with more than MaxOutside of their length outside are listed.
    /// Hooks given only as hook types and welded mesh are not measured.
    /// </summary>
    public static class RebarAudit
    {
        public const double MaxOutside = 0.05;

        public static object Check(RebarConcrete concrete, IEnumerable<BarSpec> pieces, int listLimit = 200)
        {
            double total = 0, outside = 0;
            int checkedSets = 0, hookTypes = 0;
            var flagged = new List<(BarSpec spec, double length, double outside)>();
            foreach (BarSpec p in pieces)
            {
                if (p.Points.Count < 2) continue;
                checkedSets++;
                if (!string.IsNullOrWhiteSpace(p.StartHook) || !string.IsNullOrWhiteSpace(p.EndHook)) hookTypes++;
                double length = 0, inside = 0;
                for (int k = 0; k < p.Count; k++)
                {
                    XYZ shift = p.Normal * (p.Spacing * k);
                    for (int i = 0; i + 1 < p.Points.Count; i++)
                    {
                        XYZ a = p.Points[i] + shift, b = p.Points[i + 1] + shift;
                        length += a.DistanceTo(b);
                        inside += concrete.InsideLength(a, b);
                    }
                }
                double off = Math.Max(0, length - inside);
                total += length;
                outside += off;
                if (length > 0 && off / length > MaxOutside) flagged.Add((p, length, off));
            }
            var notes = new List<string>();
            if (hookTypes > 0) notes.Add($"{hookTypes} sets end in hook types; those hooks are not measured");
            if (concrete.FailedSolids > 0) notes.Add($"{concrete.FailedSolids} solids could not be intersected and count as no concrete");
            return new
            {
                maxOutsideFraction = MaxOutside,
                setsChecked = checkedSets,
                totalLength = Math.Round(total, 2),
                outsideLength = Math.Round(outside, 2),
                outsidePercent = total > 0 ? Math.Round(100 * outside / total, 2) : 0,
                setsOutside = flagged.Count,
                list = flagged.OrderByDescending(f => f.outside / f.length).Take(listLimit).Select(f => new
                {
                    element = f.spec.Element,
                    description = f.spec.Description,
                    bar = f.spec.Size,
                    count = f.spec.Count,
                    hostId = f.spec.HostId.GetValue(),
                    length = Math.Round(f.length, 3),
                    outside = Math.Round(f.outside, 3),
                    outsidePercent = Math.Round(100 * f.outside / f.length, 1),
                    laps = f.spec.Laps
                }),
                notes
            };
        }
    }
}
