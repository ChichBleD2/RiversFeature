using System;
using System.Collections.Generic;

namespace RiverFeatures
{
    /// <summary>
    /// The world data the lake planner reads, as pure delegates in LOCAL coordinates of one chunk column (0..31 is the
    /// column itself; the planner reads up to 32 blocks out). LakePass builds it from ChunkCtx; tests build it from a
    /// synthetic HeightGrid. Every delegate must be a function of world data only (never of what this mod changed).
    /// </summary>
    public sealed class LakeWorld
    {
        /// <summary>Terrain height, -1 when unknown (HeightGrid.Raw: -32..63).</summary>
        public Func<int, int, int> Height;
        /// <summary>Existing water top Y in the column, -1 when none (ColumnExclusions.WaterTopAt).</summary>
        public Func<int, int, int> WaterTop;
        /// <summary>River surface within RiverRadius, -1 when none (ColumnExclusions.RiverLevelAt). Read only for -1..32.</summary>
        public Func<int, int, int> RiverLevel;
        /// <summary>The column's lake feature (WaterRules.BestFeature over the active features).</summary>
        public Func<int, int, FeatureHit> Feature;
        /// <summary>Within RuinMargin of a structure (same rule as ColumnExclusions.NearStructure).</summary>
        public Func<int, int, bool> NearStructure;
        /// <summary>Within OceanBuffer of the sea (same rule as ColumnExclusions.NearOcean, see OceanMap).</summary>
        public Func<int, int, bool> NearOcean;
        /// <summary>
        /// Flow-through lakes: the nearest river surface within ColumnExclusions.RiverLakeReach when the river-lake mask covers
        /// the column, else -1. Asked only for columns in no lake disc. Null = off.
        /// </summary>
        public Func<int, int, int> RiverLake;
        /// <summary>
        /// Smooth noise in [0, 1] at the column's WORLD position (local coordinates in), varying the shore slope and the bottom
        /// depth so depth contours meander instead of copying the shoreline. Null = no variation.
        /// </summary>
        public Func<int, int, double> DepthNoise;
        /// <summary>
        /// Own dry columns that may be lowered toward the water (shore shaving): only where swamps are locked in
        /// every chunk (within LakeKeepOut of a lake disc, or within RiverRadius of river water). Null = no shaving.
        /// </summary>
        public Func<int, int, bool> ShaveAllowed;
        /// <summary>
        /// Fine noise in [0, 1] at the column's WORLD position (scale a few blocks) for shore shaving: it varies how steep each
        /// shaved bank is, so the shaved steps meander instead of running as parallel lines along a straight shore. Null = the
        /// exact one-block-per-block staircase.
        /// </summary>
        public Func<int, int, double> ShaveNoise;
        /// <summary>Share of nearby columns already holding water (ColumnExclusions.WaterShareAt), or null (treated as 0).</summary>
        public Func<int, int, double> WaterShare;
        /// <summary>
        /// Flight test 11 (square pits, -2428,-8368): what an ALREADY FINISHED neighbour chunk actually holds at a column
        /// outside this chunk: -1 = not finished (use the plan), 0 = finished and dry, 1 = finished with this mod's water.
        /// The shore ramp uses it, so a lake never drops to full depth against a neighbour column that really stayed dry.
        /// Null = plan only.
        /// </summary>
        public Func<int, int, int> Finished;
    }

    /// <summary>
    /// The planner's answer for one own column: Reason None = flood it (Column holds level, cut and bed). ShaveTo &gt;= 0 on a
    /// dry shore column = lower its ground to that Y (shore shaving), -1 = leave it.
    /// </summary>
    public readonly record struct LakePlan(LakeColumn Column, LakeSkip Reason, bool Basin, bool RiverLake = false, int ShaveTo = -1)
    {
        public bool Flood => Reason == LakeSkip.None;
    }

    /// <summary>
    /// Pure lake decisions for one chunk column (fix round 1, findings 1, 2, 4). Two regimes per column of an active
    /// feature disc, chosen by the column's ORIGINAL height s against the feature's fixed level Lf:
    ///
    /// BASIN (s &lt; Lf): the connected region of columns below Lf (4-neighbour flood fill) is flooded to Lf as a whole,
    /// or not at all. It is accepted only if it is closed (every neighbour outside it has ground &gt;= Lf or water whose
    /// top is exactly Lf), its bounding box is at most WaterRules.BasinSpan per side, and every region column is a
    /// candidate at level Lf (LakeCandidate: guard, slope, rim, min depth), in a feature of level Lf, not near a
    /// structure and not near the ocean. The span bound keeps the whole region, its ring and every height/ocean/water
    /// value it reads inside the data any chunk that sees one of its columns has, so every such chunk computes the same
    /// region and the same answer. River-level matching does not apply to basins (their columns would need river data
    /// beyond the scanned area); a basin touching water at another level is rejected.
    ///
    /// BANK (s &gt;= Lf): the column's level is the river surface within RiverRadius when there is one, else Lf (river
    /// matching); it floods when LakeCandidate allows, every neighbour holds the water (WaterRules.NeighbourHolds, with
    /// accepted basin neighbours known flooded) and, when s is above the level, every neighbour ends at most one block
    /// above the water (WaterRules.BankNeighbourLow): no cut bank higher than one block (simpler option of the ruling;
    /// cost: fewer and narrower lakes on sloping ground).
    ///
    /// Basin decisions do not depend on bank decisions and bank decisions read only neighbours' candidate state and basin
    /// decisions, so nothing is circular and nothing depends on generation order.
    /// </summary>
    public sealed class LakePlanner
    {
        public const int Size = 32;
        const int Off = 32, Side = 3 * Size; // window -32..63, the HeightGrid.Raw range

        readonly LakeWorld w;
        readonly LakeParams p;
        readonly int span;
        readonly bool[] done = new bool[Side * Side];
        readonly LakeColumn[] cand = new LakeColumn[Side * Side];
        readonly bool[] basin = new bool[Side * Side];
        readonly bool[] riverLake = new bool[Side * Side];
        /// <summary>Carve depth below the level allowed by the outline fade: floor(MaxCut x Fade(distance to the outline edge)).</summary>
        readonly int[] fadeDepth = new int[Side * Side];
        readonly int[] stamp = new int[Side * Side];
        int curStamp;
        readonly List<int> queue = new List<int>();

        public LakePlanner(LakeWorld world, in LakeParams prm, int basinSpan)
        {
            w = world ?? throw new ArgumentNullException(nameof(world));
            p = prm;
            span = Math.Clamp(basinSpan, 1, 32);
        }

        static bool InWindow(int lx, int lz) => lx >= -Off && lx < Side - Off && lz >= -Off && lz < Side - Off;
        static int Idx(int lx, int lz) => (lz + Off) * Side + (lx + Off);

        int H(int lx, int lz) => InWindow(lx, lz) ? w.Height(lx, lz) : -1;

        /// <summary>
        /// Height as the slope and rim rules see it: a column holding water counts at its water surface. Measured against the
        /// river BED, every bank beside a Rivers channel looked steeper than MaxSlope and was protected, so a lake could never
        /// reach a river (flight test: no flow-through lakes anywhere).
        /// </summary>
        int HSurf(int lx, int lz)
        {
            int h = H(lx, lz);
            if (h < 0) return h;
            int wt = w.WaterTop(lx, lz);
            return wt > h ? wt : h;
        }

        void Ensure(int lx, int lz)
        {
            int i = Idx(lx, lz);
            if (done[i]) return;
            done[i] = true;
            var hit = w.Feature(lx, lz);
            int s = H(lx, lz);
            int level;
            bool isBasin;
            double distToEdge;
            bool riverLevel = false;
            if (hit.Found)
            {
                int lf = hit.Cell.WaterLevel;
                distToEdge = hit.DistToEdge;
                // Flight test 4: natural hollows fill to their own SPILL level (the highest level of the lake band at which
                // the closed region below it is accepted, AdaptiveFill), not to the feature's random level, which a hollow
                // matched only by chance. The natural floor is kept (no carve beyond the shore smoothing).
                int bl = s >= 0 ? BasinLevel(lx, lz) : -1;
                if (bl > 0)
                {
                    basin[i] = true;
                    cand[i] = new LakeColumn(true, bl, 0, s, LakeSkip.None);
                    fadeDepth[i] = (int)Math.Floor(Math.Max(0, p.MaxCut) * TerrainGuard.Fade(distToEdge, p.FadeMargin));
                    return;
                }
                if (s >= 0 && s < lf) { cand[i] = LakeColumn.No(LakeSkip.Leak, lf); return; } // below the level, no closed hollow
                isBasin = false;
                int rl = w.RiverLevel(lx, lz);
                riverLevel = rl >= 0;
                level = WaterRules.LevelFor(rl, lf);
            }
            else
            {
                // Flow-through lake (river-side zone, no disc): level = the nearest river surface, bank regime only. Such a
                // column is never part of a disc basin (FillRegion rejects any region column that is not a basin column),
                // so a zone column outside the exactly scanned area changes no basin decision.
                int rl = w.RiverLake == null ? -1 : w.RiverLake(lx, lz);
                if (rl < 0) { cand[i] = LakeColumn.No(LakeSkip.Outside); return; }
                riverLake[i] = true;
                isBasin = false;
                level = rl;
                riverLevel = true;
                distToEdge = p.FadeMargin + 1; // no disc edge: the shore ramp shapes the banks
            }
            // Flight test 3: next to a body of existing water that is mostly water around the column (a Rivers LAKE, its
            // shore or island, or a very wide river), joining it at its level only flooded islands into submerged mounds and
            // left River Features' shallow bed against the lake's deep basin as a wall. Only river CHANNELS are joined.
            if (riverLevel && w.WaterShare != null && w.WaterShare(lx, lz) > WaterRules.MaxJoinWaterShare)
            {
                cand[i] = LakeColumn.No(LakeSkip.RiverWater, level);
                basin[i] = false;
                return;
            }
            double slope = s < 0 ? double.NaN : TerrainGuard.Slope(s, HSurf(lx + 1, lz), HSurf(lx - 1, lz), HSurf(lx, lz - 1), HSurf(lx, lz + 1));
            int rim = s < 0 ? 0 : WaterRules.RimSpan(HSurf, lx, lz, WaterRules.RimRadius);
            cand[i] = WaterRules.LakeCandidate(s, w.WaterTop(lx, lz), level, slope, rim, distToEdge, p);
            basin[i] = isBasin;
            fadeDepth[i] = (int)Math.Floor(Math.Max(0, p.MaxCut) * TerrainGuard.Fade(distToEdge, p.FadeMargin));
        }

        /// <summary>The per-column rule result (level, cut, bed) at local (lx, lz) in -32..63; Unknown outside.</summary>
        public LakeColumn Column(int lx, int lz)
        {
            if (!InWindow(lx, lz)) return LakeColumn.No(LakeSkip.Unknown);
            Ensure(lx, lz);
            return cand[Idx(lx, lz)];
        }

        /// <summary>True when (lx, lz) is a natural hollow column that fills (its region was accepted at its spill level).</summary>
        public bool BasinFloods(int lx, int lz)
        {
            if (!InWindow(lx, lz)) return false;
            Ensure(lx, lz);
            int i = Idx(lx, lz);
            return basin[i] && cand[i].Candidate;
        }

        readonly Dictionary<int, sbyte[]> levelAccepted = new Dictionary<int, sbyte[]>();

        /// <summary>
        /// The spill level of the natural hollow around (lx, lz): the HIGHEST level L of the lake band (sea + 2 .. sea + MaxRise,
        /// above the column's own ground) whose region (AdaptiveFill) is accepted, or -1. Chunk-consistent: for two columns of
        /// one accepted region at L, every higher level's region containing one contains the other, so both find the same L.
        /// </summary>
        int BasinLevel(int lx, int lz)
        {
            int s = H(lx, lz);
            int lo = Math.Max(s + 1, p.SeaLevel + 2), hi = p.SeaLevel + p.MaxRise;
            for (int L = hi; L >= lo; L--)
                if (AdaptiveAccepted(lx, lz, L)) return L;
            return -1;
        }

        bool AdaptiveAccepted(int lx, int lz, int level)
        {
            if (!levelAccepted.TryGetValue(level, out var acc)) { acc = new sbyte[Side * Side]; levelAccepted[level] = acc; }
            int i = Idx(lx, lz);
            if (acc[i] != 0) return acc[i] > 0;
            bool ok = AdaptiveFill(lx, lz, level);
            foreach (int q in queue) acc[q] = (sbyte)(ok ? 1 : -1); // every visited column lies in the same region
            acc[i] = (sbyte)(ok ? 1 : -1);
            return ok;
        }

        /// <summary>
        /// The connected region (4-neighbour) of columns below <paramref name="level"/> containing (x0, z0), accepted when every
        /// region column is inside an active lake outline, above sea level, at most WaterRules.MaxBasinDepth below the level,
        /// holds no water, is not near a structure or the ocean; every neighbour outside it is ground at or above the level or
        /// water flush with it; the bounding box is at most BasinSpan; and the deepest column is at least LakeMinDepth deep.
        /// Slope and rim rules do not apply: a hollow's floor is not changed (no protection to give), only its water is added.
        /// </summary>
        bool AdaptiveFill(int x0, int z0, int level)
        {
            queue.Clear();
            curStamp++;
            int minX = x0, maxX = x0, minZ = z0, maxZ = z0, maxDepth = 0;
            int maxBasin = WaterRules.MaxBasinDepth(p.MaxCut);
            stamp[Idx(x0, z0)] = curStamp;
            queue.Add(Idx(x0, z0));
            for (int head = 0; head < queue.Count; head++)
            {
                int qi = queue[head];
                int x = qi % Side - Off, z = qi / Side - Off;
                int h = H(x, z);
                if (h < 0 || h >= level || h <= p.SeaLevel || level - h > maxBasin) return false;
                if (w.WaterTop(x, z) >= 0) return false;
                if (!w.Feature(x, z).Found) return false;
                if (w.NearStructure(x, z) || w.NearOcean(x, z)) return false;
                maxDepth = Math.Max(maxDepth, level - h);
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    int nh = H(nx, nz);
                    if (nh < 0) return false;                         // unknown or outside the window: never pour against it
                    int ni = Idx(nx, nz);
                    if (stamp[ni] == curStamp) continue;              // already in the region
                    int wt = w.WaterTop(nx, nz);
                    if (wt >= 0) { if (wt == level) continue; return false; } // flush water closes it; other water leaks
                    if (nh >= level) continue;                        // closing ground
                    stamp[ni] = curStamp;
                    if (nx < minX) minX = nx; if (nx > maxX) maxX = nx;
                    if (nz < minZ) minZ = nz; if (nz > maxZ) maxZ = nz;
                    if (maxX - minX + 1 > span || maxZ - minZ + 1 > span) return false;
                    queue.Add(ni);
                }
            }
            return maxDepth >= p.MinDepth;
        }

        /// <summary>A neighbour as the containment rules see it: accepted basin columns are known flooded, rejected ones known dry.</summary>
        public LakeNeighbour Neighbour(int lx, int lz)
        {
            if (!InWindow(lx, lz)) return new LakeNeighbour(-1, -1, false, -1);
            Ensure(lx, lz);
            int i = Idx(lx, lz);
            var nc = cand[i];
            bool flooded = basin[i] && nc.Candidate && BasinFloods(lx, lz);
            bool mayFlood = nc.Candidate && (!basin[i] || flooded);
            return new LakeNeighbour(H(lx, lz), w.WaterTop(lx, lz), mayFlood, nc.Level, flooded);
        }

        /// <summary>The decision for own column (x, z), 0..31.</summary>
        LakePlan DecideRaw(int x, int z)
        {
            Ensure(x, z);
            int i = Idx(x, z);
            var col = cand[i];
            bool rl = riverLake[i];
            if (!col.Candidate) return new LakePlan(col, WaterRules.LakeFinal(col, false, false, true), basin[i], rl);
            bool ns = w.NearStructure(x, z), no = w.NearOcean(x, z);
            if (ns || no) return new LakePlan(col, WaterRules.LakeFinal(col, ns, no, true), basin[i], rl);
            if (basin[i]) return new LakePlan(col, BasinFloods(x, z) ? LakeSkip.None : LakeSkip.Leak, true);

            int s = H(x, z);
            bool holds = true, low = true;
            for (int k = 0; k < 4; k++)
            {
                var n = Neighbour(x + (k == 0 ? 1 : k == 1 ? -1 : 0), z + (k == 2 ? -1 : k == 3 ? 1 : 0));
                if (!WaterRules.NeighbourHolds(col.Level, n)) holds = false;
                if (s > col.Level && !WaterRules.BankNeighbourLow(col.Level, n)) low = false;
            }
            var why = WaterRules.LakeFinal(col, false, false, holds);
            if (why == LakeSkip.None && !low) why = LakeSkip.Bank;
            return new LakePlan(col, why, false, rl);
        }

        /// <summary>
        /// Smallest lake body kept, in columns (flight test 9: too many mini-lakes). 0 = keep every body. A body is the
        /// 4-connected set of columns this planner floods; it is dropped (LakeSkip.Small) when it has fewer than MinArea
        /// columns, touches no existing water (a small flow-through patch beside a river widens the river and is kept), and
        /// fits in a 31 x 31 box. A body that fits that box lies inside the -32..63 window of every chunk owning one of its
        /// columns, so every chunk measures it whole and agrees; anything larger is kept.
        /// </summary>
        public int MinArea;
        readonly Dictionary<int, bool> smallBody = new Dictionary<int, bool>();

        /// <summary>The decision for column (x, z) in the window, with the small-body rule applied.</summary>
        public LakePlan Decide(int x, int z)
        {
            var pl = DecideRaw(x, z);
            if (!pl.Flood || MinArea <= 1 || !InWindow(x, z)) return pl;
            return IsSmallBody(x, z) ? pl with { Reason = LakeSkip.Small } : pl;
        }

        bool IsSmallBody(int x0, int z0)
        {
            int i0 = Idx(x0, z0);
            if (smallBody.TryGetValue(i0, out bool cached)) return cached;
            var seen = new HashSet<int> { i0 };
            var list = new List<int> { i0 };
            int minX = x0, maxX = x0, minZ = z0, maxZ = z0;
            bool small = true;
            for (int head = 0; head < list.Count && small; head++)
            {
                int qi = list[head];
                int x = qi % Side - Off, z = qi / Side - Off;
                for (int k = 0; k < 4 && small; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    if (!InWindow(nx, nz)) { small = false; break; }
                    if (w.WaterTop(nx, nz) >= 0) { small = false; break; }   // joins existing water
                    int ni = Idx(nx, nz);
                    if (seen.Contains(ni) || !DecideRaw(nx, nz).Flood) continue;
                    seen.Add(ni); list.Add(ni);
                    if (nx < minX) minX = nx; if (nx > maxX) maxX = nx;
                    if (nz < minZ) minZ = nz; if (nz > maxZ) maxZ = nz;
                    if (list.Count >= MinArea || maxX - minX + 1 > 31 || maxZ - minZ + 1 > 31) small = false;
                }
            }
            if (small && list.Count >= MinArea) small = false;
            foreach (int q in list) smallBody[q] = small;
            return small;
        }

        /// <summary>
        /// Whether a column counts as dry shore for the ramp: not flooded by the plan and holding no water, except that a
        /// column of an already finished neighbour chunk counts as it really is (Finished), whatever this plan expected.
        /// </summary>
        bool DryForRamp(int lx, int lz, bool plannedFlood)
        {
            bool own = lx >= 0 && lx < Size && lz >= 0 && lz < Size;
            if (!own && w.Finished != null)
            {
                int f = w.Finished(lx, lz);
                if (f == 1) return false;
                if (f == 0) return w.WaterTop(lx, lz) < 0;
            }
            return !plannedFlood && w.WaterTop(lx, lz) < 0;
        }

        /// <summary>Decisions for all 32x32 own columns, index z*32+x (no shore ramp: beds as LakeCandidate planned them).</summary>
        public LakePlan[] DecideAll()
        {
            var r = new LakePlan[Size * Size];
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                    r[z * Size + x] = Decide(x, z);
            return r;
        }

        /// <summary>
        /// Decisions for all 32x32 own columns with the shore ramp (flight test fix): flood decisions are taken on the window
        /// -radius..31+radius, then every flooded own column's bed is raised to WaterRules.ShoreBed with the Euclidean distance
        /// to the nearest column that stays dry (not flooded, outside, unknown), so the water deepens by one block per block
        /// away from the shore instead of dropping MaxCut at the first column. Which columns flood is unchanged.
        /// The decisions of the outer ring are the same as their owning chunk's except in the rare case of a basin near the
        /// window edge whose ocean buffer reaches past the height window; then only an underwater depth can differ.
        /// </summary>
        public LakePlan[] PlanWithShore(int radius)
        {
            int R = Math.Clamp(radius, 0, WaterRules.MaxShoreRadius);
            int E = WaterRules.BedSmoothRadius;
            int M = Math.Max(R + E, WaterRules.ShaveRadius);
            int S = Size + 2 * M;
            var plans = new LakePlan[S * S];
            for (int gz = 0; gz < S; gz++)
                for (int gx = 0; gx < S; gx++)
                    plans[gz * S + gx] = Decide(gx - M, gz - M);
            LakePlan At(int lx, int lz) => plans[(lz + M) * S + (lx + M)];

            // 1. Target bed for flooded columns on -E..31+E. Second flight test (-475,-858): the bed was capped at MaxCut below each
            //    column's OWN surface, so terraced ground left terraced shelves (rings) under water, and the shore ramp drew bands
            //    exactly parallel to the shore. Now the water depth D is measured from the LEVEL, independent of the column's own
            //    height: D = min(shore ramp with a noisy slope, noisy bottom depth, outline fade depth), at least 1; the bed is
            //    level - D, or the natural floor where that is already deeper (lakes never fill). MaxCut is therefore the depth
            //    below the water: a bank column (at most MaxCut - LakeMinDepth above the water) can lose up to that much more.
            int B = Size + 2 * E;
            var baseBed = new int[B * B];
            var low = new int[B * B];
            int r2 = R * R;
            int Fd(int lx, int lz) { Ensure(lx, lz); return fadeDepth[Idx(lx, lz)]; }
            for (int gz = 0; gz < B; gz++)
                for (int gx = 0; gx < B; gx++)
                {
                    int lx = gx - E, lz = gz - E;
                    var pl = At(lx, lz);
                    if (!pl.Flood) continue;
                    int best2 = int.MaxValue;
                    for (int dz = -R; dz <= R; dz++)
                        for (int dx = -R; dx <= R; dx++)
                        {
                            int d2 = dx * dx + dz * dz;
                            if (d2 == 0 || d2 > r2 || d2 >= best2) continue;
                            // Dry = not flooded and holding no water already (a river channel next to a flow-through
                            // lake is wet: the lake bed deepens toward it, not toward a fake shore).
                            if (DryForRamp(lx + dx, lz + dz, At(lx + dx, lz + dz).Flood)) best2 = d2;
                        }
                    double dist = best2 == int.MaxValue ? R + 1 : Math.Sqrt(best2);
                    double n1 = w.DepthNoise == null ? 0.5 : w.DepthNoise(lx, lz);
                    double n2 = w.DepthNoise == null ? 1.0 : w.DepthNoise(lx + 4099, lz - 2053);
                    int s = H(lx, lz), level = pl.Column.Level;
                    int ramp = WaterRules.ShoreDepth(dist * WaterRules.ShoreSlope(n1));
                    int depth = Math.Max(1, Math.Min(ramp, Math.Min(WaterRules.BottomDepth(p.MaxCut, n2), Fd(lx, lz))));
                    int i = gz * B + gx;
                    low[i] = level - ramp;                      // never shallower-than-shore violated by the smoothing
                    baseBed[i] = Math.Min(s, level - depth);
                }

            var r = new LakePlan[Size * Size];
            int sr = WaterRules.ShaveRadius, sr2 = sr * sr;
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                {
                    var pl = At(x, z);
                    int s = H(x, z);
                    if (pl.Flood)
                    {
                        // 2. Bed smoothing: lower envelope with one block per block (Manhattan) over flooded columns of the same
                        //    level, so a hump of natural floor standing more than one block above a deeper natural floor nearby is
                        //    cut down into a slope (only lowering; never below the shore ramp, never more than MaxCut below the
                        //    column's own surface).
                        int i = (z + E) * B + (x + E);
                        int envLake = baseBed[i], envRiver = int.MaxValue;
                        int level = pl.Column.Level;
                        for (int dz = -E; dz <= E; dz++)
                            for (int dx = -E; dx <= E; dx++)
                            {
                                int d = Math.Abs(dx) + Math.Abs(dz);
                                if (d == 0 || d > E) continue;
                                var nb = At(x + dx, z + dz);
                                if (nb.Flood && nb.Column.Level == level && !DryForRamp(x + dx, z + dz, true))
                                {
                                    int v = baseBed[(z + dz + E) * B + (x + dx + E)] + d;
                                    if (v < envLake) envLake = v;
                                }
                            }
                        // A river channel flush with this lake (flight test 3): slope down into its bed (one block per block,
                        // Manhattan, out to the shore-ramp radius so the slope meets the ramp without a step) instead of leaving
                        // the lake's shallow bed as a wall above the channel.
                        for (int dz = -R; dz <= R; dz++)
                            for (int dx = -R; dx <= R; dx++)
                            {
                                int d = Math.Abs(dx) + Math.Abs(dz);
                                if (d == 0 || d > R) continue;
                                if (w.WaterTop(x + dx, z + dz) != level) continue;
                                int hb = H(x + dx, z + dz);
                                if (hb < 0) continue;
                                int v = hb + d;
                                if (v < envRiver) envRiver = v;
                            }
                        // Smoothing toward this lake's own deeper beds: the shore-ramp bound keeps banks (ground at or above the
                        // level) shallow near dry ground; a column of a natural basin (below the level) may go down toward the
                        // deeper natural floor beside it. Smoothing toward a river channel ignores the ramp bound (it cannot make
                        // a wall against dry ground: its own slope is one block per block). Both stop MaxCut below the ground.
                        int bound = s < level ? s - p.MaxCut : Math.Max(low[i], s - p.MaxCut);
                        int bedLake = Math.Max(envLake, bound);
                        int bedRiver = envRiver == int.MaxValue ? int.MaxValue : Math.Max(envRiver, s - p.MaxCut);
                        int bed = Math.Min(baseBed[i], Math.Min(bedLake, bedRiver));
                        var c = pl.Column;
                        if (bed != c.Bed) pl = pl with { Column = c with { Bed = bed, Cut = Math.Max(0, s - bed) } };
                    }
                    else if (w.ShaveAllowed != null && s >= 0 && w.WaterTop(x, z) < 0 && w.ShaveAllowed(x, z)
                             && !w.NearStructure(x, z) && !w.NearOcean(x, z) && s <= p.SeaLevel + p.MaxRise)
                    {
                        // 3. Shore shaving: a dry column near the water whose ground stands well above it is lowered toward
                        //    level + (distance - 1), at most ShaveRadius + 1 - distance blocks, so the bank becomes a slope
                        //    instead of a step. Never below any nearby water's level + (distance - 1) (keeps every water column
                        //    walled), never on steep ground (twice MaxSlope: terrace risers yes, cliffs no).
                        double slope = TerrainGuard.Slope(s, H(x + 1, z), H(x - 1, z), H(x, z - 1), H(x, z + 1));
                        if (!(slope <= 2 * p.MaxSlope)) { r[z * Size + x] = pl; continue; }
                        // slope term: the nearest water's level + (distance - 1); wall term: every ORTHOGONALLY adjacent water's
                        // level (water only flows orthogonally), so no water column ever loses its wall.
                        // Flight test 15 (-1210,5268): the exact staircase (level + distance - 1) drew three straight steps parallel to
                        // every straight shore, a dug, stepped-pit look. Per column the bank is now 1.0..2.2 blocks of rise per block
                        // of distance plus a 0..1.4 block offset, both from fine noise: the target only ever RISES above the old
                        // staircase (never a deeper cut, the wall term below is unchanged), so steps break up and wander.
                        double sk = 1.0, sj = 0.0;
                        if (w.ShaveNoise != null)
                        {
                            double a = Math.Clamp(w.ShaveNoise(x, z), 0, 1), b = Math.Clamp(w.ShaveNoise(x + 3301, z - 7919), 0, 1);
                            sk = 1.0 + 1.2 * a; sj = 1.4 * b;
                        }
                        int slopeT = int.MaxValue, wallT = int.MinValue, best2 = int.MaxValue;
                        for (int dz = -sr; dz <= sr; dz++)
                            for (int dx = -sr; dx <= sr; dx++)
                            {
                                int d2 = dx * dx + dz * dz;
                                if (d2 == 0 || d2 > sr2) continue;
                                var nb = At(x + dx, z + dz);
                                int lv = nb.Flood ? nb.Column.Level : w.WaterTop(x + dx, z + dz);
                                if (lv < 0) continue;
                                int t = lv + (int)Math.Floor((Math.Sqrt(d2) - 1) * sk + sj + 1e-9);
                                if (t < slopeT) slopeT = t;
                                if (d2 == 1 && lv > wallT) wallT = lv;
                                if (d2 < best2) best2 = d2;
                            }
                        int target = slopeT == int.MaxValue ? int.MinValue : Math.Max(slopeT, wallT);
                        if (target != int.MinValue && s > target)
                        {
                            int maxDrop = Math.Min(p.MaxCut, sr + 1 - (int)Math.Ceiling(Math.Sqrt(best2) - 1e-9));
                            int to = Math.Max(target, s - maxDrop);
                            if (to < s) pl = pl with { ShaveTo = to };
                        }
                    }
                    r[z * Size + x] = pl;
                }
            return r;
        }
    }

    /// <summary>
    /// Ocean cells (height known and at or below sea level) over the HeightGrid window -32..63 and the OceanBuffer rule at
    /// any local column: same rule as ColumnExclusions.ComputeNearOcean (Euclidean, buffer capped at 32, unknown is not
    /// ocean), so own columns get the same answer as ColumnExclusions.NearOcean. Pure.
    /// </summary>
    public sealed class OceanMap
    {
        const int N = 32, Span3 = 3 * N;
        readonly bool[] cells; // null when there is no ocean in the window

        OceanMap(bool[] c) { cells = c; }

        public static OceanMap From(Func<int, int, int> height, int seaLevel)
        {
            bool[] a = null;
            for (int z = -N; z < 2 * N; z++)
                for (int x = -N; x < 2 * N; x++)
                {
                    int h = height(x, z);
                    if (h != -1 && h <= seaLevel) { a ??= new bool[Span3 * Span3]; a[(z + N) * Span3 + (x + N)] = true; }
                }
            return new OceanMap(a);
        }

        public bool Near(int lx, int lz, int buffer)
        {
            if (cells == null) return false;
            buffer = Math.Clamp(buffer, 0, N);
            int r2 = buffer * buffer;
            for (int dz = -buffer; dz <= buffer; dz++)
                for (int dx = -buffer; dx <= buffer; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue;
                    int x = lx + dx + N, z = lz + dz + N;
                    if (x < 0 || z < 0 || x >= Span3 || z >= Span3) continue;
                    if (cells[z * Span3 + x]) return true;
                }
            return false;
        }
    }
}
