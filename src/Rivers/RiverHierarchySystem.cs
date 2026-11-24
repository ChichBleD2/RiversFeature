using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace RiverFeatures.Rivers
{
    /// <summary>
    /// River hierarchy for the Rivers mod (5.0.2): longer main stems, main/secondary branches at forks, tributary width caps,
    /// deltas and oxbow lakes. Harmony patches on Rivers' network generation (all applied by reflection, nothing referenced at
    /// compile time):
    ///  - RiverRegion.GenerateZones prefix: sets maxNodes (river length) on the region's config.
    ///  - RiverRegion.GenerateRiver prefix/postfix: a fork enqueues two equal requests; the postfix makes one of them the
    ///    secondary branch by advancing its stage (less node budget). Prefix also stops a river at MaxNodesPerRiver.
    ///  - River.AssignRiverSizes postfix (called once per river, before Rivers adds source lakes and indexes segments):
    ///    hierarchy (longest upstream path = main stem), tributary width caps, then delta and oxbow nodes appended to the river.
    /// Any failure disables the mod for the session with one log line; Rivers then runs unchanged.
    /// </summary>
    public class RiverHierarchySystem : ModSystem
    {
        const string ConfigFile = "riverfeatures-rivers.json";
        const string HarmonyId = "riverfeatures";

        static ICoreServerAPI sapi;
        static HierarchyConfig cfg;
        static RiversAdapter rv;
        static volatile bool disabled;
        static long seed;
        static int errorLogged;
        static long statForks, statCapped, statDeltas, statDeltaChannels, statOxbows, statRivers, statStopped;
        static long statTrimmed, statSlowed, statDeltaNoOcean, statMouthColumns, statDryNodes, statDryOxbows, statDeltaSmall, statDeltaShallow, statDeltaCarving;
        static double oceanThreshold = 90; // ocean-map units (Rivers' 30 blocks), set at world start
        static GameTerrain terrain;
        static int terrainLogged;
        /// <summary>The RiverRegion whose GenerateZones is running on this thread (AssignRiverSizes is called inside it).</summary>
        [ThreadStatic] static object currentRegion;
        /// <summary>True while GenerateZones rebuilds a region already built this world (ZonesPrefix): nothing is counted or logged again.</summary>
        [ThreadStatic] static bool repeatBuild;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(long, long), byte> regionsBuilt = new System.Collections.Concurrent.ConcurrentDictionary<(long, long), byte>();
        static readonly ConditionalWeakTable<object, object> configsDone = new ConditionalWeakTable<object, object>();

        /// <summary>Adds to a session counter, except during a repeat build of a region (ZonesPrefix).</summary>
        static void CountOnce(ref long counter, long n = 1) { if (!repeatBuild && n != 0) Interlocked.Add(ref counter, n); }

        /// <summary>A /rffind entry, except during a repeat build of a region.</summary>
        static void LogOnce(double x, double z, string kind) { if (!repeatBuild) FeatureLog.Add(x, z, kind); }

        /// <summary>A river-index segment, except during a repeat build of a region (the first build added it).</summary>
        static void IndexOnce(double x1, double z1, double x2, double z2, double size) { if (!repeatBuild) RiverIndex.Add(x1, z1, x2, z2, size); }

        /// <summary>Per-world reset of every session counter and once-flag (a second world in the same game session starts clean).</summary>
        static void ResetWorld()
        {
            statForks = statCapped = statDeltas = statDeltaChannels = statOxbows = statRivers = statStopped = 0;
            statTrimmed = statSlowed = statDeltaNoOcean = statMouthColumns = statDryNodes = statDryOxbows = statDeltaSmall = statDeltaShallow = statDeltaCarving = 0;
            statLakeFits = statLakeVaried = statLakeShrunk = statLakePond = statLakeDropped = 0;
            statLakes = statLakeMissed = statLakeShapes0 = statLakeShapes1 = statLakeShapes2 = statLakeShapes3 = statLakeShapes4 = 0;
            statDeltaRolled = statDeltaPlaced = statBranchTrimmed = statOxbowsMoved = statOxbowsNoSpot = statDeltaDryChannels = 0;
            statTraceChannel = statTraceBackwater = statTracePocket = 0;
            Array.Clear(statDeltaLooks, 0, statDeltaLooks.Length);
            errorLogged = 0; lakeShapesOff = false;
            regionsBuilt.Clear();
        }

        Harmony harmony;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        // After Rivers (RiversMod 5, RiversApi 0) so its config is loaded before the first region is generated.
        public override double ExecuteOrder() => 6;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            try { cfg = api.LoadModConfig<HierarchyConfig>(ConfigFile); } catch (Exception e) { api.Logger.Warning("[RiverFeatures] {0}", "config unreadable (" + e.Message + "); using defaults"); cfg = null; }
            cfg ??= new HierarchyConfig();
            if (cfg.Migrate()) api.Logger.Notification("[RiverFeatures] {0}", "config upgraded to version " + HierarchyConfig.CurrentVersion + " (new keys added with their defaults; see the README key table).");
            cfg.Sanitize();
            // Rivers Extended (if installed) reshapes river mouths itself (EstuaryExtension) and smooths shores and valley falloff;
            // its zone, sampling and carving patches work alongside this mod, but two systems shaping the same mouth would fight.
            try
            {
                if (api.ModLoader.IsModEnabled("riversextended"))
                {
                    rextActive = true;
                    api.Logger.Notification("[RiverFeatures] {0}", "Rivers Extended is installed: its estuaries shape the river mouths, so River Features makes no deltas and leaves mouth depth alone (hierarchy, length, oxbows, dry rivers, headwaters and all lake features stay on).");
                }
            }
            catch (Exception) { }
            try { api.StoreModConfig(cfg, ConfigFile); } catch (Exception) { }
            // Reference sheet of both config files (current value, default, range, meaning): the mod's own file only.
            try
            {
                var lakes = TerrainWatersSystem.Store?.Current ?? new ConfigStore(api).Peek();
                string err = SettingsSheet.Write(api.GetOrCreateDataPath("ModConfig"), lakes, cfg);
                if (err != null) api.Logger.Warning("[RiverFeatures] {0}", "could not write " + SettingsSheet.FileName + " (" + err + ").");
            }
            catch (Exception) { }
            if (!cfg.Enabled) { api.Logger.Notification("[RiverFeatures] disabled in {0}.", ConfigFile); return; }

            var riversAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rivers");
            if (riversAsm == null) { api.Logger.Warning("[RiverFeatures] Rivers assembly not found; nothing changed."); return; }
            rv = RiversAdapter.TryBind(riversAsm);
            if (!rv.Ok)
            {
                api.Logger.Warning("[RiverFeatures] {0}", "this Rivers version does not match (missing " + rv.Missing + "); the mod stays off and Rivers runs unchanged.");
                return;
            }
            try
            {
                harmony = new Harmony(HarmonyId);
                harmony.Patch(rv.GenerateZones, prefix: new HarmonyMethod(typeof(RiverHierarchySystem), nameof(ZonesPrefix)),
                    finalizer: new HarmonyMethod(typeof(RiverHierarchySystem), nameof(ZonesFinalizer)));
                harmony.Patch(rv.GenerateRiver, prefix: new HarmonyMethod(typeof(RiverHierarchySystem), nameof(RiverPrefix)),
                    postfix: new HarmonyMethod(typeof(RiverHierarchySystem), nameof(RiverPostfix)));
                harmony.Patch(rv.AssignRiverSizes, postfix: new HarmonyMethod(typeof(RiverHierarchySystem), nameof(SizesPostfix)));
                if (rv.AddLake != null && rv.AddLake.GetParameters().Length == 5)
                    harmony.Patch(rv.AddLake, postfix: new HarmonyMethod(typeof(RiverHierarchySystem), nameof(LakePostfix)));
                else api.Logger.Warning("[RiverFeatures] {0}", "RiverRegion.AddLake not found; source lakes keep Rivers' own teardrop shape.");
                // Dry channels (dry rivers, dry oxbows): optional; without them none are made.
                try
                {
                    if (rv.BindDry(riversAsm))
                    {
                        DryChannels.Bind(rv.SegNode);
                        typeof(DryChannels.Acc<>).MakeGenericType(rv.TSample).GetMethod("Init").Invoke(null, new object[] { rv.SampleBank });
                        harmony.Patch(rv.SampleSelf, postfix: new HarmonyMethod(typeof(DryChannels).GetMethod(nameof(DryChannels.SelfPostfix)).MakeGenericMethod(rv.TSample)));
                        harmony.Patch(rv.SampleConnector, postfix: new HarmonyMethod(typeof(DryChannels).GetMethod(nameof(DryChannels.ConnectorPostfix)).MakeGenericMethod(rv.TSample)));
                        dryOn = true;
                    }
                    else api.Logger.Warning("[RiverFeatures] {0}", "Rivers' sampling members not found; no dry rivers or dry oxbows.");
                }
                catch (Exception e) { api.Logger.Warning("[RiverFeatures] {0}", "dry-channel patch failed (" + e.Message + "); no dry rivers or dry oxbows."); }
            }
            catch (Exception e)
            {
                api.Logger.Error("[RiverFeatures] {0}", "patching Rivers failed (" + e.Message + "); the mod stays off.");
                try { harmony?.UnpatchAll(HarmonyId); } catch (Exception) { }
                return;
            }
            api.Event.InitWorldGenerator(() =>
            {
                seed = api.WorldManager.Seed;
                foreach (var line in cfg.ApplyWorldSettings(k => api.World.Config?.GetString(k, null)))
                    api.Logger.Notification("[RiverFeatures] {0}", "world setting " + line + " (overrides the mod config for this world).");
                // GenMaps, landforms and Rivers' valley noise are set up in InitWorldGen; bind on first use (BindTerrain).
                terrain = null; terrainLogged = 0; mouthBankMax = double.NaN; DryChannels.Cap = double.NaN; FeatureLog.Clear(); RiverLog.Clear(); DeltaZones.Clear(); RiverIndex.Clear(); ResetWorld();
                // Rivers' oceanThreshold is in BLOCKS of submerged land (RiverConfig.cs comment), compared in SetZoneOceanicity
                // against oceanMapValue x (MapSizeY / 256, integer division) x 0.33333 (read from IL). Convert it to ocean-map units
                // (0..255), which OceanAt returns: 30 blocks = map value 90 on a 256- or 320-high world.
                try
                {
                    var lc = rv.CfgLoaded.GetValue(null); var ot = lc?.GetType().GetField("oceanThreshold")?.GetValue(lc);
                    double blocks = ot != null ? Convert.ToDouble(ot) : 30;
                    oceanThreshold = RiverNet.OceanMapThreshold(blocks, api.WorldManager.MapSizeY);
                }
                catch (Exception) { oceanThreshold = RiverNet.OceanMapThreshold(30, 256); }
            }, "standard");
            api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () =>
                api.Logger.Notification("[RiverFeatures] stage 2 this session: deltas by look sandy/marshy/overgrown/dry/intermittent/rocky {0}, dry delta channels {1}, deltas with bank character {2}; traces of the past dry channel/backwater/pocket {3}/{4}/{5}; climate from {6} (influence {7}); tributary nodes cut back where they crowded another channel {8}; delta channels placed {9} of {10} rolled.",
                    string.Join("/", statDeltaLooks), statDeltaDryChannels, DeltaZones.Count, statTraceChannel, statTraceBackwater, statTracePocket,
                    ClimateSources.Registered?.Name ?? "the game climate map", cfg.ClimateInfluence, statBranchTrimmed, statDeltaPlaced, statDeltaRolled));
            api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () =>
                api.Logger.Notification("[RiverFeatures] this session: rivers {0}, forks made secondary {1}, tributary nodes narrowed {2}, deltas {3} ({4} channels), oxbow lakes {5} (placed at another spot {6}, no spot on flat ground clear of other rivers and lakes {21}), headwater nodes trimmed on steep ground {7}, headwater nodes slowed {8}, rivers stopped at the node cap {9}, river mouths without an ocean map (no delta possible) {10}, river mouths faded at the coast {11}, dry river nodes {12}, dry oxbows {13}, deltas by type small/carving/shallow {14}/{15}/{16}, source lakes {17} (teardrop/oval/finger/winding/lobed {18}; not found to reshape {19}; clear of rivers as planned/other variant/shrunk/small pond/removed {20}).",
                    statRivers, statForks, statCapped, statDeltas, statDeltaChannels, statOxbows, statOxbowsMoved, statTrimmed, statSlowed, statStopped, statDeltaNoOcean, statMouthColumns, statDryNodes, statDryOxbows, statDeltaSmall, statDeltaCarving, statDeltaShallow, statLakes, statLakeShapes0 + "/" + statLakeShapes1 + "/" + statLakeShapes2 + "/" + statLakeShapes3 + "/" + statLakeShapes4, statLakeMissed, statLakeFits + "/" + statLakeVaried + "/" + statLakeShrunk + "/" + statLakePond + "/" + statLakeDropped, statOxbowsNoSpot));
            api.Logger.Notification("[RiverFeatures] active: max river length {0} blocks, tributary length share {1}, width share {2} (cap {3}), delta chance {4}, oxbow chance {5}, source lake chance {6}.",
                cfg.MaxRiverLengthBlocks, cfg.TributaryLengthShare, cfg.TributaryWidthShare, cfg.TributaryMaxSize, cfg.DeltaChance, cfg.OxbowChance,
                cfg.SourceLakeChance < 0 ? "Rivers' own (lakeChance in rivers.json)" : cfg.SourceLakeChance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public override void Dispose()
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch (Exception) { }
            sapi = null; rv = null; disabled = false;
        }

        static void Fail(Exception e)
        {
            disabled = true;
            if (Interlocked.Exchange(ref errorLogged, 1) == 0)
                sapi?.Logger.Error("[RiverFeatures] {0}", "error while adjusting Rivers' network; the mod is off for the rest of the session: " + e);
        }

        // ---------------------------------------------------------------- patches

        /// <summary>Sets maxNodes from MaxRiverLengthBlocks on the region's config (and Rivers' loaded config), once per config object.</summary>
        public static void ZonesPrefix(object __instance)
        {
            currentRegion = __instance;
            repeatBuild = false;
            if (disabled) return;
            // Rivers rebuilds a region's whole network every time it loads the region. The rebuild must be reshaped exactly as
            // the first time (same nodes, same dry channels), but its counters, /rffind entries and river-index segments are
            // already recorded: only the first build of a region this world records them (CountOnce, LogOnce).
            try
            {
                var o = rv.RegionOrigin(__instance);
                if (!double.IsNaN(o.X)) repeatBuild = !regionsBuilt.TryAdd(((long)o.X, (long)o.Z), 0);
            }
            catch (Exception) { repeatBuild = false; }
            try
            {
                foreach (var c in new[] { rv.RegionConfig.GetValue(__instance), rv.CfgLoaded.GetValue(null) })
                {
                    if (c == null || configsDone.TryGetValue(c, out _)) continue;
                    if (cfg.MaxRiverLengthBlocks > 0)
                    {
                        // River length only sets Rivers' maxNodes (how far GenerateRiver lets a river grow, in memory; rivers.json
                        // is never written). riverSpawnChance is not touched: Rivers rolls it per coastal zone before any river
                        // grows, so where rivers start does not depend on their length. Floor at Rivers' minNodes (see MaxNodesFor).
                        int minNodes = 1;
                        try { if (rv.CfgMinNodes != null) minNodes = Convert.ToInt32(rv.CfgMinNodes.GetValue(c)); } catch (Exception) { }
                        int nodes = RiverNet.MaxNodesFor(cfg.MaxRiverLengthBlocks, (int)rv.CfgMinLength.GetValue(c), (int)rv.CfgLengthVariation.GetValue(c), minNodes);
                        rv.CfgMaxNodes.SetValue(c, nodes);
                    }
                    // Source lakes (flight test 11): Rivers rolls lakeChance once per river end node and adds a lake there
                    // (RiverRegion, after the river is built). Dry river tips have no end flag, so they never get one.
                    if (cfg.SourceLakeChance >= 0 && rv.CfgLakeChance != null)
                        rv.CfgLakeChance.SetValue(c, Convert.ChangeType(cfg.SourceLakeChance, rv.CfgLakeChance.FieldType));
                    configsDone.Add(c, new object());
                }
            }
            catch (Exception e) { Fail(e); }
        }

        static long statLakeFits, statLakeVaried, statLakeShrunk, statLakePond, statLakeDropped;
        static long statLakes, statLakeMissed, statLakeShapes0, statLakeShapes1, statLakeShapes2, statLakeShapes3, statLakeShapes4;

        /// <summary>
        /// Flight test 12: Rivers' source lake (AddLake) is always one widening node, a teardrop. Reshape it into a shape picked
        /// from a hash of its position (LakeShapes), and log it for /rffind. Args: minSize, maxSize, river, parent, angle.
        /// </summary>
        public static void LakePostfix(object __instance, object[] __args)
        {
            // Isolated from the rest of the mod: a failure here only stops lake reshaping (Rivers' own teardrop lakes stay),
            // it never switches the river network patches off. ReshapeLake is all-or-nothing, so a failed lake is untouched.
            if (disabled || lakeShapesOff) return;
            try
            {
                var river = __args[2]; var parent = __args[3];
                var lake = river == null ? null : rv.NewLakeNode(river, parent);
                if (lake == null) { CountOnce(ref statLakeMissed); return; }
                var a = rv.StartOf(lake); var b = rv.EndOf(lake);
                var dir = (b - a).Normalized();
                float s0 = (float)rv.NodeStartSize.GetValue(lake), e = (float)rv.NodeEndSize.GetValue(lake);
                double Rnd(int salt) => RiverNet.Hash01(seed, (long)a.X, (long)a.Z, 500 + salt);
                List<LakeShapes.Piece> pieces;
                LakeShape shape;
                if (cfg.VariedSourceLakes)
                {
                    // Flight tests 13/14: procedural shape, kept LakeRiverClearance blocks of land away from every channel and
                    // lake of the region (Rivers has generated all of the region's rivers before it adds any lake). Only the
                    // lake's own river is exempt, and only at the join near the start.
                    double join = s0 + 24;
                    float unsized = 50f;
                    try { var lc = rv.CfgLoaded.GetValue(null); if (lc != null) unsized = (float)rv.CfgMaxSize.GetValue(lc); } catch (Exception) { }
                    var obstacles = rv.ObstaclesNear(__instance, river, lake, a, LakeShapes.MaxReach + 80, unsized);
                    var (plan, how) = LakeShapes.Fit(a, dir, s0, e, Rnd, obstacles, cfg.LakeRiverClearance, join);
                    if (how == LakeShapes.FitResult.Dropped)
                    {
                        // Even a small pond would crowd another river: remove Rivers' lake (the source stays a plain river end).
                        rv.RemoveTip(river, lake);
                        CountOnce(ref statLakeDropped);
                        return;
                    }
                    pieces = plan;
                    shape = LakeShapes.Classify(plan);
                    rv.ReshapeLake(river, lake, pieces, seed);
                    CountOnce(ref how == LakeShapes.FitResult.Fits ? ref statLakeFits : ref how == LakeShapes.FitResult.Varied ? ref statLakeVaried
                        : ref how == LakeShapes.FitResult.Shrunk ? ref statLakeShrunk : ref statLakePond);
                }
                else
                {
                    shape = LakeShape.Teardrop;
                    pieces = new List<LakeShapes.Piece> { new LakeShapes.Piece(a, b, s0, e, -1) };
                }
                CountOnce(ref statLakes);
                CountOnce(ref shape == LakeShape.Teardrop ? ref statLakeShapes0 : ref shape == LakeShape.Oval ? ref statLakeShapes1
                    : ref shape == LakeShape.Finger ? ref statLakeShapes2 : ref shape == LakeShape.Winding ? ref statLakeShapes3 : ref statLakeShapes4);
                var origin = rv.RegionOrigin(__instance);
                if (!double.IsNaN(origin.X))
                {
                    double cx = 0, cz = 0;
                    foreach (var p in pieces) { cx += (p.A.X + p.B.X) * 0.5; cz += (p.A.Z + p.B.Z) * 0.5; }
                    foreach (var p in pieces) IndexOnce(origin.X + p.A.X, origin.Z + p.A.Z, origin.X + p.B.X, origin.Z + p.B.Z, Math.Max(p.SizeA, p.SizeB));
                    LogOnce(origin.X + cx / pieces.Count, origin.Z + cz / pieces.Count, "lake-source-" + shape.ToString().ToLowerInvariant());
                }
            }
            catch (Exception e)
            {
                lakeShapesOff = true;
                sapi?.Logger.Warning("[RiverFeatures] {0}", "source-lake reshaping failed and is off for the rest of the session (Rivers' own lakes and everything else keep working): " + e);
            }
        }

        static volatile bool lakeShapesOff;

        public static Exception ZonesFinalizer(Exception __exception)
        {
            currentRegion = null;
            repeatBuild = false;
            terrain?.ClearCache();
            return __exception;
        }

        /// <summary>The terrain estimate, bound once the world generator is initialised; null (one log line) when unavailable.</summary>
        static GameTerrain BindTerrain()
        {
            var t = terrain;
            if (t != null) return t.Ok ? t : null;
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rivers");
            t = GameTerrain.Bind(sapi, asm, rv.CfgLoaded.GetValue(null));
            terrain = t;
            if (Interlocked.Exchange(ref terrainLogged, 1) == 0)
            {
                if (t.Ok) sapi.Logger.Notification("[RiverFeatures] {0}", "terrain estimate bound (landform map + Rivers valley): oxbows need relief <= " + cfg.OxbowMaxRelief + " blocks, headwaters <= " + cfg.HeadwaterMaxRise + " blocks above the water.");
                else sapi.Logger.Warning("[RiverFeatures] {0}", "terrain estimate unavailable (" + t.Missing + "): oxbow and headwater terrain checks are off.");
            }
            return t.Ok ? t : null;
        }

        /// <summary>Remembers the queue length (fork detection) and stops a river at MaxNodesPerRiver.</summary>
        public static bool RiverPrefix(object[] __args, ref bool __result, out int __state)
        {
            __state = -1;
            if (disabled) return true;
            try
            {
                if (__args[4] != null && rv.Nodes(__args[4]).Count >= cfg.MaxNodesPerRiver)
                {
                    CountOnce(ref statStopped);
                    __result = false;
                    return false; // skip Rivers' GenerateRiver: this branch ends here
                }
                if (__args[6] is ICollection q) __state = q.Count;
            }
            catch (Exception e) { Fail(e); }
            return true;
        }

        /// <summary>A fork enqueued two requests with the same stage: give the secondary one (picked by a position hash) less budget.</summary>
        public static void RiverPostfix(bool __result, object[] __args, int __state)
        {
            if (disabled || !__result || __state < 0 || cfg.TributaryLengthShare >= 1.0) return;
            try
            {
                if (!(__args[6] is ICollection q) || q.Count - __state != 2) return;
                object a = null, b = null;
                int i = 0;
                foreach (var r in (IEnumerable)q) { if (i == __state) a = r; else if (i == __state + 1) b = r; i++; }
                if (a == null || b == null) return;
                var start = (Vector2d)rv.ReqStartPos.GetValue(a);
                var secondary = RiverNet.Hash01(seed, (long)start.X, (long)start.Y, 41) < 0.5 ? a : b;
                int stage = (int)rv.ReqStage.GetValue(secondary);
                int maxNodes = CurrentMaxNodes();
                rv.ReqStage.SetValue(secondary, RiverNet.SecondaryStage(stage, maxNodes, cfg.TributaryLengthShare));
                CountOnce(ref statForks);
            }
            catch (Exception e) { Fail(e); }
        }

        static int CurrentMaxNodes()
        {
            var loaded = rv.CfgLoaded.GetValue(null);
            return loaded != null ? (int)rv.CfgMaxNodes.GetValue(loaded) : 20;
        }

        /// <summary>After Rivers sized one river: hierarchy and width caps, then deltas and oxbows (new nodes appended).</summary>
        public static void SizesPostfix(object __instance)
        {
            if (disabled) return;
            try
            {
                var nodes = rv.Nodes(__instance);
                if (nodes == null || nodes.Count == 0) return;
                CountOnce(ref statRivers);
                var net = rv.Read(nodes);

                // Terrain estimate (world coordinates = region origin + Rivers-local positions).
                var origin = rv.RegionOrigin(currentRegion);
                var gt = double.IsNaN(origin.X) ? null : BindTerrain();
                var model = gt?.Model(cfg.TerrainNoiseShare);

                // Headwaters on steep ground: remove source tips whose ground stands well above the water (sea level).
                if (model != null && cfg.HeadwaterMaxRise > 0 && cfg.HeadwaterMaxTrimNodes > 0)
                {
                    var trim = TerrainRules.HeadwatersToTrim(net,
                        (i, removed) => TerrainRules.HeadwaterRise(net, i, cfg.HeadwaterBankMargin, model, origin, removed) > cfg.HeadwaterMaxRise,
                        cfg.HeadwaterMaxTrimNodes, minKeep: 2);
                    if (trim.Count > 0)
                    {
                        var objs = trim.OrderByDescending(i => i).Select(i => nodes[i]).ToList(); // tips before their parents
                        foreach (var o in objs) rv.RemoveTip(__instance, o);
                        CountOnce(ref statTrimmed, objs.Count);
                        net = rv.Read(nodes);
                    }
                }

                var h = RiverNet.Classify(net);
                // Flight test 16: tributaries running right beside another channel are cut back where they crowd it.
                if (cfg.BranchMinSpacing > 0)
                {
                    var crowd = RiverNet.CrowdedBranches(net, h, cfg.BranchMinSpacing);
                    if (crowd.Count > 0)
                    {
                        var depth = RiverNet.Depths(net, h);
                        var objs = crowd.OrderByDescending(i => depth[i]).Select(i => nodes[i]).ToList(); // tips before their parents
                        foreach (var o in objs) rv.RemoveTip(__instance, o);
                        CountOnce(ref statBranchTrimmed, objs.Count);
                        net = rv.Read(nodes);
                        h = RiverNet.Classify(net);
                    }
                }
                int capped = RiverNet.CapTributaries(net, h, cfg.TributaryMaxSize, cfg.TributaryWidthShare, cfg.TributaryMinSize);
                rv.WriteSizes(nodes, net);
                CountOnce(ref statCapped, capped);

                // /rffind river: the main stem's length once the tree is final (after trims; deltas, oxbows and traces are
                // added below and are not part of it). First build of a region only, like every other log.
                if (!repeatBuild && !double.IsNaN(origin.X))
                    for (int i = 0; i < net.Count; i++)
                    {
                        if (net[i].Parent >= 0) continue;
                        var stem = RiverNet.MainStem(net, h, i);
                        if (stem == null) continue;
                        var s = stem.Value;
                        RiverLog.Add(new RiverLog.River((int)Math.Round(origin.X + s.Mouth.X), (int)Math.Round(origin.Z + s.Mouth.Z),
                            (int)Math.Round(origin.X + s.Source.X), (int)Math.Round(origin.Z + s.Source.Z), (int)Math.Round(s.Length), s.StemNodes, s.TotalNodes));
                    }

                // Calm headwaters: flow speed ramps up from the sources (Rivers gives every node speed 1).
                if (cfg.HeadwaterCalmBlocks > 0)
                {
                    var speeds = TerrainRules.HeadwaterSpeeds(net, h, cfg.HeadwaterCalmBlocks, cfg.HeadwaterMinSpeed);
                    int slowed = 0;
                    for (int i = 0; i < net.Count && i < nodes.Count; i++)
                    {
                        if (net[i].IsLake) continue;
                        rv.NodeSpeed.SetValue(nodes[i], speeds[i]);
                        if (speeds[i] < 1f) slowed++;
                    }
                    CountOnce(ref statSlowed, slowed);
                }

                var loaded = rv.CfgLoaded.GetValue(null);
                float riversMax = loaded != null ? (float)rv.CfgMaxSize.GetValue(loaded) : 50f;
                int regionSize = loaded != null ? (int)rv.CfgRegionSize.GetValue(loaded) : 32768;
                bool Inside(P2 p) => p.X > 64 && p.Z > 64 && p.X < regionSize - 64 && p.Z < regionSize - 64;
                if (dryOn && double.IsNaN(DryChannels.Cap)) DryChannels.Cap = DryBankCap(loaded);

                // Dry rivers (flight test 7): whole branches without water, likelier where it rains little.
                var dry = new bool[net.Count];
                if (dryOn && gt != null && cfg.DryRiverChance > 0)
                {
                    var o1 = origin; var drySrc = gt.AsClimateSource();
                    // Audit: rain through ClimateSources, so a registered climate source drives dry rivers too. Dry rivers are
                    // a rainfall rule (DryRainLimit), not part of the ClimateInfluence bias.
                    dry = RiverNet.DryRivers(net, h, cfg.DryRiverChance, cfg.DryRainLimit,
                        i => { var c = (net[i].Start + net[i].End) * 0.5; return ClimateSources.At(drySrc, o1.X + c.X, o1.Z + c.Z).Rain; },
                        i => RiverNet.Hash01(seed, (long)net[i].Start.X, (long)net[i].Start.Z, 300));
                    int nDry = 0;
                    for (int i = 0; i < net.Count && i < nodes.Count; i++)
                    {
                        if (!dry[i]) continue;
                        var n = nodes[i];
                        float s0 = Math.Min(net[i].StartSize, cfg.DryRiverMaxSize), s1 = Math.Min(net[i].EndSize, s0);
                        rv.NodeStartSize.SetValue(n, s0); rv.NodeEndSize.SetValue(n, s1);
                        net[i].StartSize = s0; net[i].EndSize = s1;
                        rv.NodeSpeed.SetValue(n, 0f);
                        rv.NodeEndFlag.SetValue(n, false); // no source lake at a dry tip
                        DryChannels.Register(n, new DryChannels.Info
                        {
                            FadeDownstream = net[i].Parent < 0,
                            Start = new Vector2d(net[i].Start.X, net[i].Start.Z), End = new Vector2d(net[i].End.X, net[i].End.Z),
                        });
                        nDry++;
                        // One /rffind entry per dry river: its most downstream dry node (the parent is wet or there is none).
                        int par = net[i].Parent;
                        if (par < 0 || par >= dry.Length || !dry[par])
                        {
                            var mid = (net[i].Start + net[i].End) * 0.5;
                            LogOnce(origin.X + mid.X, origin.Z + mid.Z, "dry-river");
                        }
                    }
                    CountOnce(ref statDryNodes, nDry);
                }

                // Features this postfix adds to the river (delta channels, oxbows, traces), Rivers-local: later ones keep clear of them.
                var extras = new List<LakeShapes.Obstacle>();

                // Deltas at the mouth(s).
                for (int i = 0; i < net.Count; i++)
                {
                    var m = net[i];
                    if (m.Parent >= 0 || m.IsLake || dry[i] || rextActive) continue;
                    long mx = (long)m.Start.X, mz = (long)m.Start.Z;
                    // Flight test 7: deltas on land at the coast (ocean map), not fanned out at sea from Rivers' offshore mouth.
                    if (gt == null || !gt.HasOcean) { CountOnce(ref statDeltaNoOcean); continue; }
                    var o0 = origin;
                    // Flight test 10: river mouths fade out at the coast. Rivers carves every channel column to the same depth, also
                    // out on the shelf; capping the bank factor seaward of the coast (DryChannels, the hook Rivers' terrain really
                    // calls) to a cut that ends at sea level leaves the sea floor alone, with full depth again at the coast.
                    if (dryOn && cfg.MouthShallowRange > 0)
                    {
                        if (double.IsNaN(mouthBankMax)) mouthBankMax = MouthBankCap(loaded);
                        var cst = RiverNet.FindCoast(net, h, i, q => gt.OceanAt(o0.X + q.X, o0.Z + q.Z), oceanThreshold);
                        if (cst != null)
                        {
                            double fade = Math.Max(8, cfg.MouthFadeBlocks);
                            var s0 = cst.Value.Coast + cst.Value.SeaDir * fade;
                            var e0 = s0 - cst.Value.SeaDir * (fade / 0.6);
                            var minfo = new DryChannels.Info { Cap = mouthBankMax, RelaxUpstream = true, Start = new Vector2d(s0.X, s0.Z), End = new Vector2d(e0.X, e0.Z) };
                            for (int j = i, k = 0; j >= 0 && k < 3; j = h.MainChild[j], k++) DryChannels.Register(nodes[j], minfo);
                            CountOnce(ref statMouthColumns);
                        }
                    }
                    // Stage 2: the delta's character (size, bank material, moisture, plants, branching), leaned by the local
                    // climate at the river's last stretch (DeltaTraits). The shape (small / carving / shallow) is rolled as before.
                    var traits = DeltaTraitRules.Roll(ClimateSources.At(gt.AsClimateSource(), o0.X + m.End.X, o0.Z + m.End.Z),
                        cfg.ClimateInfluence, m.StartSize / Math.Max(1f, riversMax), cfg.DeltaScaleMin, cfg.DeltaScaleMax,
                        salt => RiverNet.Hash01(seed, mx, mz, 600 + salt));
                    var channels = RiverNet.CoastDelta(net, h, i, riversMax, cfg.Delta(),
                        q => gt.OceanAt(o0.X + q.X, o0.Z + q.Z), oceanThreshold,
                        pts => model != null && cfg.DeltaMaxRise > 0 && TooHigh(pts, model, o0, cfg.DeltaMaxRise),
                        salt => RiverNet.Hash01(seed, mx, mz, 100 + salt), out P2 deltaApex, out int rolledCh, 24, traits.Scale, traits.Branching);
                    // A big delta reaches further: keep its bank character inside the region (margin 64 + bank reach + width).
                    double margin = DeltaTraitRules.BankReach(traits) + m.StartSize;
                    if (channels.Count == 0 || !channels.All(c => Inside(c.Start) && InsideBy(c.Start, regionSize, 64 + margin) && InsideBy(c.End, regionSize, 64 + margin))) continue;
                    CountOnce(ref statDeltaRolled, rolledCh); CountOnce(ref statDeltaPlaced, channels.Count);   // kept deltas only
                    int bIdx = 0;
                    // Flight test 16: shallow channels 1..5 blocks deeper, per delta, each channel +-1 around it. Flight test 16b: in
                    // their centre groove only (GrooveBankFor); the plain keeps ShallowDeltaDepth, 1-block water, as before 16.
                    int deltaExtra = 1 + (int)Math.Floor(RiverNet.Hash01(seed, mx, mz, 710) * 5);
                    // Flight test 17: the shallow plain varies 1..3 blocks of water: 0, 1 or 2 blocks below ShallowDeltaDepth
                    // (45/35/20%), per delta for the main stem and per channel around it.
                    int deltaPlain = RiverNet.PlainExtra(RiverNet.Hash01(seed, mx, mz, 740));
                    var pieces = new List<(P2 a, P2 b, float size)>();
                    foreach (var c in channels)
                    {
                        int ci = bIdx;
                        int chExtra = Math.Clamp(deltaExtra + (int)Math.Floor(RiverNet.Hash01(seed, mx, mz, 720 + ci) * 3) - 1, 1, 5);
                        int chPlain = Math.Clamp(deltaPlain + (int)Math.Floor(RiverNet.Hash01(seed, mx, mz, 750 + ci) * 3) - 1, 0, 2);
                        bool chDry = dryOn && DeltaTraitRules.ChannelDry(traits, RiverNet.Hash01(seed, mx, mz, 700 + bIdx++));
                        // Flight test 17 (-18280,-18235): one straight node read as a canal cut through the land. The channel is now
                        // a gently meandering path of connected pieces (RiverNet.Meander), unless the curve leaves low ground.
                        // Flight test 17b: the bend is limited by the angle to the nearest neighbouring channel (or the main stem),
                        // so two channels can never meander into each other.
                        double gap = RiverNet.NearestChannelAngle(channels, ci, (m.Start - deltaApex).Normalized());
                        var path = RiverNet.Meander(c.End, c.Start, salt => RiverNet.Hash01(seed, mx, mz, 800 + 10 * ci + salt), RiverNet.MeanderCap(gap));
                        // Flight test 18: if the meander leaves low ground, a barely curved path (cap 0.03), then the old single node.
                        if (path.Count > 2 && model != null && cfg.DeltaMaxRise > 0 && TooHigh(path, model, o0, cfg.DeltaMaxRise))
                        {
                            path = RiverNet.Meander(c.End, c.Start, salt => RiverNet.Hash01(seed, mx, mz, 800 + 10 * ci + salt), 0.03);
                            if (TooHigh(path, model, o0, cfg.DeltaMaxRise)) path = new List<P2> { c.End, c.Start };
                        }
                        DryChannels.Info info;
                        if (chDry) info = new DryChannels.Info();   // dry or intermittent delta: an abandoned, gravelled distributary
                        else if (dryOn && (traits.Moisture < DeltaTraits.WetFrom || c.Kind == DeltaKind.Shallow))
                            info = new DryChannels.Info { Cap = ShallowBankCap(loaded, chPlain), Groove = true, GrooveBank = GrooveBankFor(chExtra) };
                        else if (dryOn && cfg.MouthShallowRange > 0 && !double.IsNaN(mouthBankMax))
                        {
                            // The channel's last 24 blocks lie past the coast: fade them out like the river mouth (no trench on the shelf).
                            var toApex = (c.End - c.Start).Normalized();
                            var ce = c.Start + toApex * (24 / 0.6);
                            info = new DryChannels.Info { Cap = mouthBankMax, RelaxUpstream = true, Start = new Vector2d(c.Start.X, c.Start.Z), End = new Vector2d(ce.X, ce.Z) };
                        }
                        else info = null;
                        // Pieces from the sea end (Start = downstream, as Rivers' nodes) up to the fork.
                        // Flight test 18 (-9989,-18836, 5046,-16668, -6939,-4359): every piece was its own AddChannel node, which
                        // Rivers jitters independently and never joins to the next, so the meander came out as a sawtooth of
                        // sharp corners. Now ONE linked chain (as oxbows): straight segments joined parent to child, so Rivers'
                        // connector sampling rounds every joint. A path of only two points (the straight fallback) stays a
                        // single jittered node, as before flight test 17.
                        float spd = chDry ? 0f : c.Speed;
                        if (path.Count > 2)
                        {
                            var chainPts = new List<P2>(path); chainPts.Reverse();   // sea end first: each node's parent is downstream
                            int before = nodes.Count;
                            rv.AddChain(__instance, chainPts, c.Size, seed, spd);
                            for (int k = before; k < nodes.Count; k++) if (info != null) DryChannels.Register(nodes[k], info);
                        }
                        else
                        {
                            var node = rv.AddChannel(__instance, c.Start, c.End, c.Size, spd, seed);
                            if (info != null) DryChannels.Register(node, info);
                        }
                        for (int k = path.Count - 1; k > 0; k--) pieces.Add((path[k], path[k - 1], c.Size));
                        if (chDry) CountOnce(ref statDeltaDryChannels);
                    }
                    var dk = channels[0].Kind;
                    if (dk == DeltaKind.Shallow && dryOn)
                    {
                        // The river's last stretch becomes shallow and slow too, so the whole mouth is a flat delta plain: the
                        // main stem from the mouth up to the apex stays shallow, then deepens gradually over a rise of 160..320
                        // blocks (per delta) past the apex, with riffles along it (DryChannels Amp / Waves).
                        // Flight test 17: the rise was measured along the MOUTH's straight direction; on a bending river that
                        // distance never reached the end, so up to 16 nodes (kilometres) stayed shallow. Now each main-stem node
                        // gets its own Info along its own direction, offset (T0) by its distance ALONG THE RIVER past the apex.
                        var apex = deltaApex;
                        double rise = 160 + 160 * RiverNet.Hash01(seed, mx, mz, 730);
                        double amp = 0.1 + 0.2 * RiverNet.Hash01(seed, mx, mz, 731), waves = 1 + Math.Floor(RiverNet.Hash01(seed, mx, mz, 732) * 3);
                        double phase = 2 * Math.PI * RiverNet.Hash01(seed, mx, mz, 733);
                        var stem = RiverNet.StemRise(net, h, i, apex, rise);
                        for (int q = 0; q < stem.Count; q++)
                        {
                            var (j, t0) = stem[q];
                            var nd = net[j];
                            var dirJ = nd.UpDir;
                            DryChannels.Register(nodes[j], new DryChannels.Info
                            {
                                Cap = ShallowBankCap(loaded, deltaPlain), RelaxUpstream = true, Groove = true, GrooveBank = GrooveBankFor(deltaExtra),
                                Start = new Vector2d(nd.Start.X, nd.Start.Z), End = new Vector2d(nd.Start.X + dirJ.X * rise / 0.6, nd.Start.Z + dirJ.Z * rise / 0.6),
                                T0 = t0, Amp = amp, Waves = waves, Phase = phase,
                            });
                            if (q == 0) rv.NodeSpeed.SetValue(nodes[j], 0.3f);
                        }
                    }
                    CountOnce(ref dk == DeltaKind.Small ? ref statDeltaSmall : ref dk == DeltaKind.Shallow ? ref statDeltaShallow : ref statDeltaCarving);
                    var look = traits.Look;
                    CountOnce(ref statDeltaLooks[(int)look]);
                    {
                        var ap = deltaApex;
                        LogOnce(origin.X + ap.X, origin.Z + ap.Z, "delta-" + dk.ToString().ToLowerInvariant() + "-" + look.ToString().ToLowerInvariant());
                        // Bank character for the chunk pass (world coordinates): every distributary piece and the main stem from the
                        // apex to the mouth.
                        var zone = new DeltaZone { Traits = traits, Salt = (long)(RiverNet.Hash01(seed, mx, mz, 777) * long.MaxValue) };
                        foreach (var pc in pieces) zone.AddSegment(origin.X + pc.a.X, origin.Z + pc.a.Z, origin.X + pc.b.X, origin.Z + pc.b.Z, pc.size);
                        zone.AddSegment(origin.X + ap.X, origin.Z + ap.Z, origin.X + m.Start.X, origin.Z + m.Start.Z, m.StartSize);
                        DeltaZones.Add(origin.X + ap.X, origin.Z + ap.Z, zone);
                        foreach (var pc in pieces) extras.Add(new LakeShapes.Obstacle(pc.a, pc.b, pc.size, pc.size));
                    }
                    CountOnce(ref statDeltas);
                    CountOnce(ref statDeltaChannels, channels.Count);
                }

                // Oxbows beside bends (computed on the tree before the delta nodes, which are not part of it). Varied shapes;
                // some are dry (an old channel floor above the water); none beside a dry river.
                var opt = cfg.Oxbow();
                if (!dryOn) opt.DryShare = 0;
                // Stage 2: every rolled oxbow tries several placements (smaller, mirrored, closer) and keeps the first that is
                // inside the region, on ground no steeper than OxbowMaxRelief, OxbowRiverClearance clear of every other river's
                // channel and lake in the region (source lakes: LakeRiverClearance / 2), and clear of this river's deltas and
                // traces; an oxbow is left out only when no placement fits.
                float OxSize(int node) => Math.Max(cfg.TributaryMinSize, net[node].StartSize * cfg.OxbowWidthShare);
                bool AcceptOxbow(List<P2> chain, int node)
                {
                    if (dry[node] || !chain.All(Inside)) return false;
                    float size = OxSize(node);
                    var pieces = new List<LakeShapes.Piece>();
                    for (int k = 0; k + 1 < chain.Count; k++) pieces.Add(new LakeShapes.Piece(chain[k], chain[k + 1], size, size, k - 1));
                    if (extras.Count > 0 && LakeShapes.Clearance(pieces, extras, chain[0], 0) < cfg.OxbowRiverClearance) return false;
                    if (currentRegion != null)
                    {
                        var mid = chain[chain.Count / 2];
                        var others = rv.ObstaclesNear(currentRegion, __instance, null, mid, 400, riversMax).Where(o => !o.Own).ToList();
                        if (others.Count > 0 && LakeShapes.Clearance(pieces, others, chain[0], 0) < cfg.OxbowRiverClearance) return false;
                        if (cfg.VariedSourceLakes)
                        {
                            var lakes = rv.ObstaclesNear(currentRegion, null, null, mid, 400, 0f, lakesOnly: true);
                            if (lakes.Count > 0 && LakeShapes.Clearance(pieces, lakes, chain[0], 0) < cfg.LakeRiverClearance * 0.5) return false;
                        }
                    }
                    if (model != null && cfg.OxbowMaxRelief > 0)
                    {
                        double relief = TerrainRules.OxbowRelief(chain, size, cfg.OxbowBankMargin, net, model, origin);
                        if (double.IsNaN(relief) || relief > cfg.OxbowMaxRelief) return false;
                    }
                    // Audit: OxbowsAt keeps every accepted placement, so register it now; the next oxbow on this river (a
                    // neighbouring bend, a mirrored or closer retry) then keeps clear of it.
                    foreach (var pc in pieces) extras.Add(new LakeShapes.Obstacle(pc.A, pc.B, size, size));
                    return true;
                }
                var oxbows = RiverNet.OxbowsAt(net, h, opt, (i, salt) => RiverNet.Hash01(seed, (long)net[i].Start.X, (long)net[i].Start.Z, 200 + salt),
                    AcceptOxbow, out int oxRejected);
                CountOnce(ref statOxbowsNoSpot, oxRejected);
                foreach (var ox in oxbows)
                {
                    var chain = ox.Points;
                    float size = OxSize(ox.Node);
                    if (ox.Attempt > 0) CountOnce(ref statOxbowsMoved);   // already in extras (AcceptOxbow)
                    int before = nodes.Count;
                    int added = rv.AddChain(__instance, chain, size, seed);
                    if (added <= 0) continue;
                    CountOnce(ref statOxbows);
                    LogOnce(origin.X + chain[chain.Count / 2].X, origin.Z + chain[chain.Count / 2].Z, ox.Dry ? "oxbow-dry" : "oxbow");
                    if (!ox.Dry) continue;
                    for (int k = before; k < nodes.Count; k++) DryChannels.Register(nodes[k], new DryChannels.Info());
                    CountOnce(ref statDryOxbows);
                }

                // Stage 2: traces of the past (dry side channels, backwaters, floodplain pockets), climate-leaned, each clear of
                // the river tree, of this river's deltas and oxbows, of every other river and lake of the region, on flat ground.
                if (cfg.TraceChance > 0)
                {
                    var climateSrc = gt?.AsClimateSource();
                    var o2 = origin;
                    var traces = Traces.Plan(net, h, cfg.Traces(),
                        i => { if (double.IsNaN(o2.X)) return ClimateSample.Unknown; var c = (net[i].Start + net[i].End) * 0.5; return ClimateSources.At(climateSrc, o2.X + c.X, o2.Z + c.Z); },
                        (i, salt) => RiverNet.Hash01(seed, (long)net[i].Start.X, (long)net[i].Start.Z, 900 + salt), extras,
                        t =>
                        {
                            if (dry[t.Node] || !t.Points.All(Inside)) return false;
                            if (t.Kind == TraceKind.Channel && !dryOn) return false;
                            var pieces = Traces.AsPieces(t);
                            if (currentRegion != null)
                            {
                                var others = rv.ObstaclesNear(currentRegion, __instance, null, t.Points[t.Points.Count / 2], 300, riversMax).Where(o => !o.Own).ToList();
                                if (others.Count > 0 && LakeShapes.Clearance(pieces, others, t.Points[0], 0) < cfg.TraceClearance) return false;
                            }
                            if (model != null && cfg.OxbowMaxRelief > 0)
                            {
                                double relief = TerrainRules.OxbowRelief(t.Points, t.Size, cfg.OxbowBankMargin, net, model, origin);
                                if (double.IsNaN(relief) || relief > cfg.OxbowMaxRelief) return false;
                            }
                            return true;
                        });
                    foreach (var t in traces)
                    {
                        int before = nodes.Count;
                        if (rv.AddChain(__instance, t.Points, t.Size, seed) <= 0) continue;
                        if (t.Kind == TraceKind.Channel)
                            for (int k = before; k < nodes.Count; k++) DryChannels.Register(nodes[k], new DryChannels.Info());
                        CountOnce(ref t.Kind == TraceKind.Channel ? ref statTraceChannel : ref t.Kind == TraceKind.Backwater ? ref statTraceBackwater : ref statTracePocket);
                        var mid = t.Points[t.Points.Count / 2];
                        if (!double.IsNaN(origin.X)) LogOnce(origin.X + mid.X, origin.Z + mid.Z, "trace-" + t.Kind.ToString().ToLowerInvariant());
                    }
                }

                // Flight test 16: every channel of this river (with its deltas, oxbows and traces) into the world river index,
                // which lakes from lakes.json keep their distance from.
                if (!double.IsNaN(origin.X))
                    foreach (var n in rv.Read(nodes))
                        IndexOnce(origin.X + n.Start.X, origin.Z + n.Start.Z, origin.X + n.End.X, origin.Z + n.End.Z, Math.Max(n.StartSize, n.EndSize));
            }
            catch (Exception e) { Fail(e); }
        }

        static bool InsideBy(P2 p, int regionSize, double margin) => p.X > margin && p.Z > margin && p.X < regionSize - margin && p.Z < regionSize - margin;

        static long statDeltaRolled, statDeltaPlaced, statBranchTrimmed, statOxbowsMoved, statOxbowsNoSpot, statDeltaDryChannels, statTraceChannel, statTraceBackwater, statTracePocket;
        static readonly long[] statDeltaLooks = new long[6];

        /// <summary>Whether the chunk pass gives delta banks their character (DeltaSurface).</summary>
        public static bool DeltaSurfaceOn => !disabled && cfg != null && cfg.Enabled && cfg.DeltaSurface && rv != null;

        static volatile bool dryOn;

        /// <summary>Bank factor whose carving floor is DryFloorAboveSea above the sea: (heightBoost - floor) / (map height - sea).</summary>
        static double DryBankCap(object riversConfig)
        {
            int boost = 8;
            try { var f = riversConfig?.GetType().GetField("heightBoost"); if (f != null) boost = Convert.ToInt32(f.GetValue(riversConfig)); } catch (Exception) { }
            int above = Math.Max(1, sapi.WorldManager.MapSizeY - Vintagestory.ServerMods.TerraGenConfig.seaLevel);
            return Math.Max(0, boost - cfg.DryFloorAboveSea) / (double)above;
        }

        /// <summary>Bank factor whose cut ends at sea level (nothing under the sea is dug): heightBoost / (map height - sea).</summary>
        static double MouthBankCap(object riversConfig)
        {
            int boost = 8;
            try { var f = riversConfig?.GetType().GetField("heightBoost"); if (f != null) boost = Convert.ToInt32(f.GetValue(riversConfig)); } catch (Exception) { }
            int above = Math.Max(1, sapi.WorldManager.MapSizeY - Vintagestory.ServerMods.TerraGenConfig.seaLevel);
            return boost / (double)above;
        }

        /// <summary>
        /// Flight test 16b: a shallow channel's own groove, ShallowDeltaGrooveDepth + extra blocks below the delta plain, in
        /// bank-factor units (0 when grooves are off). The plain itself stays at ShallowDeltaDepth (1-block water).
        /// </summary>
        static double GrooveBankFor(int extraDepth)
        {
            if (cfg.ShallowDeltaGrooveDepth <= 0) return 0;
            int above = Math.Max(1, sapi.WorldManager.MapSizeY - Vintagestory.ServerMods.TerraGenConfig.seaLevel);
            return (cfg.ShallowDeltaGrooveDepth + Math.Max(0, extraDepth)) / above;
        }

        /// <summary>Bank factor whose carving floor is ShallowDeltaDepth + extra below the sea: (heightBoost + depth) / (map height - sea).</summary>
        static double ShallowBankCap(object riversConfig, int extraDepth = 0)
        {
            int boost = 8;
            try { var f = riversConfig?.GetType().GetField("heightBoost"); if (f != null) boost = Convert.ToInt32(f.GetValue(riversConfig)); } catch (Exception) { }
            int above = Math.Max(1, sapi.WorldManager.MapSizeY - Vintagestory.ServerMods.TerraGenConfig.seaLevel);
            return (boost + cfg.ShallowDeltaDepth + Math.Max(0, extraDepth)) / (double)above;
        }

        static bool rextActive;
        static double mouthBankMax = double.NaN; // bank factor whose cut ends at sea level: heightBoost / (map height - sea)


        /// <summary>Whether any point of a delta channel has estimated ground more than maxRise above the sea (unknown counts as too high).</summary>
        static bool TooHigh(List<P2> pts, TerrainModel m, P2 origin, double maxRise)
        {
            foreach (var p in pts)
            {
                if (!m.At(origin.X + p.X, origin.Z + p.Z, 12, out _, out double hi)) return true; // the banks, 12 blocks out
                if (hi - m.SeaLevel > maxRise) return true;
            }
            return false;
        }
    }
}
