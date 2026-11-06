using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>Welded wire mesh sheet: wire, spacing and sheet size in metres (6x6-3/3 by default).</summary>
    public class SheetSpec
    {
        public string Name { get; set; } = "6x6-3/3";
        public double WireMm { get; set; } = 6.19;
        public double Spacing { get; set; } = 0.1524;
        public double Width { get; set; } = 2.35;
        public double Length { get; set; } = 6.00;

        public static SheetSpec From(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return new SheetSpec();
            if (t.Type == JTokenType.String) return new SheetSpec { Name = t.Value<string>() };
            var s = new SheetSpec();
            s.Name = t["name"]?.Value<string>() ?? s.Name;
            s.WireMm = t["wireMm"]?.Value<double>() ?? s.WireMm;
            s.Spacing = t["spacing"]?.Value<double>() ?? s.Spacing;
            s.Width = t["width"]?.Value<double>() ?? s.Width;
            s.Length = t["length"]?.Value<double>() ?? s.Length;
            return s;
        }

        /// <summary>Wire mass per square metre of sheet, both directions.</summary>
        public double KgPerSquareMeter => 2 / Spacing * 7850 * Math.PI * Math.Pow(WireMm / 1000, 2) / 4;
    }

    /// <summary>A fabric area: outline (and holes) in metres on the host face, sheet and lap.</summary>
    public class MeshSpec
    {
        public string Element { get; set; } = "";
        public string Group { get; set; } = "";
        public string Description { get; set; } = "";
        public List<List<XYZ>> Loops { get; set; } = new List<List<XYZ>>();
        public XYZ MajorDirection { get; set; } = XYZ.BasisX;
        public XYZ Origin { get; set; }
        public bool Top { get; set; } = true;
        public double Lap { get; set; } = 0.20;
        public SheetSpec Sheet { get; set; } = new SheetSpec();
        public ElementId HostId { get; set; } = ElementId.InvalidElementId;
        public JObject Parameters { get; set; }

        public static MeshSpec From(JObject j)
        {
            var loops = (j["loops"] as JArray ?? throw new ArgumentException("every mesh needs loops (outline first, then holes)"))
                .Select(l => ((JArray)l).Select(p => { var a = (JArray)p; return new XYZ(a[0].Value<double>(), a[1].Value<double>(), a.Count > 2 ? a[2].Value<double>() : 0); }).ToList())
                .ToList();
            var m = new MeshSpec
            {
                Element = j["element"]?.Value<string>() ?? "",
                Group = j["group"]?.Value<string>() ?? "",
                Description = j["description"]?.Value<string>() ?? "",
                Loops = loops,
                Top = !string.Equals(j["location"]?.Value<string>(), "bottom", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(j["location"]?.Value<string>(), "interior", StringComparison.OrdinalIgnoreCase),
                Lap = j["lap"]?.Value<double>() ?? 0.20,
                Sheet = SheetSpec.From(j["sheet"]),
                Parameters = j["parameters"] as JObject
            };
            if (j["majorDirection"] is JArray md) m.MajorDirection = new XYZ(md[0].Value<double>(), md[1].Value<double>(), md.Count > 2 ? md[2].Value<double>() : 0).Normalize();
            if (j["origin"] is JArray o) m.Origin = new XYZ(o[0].Value<double>(), o[1].Value<double>(), o.Count > 2 ? o[2].Value<double>() : 0);
            long host = j["hostId"]?.Value<long>() ?? 0;
            if (host > 0) m.HostId = Utils.ElementIdExtensions.ToElementId(host);
            return m;
        }
    }
}
