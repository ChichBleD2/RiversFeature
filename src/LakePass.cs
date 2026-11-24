using System;
using Vintagestory.API.Common;

namespace RiverFeatures
{
    /// <summary>
    /// Lakes. Each lake is a feature (FeatureCells: fixed centre, radius and water level per world cell) that is
    /// active when the lake mask covers its centre, so a lake is either wholly on or wholly off and its water level
    /// is the same constant in every chunk it touches. The decisions are pure and live in <see cref="LakePlanner"/>
    /// (basins below the level flood as a whole region; bank columns at or above it flood under the neighbour and
    /// bank rules; every column needs LakeMinDepth of water). This pass only builds the planner's world view from
    /// ChunkCtx, adds a block-level cave check for bank columns, and plans the block edits.
    /// Flooded column: a loose plant on the old surface is cleared (ChunkCtx.ClearPlantAt), ground above the level becomes
    /// air (bank columns only, never more than one block above the neighbours), Bed+1..Level water, the bed block sand or gravel of the column's rock (code lookup with fallback;
    /// kept as is when none exists or when the block under the bed is air).
    ///
    /// Area and ocean limits are met by construction instead of by aborting a feature per chunk (a per-chunk abort
    /// would cut a lake along a straight chunk border): flooded columns lie inside the disc, so a lake never exceeds
    /// pi x LakeMaxRadius^2; no column within OceanBuffer is flooded; and the neighbour rules wall every lake with
    /// ground at least its level (> sea level), so a lake can never reach or merge with the sea.
    /// </summary>
    public static class LakePass
    {
        const int N = ChunkCtx.Size;

        /// <summary>The planner's world view of this chunk column (local coordinates; pure world data only).</summary>
        public static LakeWorld WorldOf(ChunkCtx c)
        {
            var cfg = c.Cfg;
            var hg = c.Heights;
            int bx = c.BaseX, bz = c.BaseZ;
            var ocean = OceanMap.From(hg.Raw, cfg.SeaLevel);
            var feats = c.LakeFeatures;
            var ex = c.Excl;
            var rmask = c.RiverLakeMask;
            double rthr = c.RiverLakeThreshold;
            int reach = ColumnExclusions.RiverLakeReach;
            // Flight test 11 (square lake pits; "lake seam mismatch" log): two order dependences made neighbouring chunk
            // columns decide border columns differently, so lakes stopped at chunk lines.
            // 1. Vanilla ponds (GenPonds, TerrainFeatures pass, order 0.4, before this mod's 0.55) exist in a neighbour only
            //    once that neighbour has reached this pass, so a chunk generated later saw a pond its finished neighbour never
            //    saw and wrapped a "river-side" lake around it at the pond's level. Rivers' water lies at sea level, so water
            //    more than one block above the sea is a pond: never a lake level (it still counts as existing water).
            // 2. Ruins and other generated structures appear in the region lists over time. Lakes keep RuinMargin only from
            //    story structures (planned in advance, PondBlockedAt); other structures only block their own footprint, and
            //    structures placed later are vetoed from lakes anyway (StructureGuard water veto).
            int riverMax = cfg.SeaLevel + 1;
            return new LakeWorld
            {
                Height = hg.Raw,
                WaterTop = ex.WaterTopAt,
                // Nearest river surface (not the highest within RiverRadius): on a stepped river the lake now matches the
                // river column it actually touches, so lakes can join rivers (flow-through) instead of always leaking.
                RiverLevel = (lx, lz) => ex.NearestRiverLevelAt(lx, lz, reach, riverMax),
                Feature = (lx, lz) => WaterRules.BestFeature(feats, bx + lx, bz + lz),
                NearStructure = (lx, lz) => ex.NearStructureAt(bx + lx, bz + lz, 0) || ex.NearStoryAt(bx + lx, bz + lz, cfg.RuinMargin) || ex.PondBlockedAt(bx + lx, bz + lz),
                NearOcean = (lx, lz) => ocean.Near(lx, lz, cfg.OceanBuffer),
                RiverLake = rmask == null || double.IsPositiveInfinity(rthr) ? null
                    : (lx, lz) => rmask.Covers(bx + lx, bz + lz, rthr) ? ex.NearestRiverLevelAt(lx, lz, reach, riverMax) : -1,
                DepthNoise = (lx, lz) => DepthNoiseAt(c.Seed, bx + lx, bz + lz),
                ShaveNoise = (lx, lz) => ShaveNoiseAt(c.Seed, bx + lx, bz + lz),
                Finished = c.NeighbourWater,
                WaterShare = ex.WaterShareAt,
                ShaveAllowed = (lx, lz) =>
                    WaterRules.NearAnyFeature(feats, bx + lx, bz + lz, WaterRules.LakeKeepOut - WaterRules.ShaveRadius - 1)
                    || ex.NearestRiverLevelAt(lx, lz, ColumnExclusions.RiverRadius - WaterRules.ShaveRadius - 1) >= 0,
            };
        }

        /// <summary>
        /// Lake depth noise in [0, 1] at a WORLD column: two octaves of value noise (about 28 and 14 blocks). The 5-block
        /// octave was removed in flight test 3: it made single columns stand up from the bed.
        /// </summary>
        public static double DepthNoiseAt(long seed, int wx, int wz) =>
            0.6 * WaterRules.ValueNoise(seed ^ 0x1A4E5F11L, wx / 28.0, wz / 28.0, 3) + 0.4 * WaterRules.ValueNoise(seed ^ 0x1A4E5F11L, wx / 14.0, wz / 14.0, 4);

        /// <summary>Fine noise (about 5 blocks) for shore shaving, by world position so every chunk agrees.</summary>
        public static double ShaveNoiseAt(long seed, int wx, int wz) =>
            WaterRules.ValueNoise(seed ^ 0x5A7E0C3DL, wx / 5.0, wz / 5.0, 5);

        public static int Apply(ChunkCtx c)
        {
            var cfg = c.Cfg;
            var blocks = c.Blocks;
            int bx = c.BaseX, bz = c.BaseZ;

            // Own columns within disc + LakeKeepOut (swamps keep out; finding 5b).
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                    c.NearLake[z * N + x] = WaterRules.NearAnyFeature(c.LakeFeatures, bx + x, bz + z, WaterRules.LakeKeepOut);

            if (blocks == null || blocks.WaterId == 0) return 0;
            if (c.LakeFeatures.Count == 0 && c.RiverLakeMask == null) return 0;
            var planner = new LakePlanner(WorldOf(c), LakeParams.From(cfg), WaterRules.BasinSpan(cfg.OceanBuffer)) { MinArea = cfg.LakeMinArea };
            var hg = c.Heights;
            var plans = planner.PlanWithShore(WaterRules.ShoreRadius(cfg.MaxCut));
            var finalWhy = new LakeSkip[N * N];
            for (int k = 0; k < N * N; k++) finalWhy[k] = plans[k].Reason;

            int changed = 0;
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    int i = z * N + x;
                    var plan = plans[i];
                    if (plan.ShaveTo >= 0 && !plan.Flood)
                    {
                        int s0 = hg.Raw(x, z);
                        if (Shave(c, bx + x, bz + z, s0, plan.ShaveTo))
                        {
                            c.SetTop(x, z, s0, plan.ShaveTo, plan.ShaveTo);
                            c.Touched[i] = true;
                            c.LakeEdit[i] = true;
                            c.Stats.ShoreShaved++;
                        }
                        continue;
                    }
                    if (plan.Reason == LakeSkip.Outside) continue;
                    if (plan.RiverLake && !plan.Flood) continue; // river-side zone columns that stay dry are not "skipped lake columns"
                    var col = plan.Column;
                    var why = plan.Reason;
                    bool bedOk = true;
                    if (why == LakeSkip.None)
                    {
                        if (plan.Basin)
                        {
                            // A basin floods as a whole in every chunk; leaving one column dry for a block-level reason
                            // would leave a water wall, so basins get no cave check (UNVERIFIED how worldgen water next
                            // to cave air behaves). Only the bed swap is skipped over air (sand would fall).
                            bedOk = UnderBedSolid(c, bx + x, bz + z, col.Bed);
                        }
                        else
                        {
                            bool skipE = planner.BasinFloods(x + 1, z), skipW = planner.BasinFloods(x - 1, z);
                            bool skipN = planner.BasinFloods(x, z - 1), skipS = planner.BasinFloods(x, z + 1);
                            if (!Sealed(c, bx + x, bz + z, col, skipE, skipW, skipN, skipS)) why = LakeSkip.Cave;
                        }
                    }

                    finalWhy[i] = why;
                    if (why != LakeSkip.None)
                    {
                        Count(c, i, why);
                        continue;
                    }
                    Plan(c, bx + x, bz + z, hg.Raw(x, z), col, bedOk);
                    c.SetTop(x, z, hg.Raw(x, z), col.Bed, col.Level); // terrain map = lake bed, rain map = water surface
                    c.Touched[i] = true;
                    c.WaterPlaced[i] = true;
                    c.LakeEdit[i] = true;
                    if (plan.RiverLake) c.Stats.RiverLakeColumns++;
                    changed++;
                }
            c.Stats.LakeColumns += changed;

            // Seam diagnostics (LakeSeamDiag): this chunk's view (own columns with their FINAL reason, the ring as planned),
            // compared with the views of finished neighbours, then stored for neighbours generated later.
            try
            {
                c.LakeView = LakeSeamDiag.Encode(
                    (lx, lz) => lx >= 0 && lx < N && lz >= 0 && lz < N ? plans[lz * N + lx] with { Reason = finalWhy[lz * N + lx] } : planner.Decide(lx, lz),
                    hg.Raw);
                if (c.Req != null)
                    c.Stats.SeamMismatch += LakeSeamDiag.Compare(c.LakeView, c.Req.ChunkX, c.Req.ChunkZ, c.LakeViewOf,
                        s => c.Api?.Logger.Notification("[RiverFeatures] {0}", s));
            }
            catch (Exception) { c.LakeView = null; }
            return changed;
        }

        /// <summary>Counts a feature column that stayed dry by its reason (shore and too-shallow columns are not counted: they are the lake's natural edge).</summary>
        static void Count(ChunkCtx c, int i, LakeSkip why)
        {
            switch (why)
            {
                case LakeSkip.Structure: c.Stats.SkipStructure++; break;
                case LakeSkip.Ocean: c.Stats.SkipOcean++; break;
                case LakeSkip.RiverWater: c.Stats.SkipRiver++; break;
                case LakeSkip.LevelOutOfRange:
                    if (c.Excl.NearRiver(i)) c.Stats.SkipRiver++; else c.Stats.SkipProtected++;
                    break;
                case LakeSkip.Protected: c.Stats.SkipProtected++; break;
                case LakeSkip.Leak:
                case LakeSkip.Bank:
                case LakeSkip.Cave: c.Stats.SkipLeak++; break;
                case LakeSkip.Small: c.Stats.SkipSmall++; break;
            }
        }

        /// <summary>
        /// Block-level check (bank columns) that the new water is enclosed: at every Y from Bed+1 to Level each orthogonal
        /// neighbour holds a solid or liquid block (no cave mouth, no unloaded chunk), and the block under the bed is not
        /// air (sand and gravel would fall into a cave). Sides whose neighbour is an accepted basin column are skipped:
        /// that neighbour is still air above its ground now but will be water at the same level. Reads only; any failure
        /// leaves the column dry, which never leaves a wall (its ground is at or above the level).
        /// UNVERIFIED: that a GetBlock read outside this chunk column returns air (id 0) rather than throwing when the
        /// neighbour chunk is missing; a throw is caught and treated as "not sealed".
        /// </summary>
        static bool Sealed(ChunkCtx c, int wx, int wz, in LakeColumn col, bool skipE, bool skipW, bool skipN, bool skipS)
        {
            try
            {
                var ba = c.Ba;
                if (ba.GetBlock(wx, col.Bed - 1, wz, BlockLayersAccess.Default).BlockId == 0) return false;
                for (int k = 0; k < 4; k++)
                {
                    if ((k == 0 && skipE) || (k == 1 && skipW) || (k == 2 && skipN) || (k == 3 && skipS)) continue;
                    int nx = wx + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = wz + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    if (ba.GetChunkAtBlockPos(nx, col.Level, nz) == null) return false;
                    for (int y = col.Bed + 1; y <= col.Level; y++)
                        if (ba.GetBlock(nx, y, nz, BlockLayersAccess.Default).BlockId == 0) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Lowers a dry shore column from s to top: air above, the old top block moved down when it is soil (the grassy bank
        /// stays green), the loose plant above the old surface cleared. Left alone when the old top is air or liquid.
        /// </summary>
        static bool Shave(ChunkCtx c, int wx, int wz, int s, int top)
        {
            if (top >= s || top < 1) return false;
            int oldTop;
            try
            {
                var b = c.Ba.GetBlock(wx, s, wz, BlockLayersAccess.Solid);
                if (b == null || b.BlockId == 0) return false;
                var fl = c.Ba.GetBlock(wx, s, wz, BlockLayersAccess.Fluid);
                if (fl != null && fl.BlockId != 0) return false;
                oldTop = WaterRules.IsSoil(b.Code?.Path) ? b.BlockId : 0;
            }
            catch (Exception) { return false; }
            c.ClearPlantAt(wx, s + 1, wz);
            for (int y = s; y > top; y--) c.Set(wx, y, wz, 0);
            if (oldTop > 0) c.Set(wx, top, wz, oldTop);
            return true;
        }

        static bool UnderBedSolid(ChunkCtx c, int wx, int wz, int bed)
        {
            try { return c.Ba.GetBlock(wx, bed - 1, wz, BlockLayersAccess.Default).BlockId != 0; }
            catch (Exception) { return false; }
        }

        static void Plan(ChunkCtx c, int wx, int wz, int surfaceY, in LakeColumn col, bool bedOk)
        {
            // Tall grass on the old surface (block layers put it at surfaceY + 1) would float over a lowered bank or sit in
            // the air above the water: clear it first. Where the water reaches surfaceY + 1, the water edit below replaces it.
            c.ClearPlantAt(wx, surfaceY + 1, wz);
            for (int y = surfaceY; y > col.Level; y--) c.Set(wx, y, wz, 0);
            for (int y = col.Bed + 1; y <= col.Level; y++) c.Set(wx, y, wz, c.Blocks.WaterId);
            if (!bedOk) return;
            int bed = c.Blocks.BedId(RockBelow(c, wx, col.Bed, wz), col.Level - col.Bed);
            if (bed > 0) c.Set(wx, col.Bed, wz, bed);
        }

        /// <summary>The rock name of the first "rock-*" block at or below y (16 blocks deep), or null.</summary>
        internal static string RockBelow(ChunkCtx c, int wx, int y, int wz)
        {
            try
            {
                for (int yy = y; yy >= Math.Max(1, y - 16); yy--)
                {
                    string r = WaterRules.RockOf(c.Ba.GetBlock(wx, yy, wz, BlockLayersAccess.Solid)?.Code?.Path);
                    if (r != null) return r;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
