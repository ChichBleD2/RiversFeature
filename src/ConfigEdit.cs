using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RiverFeatures
{
    /// <summary>
    /// Pure (no game types) editing of one config field by name, used by ConfigStore.TrySet and the /riverfeatures command.
    /// The field list comes from the public fields of <see cref="TerrainWatersConfig"/> by reflection, and the value goes through
    /// <see cref="TerrainWatersConfig.Parse(string, Action{string})"/>, so a new setting, its type and its clamping rules are
    /// picked up here automatically (there is no second list or second set of rules).
    /// </summary>
    public static class ConfigEdit
    {
        static FieldInfo[] Fields() => typeof(TerrainWatersConfig).GetFields(BindingFlags.Public | BindingFlags.Instance);

        public static string[] KeyNames() => Fields().Select(f => f.Name).ToArray();

        /// <summary>One "Name = value" line per config field, in declaration order.</summary>
        public static string[] Describe(TerrainWatersConfig cfg) => Fields().Select(f => f.Name + " = " + Format(f.GetValue(cfg)) + " (" + TypeName(f.FieldType) + ")").ToArray();

        static string TypeName(Type t) => t == typeof(bool) ? "bool" : t == typeof(int) ? "int" : t == typeof(double) ? "double" : t == typeof(string[]) ? "string[]" : t.Name;

        /// <summary>
        /// Parses file text for a reload without any side effects. Fails (error says why) for empty text, invalid JSON or a
        /// non-object; otherwise returns the sanitized config and the clamping notes.
        /// </summary>
        public static bool TryParseCandidate(string text, out TerrainWatersConfig cfg, out List<string> notes, out string error)
        {
            cfg = null; notes = new List<string>(); error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "the file is empty"; return false; }
            JToken t;
            try { t = JToken.Parse(text); }
            catch (Exception e) { error = "it is not valid JSON (" + e.Message + ")"; return false; }
            if (!(t is JObject)) { error = "it is not a JSON object"; return false; }
            cfg = TerrainWatersConfig.Parse(text, notes.Add);
            return true;
        }

        public static string Format(object v)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case string[] a: return string.Join(", ", a);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Sets one field (key case-insensitive). On success <paramref name="result"/> is a new, sanitized config and the message
        /// names the value actually stored (so clamping is visible); on failure <paramref name="result"/> is null and
        /// <paramref name="current"/> is untouched.
        /// </summary>
        public static bool TrySet(TerrainWatersConfig current, string key, string value, out TerrainWatersConfig result, out string message)
        {
            result = null;
            key = (key ?? "").Trim();
            value = (value ?? "").Trim();
            var field = Fields().FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (field == null)
            {
                message = "Unknown setting '" + key + "'. Valid keys: " + string.Join(", ", KeyNames()) + ".";
                return false;
            }
            if (field.Name == nameof(TerrainWatersConfig.RiverLakeChance))
            {
                message = "RiverLakeChance is no longer used: river-side (flow-through) lakes were removed, so the value is ignored. Nothing was changed.";
                return false;
            }
            if (field.Name == nameof(TerrainWatersConfig.RiverSpawnChance))
            {
                message = "RiverSpawnChance is no longer used: River Features never writes another mod's config. Set 'riverSpawnChance' in the Rivers mod's own ModConfig/rivers.json. Nothing was changed.";
                return false;
            }
            if (!TryToken(field.FieldType, value, out var token, out var expected))
            {
                message = field.Name + " expects " + expected + "; '" + value + "' is not valid. Nothing was changed.";
                return false;
            }

            var o = JObject.FromObject(current ?? new TerrainWatersConfig());
            o[field.Name] = token;
            var notes = new List<string>();
            var parsed = TerrainWatersConfig.Parse(o.ToString(), notes.Add);
            result = parsed;
            message = field.Name + " is now " + Format(field.GetValue(parsed)) + (notes.Count > 0 ? " (clamped: " + string.Join("; ", notes) + ")" : "") + ".";
            return true;
        }

        static bool TryToken(Type t, string text, out JToken token, out string expected)
        {
            token = null;
            if (t == typeof(bool))
            {
                expected = "true or false";
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) { token = new JValue(true); return true; }
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) { token = new JValue(false); return true; }
                return false;
            }
            if (t == typeof(int))
            {
                expected = "a whole number";
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { token = new JValue(l); return true; }
                return false;
            }
            if (t == typeof(double))
            {
                expected = "a number (use a dot for decimals)";
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
                { token = new JValue(d); return true; }
                return false;
            }
            if (t == typeof(string[]))
            {
                expected = "a comma separated list of text";
                token = new JArray(text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Cast<object>().ToArray());
                return true;
            }
            expected = "a " + t.Name + " (not editable from the command; edit riverfeatures-lakes.json)";
            return false;
        }
    }

    /// <summary>Pure comparison of two worldgen setups: which changes make new chunks mismatch already generated ones.</summary>
    public static class SeamCheck
    {
        const string Tail = " Existing chunks keep the old lakes; new chunks use the new grid, so expect cut-off lakes/swamps at the border between old and new terrain.";

        public static List<string> Warnings(long oldSeed, int oldCell, int oldMaxRadius, int oldMinRadius,
                                            long newSeed, int newCell, int newMaxRadius, int newMinRadius)
        {
            var w = new List<string>();
            if (oldSeed != newSeed) w.Add("SeedOffset changed, so the lake and swamp noise changed." + Tail);
            if (oldMaxRadius != newMaxRadius) w.Add("The lake size (maximum radius " + oldMaxRadius + " -> " + newMaxRadius + ") changed." + Tail);
            if (oldMinRadius != newMinRadius) w.Add("The minimum lake radius changed (" + oldMinRadius + " -> " + newMinRadius + ")." + Tail);
            if (oldCell != newCell) w.Add("The lake feature grid (cell size " + oldCell + " -> " + newCell + ") and the lake and swamp noise scale changed." + Tail);
            return w;
        }
    }
}
