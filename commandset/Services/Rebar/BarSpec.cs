using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Common;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// One set of equal bars, in metres: the polyline of the first bar (hook legs included as
    /// segments), the direction the set repeats in, how many bars and how far apart.
    /// </summary>
    public class BarSpec
    {
        public string Element { get; set; } = "";
        public string Group { get; set; } = "";
        public string Description { get; set; } = "";
        public string Size { get; set; }
        public List<XYZ> Points { get; set; } = new List<XYZ>();
        public XYZ Normal { get; set; } = XYZ.BasisZ;
        public int Count { get; set; } = 1;
        public double Spacing { get; set; }
        public bool Stirrup { get; set; }
        public ElementId HostId { get; set; } = ElementId.InvalidElementId;
        public string StartHook { get; set; }
        public string EndHook { get; set; }
        /// <summary>Where a splice centre may fall, as distances along the longest segment from its start.</summary>
        public List<double[]> SpliceZones { get; set; }
        /// <summary>Lap ids this piece takes part in ("T004,T005").</summary>
        public string Laps { get; set; } = "";
        public JObject Parameters { get; set; }

        public double Length
        {
            get
            {
                double l = 0;
                for (int i = 0; i + 1 < Points.Count; i++) l += Points[i].DistanceTo(Points[i + 1]);
                return l;
            }
        }

        public BarSpec Copy() => (BarSpec)MemberwiseClone();

        public static BarSpec From(JObject j, RebarCatalog catalog)
        {
            var points = (j["points"] as JArray ?? throw new ArgumentException("every bar needs points [[x,y,z], ...] in metres"))
                .Select(p => { var a = (JArray)p; return new XYZ(a[0].Value<double>(), a[1].Value<double>(), a.Count > 2 ? a[2].Value<double>() : 0); })
                .ToList();
            bool closed = j["closed"]?.Value<bool>() ?? false;
            if (closed && points.Count > 2 && !points[0].IsAlmostEqualTo(points[points.Count - 1])) points.Add(points[0]);
            var spec = new BarSpec
            {
                Element = j["element"]?.Value<string>() ?? "",
                Group = j["group"]?.Value<string>() ?? "",
                Description = j["description"]?.Value<string>() ?? "",
                Size = RebarCatalog.Key(j["bar"]?.Value<string>() ?? throw new ArgumentException("every bar needs bar (size)")),
                Points = points,
                Normal = Vector(j["normal"]) ?? XYZ.BasisZ,
                Count = Math.Max(1, j["count"]?.Value<int>() ?? 1),
                Spacing = j["spacing"]?.Value<double>() ?? 0,
                Stirrup = closed || (j["style"]?.Value<string>() ?? "").Equals("stirrup", StringComparison.OrdinalIgnoreCase),
                StartHook = j["startHook"]?.Value<string>(),
                EndHook = j["endHook"]?.Value<string>(),
                SpliceZones = (j["spliceZones"] as JArray)?.Select(z => new[] { z[0].Value<double>(), z[1].Value<double>() }).ToList(),
                Parameters = j["parameters"] as JObject
            };
            long host = j["hostId"]?.Value<long>() ?? 0;
            if (host > 0) spec.HostId = Utils.ElementIdExtensions.ToElementId(host);
            spec.Simplify();
            return spec;
        }

        private static XYZ Vector(JToken t)
        {
            if (!(t is JArray a) || a.Count < 3) return null;
            var v = new XYZ(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>());
            return v.GetLength() < 1e-9 ? null : v.Normalize();
        }

        /// <summary>Drops repeated points and collinear middle points: Revit rejects two consecutive segments on one line.</summary>
        public void Simplify()
        {
            var output = new List<XYZ>();
            foreach (XYZ p in Points)
                if (output.Count == 0 || output[output.Count - 1].DistanceTo(p) > 1e-4) output.Add(p);
            bool changed = true;
            while (changed && output.Count > 2)
            {
                changed = false;
                for (int i = 1; i < output.Count - 1; i++)
                {
                    XYZ a = (output[i] - output[i - 1]).Normalize(), b = (output[i + 1] - output[i]).Normalize();
                    if (a.DotProduct(b) > 0.9999) { output.RemoveAt(i); changed = true; break; }
                }
            }
            Points = output;
        }

        public JObject Describe() => new JObject
        {
            ["element"] = Element,
            ["group"] = Group,
            ["description"] = Description,
            ["bar"] = Size,
            ["count"] = Count,
            ["length"] = Math.Round(Length, 3),
            ["laps"] = Laps
        };
    }

    /// <summary>A lap splice: where two pieces of a set overlap, in metres.</summary>
    public class LapSplice
    {
        public string Id { get; set; }
        public string Element { get; set; }
        public string Description { get; set; }
        public string Size { get; set; }
        public int Count { get; set; }
        public double Length { get; set; }
        public double Spacing { get; set; }
        public XYZ Start { get; set; }
        public XYZ End { get; set; }
        public XYZ Normal { get; set; }
        public bool InAllowedZone { get; set; }

        public object Describe() => new
        {
            id = Id,
            element = Element,
            description = Description,
            bar = Size,
            count = Count,
            lap = Length,
            start = Metric.Out(Start * Metric.FeetPerMeter),
            end = Metric.Out(End * Metric.FeetPerMeter),
            inAllowedZone = InAllowedZone
        };
    }
}
