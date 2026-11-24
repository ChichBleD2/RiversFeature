using System;

namespace RiverFeatures.Rivers
{
    /// <summary>ModConfig/riverfeatures-rivers.json. Values out of range are clamped at load (Sanitize).</summary>
    public class HierarchyConfig
    {
        public bool Enabled = true;

        /// <summary>Keys this version does not know (a newer or older config): kept and written back unchanged, as in the lakes config.</summary>
        [Newtonsoft.Json.JsonExtensionData]
        public System.Collections.Generic.IDictionary<string, Newtonsoft.Json.Linq.JToken> UnknownKeys;

        /// <summary>
        /// Growth cap for a river's main stem from the mouth, in blocks; sets Rivers' maxNodes (stage limit) in memory. A cap,
        /// not a target: terrain, coasts, other rivers and spacing end most rivers sooner, and in-world tests topped out around
        /// 10000 to 11000 blocks even at 15000 to 20000. 0 = leave Rivers' value (about 5000).
        /// </summary>
        public int MaxRiverLengthBlocks = 10000;
        /// <summary>Safety cap on nodes per river (longer rivers branch more; this bounds the work per Rivers region).</summary>
        public int MaxNodesPerRiver = 900;
        /// <summary>Flight test 11: chance of a lake at a river's source (Rivers' own lakeChance roll at the end nodes). -1 = leave Rivers' value.</summary>
        public double SourceLakeChance = -1;
        /// <summary>Flight test 12: give source lakes varied shapes (oval, finger, winding, lobed) instead of Rivers' teardrop only.</summary>
        public bool VariedSourceLakes = true;
        /// <summary>Flight test 13: least land (blocks) between a source lake and any river channel or lake (only the join with its own river is excepted). Flight test 14: 14 left thin strips, now 32.</summary>
        public double LakeRiverClearance = 32;

        /// <summary>At a fork the main branch keeps the full remaining budget; the secondary keeps this share (Rivers: 1, symmetric).</summary>
        public double TributaryLengthShare = 0.6;
        /// <summary>A tributary is at most this share of the width of the river it joins (cascades at every secondary fork).</summary>
        public float TributaryWidthShare = 0.6f;
        /// <summary>Absolute width cap for any tributary (Rivers' widths are 14..50 by default).</summary>
        public float TributaryMaxSize = 28f;
        /// <summary>Tributaries are not narrowed below this.</summary>
        public float TributaryMinSize = 8f;

        public double DeltaChance = 0.35;
        public float DeltaMinRiverSize = 30f;
        public int DeltaMinBranches = 2;
        public int DeltaMaxBranches = 4;
        public double DeltaSpreadDegrees = 45;
        public int DeltaMinLength = 120;
        public int DeltaMaxLength = 260;
        public float DeltaWidthShare = 0.5f;

        /// <summary>Flight test 4: 0.2 gave 21 oxbows for 144 rivers; raised (with a lower bend threshold and more eligible rivers).</summary>
        public double OxbowChance = 0.65;
        public double OxbowMinBendDegrees = 12;
        public float OxbowMinRiverSize = 10f;
        public int OxbowMaxLevel = 2;

        /// <summary>File layout version; files from 0.1.0 (version 0/1) get the new oxbow defaults where they still hold the old ones.</summary>
        public int ConfigVersion = 0; // a file without the key is from 0.1.0; Migrate() sets CurrentVersion
        public const int CurrentVersion = 8;

        /// <summary>Upgrades an older file in place; returns true when anything changed (the caller saves).</summary>
        public bool Migrate()
        {
            if (ConfigVersion >= CurrentVersion) return false;
            if (ConfigVersion < 2)
            {
                if (OxbowChance == 0.2) OxbowChance = 0.65;
                if (OxbowMinBendDegrees == 20) OxbowMinBendDegrees = 12;
                if (OxbowMinRiverSize == 16f) OxbowMinRiverSize = 10f;
                if (OxbowMaxLevel == 1) OxbowMaxLevel = 2;
            }
            if (ConfigVersion < 6 && DeltaSmallShare == 0.35 && DeltaShallowShare == 0.3) { DeltaSmallShare = 0.334; DeltaShallowShare = 0.333; }
            if (ConfigVersion < 7 && LakeRiverClearance == 14) LakeRiverClearance = 32;
            // Versions 3 to 8 only add keys (terrain checks, headwaters, stage 2); their defaults come from the field initialisers.
            ConfigVersion = CurrentVersion;
            return true;
        }
        public int OxbowMinRadius = 30;
        public int OxbowMaxRadius = 60;
        public float OxbowWidthShare = 0.5f;

        /// <summary>
        /// Flight test 5: an oxbow is only made where the estimated ground across its footprint and banks (water at sea level
        /// included) varies by at most this many blocks. Rivers carves a fixed slot around sea level, so on higher or uneven
        /// ground the horseshoe became a pit with cliff walls. 0 = no terrain check.
        /// </summary>
        public double OxbowMaxRelief = 7;
        /// <summary>Bank width (blocks beyond the oxbow's edge) included in the relief check.</summary>
        public double OxbowBankMargin = 12;

        /// <summary>
        /// Headwaters: a source tip whose upstream half (and banks) has estimated ground more than this many blocks above the
        /// water (sea level) is removed, up to HeadwaterMaxTrimNodes nodes per source, so sources sit where Rivers' channel can
        /// hold water instead of in a high, stepped slot of separate pools. 0 = keep every source.
        /// </summary>
        public double HeadwaterMaxRise = 8;
        public int HeadwaterMaxTrimNodes = 3;
        public double HeadwaterBankMargin = 10;
        /// <summary>Flow speed ramps from HeadwaterMinSpeed at a source to Rivers' full speed this many blocks downstream (0 = off).</summary>
        public double HeadwaterCalmBlocks = 800;
        public double HeadwaterMinSpeed = 0.25;

        /// <summary>How much of the landform noise counts as ground: the estimate spans solid probability 0.5 +- 0.3 x this (1 = 0.2..0.8; higher = stricter).</summary>
        public double TerrainNoiseShare = 1;

        /// <summary>
        /// Flight test 7: a delta distributary is dropped where the estimated ground along it stands more than this many
        /// blocks above the sea (so deltas form on low coastal plains, not through coastal hills).
        /// </summary>
        public double DeltaMaxRise = 6;
        /// <summary>Flight test 9: delta types. Small = a simple mouth with a short two-channel delta; Shallow = a long, wide, slow delta whose channels (and the river's last stretch) are at most ShallowDeltaDepth deep with gentle banks; the rest carve the full delta.</summary>
        public double DeltaSmallShare = 0.334; // flight test 11: a third each (small, shallow, carving)
        public double DeltaShallowShare = 0.333;
        public int ShallowDeltaDepth = 2;
        /// <summary>Flight test 11: rivulets in shallow deltas, a narrow groove this many blocks below the flat delta floor along each channel (0 = off).</summary>
        public double ShallowDeltaGrooveDepth = 3;
        /// <summary>
        /// River mouths: where a channel runs into the sea, Rivers' carving depth fades out past the coast (over
        /// MouthFadeBlocks) down to a depth that cuts nothing below sea level, so a river no longer digs a trench across the
        /// shelf or leaves a cliff at the shore. Only an on/off switch now: any value above 0 = on, 0 = off (the number was
        /// an oceanicity range for the removed SampleRiver patch and is kept so existing configs still load).
        /// </summary>
        public double MouthShallowRange = 60;
        /// <summary>Flight test 10: river mouths and delta channels fade to a cut ending at sea level over this many blocks past the coast (0 MouthShallowRange = off).</summary>
        public double MouthFadeBlocks = 40;

        /// <summary>Flight test 7: share of oxbows that are dry (an old channel floor above the water level).</summary>
        public double DryOxbowShare = 0.3;
        /// <summary>
        /// Dry rivers (wadis): chance that a river or a tributary branch is dry, scaled by dryness:
        /// chance x (DryRainLimit - rainfall) / DryRainLimit, so full chance with no rain, none at DryRainLimit (0..1) or wetter.
        /// </summary>
        public double DryRiverChance = 0.6;
        public double DryRainLimit = 0.4;
        /// <summary>Widest dry river (Rivers' size is the half-width, so 12 = about 24 blocks wide).</summary>
        public float DryRiverMaxSize = 12f;
        /// <summary>The floor of dry channels (dry rivers, dry oxbows) stays at least this many blocks above sea level, so no water forms.</summary>
        public int DryFloorAboveSea = 3;

        // ---- Stage 2: delta character, climate, traces of the past, oxbow clearance ----
        /// <summary>
        /// How strongly local climate (the game's climate map, or a climate source another mod registers) leans delta looks and
        /// traces of the past: 0 = climate-blind, 1 = full. Dry and intermittent deltas and old dry channels lean to dry land,
        /// marshy and overgrown deltas, backwaters and pockets to wet land. Traces of every kind occur everywhere; delta looks
        /// lean harder: at 0.7, very wet land (rain 0.8+) never rolls a dry delta. Dry rivers do not use this (DryRainLimit).
        /// </summary>
        public double ClimateInfluence = 0.7;
        /// <summary>Delta size range (x the usual apex distance; width x its square root). Bigger rivers lean to the top of the range.</summary>
        public double DeltaScaleMin = 0.6;
        public double DeltaScaleMax = 1.6;
        /// <summary>Delta banks get their look (sand, mud, gravel, rock and plants by moisture and vegetation). false = plain banks.</summary>
        public bool DeltaSurface = true;
        /// <summary>Chance per main-stem or first-branch river node of a trace of the past (dry side channel, backwater or floodplain pocket). 0 = none.</summary>
        public double TraceChance = 0.25;
        /// <summary>Least land (blocks) between a trace and any other channel, lake, oxbow or delta (its own river's floodplain excepted).</summary>
        public double TraceClearance = 24;
        /// <summary>Least land (blocks) between an oxbow and any OTHER river's channel or lake in the region (its own river: its width + 6).</summary>
        public double OxbowRiverClearance = 20;
        /// <summary>Flight test 16: least land (blocks) between a tributary and any channel it is not joined to; a tributary running closer (base Rivers branches can run right beside the main stem) is cut back from there. 0 = off.</summary>
        public double BranchMinSpacing = 32;

        /// <summary>
        /// World-creation setting rhOxbowChance (worldconfig.json): a number 0..1 replaces OxbowChance for this world;
        /// "config", empty or null keeps it. Returns true when applied. Pure.
        /// </summary>
        public bool ApplyWorldOxbow(string value)
        {
            if (!TryWorldNumber(value, 0, 1, out double d)) return false;
            OxbowChance = d;
            return true;
        }

        static bool TryWorldNumber(string value, double lo, double hi, out double d)
        {
            d = 0;
            if (string.IsNullOrWhiteSpace(value) || value.Trim() == "config") return false;
            return double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)
                && !double.IsNaN(d) && d >= lo && d <= hi;
        }

        /// <summary>
        /// All world-creation settings (worldconfig.json): rhRiverLength (blocks, 0 = Rivers' own length), rhDeltaChance and
        /// rhOxbowChance (0..1). "config" or anything unreadable keeps the mod config value. Returns one line per applied value.
        /// </summary>
        public System.Collections.Generic.List<string> ApplyWorldSettings(Func<string, string> get)
        {
            var applied = new System.Collections.Generic.List<string>();
            if (get == null) return applied;
            string G(string k) { try { return get(k); } catch (Exception) { return null; } }
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (TryWorldNumber(G("rhRiverLength"), 0, 30000, out double len)) { MaxRiverLengthBlocks = (int)len; applied.Add("rhRiverLength = " + MaxRiverLengthBlocks.ToString(ic)); }
            if (TryWorldNumber(G("rhDeltaChance"), 0, 1, out double dc)) { DeltaChance = dc; applied.Add("rhDeltaChance = " + dc.ToString(ic)); }
            if (ApplyWorldOxbow(G("rhOxbowChance"))) applied.Add("rhOxbowChance = " + OxbowChance.ToString(ic));
            if (TryWorldNumber(G("rhDryRiverChance"), 0, 1, out double dr)) { DryRiverChance = dr; applied.Add("rhDryRiverChance = " + dr.ToString(ic)); }
            if (TryWorldNumber(G("rhSourceLakeChance"), 0, 1, out double sl)) { SourceLakeChance = sl; applied.Add("rhSourceLakeChance = " + sl.ToString(ic)); }
            if (TryWorldNumber(G("rhLakeShapes"), 0, 1, out double ls)) { VariedSourceLakes = ls >= 0.5; applied.Add("rhLakeShapes = " + (VariedSourceLakes ? "varied" : "Rivers teardrop")); }
            return applied;
        }

        public void Sanitize()
        {
            MaxRiverLengthBlocks = Math.Clamp(MaxRiverLengthBlocks, 0, 30000);
            MaxNodesPerRiver = Math.Clamp(MaxNodesPerRiver, 50, 5000);
            TributaryLengthShare = Clamp(TributaryLengthShare, 0.1, 1.0, 0.6);
            TributaryWidthShare = (float)Clamp(TributaryWidthShare, 0.1, 1.0, 0.6);
            TributaryMaxSize = (float)Clamp(TributaryMaxSize, 2, 200, 28);
            TributaryMinSize = (float)Clamp(TributaryMinSize, 1, TributaryMaxSize, 8);
            DeltaChance = Clamp(DeltaChance, 0, 1, 0.35);
            DeltaMinRiverSize = (float)Clamp(DeltaMinRiverSize, 1, 200, 30);
            DeltaMinBranches = Math.Clamp(DeltaMinBranches, 2, 8);
            DeltaMaxBranches = Math.Clamp(DeltaMaxBranches, DeltaMinBranches, 8);
            DeltaSpreadDegrees = Clamp(DeltaSpreadDegrees, 5, 80, 45);
            DeltaMinLength = Math.Clamp(DeltaMinLength, 20, 1000);
            DeltaMaxLength = Math.Clamp(DeltaMaxLength, DeltaMinLength, 2000);
            DeltaWidthShare = (float)Clamp(DeltaWidthShare, 0.1, 1.0, 0.5);
            OxbowChance = Clamp(OxbowChance, 0, 1, 0.65);
            OxbowMinBendDegrees = Clamp(OxbowMinBendDegrees, 1, 170, 12);
            OxbowMinRiverSize = (float)Clamp(OxbowMinRiverSize, 1, 200, 10);
            OxbowMaxLevel = Math.Clamp(OxbowMaxLevel, 0, 20);
            OxbowMinRadius = Math.Clamp(OxbowMinRadius, 8, 300);
            OxbowMaxRadius = Math.Clamp(OxbowMaxRadius, OxbowMinRadius, 400);
            OxbowWidthShare = (float)Clamp(OxbowWidthShare, 0.1, 1.0, 0.5);
            OxbowMaxRelief = Clamp(OxbowMaxRelief, 0, 200, 7);
            OxbowBankMargin = Clamp(OxbowBankMargin, 0, 64, 12);
            HeadwaterMaxRise = Clamp(HeadwaterMaxRise, 0, 200, 8);
            HeadwaterMaxTrimNodes = Math.Clamp(HeadwaterMaxTrimNodes, 0, 10);
            HeadwaterBankMargin = Clamp(HeadwaterBankMargin, 0, 64, 10);
            HeadwaterCalmBlocks = Clamp(HeadwaterCalmBlocks, 0, 5000, 800);
            HeadwaterMinSpeed = Clamp(HeadwaterMinSpeed, 0.05, 1.0, 0.25);
            TerrainNoiseShare = Clamp(TerrainNoiseShare, 0, 1, 1);
            DeltaMaxRise = Clamp(DeltaMaxRise, 0, 200, 6);
            DeltaSmallShare = Clamp(DeltaSmallShare, 0, 1, 0.334);
            DeltaShallowShare = Clamp(DeltaShallowShare, 0, 1 - DeltaSmallShare, 0.333);
            ShallowDeltaGrooveDepth = Clamp(ShallowDeltaGrooveDepth, 0, 6, 3);
            ShallowDeltaDepth = Math.Clamp(ShallowDeltaDepth, 1, 6);
            MouthShallowRange = Clamp(MouthShallowRange, 0, 255, 60);
            MouthFadeBlocks = Clamp(MouthFadeBlocks, 8, 200, 40);
            DryOxbowShare = Clamp(DryOxbowShare, 0, 1, 0.3);
            DryRiverChance = Clamp(DryRiverChance, 0, 1, 0.6);
            SourceLakeChance = double.IsNaN(SourceLakeChance) || SourceLakeChance < 0 ? -1 : Math.Min(1, SourceLakeChance);
            LakeRiverClearance = Clamp(LakeRiverClearance, 0, 96, 32);
            DryRainLimit = Clamp(DryRainLimit, 0, 1, 0.4);
            DryRiverMaxSize = (float)Clamp(DryRiverMaxSize, 2, 50, 12);
            DryFloorAboveSea = Math.Clamp(DryFloorAboveSea, 1, 7);
            ClimateInfluence = Clamp(ClimateInfluence, 0, 1, 0.7);
            DeltaScaleMin = Clamp(DeltaScaleMin, 0.3, 2.5, 0.6);
            DeltaScaleMax = Clamp(DeltaScaleMax, DeltaScaleMin, 2.5, 1.6);
            TraceChance = Clamp(TraceChance, 0, 1, 0.25);
            TraceClearance = Clamp(TraceClearance, 0, 96, 24);
            OxbowRiverClearance = Clamp(OxbowRiverClearance, 0, 96, 20);
            BranchMinSpacing = Clamp(BranchMinSpacing, 0, 200, 32);
        }

        public TraceOptions Traces() => new TraceOptions
        {
            Chance = TraceChance, MaxLevel = Math.Min(OxbowMaxLevel, 1), MinRiverSize = OxbowMinRiverSize, Clearance = TraceClearance,
            ClimateInfluence = ClimateInfluence,
        };

        static double Clamp(double v, double lo, double hi, double def) => double.IsNaN(v) || double.IsInfinity(v) ? def : Math.Clamp(v, lo, hi);

        public DeltaOptions Delta() => new DeltaOptions
        {
            Chance = DeltaChance, MinRiverSize = DeltaMinRiverSize, MinBranches = DeltaMinBranches, MaxBranches = DeltaMaxBranches,
            SpreadDegrees = DeltaSpreadDegrees, MinLength = DeltaMinLength, MaxLength = DeltaMaxLength, WidthShare = DeltaWidthShare,
            MinBranchSize = TributaryMinSize, SmallShare = DeltaSmallShare, ShallowShare = DeltaShallowShare,
        };

        public OxbowOptions Oxbow() => new OxbowOptions
        {
            Chance = OxbowChance, MinBendDegrees = OxbowMinBendDegrees, MinRiverSize = OxbowMinRiverSize, MaxLevel = OxbowMaxLevel,
            MinRadius = OxbowMinRadius, MaxRadius = OxbowMaxRadius, WidthShare = OxbowWidthShare, DryShare = DryOxbowShare,
        };
    }
}
