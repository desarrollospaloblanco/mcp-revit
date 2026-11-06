using Autodesk.Revit.DB.Structure;

namespace RevitMCPCommandSet.Services.Rebar
{
    /// <summary>
    /// Bar sizes by name ("#4", "No.4", "ø7.2", "7.2mm", "12mm"), with their diameter, weight per metre,
    /// lap length and hook leg. The defaults are the ones used in the DPB steel take-offs: ASTM weights
    /// for imperial bars, 7850 kg/m³ for wire, laps of 40 db rounded to 0.05 m and hooks from a table.
    /// </summary>
    public class RebarCatalog
    {
        public const double KgPerQuintal = 45.359237;

        private static readonly Dictionary<int, double> ImperialMm = new Dictionary<int, double>
        {
            { 2, 6.35 }, { 3, 9.525 }, { 4, 12.7 }, { 5, 15.875 }, { 6, 19.05 }, { 7, 22.225 }, { 8, 25.4 },
            { 9, 28.65 }, { 10, 32.26 }, { 11, 35.81 }
        };

        private static readonly Dictionary<int, double> ImperialKgM = new Dictionary<int, double>
        {
            { 2, 0.249 }, { 3, 0.560 }, { 4, 0.994 }, { 5, 1.552 }, { 6, 2.235 }, { 7, 3.042 }, { 8, 3.973 },
            { 9, 5.060 }, { 10, 6.404 }, { 11, 7.907 }
        };

        private static readonly Dictionary<int, double> ImperialHook = new Dictionary<int, double>
        {
            { 2, 0.10 }, { 3, 0.15 }, { 4, 0.20 }, { 5, 0.25 }, { 6, 0.30 }, { 7, 0.35 }, { 8, 0.40 },
            { 9, 0.45 }, { 10, 0.50 }, { 11, 0.55 }
        };

        public double StockLength { get; }
        public double LapFactor { get; }
        private readonly Dictionary<string, double> _lapOverrides;
        private readonly Dictionary<string, double> _hookOverrides;

        public RebarCatalog(double stockLength, double lapFactor, Dictionary<string, double> lapOverrides, Dictionary<string, double> hookOverrides)
        {
            if (stockLength <= 1) throw new ArgumentException("stockLength must be the commercial bar length in metres (6, 9 or 12)");
            StockLength = stockLength;
            LapFactor = lapFactor > 0 ? lapFactor : 40;
            _lapOverrides = (lapOverrides ?? new Dictionary<string, double>()).ToDictionary(k => Key(k.Key), k => k.Value);
            _hookOverrides = (hookOverrides ?? new Dictionary<string, double>()).ToDictionary(k => Key(k.Key), k => k.Value);
        }

        /// <summary>Canonical key: "#4" for imperial sizes, "ø7.2" for millimetre bars and wire.</summary>
        public static string Key(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("bar size is empty");
            string s = name.Trim().ToLowerInvariant().Replace(" ", "").Replace(",", ".");
            if (s.StartsWith("#")) return "#" + int.Parse(s.Substring(1));
            if (s.StartsWith("no.")) return "#" + int.Parse(s.Substring(3));
            if (s.StartsWith("no")) return "#" + int.Parse(s.Substring(2));
            s = s.TrimStart('ø', 'Ø', 'φ', 'd').Replace("mm", "");
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mm))
                return "ø" + mm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            throw new ArgumentException("Unknown bar size '" + name + "'; use #3, No.4, ø7.2 or 12mm");
        }

        public static bool IsImperial(string key) => key.StartsWith("#");

        public static double DiameterMm(string key)
        {
            if (IsImperial(key))
            {
                int n = int.Parse(key.Substring(1));
                return ImperialMm.TryGetValue(n, out double mm) ? mm : n * 25.4 / 8;
            }
            return double.Parse(key.Substring(1), System.Globalization.CultureInfo.InvariantCulture);
        }

        public static double KgPerMeter(string key)
        {
            if (IsImperial(key) && ImperialKgM.TryGetValue(int.Parse(key.Substring(1)), out double kg)) return kg;
            double d = DiameterMm(key) / 1000;
            return Math.Round(7850 * Math.PI * d * d / 4, 4);
        }

        /// <summary>Lap splice length in metres: the override, or LapFactor × db rounded to the nearest 0.05.</summary>
        public double Lap(string key)
        {
            if (_lapOverrides.TryGetValue(key, out double lap)) return lap;
            return Math.Max(0.15, Math.Round(LapFactor * DiameterMm(key) / 1000 / 0.05, MidpointRounding.AwayFromZero) * 0.05);
        }

        /// <summary>Length of a 90° hook leg in metres.</summary>
        public double Hook(string key)
        {
            if (_hookOverrides.TryGetValue(key, out double hook)) return hook;
            if (IsImperial(key) && ImperialHook.TryGetValue(int.Parse(key.Substring(1)), out double h)) return h;
            return Math.Max(0.08, Math.Ceiling(12 * DiameterMm(key) / 1000 / 0.05) * 0.05);
        }

        /// <summary>Display name used in parameters and schedules: "#4" or "Ø 7.2 mm".</summary>
        public static string Display(string key) => IsImperial(key) ? key : "Ø " + key.Substring(1) + " mm";

        /// <summary>Bar type name created when the model has none of that size: "No.4" or "7.2mm".</summary>
        public static string TypeName(string key) => IsImperial(key) ? "No." + key.Substring(1) : key.Substring(1) + "mm";

        // ------------------------------------------------------------------ bar types in the model

        public static double TypeDiameterMm(RebarBarType t)
        {
#if REVIT2023_OR_GREATER
            return t.BarNominalDiameter * 304.8;
#else
            return t.BarDiameter * 304.8;
#endif
        }

        /// <summary>Size key of a bar type in the model, by name first and by diameter second.</summary>
        public static string KeyOf(RebarBarType t)
        {
            try { return Key(t.Name); }
            catch (Exception) { }
            double mm = TypeDiameterMm(t);
            foreach (var kv in ImperialMm)
                if (Math.Abs(kv.Value - mm) < 0.3) return "#" + kv.Key;
            return "ø" + Math.Round(mm, 1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Finds the bar type of that size (name or diameter) or creates it; call inside a transaction.</summary>
        public static RebarBarType BarType(Document doc, string key, Dictionary<string, RebarBarType> cache, List<string> notes)
        {
            if (cache.TryGetValue(key, out RebarBarType cached)) return cached;
            double mm = DiameterMm(key);
            var all = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            RebarBarType found = all.FirstOrDefault(t => { try { return Key(t.Name) == key; } catch (Exception) { return false; } })
                                 ?? all.FirstOrDefault(t => Math.Abs(TypeDiameterMm(t) - mm) < 0.05);
            if (found == null)
            {
                found = RebarBarType.Create(doc);
                found.Name = TypeName(key);
#if REVIT2023_OR_GREATER
                found.BarNominalDiameter = mm / 304.8;
                found.BarModelDiameter = mm / 304.8;
#else
                found.BarDiameter = mm / 304.8;
#endif
                found.StandardBendDiameter = 6 * mm / 304.8;
                found.StandardHookBendDiameter = 6 * mm / 304.8;
                found.StirrupTieBendDiameter = 4 * mm / 304.8;
                notes?.Add("created bar type " + found.Name);
            }
            cache[key] = found;
            return found;
        }
    }
}
