using System;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace RiverFeatures
{
    /// <summary>
    /// Swamps: wet, flat, boggy low ground. A column is swamp where the swamp mask covers it, rainfall is at least
    /// SwampMinRain, it is not excluded (structure margin, river, ocean buffer), not within LakeKeepOut (6) of an active
    /// lake disc, and not locked by the mountain rules (SwampColumnAllowed: MaxRiseAboveSea and MaxSlope; MaxRimVariation).
    /// Its surface is flattened toward sea level + 1 by at most MaxCut (LimitCut) / MaxFill (LimitFill), scaled by a
    /// weight that fades to 0 smoothly at every edge so no wall is ever formed:
    ///   locked or excluded ground, and every column outside the swamp mask (fix round 1, finding 6: exact distance,
    ///   no noise-gradient estimate): Fade over FadeMargin of the distance to the nearest locked column, judged on the
    ///   same world data by every chunk that sees it;
    ///   rain edge (WaterRules.RainWeight ramp above SwampMinRain).
    /// The top block becomes a mud/peat-like block (code lookup with fallbacks); about SwampWaterCoverage of the swamp
    /// columns (clumpy noise) hold a 1-deep water pocket when WaterRules.PocketNeighbourOk holds on all four sides;
    /// some land columns get a plant from SwampPlantCodes (unknown codes were skipped with one log line each at load).
    /// A loose plant left on the old surface (tall grass at s + 1) is cleared where the ground is cut or a pocket floods it.
    /// </summary>
    public static class SwampPass
    {
        const int N = ChunkCtx.Size;
        /// <summary>Radius (Manhattan) of the swamp height smoothing (second flight test: terrace risers and pits inside bogs).</summary>
        public const int SmoothRadius = ColumnExclusions.SwampSmoothRadius;
        const int O = 1 + SmoothRadius;  // planning window -O..31+O: the -1..32 ring plus the smoothing sources
        const int W = N + 2 * O;

        /// <summary>
        /// Most ground one swamp column loses: ceil(MaxCut / 3) (2 at the default MaxCut 5; was ceil(MaxCut / 2) = 3, whose
        /// full depth showed as a 3-block wall at 1506,-3214). Swamps are boggy low ground, not excavations: the full MaxCut
        /// sank whole swamps 5 blocks into the plain (flight test, -3056,-292 and -1360,96). Lakes keep the full MaxCut. Pure.
        /// </summary>
        public static int SwampCut(int maxCut) => maxCut <= 0 ? 0 : (maxCut + 2) / 3;

        /// <summary>
        /// The swamp's fade distance: max(FadeMargin, 3 x SwampCut), capped at ColumnExclusions.MaxSwampFade (16). With the
        /// smoothstep fade the steepest added slope is 1.5 x cut / fade, so this keeps the swamp's own step below half a
        /// block per block (was up to about 0.94 with cut 5 over 8). Pure.
        /// </summary>
        public static int FadeOf(int fadeMargin, int maxCut) => Math.Clamp(Math.Max(fadeMargin, 3 * SwampCut(maxCut)), 0, ColumnExclusions.MaxSwampFade);
        public static int FadeOf(ResolvedConfig cfg) => FadeOf(cfg.FadeMargin, cfg.MaxCut);

        public static int Apply(ChunkCtx c)
        {
            var cfg = c.Cfg;
            if (c.SwampMask == null || double.IsPositiveInfinity(c.SwampThreshold) || cfg.SwampChance <= 0) return 0;
            int bx = c.BaseX, bz = c.BaseZ;
            int sea = cfg.SeaLevel, target = sea + 1;
            int fm = FadeOf(cfg);
            int cutMax = SwampCut(cfg.MaxCut);
            var hg = c.Heights;

            // 1. Nothing to do unless an own column is covered by the mask.
            bool any = false;
            for (int z = 0; z < N && !any; z++)
                for (int x = 0; x < N; x++)
                    if (c.SwampMask.Covers(bx + x, bz + z, c.SwampThreshold)) { any = true; break; }
            if (!any) return 0;

            // 2. Locked columns on -(fm+1)..32+fm: outside the mask, or Locked (rules below). The fade by exact distance to
            //    the nearest locked column covers the -1..32 window; every input is world data (mask noise at world
            //    coordinates, heights, structures, the wide water scan), so two chunks agree on every shared column.
            int lr = fm + O, ls = N + 2 * lr;
            var ocean = OceanMap.From(hg.Raw, sea);
            System.Func<int, int, int> raw = hg.Raw;
            var locked = new bool[ls * ls];
            var inMask = new bool[ls * ls];
            for (int lz = -lr; lz < N + lr; lz++)
                for (int lx = -lr; lx < N + lr; lx++)
                {
                    int li = (lz + lr) * ls + (lx + lr);
                    inMask[li] = c.SwampMask.Covers(bx + lx, bz + lz, c.SwampThreshold);
                    locked[li] = !inMask[li] || Locked(c, raw, ocean, lx, lz);
                }
            bool IsLocked(int lx, int lz) => locked[(lz + lr) * ls + (lx + lr)];
            bool InMask(int lx, int lz) => inMask[(lz + lr) * ls + (lx + lr)];
            System.Func<int, int, bool> isLocked = IsLocked;

            var weight = new double[W * W];
            var raw0 = new int[W * W];
            var low = new int[W * W];
            var minFinal = new int[W * W];
            var pocketCapable = new bool[W * W];
            var pos = new BlockPos(0, 0, 0);
            for (int lz = -O; lz < N + O; lz++)
                for (int lx = -O; lx < N + O; lx++)
                {
                    int wi = (lz + O) * W + (lx + O);
                    int s = hg.Raw(lx, lz);
                    raw0[wi] = s; low[wi] = s; minFinal[wi] = s;
                    if (s < 0) continue;
                    double lockFade = WaterRules.LockFade(isLocked, lx, lz, fm); // 0 when locked or outside the mask
                    if (lockFade <= 0) continue;
                    // Lowest final surface any chunk can give this column (rain weight <= 1 only lowers the cut).
                    minFinal[wi] = WaterRules.SwampLowest(s, target, cutMax, lockFade);
                    double w = lockFade * WaterRules.RainWeight(Rain(c, pos, bx + lx, bz + lz, sea), cfg.SwampMinRain);
                    if (w <= 0) continue;
                    weight[wi] = w;
                    raw0[wi] = WaterRules.SwampFinal(s, target, cutMax, cfg.MaxFill, w);
                    low[wi] = Math.Min(raw0[wi], WaterRules.SwampLowest(s, target, cutMax, w));
                    pocketCapable[wi] = c.SwampWaterMask != null && c.SwampWaterMask.Covers(bx + lx, bz + lz, c.SwampWaterThreshold);
                }

            // 2b. Height smoothing on -1..32 (second flight test, 938,1191 and 1296,1973): the cut followed each column's own
            //     height, so terrace risers stayed as 2..3 block steps inside the bog. A lower envelope with one block per block
            //     (Manhattan, radius SmoothRadius) over the planned surfaces lowers a swamp column that stands more than one block
            //     per block above a nearby lower surface, never below its own faded lowest (SwampLowest with its weight), so the
            //     edge fade still holds. Inputs are the pure planned surfaces of the window, the same in every chunk.
            var final = (int[])raw0.Clone();
            for (int lz = -1; lz <= N; lz++)
                for (int lx = -1; lx <= N; lx++)
                {
                    int wi = (lz + O) * W + (lx + O);
                    if (weight[wi] <= 0 || raw0[wi] < 0) continue;
                    final[wi] = WaterRules.SmoothedSurface(raw0, W, lx + O, lz + O, SmoothRadius, low[wi]);
                }

            // 2c. Seam fitting (flight test 6, 1506,-3214): columns of neighbour chunks this mod has already finished are
            //     known as they ARE, not as planned; own columns within SeamRadius of them ramp to them (WaterRules.SeamFit),
            //     so a disagreement between two chunks can no longer leave a wall along the border.
            int seamR = cutMax + 1;
            var seamLowered = new bool[N * N];
            var now = c.NeighbourNow;
            if (now != null && cutMax > 0)
            {
                for (int lz = -O; lz < N + O; lz++)
                    for (int lx = -O; lx < N + O; lx++)
                    {
                        if (lx >= 0 && lx < N && lz >= 0 && lz < N) continue;
                        int v = now(lx, lz);
                        if (v < 0) continue;
                        int wi = (lz + O) * W + (lx + O);
                        final[wi] = v; minFinal[wi] = v; pocketCapable[wi] = false;
                    }
                var list = new System.Collections.Generic.List<WaterRules.SeamNeighbour>();
                for (int z = 0; z < N; z++)
                    for (int x = 0; x < N; x++)
                    {
                        if (x >= seamR && z >= seamR && x < N - seamR && z < N - seamR) continue;
                        int s = hg.Raw(x, z);
                        if (s < 0) continue;
                        list.Clear();
                        for (int dz = -seamR; dz <= seamR; dz++)
                            for (int dx = -seamR; dx <= seamR; dx++)
                            {
                                int d = Math.Abs(dx) + Math.Abs(dz), nx = x + dx, nz = z + dz;
                                if (d == 0 || d > seamR || (nx >= 0 && nx < N && nz >= 0 && nz < N)) continue;
                                int v = now(nx, nz);
                                if (v >= 0) list.Add(new WaterRules.SeamNeighbour(hg.Raw(nx, nz), v, d));
                            }
                        if (list.Count == 0) continue;
                        int wi = (z + O) * W + (x + O);
                        int planned = weight[wi] > 0 ? final[wi] : s;
                        int fit = WaterRules.SeamFit(s, planned, cutMax, list);
                        if (fit == planned) continue;
                        final[wi] = fit;
                        if (weight[wi] <= 0 && fit < s) seamLowered[z * N + x] = true;
                        c.Stats.SeamFitted++;
                    }
            }

            // 3. Own columns.
            var blocks = c.Blocks;
            int changed = 0;
            Span<SwampNeighbour> nb = stackalloc SwampNeighbour[4];
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    int i = z * N + x, wi = (z + O) * W + (x + O);
                    if (seamLowered[i])
                    {
                        // Not swamp itself: only ramped down to a neighbour's lowered swamp (grass moved down, nothing else).
                        if (c.Touched[i] || c.Excl.Excluded(i) || c.NearLake[i]) continue;
                        int s0 = hg.Raw(x, z), f0 = final[wi];
                        int before0 = c.PlannedEdits;
                        int top0 = Plan(c, bx + x, bz + z, s0, f0, false, false, false);
                        c.SetTop(x, z, s0, top0, top0);
                        if (c.PlannedEdits == before0) continue;
                        c.Touched[i] = true;
                        changed++;
                        continue;
                    }
                    if (!InMask(x, z) || c.Touched[i]) continue;
                    if (c.Excl.Excluded(i)) { c.Stats.CountExclusion(c.Excl.Classify(i)); continue; }
                    if (c.NearLake[i]) continue;
                    if (IsLocked(x, z)) { c.Stats.SkipProtected++; continue; }
                    double w = weight[wi];
                    if (w <= 0) continue;
                    int wx = bx + x, wz = bz + z;
                    int s = hg.Raw(x, z), f = final[wi];

                    bool pocket = false;
                    if (pocketCapable[wi] && blocks.WaterId > 0 && f <= s)
                    {
                        nb[0] = Nb(hg, final, minFinal, pocketCapable, x + 1, z);
                        nb[1] = Nb(hg, final, minFinal, pocketCapable, x - 1, z);
                        nb[2] = Nb(hg, final, minFinal, pocketCapable, x, z - 1);
                        nb[3] = Nb(hg, final, minFinal, pocketCapable, x, z + 1);
                        pocket = true;
                        for (int k = 0; k < 4; k++) if (!WaterRules.PocketNeighbourOk(f, nb[k])) { pocket = false; break; }
                        if (pocket) pocket = PocketSealed(c, wx, wz, f);
                    }

                    // Peat only where the bog is wet: in noise clumps of the core (w >= 0.5) and around possible pockets.
                    // Elsewhere the grassy top stays (moved down onto cut ground). The old per-column dither scattered single
                    // brown peat blocks over the swamp edge, which read as dug pits (flight test, -2184,-237).
                    bool nearWater = pocketCapable[wi + 1] || pocketCapable[wi - 1] || pocketCapable[wi + W] || pocketCapable[wi - W];
                    bool peat = pocket || (w >= 0.5 && (nearWater || PeatClump(c, wx, wz)));

                    int before = c.PlannedEdits;
                    int top = Plan(c, wx, wz, s, f, pocket, peat, nearWater);
                    c.SetTop(x, z, s, top, pocket ? f : top);
                    if (!pocket && w >= 0.5) TryPlant(c, wx, wz, s, f);
                    if (c.PlannedEdits == before) continue; // nothing planned (no height change, no peat, no plant): not changed
                    if (pocket) c.Stats.SwampPockets++;
                    if (pocket) c.WaterPlaced[i] = true;
                    c.Touched[i] = true;
                    changed++;
                }
            c.Stats.SwampColumns += changed;
            return changed;
        }

        static SwampNeighbour Nb(HeightGrid hg, int[] final, int[] minFinal, bool[] pocketCapable, int lx, int lz)
        {
            int wi = (lz + O) * W + (lx + O);
            return new SwampNeighbour(hg.Raw(lx, lz), minFinal[wi], pocketCapable[wi], final[wi]);
        }

        /// <summary>
        /// Whether a swamp may never act at local (lx, lz). Own columns use the ColumnExclusions flags; columns outside
        /// use the same world data recomputed (structure rectangles + RuinMargin, ocean within OceanBuffer from heights,
        /// river level from the wide water scan, exact over the whole lock window since fix round 1 finding 5a).
        /// Lakes: within LakeKeepOut (= RiverRadius) of an active lake disc, in every chunk (finding 5b), because a
        /// neighbour generated after the lake sees the lake water as river water and excludes RiverRadius around it.
        /// Height rules: SwampColumnAllowed (MaxRiseAboveSea, MaxSlope, NaN slope locked) and MaxRimVariation.
        /// Unknown height (-1) is not locked (ColumnExclusions policy: no strip along missing data).
        /// </summary>
        // System.Func qualified: Vintagestory.API.Common also declares Func<,,> (CS0104)
        static bool Locked(ChunkCtx c, System.Func<int, int, int> raw, OceanMap ocean, int lx, int lz)
        {
            var cfg = c.Cfg; var hg = c.Heights;
            int s = hg.Raw(lx, lz);
            if (s < 0) return false;
            double slope = TerrainGuard.Slope(s, hg.Raw(lx + 1, lz), hg.Raw(lx - 1, lz), hg.Raw(lx, lz - 1), hg.Raw(lx, lz + 1));
            if (double.IsNaN(slope) || !TerrainGuard.SwampColumnAllowed(s, cfg.SeaLevel, cfg.MaxRiseAboveSea, slope, cfg.MaxSlope)) return true;
            if (WaterRules.RimSpan(raw, lx, lz, 2) > cfg.MaxRimVariation) return true;
            int wx = c.BaseX + lx, wz = c.BaseZ + lz;
            if (WaterRules.NearAnyFeature(c.LakeFeatures, wx, wz, WaterRules.LakeKeepOut)) return true;
            if (c.Excl.PondBlockedAt(wx, wz)) return true; // story structure keep-out (no ponds = no bog either)
            if (lx >= 0 && lx < N && lz >= 0 && lz < N) return c.Excl.Excluded(lz * N + lx);
            if (c.Excl.NearStructureAt(wx, wz, cfg.RuinMargin)) return true;
            if (c.Excl.RiverLevelAt(lx, lz) >= 0) return true;
            return ocean.Near(lx, lz, cfg.OceanBuffer);
        }

        /// <summary>
        /// Yearly-average rainfall 0..1 at the column. UNVERIFIED: that GetClimateAt with WorldGenValues is valid during
        /// the TerrainFeatures pass through the worldgen accessor and that its Rainfall holds the worldgen value. A null
        /// result or an exception gives 0 (no swamp) and one warning per session (fix round 1, finding 7).
        /// </summary>
        static double Rain(ChunkCtx c, BlockPos pos, int wx, int wz, int sea)
        {
            try
            {
                pos.Set(wx, sea, wz);
                var cond = c.Ba.GetClimateAt(pos, EnumGetClimateMode.WorldGenValues);
                if (cond == null) { WarnClimate(c, "returned null"); return 0.0; }
                return cond.Rainfall;
            }
            catch (Exception e) { WarnClimate(c, "threw " + e.GetType().Name + ": " + e.Message); return 0.0; }
        }

        static int climateWarned;

        static void WarnClimate(ChunkCtx c, string what)
        {
            if (Interlocked.Exchange(ref climateWarned, 1) != 0) return;
            c.Api?.Logger.Warning("[RiverFeatures] GetClimateAt {0} during worldgen; such columns count as dry (no swamp). Logged once per session.", what);
        }

        /// <summary>A pocket's water at y=f must not touch air: the four sides at y=f and the block two below must be filled.</summary>
        static bool PocketSealed(ChunkCtx c, int wx, int wz, int f)
        {
            try
            {
                var ba = c.Ba;
                if (ba.GetBlock(wx, f - 2, wz, BlockLayersAccess.Default).BlockId == 0) return false;
                for (int k = 0; k < 4; k++)
                {
                    int nx = wx + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = wz + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    if (ba.GetChunkAtBlockPos(nx, f, nz) == null) return false;
                    if (ba.GetBlock(nx, f, nz, BlockLayersAccess.Default).BlockId == 0) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Clumpy peat noise: the pocket noise sampled at an offset (decorrelated from the pockets), about SwampWaterCoverage of the area.</summary>
        static bool PeatClump(ChunkCtx c, int wx, int wz) =>
            c.SwampWaterMask != null && c.SwampWaterMask.Covers(wx + 7919, wz - 7919, c.SwampWaterThreshold);

        /// <summary>Plans the column and returns its new top solid ground Y (for the terrain height map).</summary>
        static int Plan(ChunkCtx c, int wx, int wz, int s, int f, bool pocket, bool peat, bool nearPocket)
        {
            int surface = c.Blocks.SwampSurfaceId;
            // The old top (normally grassy soil): moved down onto cut ground, and (flight test 6) laid over peat on land, so
            // the bog reads as grass with peat showing only where a pocket's bank exposes it.
            int oldTop = 0;
            if (f != s || (peat && !pocket))
            {
                try
                {
                    var b = c.Ba.GetBlock(wx, s, wz, BlockLayersAccess.Solid);
                    if (b != null && WaterRules.IsSoil(b.Code?.Path)) oldTop = b.BlockId;
                }
                catch (Exception) { oldTop = 0; }
            }
            // Loose plant at s + 1 (tall grass from the block layers): cleared where the ground is cut (it would float) or a
            // pocket floods the top (it would stand on water). Planned first, so a fill or a swamp plant at s + 1 replaces it.
            if (ClearsPlant(s, f, pocket)) c.ClearPlantAt(wx, s + 1, wz);
            for (int y = s; y > f; y--) c.Set(wx, y, wz, 0);
            // Fill (f > s): swamp block, or the old top block repeated when no swamp block exists.
            int fill = surface > 0 ? surface : (f > s ? SolidAt(c, wx, s, wz) : 0);
            if (fill > 0) for (int y = s + 1; y <= f; y++) c.Set(wx, y, wz, fill);
            int top = f > s && fill <= 0 ? s : f;
            if (pocket)
            {
                c.Set(wx, f, wz, c.Blocks.WaterId);
                if (surface > 0) c.Set(wx, f - 1, wz, surface);
                return f - 1;
            }
            if (peat && surface > 0)
            {
                // Grass over peat (flight test 6): the grassy top on the surface, peat right below it. Beside a pocket the
                // top is peat with a thin grass cover (peat-verysparse) so the water's edge still looks boggy.
                int grassTop = nearPocket ? c.Blocks.SwampGrassId : (oldTop > 0 ? oldTop : c.Blocks.GrassSoilId);
                if (grassTop <= 0) grassTop = c.Blocks.SwampGrassId > 0 ? c.Blocks.SwampGrassId : surface;
                c.Set(wx, f, wz, grassTop);
                if (grassTop != surface && grassTop != c.Blocks.SwampGrassId) c.Set(wx, f - 1, wz, surface);
            }
            else if (oldTop > 0) c.Set(wx, f, wz, oldTop);
            return top;
        }

        /// <summary>Whether a swamp column's old plant layer (s + 1) must be cleared: the surface is cut (f &lt; s) or holds a pocket. Pure.</summary>
        public static bool ClearsPlant(int s, int f, bool pocket) => f < s || pocket;

        static int SolidAt(ChunkCtx c, int wx, int y, int wz)
        {
            try { return c.Ba.GetBlock(wx, y, wz, BlockLayersAccess.Solid).BlockId; } catch (Exception) { return 0; }
        }

        static void TryPlant(ChunkCtx c, int wx, int wz, int s, int f)
        {
            var ids = c.Blocks.PlantIds;
            if (ids.Length == 0 || WaterRules.Hash01(c.Seed, wx, wz, 23) >= WaterRules.PlantChance) return;
            int y = f + 1;
            if (f >= s)
            {
                try { if (c.Ba.GetBlock(wx, y, wz, BlockLayersAccess.Default).BlockId != 0) return; }
                catch (Exception) { return; }
            }
            int pick = (int)(WaterRules.Hash01(c.Seed, wx, wz, 29) * ids.Length);
            c.Set(wx, y, wz, ids[Math.Min(pick, ids.Length - 1)]);
            c.Stats.Plants++;
        }
    }
}
