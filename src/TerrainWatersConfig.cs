using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RiverFeatures
{
    /// <summary>
    /// User-editable settings. Distances and heights are in blocks for a 256-high world and are
    /// scaled to the real world height by <see cref="Resolve"/>.
    /// Invalid values are clamped or replaced by defaults (never thrown on); each repair is reported to the log callback.
    /// </summary>
    public class TerrainWatersConfig
    {
        public bool Enabled = true;
        /// <summary>No longer used: River Features never writes another mod's config. Set riverSpawnChance in Rivers' own rivers.json.</summary>
        public double RiverSpawnChance = -1;
        /// <summary>
        /// Plain lakes (the lake features of this file: LakeChance, LakeMaxRadius, LakeMinDepth, LakeMinArea, LakeRiverGap,
        /// shore easing). Deferred for 1.0.0: false = no plain lakes are planned at all, and the lake keys below are kept
        /// but unused. Source lakes (river network, riverfeatures-rivers.json) and swamps do not depend on this.
        /// </summary>
        public bool PlainLakes = false;
        /// <summary>
        /// Share of the map inside active lakes (0..1). Lake features sit one per cell (3 x LakeMaxRadius square), which covers
        /// about 8% of the map when every one is active; above that the cell shrinks (down to 2.25 x LakeMaxRadius, about 15%)
        /// so more lakes fit. Values above that ceiling change nothing further (the startup log says so). Default 0.12 (flight
        /// test 6, was 0.05). The water itself covers less, since only suitable ground inside a lake shape floods (basins and
        /// flats at the lake level).
        /// </summary>
        public double LakeChance = 0.12;
        /// <summary>
        /// Share of river-side land (within ColumnExclusions.RiverLakeReach of river water) inside the river-lake noise mask
        /// (0..1). Where it covers low, flat ground next to a river, the ground floods at the river's own surface: a
        /// flow-through lake the river runs into and out of. 0 turns river-side lakes off.
        /// REMOVED in flight test 11: Rivers' water lies at sea level, below every lake level this mod allows, so the only
        /// "river" water these lakes ever attached to was vanilla ponds, which made chunk-border walls. The key is still read
        /// from old files but ignored (always 0); source lakes (riverfeatures-rivers.json SourceLakeChance) replace it.
        /// </summary>
        public double RiverLakeChance = 0;
        public double SwampChance = 0.15;
        public int LakeMaxRadius = 48;
        public int LakeMinDepth = 3;
        /// <summary>Smallest lake (water columns, reference world; scaled by the horizontal scale squared). Flight test 9: mini-lakes everywhere. 0 = keep all.</summary>
        public int LakeMinArea = 80;
        public int OceanBuffer = 10;
        /// <summary>Flight test 16: least land (blocks) between a lake and any Rivers channel (also delta channels, oxbows, traces, source lakes). A lake that would come closer shrinks, or is dropped below its minimum size. 0 = off.</summary>
        public int LakeRiverGap = 24;
        public int RuinMargin = 12;
        public double SwampWaterCoverage = 0.35;
        public double SwampMinRain = 0.45;
        public int MaxRiseAboveSea = 12;
        public double MaxSlope = 1.0;
        public int MaxCut = 5;
        public int MaxFill = 2;
        public int MaxRimVariation = 6;
        public int FadeMargin = 8;
        /// <summary>
        /// Block codes placed on swamp land. Checked against vssurvivalmod source (no asset JSON was available):
        /// "tallgrass-tall-free" is used verbatim in BlockSoil.cs; the "tallplant-{coopersreed|tule|papyrus}-land-*-free"
        /// pattern is used in ItemCattailRoots.cs, but only with state "harvested" — the "normal" state is UNVERIFIED.
        /// Unknown codes are skipped with one log line each at world start, never an error.
        /// </summary>
        public string[] SwampPlantCodes = DefaultPlants();
        public int SeedOffset = 0;

        static string[] DefaultPlants() => new[] { "tallplant-coopersreed-land-normal-free", "tallplant-tule-land-normal-free", "tallgrass-tall-free" };

        /// <summary>Parses JSON text. Broken JSON gives defaults; bad fields fall back individually.</summary>
        public static TerrainWatersConfig Parse(string json) => Parse(json, null);

        public static TerrainWatersConfig Parse(string json, Action<string> log)
        {
            var cfg = new TerrainWatersConfig();
            if (string.IsNullOrWhiteSpace(json))
            {
                log?.Invoke("config is empty; using defaults");
                return cfg;
            }
            JObject o;
            try
            {
                o = JToken.Parse(json) as JObject;
            }
            catch (Exception e)
            {
                log?.Invoke("config is not valid JSON (" + e.Message + "); using defaults");
                return cfg;
            }
            if (o == null)
            {
                log?.Invoke("config is not a JSON object; using defaults");
                return cfg;
            }

            ReadBool(o, nameof(Enabled), ref cfg.Enabled, log);
            ReadBool(o, nameof(PlainLakes), ref cfg.PlainLakes, log);
            ReadDouble(o, nameof(RiverSpawnChance), ref cfg.RiverSpawnChance, log);
            ReadDouble(o, nameof(LakeChance), ref cfg.LakeChance, log);
            ReadDouble(o, nameof(RiverLakeChance), ref cfg.RiverLakeChance, log);
            ReadDouble(o, nameof(SwampChance), ref cfg.SwampChance, log);
            ReadInt(o, nameof(LakeMaxRadius), ref cfg.LakeMaxRadius, log);
            ReadInt(o, nameof(LakeMinDepth), ref cfg.LakeMinDepth, log);
            ReadInt(o, nameof(LakeMinArea), ref cfg.LakeMinArea, log);
            ReadInt(o, nameof(OceanBuffer), ref cfg.OceanBuffer, log);
            ReadInt(o, nameof(LakeRiverGap), ref cfg.LakeRiverGap, log);
            ReadInt(o, nameof(RuinMargin), ref cfg.RuinMargin, log);
            ReadDouble(o, nameof(SwampWaterCoverage), ref cfg.SwampWaterCoverage, log);
            ReadDouble(o, nameof(SwampMinRain), ref cfg.SwampMinRain, log);
            ReadInt(o, nameof(MaxRiseAboveSea), ref cfg.MaxRiseAboveSea, log);
            ReadDouble(o, nameof(MaxSlope), ref cfg.MaxSlope, log);
            ReadInt(o, nameof(MaxCut), ref cfg.MaxCut, log);
            ReadInt(o, nameof(MaxFill), ref cfg.MaxFill, log);
            ReadInt(o, nameof(MaxRimVariation), ref cfg.MaxRimVariation, log);
            ReadInt(o, nameof(FadeMargin), ref cfg.FadeMargin, log);
            ReadStrings(o, nameof(SwampPlantCodes), ref cfg.SwampPlantCodes, log);
            ReadInt(o, nameof(SeedOffset), ref cfg.SeedOffset, log);

            var unknown = UnknownKeys(o);
            if (unknown.Count > 0) log?.Invoke(UnknownKeysNote(unknown));
            var removed = o.Properties().Select(p => p.Name).Where(n => Array.IndexOf(RemovedKeys, n) >= 0).ToList();
            if (removed.Count > 0) log?.Invoke("removed setting(s) dropped from the file: " + string.Join(", ", removed) + " (erosion was removed in 1.0.0)");

            cfg.Sanitize(log);
            return cfg;
        }

        /// <summary>The setting names (public instance fields), exact spelling; keys are matched case-sensitively.</summary>
        public static string[] KnownKeys() =>
            typeof(TerrainWatersConfig).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(f => f.Name).ToArray();

        /// <summary>Top-level keys of o that are not settings (misspelled, wrong case, or from another mod), in file order. Pure.</summary>
        public static List<string> UnknownKeys(JObject o)
        {
            var known = new HashSet<string>(KnownKeys(), StringComparer.Ordinal);
            var list = new List<string>();
            if (o == null) return list;
            foreach (var p in o.Properties()) if (!known.Contains(p.Name) && Array.IndexOf(RemovedKeys, p.Name) < 0) list.Add(p.Name);
            return list;
        }

        /// <summary>
        /// Keys of features removed from the mod (erosion, 1.0.0). Not "unknown": they are dropped from the file at the next
        /// rewrite with one log line, instead of being kept and warned about forever.
        /// </summary>
        public static readonly string[] RemovedKeys = { "Erosion", "ErosionStrength", "ErosionMaxCut", "ErosionMinSlope" };

        /// <summary>One line naming unknown keys, with the right spelling for a key that differs only in case. Pure.</summary>
        public static string UnknownKeysNote(IList<string> unknown)
        {
            var known = KnownKeys();
            var parts = new List<string>();
            foreach (var k in unknown)
            {
                var match = known.FirstOrDefault(n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase));
                parts.Add(match != null ? "'" + k + "' (did you mean '" + match + "'?)" : "'" + k + "'");
            }
            return "unknown setting(s) ignored and kept in the file as written: " + string.Join(", ", parts) + " (keys are case-sensitive)";
        }

        /// <summary>
        /// The JSON object written to the file for cfg: every setting, then every unknown key of keepFrom (in its order, value
        /// unchanged), so a rewrite never erases a misspelled key the player typed. keepFrom may be null. Pure.
        /// </summary>
        public static JObject ToFileObject(TerrainWatersConfig cfg, JObject keepFrom)
        {
            var o = JObject.FromObject(cfg ?? new TerrainWatersConfig());
            if (keepFrom != null)
                foreach (var k in UnknownKeys(keepFrom))
                    if (o[k] == null) o[k] = keepFrom[k].DeepClone();
            return o;
        }

        /// <summary>Clamps every field into its valid range in place, reporting each change to <paramref name="log"/>.</summary>
        public void Sanitize(Action<string> log)
        {
            // River chance: -1 (or any negative) = leave Rivers alone, else 0..1.
            if (double.IsNaN(RiverSpawnChance) || double.IsInfinity(RiverSpawnChance))
            { log?.Invoke("RiverSpawnChance is not a finite number; using -1"); RiverSpawnChance = -1; }
            else if (RiverSpawnChance < 0)
            { if (RiverSpawnChance != -1) log?.Invoke("RiverSpawnChance " + Inv(RiverSpawnChance) + " is negative; using -1 (leave Rivers alone)"); RiverSpawnChance = -1; }
            else if (RiverSpawnChance > 1)
            { log?.Invoke("RiverSpawnChance " + Inv(RiverSpawnChance) + " above 1; using 1"); RiverSpawnChance = 1; }

            var d = new TerrainWatersConfig();
            ClampD(ref LakeChance, 0, 1, d.LakeChance, nameof(LakeChance), log);
            ClampD(ref RiverLakeChance, 0, 1, d.RiverLakeChance, nameof(RiverLakeChance), log);
            ClampD(ref SwampChance, 0, 1, d.SwampChance, nameof(SwampChance), log);
            ClampI(ref LakeMaxRadius, 8, 200, d.LakeMaxRadius, nameof(LakeMaxRadius), log);
            ClampI(ref LakeMinDepth, 1, 20, d.LakeMinDepth, nameof(LakeMinDepth), log);
            ClampI(ref LakeMinArea, 0, 900, d.LakeMinArea, nameof(LakeMinArea), log);
            ClampI(ref OceanBuffer, 0, 100, d.OceanBuffer, nameof(OceanBuffer), log);
            ClampI(ref LakeRiverGap, 0, 128, d.LakeRiverGap, nameof(LakeRiverGap), log);
            ClampI(ref RuinMargin, 0, 100, d.RuinMargin, nameof(RuinMargin), log);
            ClampD(ref SwampWaterCoverage, 0.05, 0.9, d.SwampWaterCoverage, nameof(SwampWaterCoverage), log);
            ClampD(ref SwampMinRain, 0, 1, d.SwampMinRain, nameof(SwampMinRain), log);
            ClampI(ref MaxRiseAboveSea, 0, 64, d.MaxRiseAboveSea, nameof(MaxRiseAboveSea), log);
            ClampD(ref MaxSlope, 0.1, 4, d.MaxSlope, nameof(MaxSlope), log);
            ClampI(ref MaxCut, 0, 32, d.MaxCut, nameof(MaxCut), log);
            ClampI(ref MaxFill, 0, 32, d.MaxFill, nameof(MaxFill), log);
            ClampI(ref MaxRimVariation, 0, 32, d.MaxRimVariation, nameof(MaxRimVariation), log);
            ClampI(ref FadeMargin, 0, 64, d.FadeMargin, nameof(FadeMargin), log);

            var plants = (SwampPlantCodes ?? new string[0]).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (plants.Length == 0)
            { log?.Invoke("SwampPlantCodes is empty; using defaults"); plants = DefaultPlants(); }
            SwampPlantCodes = plants;
        }

        /// <summary>World-creation setting codes (worldconfig.json) and the config field each overrides.</summary>
        public static readonly (string code, string field)[] WorldSettingCodes =
        {
            ("twLakeChance", nameof(LakeChance)), ("twSwampChance", nameof(SwampChance)),
        };

        /// <summary>Still read (so a world made with them keeps its value) but not offered on the world-creation screen: plain
        /// lakes are deferred for 1.0.0, so twLakeChance would do nothing. Put it back in worldconfig.json with PlainLakes.</summary>
        public static readonly string[] HiddenWorldSettings =
        {
            "twLakeChance",
        };

        /// <summary>
        /// A copy with the world-creation settings applied: each code's value (a number, invariant culture) replaces the field;
        /// "config", empty or a missing value keeps the mod config's value; anything unreadable is reported and ignored. Pure.
        /// </summary>
        public TerrainWatersConfig WithWorldSettings(Func<string, string> get, Action<string> log)
        {
            var c = (TerrainWatersConfig)MemberwiseClone();
            c.SwampPlantCodes = (string[])SwampPlantCodes.Clone();
            if (get == null) return c;
            foreach (var (code, field) in WorldSettingCodes)
            {
                string v = null;
                try { v = get(code); } catch (Exception) { v = null; }
                if (string.IsNullOrWhiteSpace(v) || v.Trim() == "config") continue;
                if (!double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || d < 0 || d > 1)
                { log?.Invoke("world setting " + code + " = '" + v + "' is not a number from 0 to 1; using the mod config value"); continue; }
                typeof(TerrainWatersConfig).GetField(field).SetValue(c, d);
                log?.Invoke("world setting " + code + " = " + Inv(d) + " (overrides " + field + " from the mod config for this world)");
            }
            return c;
        }

        /// <summary>Scales to the given world. Works on a sanitized copy; never throws and never leaves the world's height range.</summary>
        public ResolvedConfig Resolve(WorldScale s)
        {
            var c = (TerrainWatersConfig)MemberwiseClone();
            c.Sanitize(null);

            // Horizontal distances also follow LandformScale (ScaleHorizontal); heights, depths and RuinMargin use world height only.
            int maxRadius = s.ScaleHorizontal(c.LakeMaxRadius);
            // Keep SeaLevel + rise <= WorldHeight - 2; never negative.
            long headroom = (long)s.WorldHeight - 2 - s.SeaLevel;
            int rise = (int)Math.Max(0, Math.Min(s.Scale(c.MaxRiseAboveSea), headroom));

            return new ResolvedConfig
            {
                Enabled = c.Enabled,
                RiverSpawnChance = c.RiverSpawnChance,
                LakeChance = c.LakeChance,
                PlainLakes = c.PlainLakes,
                RiverLakeChance = 0, // flow-through lakes removed (see the field)
                SwampChance = c.SwampChance,
                LakeMaxRadius = maxRadius,
                LakeMinRadius = Math.Min(Math.Max(8, maxRadius / 3), maxRadius),
                // 3 x (was 4 x): shaped lakes cover about half their disc, so a denser grid keeps the LakeChance ceiling
                // near its old value. Centres still sit Radius from the cell edge (FeatureCells: 3R - R > R).
                CellSize = 3 * maxRadius,
                LakeMinDepth = s.Scale(c.LakeMinDepth),
                LakeMinArea = (int)Math.Round(c.LakeMinArea * Math.Pow(s.ScaleHorizontal(100) / 100.0, 2)),
                OceanBuffer = s.ScaleHorizontal(c.OceanBuffer),
                LakeRiverGap = s.ScaleHorizontal(c.LakeRiverGap),
                RuinMargin = s.Scale(c.RuinMargin),
                SwampWaterCoverage = c.SwampWaterCoverage,
                SwampMinRain = c.SwampMinRain,
                MaxRiseAboveSea = rise,
                MaxSlope = c.MaxSlope,
                MaxCut = s.Scale(c.MaxCut),
                MaxFill = s.Scale(c.MaxFill),
                MaxRimVariation = s.Scale(c.MaxRimVariation),
                FadeMargin = s.ScaleHorizontal(c.FadeMargin),
                SwampPlantCodes = (string[])c.SwampPlantCodes.Clone(),
                SeedOffset = c.SeedOffset,
                WorldHeight = s.WorldHeight,
                SeaLevel = s.SeaLevel,
            };
        }

        static void ReadBool(JObject o, string name, ref bool field, Action<string> log)
        {
            if (!o.TryGetValue(name, out var t)) return;
            if (t.Type == JTokenType.Boolean) field = t.Value<bool>();
            else log?.Invoke(name + " should be true or false; using default " + (field ? "true" : "false"));
        }

        static bool TryNumber(JToken t, out double v)
        {
            v = 0;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return false;
            try { v = t.Value<double>(); } catch (Exception) { return false; }
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        static void ReadDouble(JObject o, string name, ref double field, Action<string> log)
        {
            if (!o.TryGetValue(name, out var t)) return;
            if (TryNumber(t, out var v)) field = v;
            else log?.Invoke(name + " should be a finite number; using default " + Inv(field));
        }

        static void ReadInt(JObject o, string name, ref int field, Action<string> log)
        {
            if (!o.TryGetValue(name, out var t)) return;
            if (TryNumber(t, out var v)) field = (int)Math.Round(Math.Clamp(v, int.MinValue, int.MaxValue));
            else log?.Invoke(name + " should be a whole number; using default " + Inv(field));
        }

        static void ReadStrings(JObject o, string name, ref string[] field, Action<string> log)
        {
            if (!o.TryGetValue(name, out var t)) return;
            if (t is JArray arr)
            {
                var list = new List<string>();
                foreach (var e in arr)
                {
                    if (e.Type == JTokenType.String) list.Add(e.Value<string>());
                    else log?.Invoke(name + " entry ignored (not text): " + e);
                }
                field = list.ToArray(); // empty result is replaced by defaults in Sanitize
            }
            else log?.Invoke(name + " should be a list of text; using defaults");
        }

        static void ClampD(ref double v, double lo, double hi, double def, string name, Action<string> log)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            { log?.Invoke(name + " is not a finite number; using default " + Inv(def)); v = def; return; }
            var c = Math.Clamp(v, lo, hi);
            if (c != v) log?.Invoke(name + " " + Inv(v) + " out of range " + Inv(lo) + ".." + Inv(hi) + "; using " + Inv(c));
            v = c;
        }

        static void ClampI(ref int v, int lo, int hi, int def, string name, Action<string> log)
        {
            var c = Math.Clamp(v, lo, hi);
            if (c != v) log?.Invoke(name + " " + Inv(v) + " out of range " + Inv(lo) + ".." + Inv(hi) + "; using " + Inv(c));
            v = c;
        }

        /// <summary>Culture-invariant text of a number (dot decimals), so log and reply text is the same on every locale.</summary>
        static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
