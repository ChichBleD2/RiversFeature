using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace RiverFeatures
{
    /// <summary>
    /// The worldgen entry point: one chunk-column handler in the TerrainFeatures pass at execute order 0.55, after
    /// structures are placed (so the ruin check is authoritative). Per column: BeginColumn, heights, exclusions,
    /// then every pass plans its changes (lakes, delta banks, swamps), then one Commit writes
    /// them. Any exception is logged once and the column is left untouched (planning never writes).
    /// </summary>
    public class TerrainWatersSystem : ModSystem
    {
        public static ConfigStore Store { get; private set; }

        /// <summary>Everything derived from the config for one world; replaced as a whole (Task 8's reload builds a new one).</summary>
        sealed class WorldState
        {
            public ResolvedConfig Cfg;
            public WorldScale Scale;
            public long Seed;
            public FeatureMask LakeMask, SwampMask, SwampWaterMask;
            public double LakeThreshold, SwampThreshold, SwampWaterThreshold;
            /// <summary>Share of the map inside shaped lakes when every feature is active (DiscCoverage x ShapeAreaFactor): the LakeChance ceiling.</summary>
            public double LakeCoverage;
            public BlockCodes Blocks;
        }

        ICoreServerAPI api;
        IWorldGenBlockAccessor worldgenBa;
        volatile WorldState state;
        readonly GenStats totals = new GenStats();
        /// <summary>Original heights of chunk columns this mod changed (bounded, oldest evicted; mod data is the fallback).</summary>
        readonly OriginalHeights originals = new OriginalHeights();
        /// <summary>A world-creation setting (world config string), null when absent or unreadable.</summary>
        string WorldSetting(string code)
        {
            try { return api.World?.Config?.GetString(code, null); } catch (Exception) { return null; }
        }

        /// <summary>Chunk columns finished this session (seam fitting); after a restart the map chunk's DoneKey mod data says it.</summary>
        readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> done = new System.Collections.Concurrent.ConcurrentDictionary<long, byte>();

        static long ChunkKey(int cx, int cz) => ((long)cx << 32) | (uint)cz;

        /// <summary>Current terrain heights of chunk column (cx, cz) if this mod has finished it, else null.</summary>
        ushort[] DoneNow(IWorldGenBlockAccessor ba, int cx, int cz)
        {
            bool fin = done.ContainsKey(ChunkKey(cx, cz));
            if (!fin)
            {
                try { fin = ba.GetMapChunk(cx, cz)?.GetModdata(ChunkCtx.DoneKey) != null; } catch (Exception) { fin = false; }
            }
            return fin ? MapHeightsOf(ba, cx, cz) : null;
        }

        /// <summary>ChunkCtx.NeighbourNow for one chunk column: the 8 neighbours' current heights, read once each.</summary>
        System.Func<int, int, int> NeighbourNowFn(IWorldGenBlockAccessor ba, int chunkX, int chunkZ)
        {
            var cache = new ushort[9][];
            var asked = new bool[9];
            const int S = ChunkCtx.Size;
            return (lx, lz) =>
            {
                int dx = lx < 0 ? -1 : lx >= S ? 1 : 0, dz = lz < 0 ? -1 : lz >= S ? 1 : 0;
                if ((dx == 0 && dz == 0) || lx < -S || lz < -S || lx >= 2 * S || lz >= 2 * S) return -1;
                int k = (dz + 1) * 3 + (dx + 1);
                if (!asked[k]) { cache[k] = DoneNow(ba, chunkX + dx, chunkZ + dz); asked[k] = true; }
                var a = cache[k];
                return a == null ? -1 : a[(lz - dz * S) * S + (lx - dx * S)];
            };
        }
        /// <summary>ChunkCtx.NeighbourWater: -1 not finished, 0 finished and dry, 1 finished with this mod's water (per local column).</summary>
        System.Func<int, int, int> NeighbourWaterFn(IWorldGenBlockAccessor ba, int chunkX, int chunkZ)
        {
            var fin = new sbyte[9];      // 0 = not asked, 1 = finished, -1 = not finished
            var flags = new bool[9][];
            const int S = ChunkCtx.Size;
            return (lx, lz) =>
            {
                int dx = lx < 0 ? -1 : lx >= S ? 1 : 0, dz = lz < 0 ? -1 : lz >= S ? 1 : 0;
                if ((dx == 0 && dz == 0) || lx < -S || lz < -S || lx >= 2 * S || lz >= 2 * S) return -1;
                int k = (dz + 1) * 3 + (dx + 1);
                if (fin[k] == 0)
                {
                    int cx = chunkX + dx, cz = chunkZ + dz;
                    bool f = done.ContainsKey(ChunkKey(cx, cz));
                    if (!f) { try { f = ba.GetMapChunk(cx, cz)?.GetModdata(ChunkCtx.DoneKey) != null; } catch (Exception) { f = false; } }
                    fin[k] = (sbyte)(f ? 1 : -1);
                    if (f) flags[k] = PlacedOf(ba, cx, cz);
                }
                if (fin[k] < 0) return -1;
                var a = flags[k];
                return a != null && a.Length == S * S && a[(lz - dz * S) * S + (lx - dx * S)] ? 1 : 0;
            };
        }

        /// <summary>Columns of changed chunk columns that hold this mod's water (bounded; mod data is the fallback).</summary>
        readonly PlacedWater placed = new PlacedWater();
        int errorLogged, noAccessorLogged, noNeighboursLogged, summaryLogged, storyWarned;
        /// <summary>Vanilla story structures (placed after this mod, Vegetation pass) and the structure-provider query; null when absent.</summary>
        GenStoryStructures storySys;
        ModSystemWorldGenStructures structureProviders;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override double ExecuteOrder() => 0.55;

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;
            // ConfigStore.Load never throws by contract (it degrades to defaults and logs); the guard here makes sure a broken
            // config, or a logger that rejects a message, can never abort StartServerSide and with it worldgen registration.
            Store = new ConfigStore(api);
            try { Store.Load(); }
            catch (Exception e)
            {
                Store.ResetToDefaults();
                try { api.Logger.Warning("[RiverFeatures] {0}", "config could not be loaded (" + e.Message + "); using defaults for this session."); } catch (Exception) { }
            }
            Store.ApplyLive = ApplyConfigLive;
            try { ConfigCommands.Register(api, Store); ProbeCommand.Register(api); FeatureLog.Register(api); }
            catch (Exception e)
            {
                try { api.Logger.Warning("[RiverFeatures] {0}", "/riverfeatures could not be registered (" + e.Message + "); edit riverfeatures-lakes.json and restart instead."); } catch (Exception) { }
            }
            api.Event.GetWorldgenBlockAccessor(OnWorldGenBlockAccessor);
            api.Event.InitWorldGenerator(InitWorldGen, "standard");
            api.Event.ChunkColumnGeneration(OnColumn, EnumWorldGenPass.TerrainFeatures, "standard");
            api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, LogSummary);
        }

        /// <summary>
        /// Called by /riverfeatures set and reload (command thread) after the stored config changed. Builds a complete new
        /// WorldState (resolved config, masks, recalibrated thresholds, block codes) and swaps the volatile reference, so a chunk
        /// being generated finishes on the old state and the next one uses the new. Returns a note for the player or null.
        /// </summary>
        string ApplyConfigLive()
        {
            var old = state;
            if (old == null)
                return "World generation is not initialised in this session (see the server log), so the new settings are used only after a restart.";
            try
            {
                RebuildWorldState();
                if (state?.Cfg.LakeRiverGap != old.Cfg.LakeRiverGap || state?.Cfg.LakeMinRadius != old.Cfg.LakeMinRadius) lakeFits.Clear();
            }
            catch (Exception e)
            {
                api.Logger.Error("[RiverFeatures] could not apply the new settings ({0}); the previous ones stay active for new chunks.", e.Message);
                return "The new settings could not be applied (" + e.Message + "); the previous ones stay active for new chunks.";
            }
            var cur = state;
            if (cur != null)
            {
                var w = SeamCheck.Warnings(old.Seed, old.Cfg.CellSize, old.Cfg.LakeMaxRadius, old.Cfg.LakeMinRadius,
                    cur.Seed, cur.Cfg.CellSize, cur.Cfg.LakeMaxRadius, cur.Cfg.LakeMinRadius);
                var parts = new System.Collections.Generic.List<string>(w);
                var cap = WaterRules.LakeChanceCeilingNote(cur.Cfg.LakeChance, cur.LakeCoverage);
                if (cap != null) parts.Add(cap);
                if (parts.Count > 0) return string.Join(" ", parts);
            }
            return null;
        }

        void OnWorldGenBlockAccessor(IChunkProviderThread chunkProvider)
        {
            // updateHeightmap = true: UNVERIFIED that this keeps the rain height map in step with our SetBlock calls.
            worldgenBa = chunkProvider.GetBlockAccessor(true);
        }

        void InitWorldGen()
        {
            lakeFits.Clear();
            // Story structures are determined in GenStoryStructures.InitWorldGen (execute order 0.2, before this one).
            try { InitStructureProtection(); }
            catch (Exception e)
            {
                storySys = null; structureProviders = null;
                api.Logger.Warning("[RiverFeatures] {0}", "story structures could not be read (" + e.Message + "); they are not protected this session.");
            }
            try { RebuildWorldState(); }
            catch (Exception e)
            {
                state = null;
                api.Logger.Error("[RiverFeatures] could not initialise ({0}); lakes and swamps are off for this session.", e.Message);
            }
        }

        /// <summary>Separate method so a missing survival mod (type load failure) surfaces inside the caller's try.</summary>
        void InitStructureProtection()
        {
            storySys = api.ModLoader.GetModSystem<GenStoryStructures>();
            structureProviders = api.ModLoader.GetModSystem<ModSystemWorldGenStructures>();
            // Water veto for every structure GenStructures places (ruins, BetterRuins, trader camps incl. post-pass): its public
            // OnPreventSchematicPlaceAt hook, asked from WorldGenStructure.WouldOverlapAt. Attached to THIS world's GenStructures
            // instance (a second world in the same game session has a new one) and detached again in Dispose.
            var gen = api.ModLoader.GetModSystem<GenStructures>();
            if (gen != null && !ReferenceEquals(gen, vetoGen))
            {
                if (vetoGen != null) vetoGen.OnPreventSchematicPlaceAt -= OnPreventSchematic;
                gen.OnPreventSchematicPlaceAt += OnPreventSchematic;
                vetoGen = gen;
                api.Logger.Notification("[RiverFeatures] structure water veto active: surface structures are not placed in or beside water above their floor (trader-shallow exempt).");
            }
            int n = 0;
            if (storySys?.Structures != null) foreach (var _ in storySys.Structures) n++;
            api.Logger.Notification("[RiverFeatures] structure protection: {0} story structure location(s) protected (footprint + RuinMargin, pond keep-out for lakes and swamps); pond keep-out query {1}; lake and cut footprints are registered as generated structures for structures placed later.",
                n, structureProviders != null ? "available" : "NOT available");
        }

        /// <summary>
        /// Resolves the config for this world, builds the noise masks and calibrates their thresholds from LakeChance,
        /// SwampChance and SwampWaterCoverage, and looks up block codes. Public so Task 8's reload can call it; new
        /// values apply to chunks generated afterwards.
        /// </summary>
        public void RebuildWorldState()
        {
            var scale = WorldScale.From(api.World, api.WorldManager);
            // World-creation settings (worldconfig.json: twLakeChance, twSwampChance) override the mod
            // config for this world; "config" (the default) keeps the mod config's value.
            var baseCfg = (Store?.Current ?? new TerrainWatersConfig()).WithWorldSettings(code => WorldSetting(code), msg => api.Logger.Notification("[RiverFeatures] {0}", msg));
            var cfg = baseCfg.Resolve(scale);
            // IWorldManagerAPI.Seed (int) in 1.22.7; SeedWorldgen exists only from 1.23 on (checked against the vsapi history).
            long seed = (long)api.WorldManager.Seed + cfg.SeedOffset;
            int swampCell = cfg.CellSize;
            // Flight test 6: more lakes. With every feature active the 3R grid covers about 8% of the map; a LakeChance above
            // that now packs the features closer (down to 2.25R, centres still R from every cell edge) instead of capping.
            foreach (double k in new[] { 2.75, 2.5, 2.25 })
            {
                double cov = WaterRules.DiscCoverage(cfg.CellSize, cfg.LakeMinRadius, cfg.LakeMaxRadius)
                    * WaterRules.ShapeAreaFactor(seed, cfg.CellSize, cfg.SeaLevel, cfg.MaxRiseAboveSea, cfg.LakeMinRadius, cfg.LakeMaxRadius);
                if (!(cfg.LakeChance > cov)) break;
                cfg = cfg.WithCellSize((int)Math.Round(k * cfg.LakeMaxRadius));
            }

            // NormalizedSimplexNoise returns about 0..1 rather than FeatureMask's nominal -1..1; the thresholds are
            // quantiles measured on the noise itself (ThresholdFor), so only the ordering matters.
            // Lake mask: sampled at feature centres (one decision per lake), wavelength about two cells.
            var lakeNoise = NormalizedSimplexNoise.FromDefaultOctaves(2, 1.0, 0.5, seed + 7001);
            var lakeMask = new FeatureMask(lakeNoise.Noise, 1.0 / Math.Max(16, 2 * cfg.CellSize));
            // Swamp mask: per column, patches about one cell across.
            var swampNoise = NormalizedSimplexNoise.FromDefaultOctaves(3, 1.0, 0.5, seed + 7002);
            var swampMask = new FeatureMask(swampNoise.Noise, 1.0 / Math.Max(16, swampCell));
            // Swamp water pockets: small clumps a few blocks across.
            var pocketNoise = NormalizedSimplexNoise.FromDefaultOctaves(2, 1.0, 0.5, seed + 7003);
            var pocketMask = new FeatureMask(pocketNoise.Noise, 1.0 / 6.0);

            // LakeChance is the share of the MAP inside active lakes (spec), not the share of features: shaped lakes cover only
            // LakeCoverage = DiscCoverage x ShapeAreaFactor of the map when every feature is active, so activate
            // min(1, LakeChance / LakeCoverage) of the features. LakeChance above the coverage activates every feature.
            double coverage = WaterRules.DiscCoverage(cfg.CellSize, cfg.LakeMinRadius, cfg.LakeMaxRadius)
                * WaterRules.ShapeAreaFactor(seed, cfg.CellSize, cfg.SeaLevel, cfg.MaxRiseAboveSea, cfg.LakeMinRadius, cfg.LakeMaxRadius);
            double lakeShare = cfg.LakeChance <= 0 || !(coverage > 0) ? 0.0 : Math.Min(1.0, cfg.LakeChance / coverage);

            var st = new WorldState
            {
                Cfg = cfg,
                Scale = scale,
                Seed = seed,
                LakeMask = lakeMask,
                SwampMask = swampMask,
                SwampWaterMask = pocketMask,
                LakeThreshold = lakeMask.ThresholdFor(lakeShare),
                SwampThreshold = swampMask.ThresholdFor(cfg.SwampChance),
                SwampWaterThreshold = pocketMask.ThresholdFor(cfg.SwampWaterCoverage),
                LakeCoverage = coverage,
                Blocks = BlockCodes.Resolve(api, cfg),
            };
            state = st;
            // Numbers are formatted here with the invariant culture (dot decimals) so the log reads the same on every locale
            // and matches the README checklist; the logger only receives finished strings as {n} arguments.
            if (cfg.PlainLakes)
            {
                api.Logger.Notification(
                    "[RiverFeatures] worldgen ready: height {0}, sea {1}, lake radius {2}..{3} (cell {4}), lake chance {5} (map share; ceiling {9}, {10} of lake features active), swamp chance {6}, swamp surface '{7}', {8} swamp plant(s).",
                    cfg.WorldHeight, cfg.SeaLevel, cfg.LakeMinRadius, cfg.LakeMaxRadius, cfg.CellSize, ConfigEdit.Format(cfg.LakeChance), ConfigEdit.Format(cfg.SwampChance),
                    st.Blocks.SwampSurfaceCode ?? "none", st.Blocks.PlantIds.Length,
                    Inv3(coverage), Inv3(lakeShare));
                string ceilingNote = WaterRules.LakeChanceCeilingNote(cfg.LakeChance, coverage);
                if (ceilingNote != null) api.Logger.Warning("[RiverFeatures] {0}", ceilingNote);
            }
            else
                api.Logger.Notification(
                    "[RiverFeatures] worldgen ready: height {0}, sea {1}, plain lakes deferred (PlainLakes false in riverfeatures-lakes.json; source lakes are unaffected), swamp chance {2}, swamp surface '{3}', {4} swamp plant(s).",
                    cfg.WorldHeight, cfg.SeaLevel, ConfigEdit.Format(cfg.SwampChance), st.Blocks.SwampSurfaceCode ?? "none", st.Blocks.PlantIds.Length);
        }

        /// <summary>A number with at most 3 decimals and a dot, whatever the server locale (log text).</summary>
        static string Inv3(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        void OnColumn(IChunkColumnGenerateRequest req)
        {
            var st = state;
            if (st == null || !st.Cfg.Enabled) return;
            var ba = worldgenBa;
            if (ba == null)
            {
                if (Interlocked.Exchange(ref noAccessorLogged, 1) == 0)
                    api.Logger.Warning("[RiverFeatures] no worldgen block accessor was handed to the mod; lakes and swamps are skipped.");
                return;
            }
            try
            {
                ba.BeginColumn();
                var ctx = new ChunkCtx
                {
                    Req = req,
                    Ba = ba,
                    Api = api,
                    Cfg = st.Cfg,
                    Scale = st.Scale,
                    Seed = st.Seed,
                    LakeMask = st.LakeMask,
                    SwampMask = st.SwampMask,
                    SwampWaterMask = st.SwampWaterMask,
                    LakeThreshold = st.LakeThreshold,
                    SwampThreshold = st.SwampThreshold,
                    SwampWaterThreshold = st.SwampWaterThreshold,
                    Blocks = st.Blocks,
                    BaseX = req.ChunkX * ChunkCtx.Size,
                    BaseZ = req.ChunkZ * ChunkCtx.Size,
                };
                ctx.Originals = originals;
                ctx.PlacedStore = placed;
                ctx.NeighbourNow = NeighbourNowFn(ba, req.ChunkX, req.ChunkZ);
                ctx.NeighbourWater = NeighbourWaterFn(ba, req.ChunkX, req.ChunkZ);
                ctx.LakeViewOf = (cx, cz) =>
                {
                    try { return ba.GetMapChunk(cx, cz)?.GetModdata(ChunkCtx.LakeViewKey); } catch (Exception) { return null; }
                };
                // Neighbour heights, first valid wins (HeightGrid class doc): ORIGINAL heights of neighbours this mod changed
                // (cache, then their mod data), else a copy of the neighbour's map-chunk terrain height map, else the request's
                // NeighbourTerrainHeight, else unknown. The map copy is never put into the originals store.
                ctx.Heights = HeightGrid.From(req, (cx, cz) => OriginalOf(ba, cx, cz), (cx, cz) => MapHeightsOf(ba, cx, cz));
                if (ctx.Heights.Raw(0, 0) == -1) return; // own height map missing: nothing can be judged
                WarnIfNoNeighbours(ctx.Heights);
                ctx.Excl = ColumnExclusions.Build(api, ba, req, st.Cfg, st.Cfg.SeaLevel, ctx.Heights, (cx, cz) => PlacedOf(ba, cx, cz),
                    StoryRectsNear(ctx.BaseX, ctx.BaseZ, st.Cfg), PondBlockedFn());

                var cfg = st.Cfg;
                // Features whose disc may reach: the swamp lock window (swamp fade, plus 1) widened by the lake keep-out
                // (LakeKeepOut), the lake basin window (BasinSpan + 1 beyond the decided columns, which reach ShoreRadius beyond
                // the chunk for the shore ramp).
                int fmc = SwampPass.FadeOf(cfg);
                int reach = Math.Max(fmc + 1 + SwampPass.SmoothRadius + WaterRules.LakeKeepOut,
                    WaterRules.BasinSpan(cfg.OceanBuffer) + 2 + WaterRules.ShoreRadius(cfg.MaxCut) + WaterRules.BedSmoothRadius);
                reach += 1;
                // 1.0.0: plain lakes are deferred (PlainLakes false): no lake features, so LakePass plans nothing and swamps
                // have no lake keep-out. Source lakes are Rivers nodes (river network) and are not affected.
                ctx.LakeFeatures = !cfg.PlainLakes ? new List<FeatureCell>() : WaterRules.FeaturesNear(st.Seed, ctx.BaseX - reach, ctx.BaseZ - reach,
                    ctx.BaseX + ChunkCtx.Size - 1 + reach, ctx.BaseZ + ChunkCtx.Size - 1 + reach,
                    cfg.CellSize, cfg.SeaLevel, cfg.MaxRiseAboveSea, cfg.LakeMinRadius, cfg.LakeMaxRadius,
                    (x, z) => st.LakeMask.Covers(x, z, st.LakeThreshold));
                // Flight test 16: lakes keep LakeRiverGap blocks of land from every Rivers channel (world-level decision per
                // lake from the river index, so every chunk that sees the lake agrees): shrink, or drop when too small.
                if (cfg.PlainLakes && cfg.LakeRiverGap > 0 && !Rivers.RiverIndex.Empty)
                {
                    var kept = new List<FeatureCell>(ctx.LakeFeatures.Count);
                    foreach (var f in ctx.LakeFeatures)
                    {
                        // Audit: the index only holds rivers of regions built so far, so two chunks of one lake could see
                        // different rivers and cut a seam. The first decision for a lake is kept for the rest of the session.
                        var key = ((long)Math.Round(f.CenterX), (long)Math.Round(f.CenterZ));
                        double r = lakeFits.GetOrAdd(key, _ =>
                        {
                            double d = Rivers.RiverIndex.EdgeDistance(f.CenterX, f.CenterZ, f.Radius + cfg.LakeRiverGap + 8);
                            return Rivers.RiverIndex.FitRadius(f.Radius, d, cfg.LakeRiverGap, cfg.LakeMinRadius);
                        });
                        if (!double.IsNaN(r)) kept.Add(r == f.Radius ? f : f with { Radius = r });
                    }
                    ctx.LakeFeatures = kept;
                }

                int lakeCols = LakePass.Apply(ctx);
                if (lakeCols >= 48)
                {
                    // /rffind lake: the middle of this chunk column's flooded columns, once per 64-block cell.
                    double sx = 0, sz = 0; int n = 0;
                    for (int i = 0; i < ChunkCtx.Size * ChunkCtx.Size; i++)
                        if (ctx.LakeEdit[i] && ctx.WaterPlaced[i]) { sx += i % ChunkCtx.Size; sz += i / ChunkCtx.Size; n++; }
                    if (n > 0) FeatureLog.AddOncePerCell(ctx.BaseX + sx / n, ctx.BaseZ + sz / n, "lake", 64);
                }
                // Stage 2: delta bank character (material, plants) before swamps, which then skip those columns.
                if (Rivers.RiverHierarchySystem.DeltaSurfaceOn) DeltaSurface.Apply(ctx);
                int swampBefore = (int)ctx.Stats.SwampColumns;
                SwampPass.Apply(ctx);
                if (ctx.Stats.SwampColumns - swampBefore >= 64) FeatureLog.Add(ctx.BaseX + 16, ctx.BaseZ + 16, "swamp");

                ctx.Commit();
                done[ChunkKey(req.ChunkX, req.ChunkZ)] = 1;
                WaterSeal.Apply(ctx);
                StillFlow(ctx);
                ctx.Stats.Chunks = 1;
                totals.Add(ctx.Stats);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref totals.Errors);
                if (Interlocked.Exchange(ref errorLogged, 1) == 0)
                    api.Logger.Error("[RiverFeatures] error in chunk column {0},{1}; that column is left as generated (further errors are not logged): {2}",
                        req.ChunkX, req.ChunkZ, e);
            }
        }

        /// <summary>
        /// Original heights of chunk column (cx, cz) if this mod changed it: the session cache, else the map chunk's mod
        /// data (written at Commit, survives restarts), else null (unchanged: the game's neighbour heights are original).
        /// UNVERIFIED: that IBlockAccessor.GetMapChunk on the worldgen accessor is safe from the worldgen thread and returns
        /// a neighbour's map chunk during TerrainFeatures; any exception counts as a miss.
        /// </summary>
        ushort[] OriginalOf(IWorldGenBlockAccessor ba, int cx, int cz)
        {
            var h = originals.Get(cx, cz);
            if (h != null) return h;
            try
            {
                h = OriginalHeights.Decode(ba.GetMapChunk(cx, cz)?.GetModdata(ChunkCtx.OriginalHeightsKey));
                if (h != null) originals.Remember(cx, cz, h);
                return h;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Footprints (X/Z of StoryStructureLocation.Location, fixed at world start) of story structures whose rectangle plus
        /// RuinMargin can reach this chunk column's widest window (64 blocks around it). Read live each chunk: /wgen story commands
        /// can move them. Null when there are none or the system is absent.
        /// </summary>
        List<ColumnExclusions.StructureRect> StoryRectsNear(int baseX, int baseZ, ResolvedConfig cfg)
        {
            var sys = storySys;
            if (sys?.Structures == null) return null;
            List<ColumnExclusions.StructureRect> list = null;
            try
            {
                int reach = 64 + cfg.RuinMargin;
                foreach (var kv in sys.Structures)
                {
                    var loc = kv.Value?.Location;
                    if (loc == null) continue;
                    if (loc.MaxX + reach < baseX || loc.MinX - reach > baseX + ChunkCtx.Size || loc.MaxZ + reach < baseZ || loc.MinZ - reach > baseZ + ChunkCtx.Size) continue;
                    (list ??= new List<ColumnExclusions.StructureRect>()).Add(new ColumnExclusions.StructureRect(loc.MinX, loc.MinZ, loc.MaxX, loc.MaxZ));
                }
            }
            catch (Exception e) { WarnStory(e); }
            return list;
        }

        GenStructures vetoGen;
        long vetoCount;
        /// <summary>Lake radius after the LakeRiverGap check (NaN = dropped), per lake centre, first decision this session.</summary>
        readonly System.Collections.Concurrent.ConcurrentDictionary<(long, long), double> lakeFits = new System.Collections.Concurrent.ConcurrentDictionary<(long, long), double>();

        static bool IsLava(Block b) =>
            string.Equals(b.LiquidCode, "lava", StringComparison.OrdinalIgnoreCase)
            || (b.Code?.Path?.StartsWith("lava", StringComparison.OrdinalIgnoreCase) ?? false);

        public override void Dispose()
        {
            try { if (vetoGen != null) vetoGen.OnPreventSchematicPlaceAt -= OnPreventSchematic; } catch (Exception) { }
            vetoGen = null;
            base.Dispose();
        }

        /// <summary>GenStructures placement hook: true = do not place (StructureGuard.WaterVeto). Never throws.</summary>
        bool OnPreventSchematic(IBlockAccessor ba, BlockPos pos, Cuboidi loc, string code)
        {
            if (ba == null || loc == null) return false;
            try
            {
                var st = state;
                if (st != null && !st.Cfg.Enabled) return false;
                var p = new BlockPos(loc.CenterX, 0, loc.CenterZ);
                int terrain = ba.GetTerrainMapheightAt(p);
                var q = new BlockPos(0, 0, 0);
                bool veto = StructureGuard.WaterVeto(loc.X1, loc.Y1, loc.Z1, loc.X2, loc.Y2, loc.Z2, terrain, code, (x, y, z) =>
                {
                    q.Set(x, y, z);
                    var b = ba.GetBlock(q, BlockLayersAccess.Fluid);
                    // Water only: lava (a volcanic or cave lava pool) is not a reason to drop a structure here.
                    return b != null && b.BlockId != 0 && b.IsLiquid() && !IsLava(b);
                });
                if (veto) Interlocked.Increment(ref vetoCount);
                return veto;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The story-structure pond keep-out (the query vanilla GenPonds makes), or null when unavailable.</summary>
        System.Func<int, int, bool> PondBlockedFn()
        {
            var prov = structureProviders;
            if (prov == null) return null;
            int pond = ModStdWorldGen.PondsHashCode;
            return (wx, wz) => prov.GetIntersectingStructure(wx, wz, pond) != null;
        }

        void WarnStory(Exception e)
        {
            if (Interlocked.Exchange(ref storyWarned, 1) == 0)
                api.Logger.Warning("[RiverFeatures] {0}", "reading story structure locations failed (" + e.Message + "); logged once.");
        }

        static int flowWarned;

        /// <summary>
        /// Rivers' flow map ("flowVectors" in the bottom chunk's mod data, FlowFix): clears the flow on columns where this mod
        /// placed water (lakes and swamp pockets are still water) and on small enclosed pools of existing water (stretches of a
        /// Rivers channel that ended up as separate pools), so they no longer look and act like rapids. No map (no Rivers flow
        /// here): nothing to do. Failures are logged once; the blocks are already written.
        /// </summary>
        void StillFlow(ChunkCtx ctx)
        {
            try
            {
                var chunk = ctx.Req?.Chunks?[0];
                if (chunk == null) return;
                var flow = chunk.GetModdata<float[]>(FlowFix.ModDataKey, null);
                if (flow == null || flow.Length != 2 * FlowFix.Cells) return;
                var mark = (bool[])ctx.WaterPlaced.Clone();
                var iso = FlowFix.IsolatedWater((x, z) => ctx.Excl.WaterTopAt(x, z) >= 0, ColumnExclusions.WideRadius);
                for (int i = 0; i < FlowFix.Cells; i++) if (iso[i]) { mark[i] = true; }
                int changed = FlowFix.Clear(flow, mark);
                if (changed == 0) return;
                chunk.SetModdata(FlowFix.ModDataKey, flow);
                chunk.MarkModified();
                Interlocked.Add(ref totals.FlowCleared, changed);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref flowWarned, 1) == 0)
                    api.Logger.Warning("[RiverFeatures] {0}", "could not update Rivers' flow map (" + e.Message + "); still water in Rivers' flow zone may look like flowing water (logged once).");
            }
        }

        /// <summary>Placed-water flags of chunk column (cx, cz): the session cache, else the map chunk's mod data, else null.</summary>
        bool[] PlacedOf(IWorldGenBlockAccessor ba, int cx, int cz)
        {
            var f = placed.Get(cx, cz);
            if (f != null) return f;
            try
            {
                f = PlacedWater.Decode(ba.GetMapChunk(cx, cz)?.GetModdata(ChunkCtx.PlacedWaterKey));
                if (f != null) placed.Remember(cx, cz, f);
                return f;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// A COPY of chunk column (cx, cz)'s WorldGenTerrainHeightMap through the worldgen accessor, or null. Used only as a
        /// fallback after the originals store (HeightGrid), never remembered as original: a neighbour this mod changed has a
        /// lowered map. UNVERIFIED: that GetMapChunk on the worldgen accessor returns a neighbour's map chunk during
        /// TerrainFeatures (vsapi EnumWorldGenPass: neighbours are at the same or a higher pass, so their terrain map exists);
        /// any exception counts as a miss.
        /// </summary>
        static ushort[] MapHeightsOf(IWorldGenBlockAccessor ba, int cx, int cz)
        {
            try
            {
                var h = ba.GetMapChunk(cx, cz)?.WorldGenTerrainHeightMap;
                return h == null || h.Length != HeightGrid.Size * HeightGrid.Size ? null : (ushort[])h.Clone();
            }
            catch (Exception) { return null; }
        }

        void WarnIfNoNeighbours(HeightGrid g)
        {
            if (g.Raw(-1, 0) != -1 || g.Raw(32, 0) != -1 || g.Raw(0, -1) != -1 || g.Raw(0, 32) != -1) return;
            if (Interlocked.Exchange(ref noNeighboursLogged, 1) != 0) return;
            api.Logger.Warning("[RiverFeatures] a chunk column had no neighbour terrain heights (no stored originals, no neighbour map chunk, no NeighbourTerrainHeight); lakes stay dry along such borders. If this repeats everywhere, GetMapChunk on the worldgen accessor does not reach neighbours (logged once).");
        }

        void LogSummary()
        {
            if (Interlocked.Exchange(ref summaryLogged, 1) != 0) return;
            api?.Logger.Notification("[RiverFeatures] generated this session: {0}; structure placements vetoed for water {1}", totals, Interlocked.Read(ref vetoCount));
        }
    }
}
