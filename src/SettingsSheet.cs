using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RiverFeatures.Rivers;

namespace RiverFeatures
{
    /// <summary>
    /// ModConfig/riverfeatures-settings.txt: every key of both config files with its current value, default, range and
    /// meaning, rewritten at every server start (read-only for the mod: it never reads this file back). JSON config files
    /// cannot carry comments that survive the mod rewriting them, so this sheet is where the numbers are explained. Pure
    /// text building (Build) plus one write of the mod's own file (Write).
    /// </summary>
    public static class SettingsSheet
    {
        public const string FileName = "riverfeatures-settings.txt";

        public readonly record struct Entry(string Range, string Meaning);

        /// <summary>riverfeatures-lakes.json: swamps, plain lakes (deferred), structure margin.</summary>
        public static readonly Dictionary<string, Entry> Lakes = new Dictionary<string, Entry>
        {
            ["Enabled"] = new("true/false", "Master switch for the terrain pass (swamps, plain lakes, delta banks)."),
            ["PlainLakes"] = new("true/false", "Plain (standalone) lakes. Deferred in 1.0.0: false = none; every Lake* key below then does nothing. Source lakes are in the rivers file."),
            ["SwampChance"] = new("0..1", "Share of the map the swamp noise covers (visible swamp is less: low, flat, rainy ground only). World setting Swamps overrides it."),
            ["SwampWaterCoverage"] = new("0.05..0.9", "Share of swamp ground that gets shallow water pockets."),
            ["SwampMinRain"] = new("0..1", "Least climate rainfall for a swamp."),
            ["SwampPlantCodes"] = new("list of block codes", "Plants placed on swamp land; unknown codes are skipped."),
            ["MaxRiseAboveSea"] = new("0..64 blocks", "Swamps (and plain lakes) only up to this height above sea level."),
            ["MaxSlope"] = new("0.1..4", "Steepest ground (blocks per block) swamps and plain lakes touch."),
            ["MaxCut"] = new("0..32 blocks", "Deepest cut; swamps cut at most ceil(MaxCut / 2)."),
            ["MaxFill"] = new("0..32 blocks", "Most ground a swamp may gain (currently no effect)."),
            ["MaxRimVariation"] = new("0..32 blocks", "A column is skipped if the 5x5 heights around it vary more than this."),
            ["FadeMargin"] = new("0..64 blocks", "Cuts fade to zero over this distance from a feature's edge."),
            ["OceanBuffer"] = new("0..100 blocks", "Swamps and plain lakes keep this far from the ocean (effective cap 32)."),
            ["RuinMargin"] = new("0..100 blocks", "Ground left untouched around every worldgen structure."),
            ["LakeChance"] = new("0..1", "Plain lakes: share of the map inside lake outlines (only with PlainLakes true)."),
            ["LakeMaxRadius"] = new("8..200 blocks", "Plain lakes: largest radius."),
            ["LakeMinDepth"] = new("1..20 blocks", "Plain lakes: least water depth."),
            ["LakeMinArea"] = new("0..900 columns", "Plain lakes: smallest lake kept."),
            ["LakeRiverGap"] = new("0..128 blocks", "Plain lakes: least land to any river channel."),
            ["SeedOffset"] = new("whole number", "Added to the world seed for this file's noise (changing it mid-world leaves seams)."),
            ["RiverSpawnChance"] = new("ignored", "No longer used: River Features never writes another mod's config. Set riverSpawnChance in Rivers' own rivers.json."),
            ["RiverLakeChance"] = new("ignored", "Removed (flow-through lakes, flight test 11)."),
        };

        /// <summary>riverfeatures-rivers.json: the river network (read at server start).</summary>
        public static readonly Dictionary<string, Entry> Rivers = new Dictionary<string, Entry>
        {
            ["Enabled"] = new("true/false", "Master switch for the river network part."),
            ["ConfigVersion"] = new("do not edit", "File format version (the mod adds new keys on upgrade)."),
            ["MaxRiverLengthBlocks"] = new("0..30000 blocks", "Growth cap for the main stem, not a target length: sets Rivers' maxNodes in memory only (length / 250 with Rivers' defaults, never below its minNodes). 0 = Rivers' own (20 nodes, about 5000 blocks). Terrain, coasts, other rivers and spacing end most rivers sooner; in-world tests topped out around 10000 to 11000 blocks even at 15000 to 20000, so there is no need to hunt seeds or max this out. Does not touch riverSpawnChance. World setting River length overrides it."),
            ["MaxNodesPerRiver"] = new("50..5000", "Safety cap on nodes per river."),
            ["TributaryLengthShare"] = new("0.1..1", "Length budget of the secondary branch at a fork (1 = symmetric)."),
            ["TributaryWidthShare"] = new("0.1..1", "A tributary is at most this share of the width of the river it joins."),
            ["TributaryMaxSize"] = new("2..200", "Absolute width cap for tributaries (Rivers' size, about half the width)."),
            ["TributaryMinSize"] = new("1..TributaryMaxSize", "Tributaries are not narrowed below this."),
            ["BranchMinSpacing"] = new("0..200 blocks", "Least land between a tributary and other channels; crowded ones are cut back. 0 = off."),
            ["SourceLakeChance"] = new("-1 or 0..1", "Lake chance at river sources (-1 = Rivers' lakeChance, kept in memory only). World setting Source lakes overrides it."),
            ["VariedSourceLakes"] = new("true/false", "Varied source lake shapes instead of Rivers' teardrop. World setting Lake shapes overrides it."),
            ["LakeRiverClearance"] = new("0..96 blocks", "Least land between a source lake and other channels and lakes."),
            ["DeltaChance"] = new("0..1", "Delta chance per river mouth (x 0.5 + mouth size / Rivers' maxSize). World setting River deltas overrides it."),
            ["DeltaMinRiverSize"] = new("1..200", "No delta on narrower mouths."),
            ["DeltaMinBranches"] = new("2..8", "Least distributaries rolled for a carving delta."),
            ["DeltaMaxBranches"] = new("DeltaMinBranches..8", "Most distributaries rolled for a carving delta."),
            ["DeltaSpreadDegrees"] = new("5..80 degrees", "Fan spread (channels stay within 65 degrees of the sea direction)."),
            ["DeltaMinLength"] = new("20..1000 blocks", "Shortest apex distance up the river from the coast."),
            ["DeltaMaxLength"] = new("DeltaMinLength..2000 blocks", "Longest apex distance."),
            ["DeltaWidthShare"] = new("0.1..1", "Distributary width as a share of the mouth."),
            ["DeltaMaxRise"] = new("0..200 blocks", "Channels whose banks stand higher above the sea bend less, then are dropped."),
            ["DeltaSmallShare"] = new("0..1", "Share of small deltas."),
            ["DeltaShallowShare"] = new("0..1 - DeltaSmallShare", "Share of shallow deltas (the rest carve)."),
            ["ShallowDeltaDepth"] = new("1..6 blocks", "Depth of the shallow delta plain (it varies 1..3 blocks of water)."),
            ["ShallowDeltaGrooveDepth"] = new("0..6 blocks", "Extra depth of each shallow channel's centre groove (plus 1..5 per delta)."),
            ["DeltaScaleMin"] = new("0.3..2.5", "Smallest delta size factor."),
            ["DeltaScaleMax"] = new("DeltaScaleMin..2.5", "Largest delta size factor (bigger rivers lean to it)."),
            ["DeltaSurface"] = new("true/false", "Delta bank material and plants."),
            ["MouthShallowRange"] = new("0..255", "On/off switch for the river-mouth fade (above 0 = on)."),
            ["MouthFadeBlocks"] = new("8..200 blocks", "Length of the river-mouth fade past the coast."),
            ["ClimateInfluence"] = new("0..1", "How strongly climate leans delta looks and trace kinds (0 = ignore climate)."),
            ["OxbowChance"] = new("0..1", "Oxbow chance per eligible bend. World setting Oxbow lakes overrides it."),
            ["OxbowMinBendDegrees"] = new("1..170 degrees", "Least turn at a bend."),
            ["OxbowMinRiverSize"] = new("1..200", "No oxbows on narrower rivers."),
            ["OxbowMaxLevel"] = new("0..20", "Deepest branch level with oxbows (0 = main stems only)."),
            ["OxbowMinRadius"] = new("8..300 blocks", "Smallest oxbow radius."),
            ["OxbowMaxRadius"] = new("OxbowMinRadius..400 blocks", "Largest oxbow radius."),
            ["OxbowWidthShare"] = new("0.1..1", "Oxbow width as a share of the river."),
            ["OxbowMaxRelief"] = new("0..200 blocks", "Oxbows and traces only on ground this flat."),
            ["OxbowBankMargin"] = new("0..64 blocks", "How far out the flatness is sampled."),
            ["OxbowRiverClearance"] = new("0..96 blocks", "Least land between an oxbow and other rivers, deltas, traces and other oxbows."),
            ["DryOxbowShare"] = new("0..1", "Share of dry oxbows."),
            ["TraceChance"] = new("0..1", "Traces of the past (dry channels, backwaters, pockets) per eligible river node."),
            ["TraceClearance"] = new("0..96 blocks", "Least land between a trace and everything else."),
            ["DryRiverChance"] = new("0..1", "Dry branch chance at zero rainfall. World setting Dry rivers overrides it."),
            ["DryRainLimit"] = new("0..1", "No dry rivers at this rainfall or wetter."),
            ["DryRiverMaxSize"] = new("2..50", "Widest dry channel (Rivers' size)."),
            ["DryFloorAboveSea"] = new("1..7 blocks", "Dry channel floors stay this far above sea level."),
            ["HeadwaterMaxRise"] = new("0..200 blocks", "Source tips on ground higher than this above the water are trimmed."),
            ["HeadwaterMaxTrimNodes"] = new("0..10", "Most source nodes trimmed per river."),
            ["HeadwaterBankMargin"] = new("0..64 blocks", "How far out headwater ground is sampled."),
            ["HeadwaterCalmBlocks"] = new("0..5000 blocks", "Flow speed ramps up from the sources over this distance."),
            ["HeadwaterMinSpeed"] = new("0.05..1", "Flow speed at the source."),
            ["TerrainNoiseShare"] = new("0..1", "How much landform noise the terrain estimate includes."),
        };

        static IEnumerable<FieldInfo> Keys(Type t) =>
            t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsDefined(typeof(Newtonsoft.Json.JsonExtensionDataAttribute)));

        /// <summary>The fields of a config type that have no sheet entry, and sheet entries that are no field (both should be empty).</summary>
        public static (List<string> missing, List<string> stale) Check(Type configType, Dictionary<string, Entry> sheet)
        {
            var names = Keys(configType).Select(f => f.Name).ToList();
            return (names.Where(n => !sheet.ContainsKey(n)).ToList(), sheet.Keys.Where(k => !names.Contains(k)).ToList());
        }

        public static string Build(TerrainWatersConfig lakes, HierarchyConfig rivers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("River Features 1.0.0 settings reference (rewritten at every server start; editing THIS file changes nothing).");
            sb.AppendLine("Edit the numbers in the two JSON files next to it, then restart the server (or /riverfeatures reload for the lakes file).");
            sb.AppendLine("Changes apply to newly generated chunks only. A world-creation setting other than 'Default' overrides its key for that world.");
            sb.AppendLine("Out-of-range values are clamped at load, a value that is not a number falls back to the default; the server log names both.");
            sb.AppendLine("River Features never writes any other mod's config file.");
            Section(sb, "riverfeatures-rivers.json (river network: hierarchy, source lakes, deltas, oxbows, traces, dry rivers, headwaters)", rivers ?? new HierarchyConfig(), new HierarchyConfig(), Rivers);
            Section(sb, "riverfeatures-lakes.json (terrain pass: swamps, plain lakes, structure margin)", lakes ?? new TerrainWatersConfig(), new TerrainWatersConfig(), Lakes);
            return sb.ToString();
        }

        static void Section(StringBuilder sb, string title, object cur, object def, Dictionary<string, Entry> sheet)
        {
            sb.AppendLine().AppendLine("== " + title).AppendLine();
            foreach (var f in Keys(cur.GetType()))
            {
                sheet.TryGetValue(f.Name, out var e);
                string now = ConfigEdit.Format(f.GetValue(cur)), was = ConfigEdit.Format(f.GetValue(def));
                sb.Append(f.Name).Append(" = ").Append(now);
                if (now != was) sb.Append("   (default ").Append(was).Append(')');
                else sb.Append("   (default)");
                sb.AppendLine();
                sb.Append("    range: ").Append(e.Range ?? "?").Append(" | ").AppendLine(e.Meaning ?? "");
            }
        }

        /// <summary>Writes the sheet into the given ModConfig folder (the mod's own file only). Never throws; returns an error or null.</summary>
        public static string Write(string modConfigDir, TerrainWatersConfig lakes, HierarchyConfig rivers)
        {
            try
            {
                File.WriteAllText(Path.Combine(modConfigDir, FileName), Build(lakes, rivers), new UTF8Encoding(false));
                return null;
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
