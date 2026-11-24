using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    public enum ExclusionKind { None = 0, Structure = 1, River = 2, Ocean = 3 }

    /// <summary>
    /// Per-column "leave this alone" flags for one 32x32 chunk column (index z*32+x, local coordinates).
    /// Every flag is a pure function of WORLD coordinates and world data (structure rectangles, terrain
    /// heights, water blocks), never of chunk-relative state, so two neighbouring chunks agree on the same
    /// world column and there are no seams or holes at chunk borders. Structures and heights reach into the
    /// neighbouring chunks/regions on purpose: a ruin's footprint plus RuinMargin excludes columns in the
    /// next chunk too.
    ///
    /// PRECEDENCE when zones overlap (Classify): 1. Structure/ruin margin, 2. River water, 3. Ocean.
    /// A column is excluded if ANY flag is set; the precedence only decides which reason is reported, and
    /// therefore which rule the consumer applies: a ruin always wins (nothing near it is touched, not even
    /// to join a river), a river wins over the ocean (a river mouth column keeps its river water level so the
    /// lake handler can match it instead of treating it as sea). Ocean is the weakest because the ocean
    /// buffer is a pure keep-away rule with no water level to match.
    /// River mouth caveat: river water is only recorded for columns whose terrain height is above sea level
    /// (below that the water is sea). At a mouth whose bed is at or below sea level the column is therefore
    /// excluded as Ocean, not River: still excluded, only the reported reason differs. River precedence holds
    /// where the bed is above sea level.
    ///
    /// KNOWN LIMIT, missing neighbour data (policy, ruled by the coordinator): a height of -1 (no source had the
    /// neighbour's heights, see HeightGrid) means "unknown" and is treated as NOT ocean, NOT river water.
    /// Border strips are deliberately not excluded "to be safe", because that would leave an unmodified strip
    /// on one side only, a straight seam. vsapi EnumWorldGenPass: at TerrainFeatures every neighbour chunk exists at the
    /// same or a higher pass, so its terrain and its river water exist; HeightGrid reads its heights from the neighbour's
    /// map chunk when the request carries none. The remaining order dependences at a border are: structures a neighbour
    /// places later in its own TerrainFeatures pass (not seen by the chunk generated first), neighbour blocks this mod
    /// already changed (its lake water reads as river water: hence LakeKeepOut), and neighbour heights no source could
    /// supply (unknown, see above). The structure check is order-free for structures already placed. The second guard
    /// is the Task 6 lake rules (TerrainGuard.LakeColumnAllowed: depth, rise above sea, slope), which stop a
    /// feature from forming on ground that is clearly not suitable even when this probe is blind.
    /// Any non-lava liquid counts as river water (over-exclusion, safe).
    /// </summary>
    public class ColumnExclusions
    {
        public const int Size = 32;
        /// <summary>
        /// Swamps stay this far from river water. 10 (was 6) so that it encloses the former flow-through lake zone
        /// (RiverLakeReach = RiverRadius - 1) plus the ground that walls it: swamps can never lower a river bank.
        /// </summary>
        public const int RiverRadius = 10;
        /// <summary>River-side ("flow-through") lakes reach at most this far from river water (WaterRules / LakePlanner).</summary>
        public const int RiverLakeReach = RiverRadius - 1;
        /// <summary>How far above the terrain height to look for river water.</summary>
        public const int RiverScanHeight = 24;

        public bool[] NearStructure = new bool[Size * Size];
        public bool[] NearOcean = new bool[Size * Size];
        /// <summary>Highest river water Y within RiverRadius of the column, or -1 when no river water is nearby.</summary>
        public int[] RiverSurfaceY = NewMinusOne(Size * Size);

        static int[] NewMinusOne(int n) { var a = new int[n]; Array.Fill(a, -1); return a; }

        public bool NearRiver(int index) => RiverSurfaceY[index] >= 0;

        /// <summary>Largest FadeMargin the swamp lock window uses (SwampPass clamps FadeMargin to this).</summary>
        public const int MaxSwampFade = 16;

        /// <summary>
        /// Radius of the raw water scan kept in <see cref="WaterTopWide"/>: RiverRadius + MaxSwampFade + 1 (fix round 1,
        /// finding 5a). The swamp lock window reaches -(FadeMargin+1) and needs the river level there, which looks
        /// RiverRadius further; the lake basin window (WaterRules.BasinSpan) needs water tops out to -(span+1). With this
        /// radius every such query is computed from the same columns the owning chunk scans, so both chunks agree.
        /// Only the inner ring (RiverRadius + 1) is scanned eagerly; columns further out are scanned on first use.
        /// </summary>
        public const int WideRadius = RiverRadius + MaxSwampFade + 1 + SwampSmoothRadius;

        /// <summary>The swamp's height smoothing reaches this far (SwampPass.SmoothRadius); the lock window grows by it.</summary>
        public const int SwampSmoothRadius = 3;

        /// <summary>Radius scanned eagerly in Build (the 1-block border ring's river level needs RiverRadius + 1).</summary>
        public const int EagerRadius = RiverRadius + 1;

        /// <summary>Marker in <see cref="WaterTopWide"/> for a column not scanned yet (lazy scan).</summary>
        public const int NotScanned = -2;

        /// <summary>
        /// Raw water scan (Task 6): existing water top Y per column, -1 = none, local coordinates -WideRadius..31+WideRadius,
        /// side 32 + 2*WideRadius, index (z+WideRadius)*side + (x+WideRadius). Null when built by Compose alone (tests).
        /// Entries equal to NotScanned are filled through <see cref="Scanner"/> on first read.
        /// </summary>
        public int[] WaterTopWide;

        /// <summary>Lazy water scan of one local column (Build sets it; null in tests, where NotScanned reads as -1).</summary>
        public System.Func<int, int, int> Scanner; // qualified: Vintagestory.API.Common also declares Func<,,> (CS0104)

        /// <summary>Structure rectangles (without margin) that reach this chunk column or its surroundings, as passed to Compose.</summary>
        public List<StructureRect> Structures = new List<StructureRect>();

        /// <summary>Existing (river) water top Y at local (lx, lz) in -WideRadius..31+WideRadius, -1 when none or unknown.</summary>
        public int WaterTopAt(int lx, int lz)
        {
            if (WaterTopWide == null) return -1;
            int side = Size + 2 * WideRadius;
            int ix = lx + WideRadius, iz = lz + WideRadius;
            if (ix < 0 || iz < 0 || ix >= side || iz >= side || WaterTopWide.Length != side * side) return -1;
            int i = iz * side + ix;
            int v = WaterTopWide[i];
            if (v == NotScanned)
            {
                v = -1;
                if (Scanner != null)
                {
                    try { v = Scanner(lx, lz); } catch (Exception) { v = -1; }
                }
                WaterTopWide[i] = v;
            }
            return v;
        }

        /// <summary>
        /// River surface Y (max water top within RiverRadius, Euclidean) at local (lx, lz): the own 32x32 columns return
        /// RiverSurfaceY; other columns are spread from WaterTopWide, which is exact out to WideRadius - RiverRadius
        /// (= MaxSwampFade + 1) blocks beyond the chunk: the whole swamp lock window. Further out the disc is clipped
        /// to the scanned area (may under-report). -1 = no river water nearby.
        /// </summary>
        public int RiverLevelAt(int lx, int lz)
        {
            if (lx >= 0 && lx < Size && lz >= 0 && lz < Size) return RiverSurfaceY[lz * Size + lx];
            if (WaterTopWide == null) return -1;
            int best = -1, r2 = RiverRadius * RiverRadius;
            for (int dz = -RiverRadius; dz <= RiverRadius; dz++)
                for (int dx = -RiverRadius; dx <= RiverRadius; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue;
                    int v = WaterTopAt(lx + dx, lz + dz);
                    if (v > best) best = v;
                }
            return best;
        }

        /// <summary>
        /// Water top of the NEAREST river column within radius (Euclidean; ties: the lowest top) at local (lx, lz), -1 when
        /// none. Lakes use this as their level next to a river (not the highest top within RiverRadius, which on a stepped
        /// river gave a level that matched none of the adjacent river columns, so the lake never joined the river). Exact
        /// for lx, lz in -(WideRadius - radius)..31 + (WideRadius - radius).
        /// </summary>
        public int NearestRiverLevelAt(int lx, int lz, int radius) => NearestRiverLevelAt(lx, lz, radius, int.MaxValue);

        /// <summary>As above, ignoring water whose top is above <paramref name="maxTop"/> (vanilla ponds, see LakePass.WorldOf).</summary>
        public int NearestRiverLevelAt(int lx, int lz, int radius, int maxTop)
        {
            if (WaterTopWide == null) return -1;
            int bestD2 = int.MaxValue, best = -1, r2 = radius * radius;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int d2 = dx * dx + dz * dz;
                    if (d2 > r2 || d2 > bestD2) continue;
                    int v = WaterTopAt(lx + dx, lz + dz);
                    if (v < 0 || v > maxTop) continue;
                    if (d2 < bestD2 || v < best) { bestD2 = d2; best = v; }
                }
            return best;
        }

        /// <summary>Radius of the existing-water share (<see cref="WaterShareAt"/>).</summary>
        public const int WaterShareRadius = 8;

        readonly Dictionary<long, double> shareCache = new Dictionary<long, double>();

        /// <summary>
        /// Share of the columns within WaterShareRadius (Euclidean) of local (lx, lz) that already hold water (WaterTopAt).
        /// Beside a river channel it stays well under half; on a Rivers LAKE's shore or island it is about half or more
        /// (flight test 3: River Features flooded Rivers lakes' islands and shores at the lake's level, leaving submerged mounds,
        /// plateaus and walls). Exact for lx, lz in -(WideRadius - radius)..31 + (WideRadius - radius). Cached.
        /// </summary>
        public double WaterShareAt(int lx, int lz)
        {
            if (WaterTopWide == null) return 0.0;
            long k = ((long)lx << 32) | (uint)lz;
            if (shareCache.TryGetValue(k, out double v)) return v;
            int r = WaterShareRadius, r2 = r * r, all = 0, wet = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue;
                    all++;
                    if (WaterTopAt(lx + dx, lz + dz) >= 0) wet++;
                }
            v = all == 0 ? 0.0 : wet / (double)all;
            shareCache[k] = v;
            return v;
        }

        /// <summary>Square rows of side 32+2*fromRadius trimmed to side 32+2*toRadius (centred). Pure.</summary>
        public static int[] Trim(int[] src, int fromRadius, int toRadius)
        {
            int fs = Size + 2 * fromRadius, ts = Size + 2 * toRadius, off = fromRadius - toRadius;
            var dst = new int[ts * ts];
            for (int z = 0; z < ts; z++)
                for (int x = 0; x < ts; x++)
                    dst[z * ts + x] = src[(z + off) * fs + (x + off)];
            return dst;
        }

        /// <summary>
        /// Structure margin at any WORLD column from <see cref="Structures"/> (same rule as NearStructure for own columns;
        /// used for columns outside the chunk, e.g. lake basins and the swamp lock window).
        /// </summary>
        /// <summary>Story structures only (planned before any chunk is generated, so the same for every chunk).</summary>
        public List<StructureRect> Story = new List<StructureRect>();
        public bool NearStoryAt(int wx, int wz, int margin)
        {
            foreach (var r in Story)
                if (RectNear(wx, wz, r.MinX, r.MinZ, r.MaxX, r.MaxZ, margin)) return true;
            return false;
        }

        public bool NearStructureAt(int wx, int wz, int margin)
        {
            foreach (var r in Structures)
                if (RectNear(wx, wz, r.MinX, r.MinZ, r.MaxX, r.MaxZ, margin)) return true;
            return false;
        }

        /// <summary>Story-structure pond keep-out query (WORLD coordinates), or null. Read through <see cref="PondBlockedAt"/> (cached).</summary>
        public System.Func<int, int, bool> PondBlockedFn;
        readonly Dictionary<long, bool> pondCache = new Dictionary<long, bool>();

        /// <summary>
        /// True where a story structure's "pond" keep-out (skipGenerationCategories) covers WORLD column (wx, wz): no lake water,
        /// no swamp. Same query vanilla GenPonds makes. Cached per column; a failing query counts as blocked (safe side).
        /// </summary>
        public bool PondBlockedAt(int wx, int wz)
        {
            if (PondBlockedFn == null) return false;
            long k = ((long)wx << 32) | (uint)wz;
            if (pondCache.TryGetValue(k, out bool v)) return v;
            try { v = PondBlockedFn(wx, wz); } catch (Exception) { v = true; }
            pondCache[k] = v;
            return v;
        }

        public bool Excluded(int index) => NearStructure[index] || NearOcean[index] || NearRiver(index);

        /// <summary>The winning reason for this column under the precedence in the class doc.</summary>
        public ExclusionKind Classify(int index)
        {
            if (NearStructure[index]) return ExclusionKind.Structure;
            if (NearRiver(index)) return ExclusionKind.River;
            if (NearOcean[index]) return ExclusionKind.Ocean;
            return ExclusionKind.None;
        }

        /// <summary>
        /// True when WORLD block (x, z) lies within margin blocks (square/Chebyshev distance, so the rectangle
        /// grows by margin on every side) of the structure rectangle minX..maxX, minZ..maxZ (inclusive block
        /// coordinates). Pure world coordinates: a structure ending at x=31 of one chunk is near x=32 of the next.
        /// </summary>
        public static bool RectNear(int x, int z, int minX, int minZ, int maxX, int maxZ, int margin)
        {
            if (margin < 0) margin = 0;
            long lo = (long)minX - margin, hi = (long)maxX + margin;
            long zlo = (long)minZ - margin, zhi = (long)maxZ + margin;
            return x >= lo && x <= hi && z >= zlo && z <= zhi;
        }

        /// <summary>
        /// Ocean flags for one chunk column: true when the terrain height is at or below seaLevel, or some
        /// column within buffer blocks (Euclidean) is. Unavailable heights (-1) are never ocean. buffer is
        /// capped at 32 (the neighbour data reaches one chunk out). Pure function of the heights.
        /// </summary>
        public static bool[] ComputeNearOcean(HeightGrid grid, int seaLevel, int buffer)
        {
            var result = new bool[Size * Size];
            if (buffer < 0) buffer = 0;
            if (buffer > Size) buffer = Size;

            // Ocean map of the 3x3 chunk block, built once; skip everything when there is no ocean in reach.
            const int Span = 3 * Size;
            var ocean = new bool[Span * Span];
            bool any = false;
            int lo = -buffer, hi = Size - 1 + buffer;
            for (int z = lo; z <= hi; z++)
                for (int x = lo; x <= hi; x++)
                {
                    int h = grid.Raw(x, z);
                    if (h != -1 && h <= seaLevel) { ocean[(z + Size) * Span + (x + Size)] = true; any = true; }
                }
            if (!any) return result;

            int r2 = buffer * buffer;
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                {
                    bool near = false;
                    for (int dz = -buffer; dz <= buffer && !near; dz++)
                        for (int dx = -buffer; dx <= buffer; dx++)
                        {
                            if (dx * dx + dz * dz > r2) continue;
                            if (ocean[(z + dz + Size) * Span + (x + dx + Size)]) { near = true; break; }
                        }
                    result[z * Size + x] = near;
                }
            return result;
        }

        /// <summary>
        /// Spreads river water heights: dest[z*32+x] = max of src over columns within radius (Euclidean) of
        /// (x, z), -1 if none has water. src covers local coordinates -radius..31+radius (side 32+2*radius,
        /// index (z+radius)*side + (x+radius)), -1 meaning no river water. Pure.
        /// </summary>
        public static int[] SpreadRiver(int[] src, int radius)
        {
            int side = Size + 2 * radius;
            var dest = NewMinusOne(Size * Size);
            int r2 = radius * radius;
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                {
                    int best = -1;
                    for (int dz = -radius; dz <= radius; dz++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            if (dx * dx + dz * dz > r2) continue;
                            int v = src[(z + dz + radius) * side + (x + dx + radius)];
                            if (v > best) best = v;
                        }
                    dest[z * Size + x] = best;
                }
            return dest;
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        /// <summary>
        /// Builds all three exclusion layers for the chunk column in req. Must be called while the
        /// worldgen block accessor is positioned on this column (the caller, TerrainWatersSystem, does BeginColumn).
        /// Rivers: UNVERIFIED which route. The Rivers mod's public metadata (a RiversApi type) was not
        /// inspected here and no distance query is assumed, so the in-chunk water scan is used; the choice is
        /// logged once per process at the first call.
        /// </summary>
        /// <param name="heights">The handler's grid (original heights; neighbours from the originals store, then their map chunks, then the request); null builds one from req alone.</param>
        /// <param name="placedOf">Placed-water flags of chunk column (cx, cz) (PlacedWater), or null; such water is this mod's, not a river.</param>
        /// <param name="storyRects">Footprints of story structures (placed later, in the Vegetation pass); protected like any structure.</param>
        /// <param name="pondBlocked">True where a structure provider (story structures) forbids ponds: lakes and swamps stay out.</param>
        public static ColumnExclusions Build(ICoreServerAPI api, IWorldGenBlockAccessor ba, IChunkColumnGenerateRequest req, ResolvedConfig cfg, int seaLevel, HeightGrid heights = null,
            System.Func<int, int, bool[]> placedOf = null, IList<StructureRect> storyRects = null, System.Func<int, int, bool> pondBlocked = null)
        {
            LogRouteOnce(api);

            int baseX = req.ChunkX * Size, baseZ = req.ChunkZ * Size;
            var grid = heights ?? HeightGrid.From(req);

            // --- 1. Structures: own region plus the 8 surrounding regions (a ruin just across a region border).
            var rects = new List<StructureRect>();
            var seen = new HashSet<IMapRegion>();
            void AddRegion(IMapRegion region)
            {
                if (region == null || !seen.Add(region)) return;
                var list = region.GeneratedStructures;
                if (list == null) return;
                GeneratedStructure[] snapshot;
                try { snapshot = list.ToArray(); } catch (Exception) { return; } // list may be appended to by other worldgen threads
                // Cuboidi.MaxX/MaxZ are EXCLUSIVE in 1.22.7 (Cuboidi.Contains tests x < MaxX). The rectangle keeps MaxX/MaxZ as
                // its inclusive max on purpose, so on the max sides the margin is effectively RuinMargin + 1 (one block more
                // protection; behaviour kept, documented here and in the README).
                foreach (var s in snapshot)
                {
                    // This mod's own footprints (StructureGuard) are there for OTHER generators; never a reason to keep out.
                    if (s?.Location == null || StructureGuard.IsOwn(s.Code)) continue;
                    rects.Add(new StructureRect(s.Location.MinX, s.Location.MinZ, s.Location.MaxX, s.Location.MaxZ));
                }
            }
            if (storyRects != null) rects.AddRange(storyRects);
            try { AddRegion(req.Chunks?[0]?.MapChunk?.MapRegion); } catch (Exception) { }
            // Neighbour regions through the worldgen accessor (IBlockAccessor.RegionSize / GetMapRegion, as vanilla
            // GenStoryStructures does), not api.WorldManager. GetMapRegion returns null for a region that is not loaded.
            int regionSize = 0;
            try { regionSize = ba.RegionSize; } catch (Exception) { regionSize = 0; }
            if (regionSize > 0)
            {
                int rx = FloorDiv(baseX, regionSize), rz = FloorDiv(baseZ, regionSize);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        try { AddRegion(ba.GetMapRegion(rx + dx, rz + dz)); } catch (Exception) { }
                    }
            }

            // --- 3. River water: scan columns -EagerRadius..31+EagerRadius now for liquid above the terrain height
            // (one column beyond RiverRadius so the border ring's river level is exact); the rest of the wide square
            // (-WideRadius..31+WideRadius) is scanned on first use through ex.Scanner (same rule, same accessor).
            int side = Size + 2 * WideRadius;
            var waterTop = new int[side * side];
            Array.Fill(waterTop, NotScanned);
            int maxY = Math.Max(1, cfg.WorldHeight) - 1;
            bool hiddenWater = false; // water found BELOW the terrain height map, which the scan (starting at it) cannot see
            var placedCache = new Dictionary<long, bool[]>();
            bool PlacedByMod(int wx, int wz)
            {
                if (placedOf == null) return false;
                int cx = FloorDiv(wx, Size), cz = FloorDiv(wz, Size);
                long k = ((long)cx << 32) | (uint)cz;
                if (!placedCache.TryGetValue(k, out var f))
                {
                    try { f = placedOf(cx, cz); } catch (Exception) { f = null; }
                    placedCache[k] = f;
                }
                if (f == null || f.Length != Size * Size) return false;
                return f[(wz - cz * Size) * Size + (wx - cx * Size)];
            }
            int ScanColumn(int lx, int lz)
            {
                int h = grid.Raw(lx, lz);
                // Columns at or below sea level hold sea/ocean water: that is the Ocean rule's job, not a river.
                if (h == -1 || h <= seaLevel) return -1;
                try
                {
                    bool inside = lx >= 0 && lx < Size && lz >= 0 && lz < Size;
                    int wx = baseX + lx, wz = baseZ + lz;
                    // Lake water and swamp pockets this mod put into an already generated neighbour are not river water
                    // (otherwise this chunk locks around them and leaves a wall along the border).
                    if (!inside && PlacedByMod(wx, wz)) return -1;
                    // Outside this column: only read the neighbour chunk if the accessor has it (it should, see the class doc;
                    // UNVERIFIED that null means "not available").
                    if (!inside && ba.GetChunkAtBlockPos(wx, h, wz) == null) return -1;
                    int top = -1;
                    int yEnd = Math.Min(h + RiverScanHeight, maxY);
                    // Starts at h itself: UNVERIFIED whether the height map points at the river bed or at the water surface.
                    for (int y = h; y <= yEnd; y++)
                    {
                        if (IsWater(ba.GetBlock(wx, y, wz, BlockLayersAccess.Fluid))) top = y;
                        else if (top >= 0) break;
                    }
                    // Frozen rivers and lakes (flight test 3): Rivers caps cold water with lake ice, so the terrain height map
                    // points at the ice and the water lies just below it. Water directly under the surface block (1..2 down)
                    // counts as river water at its own top.
                    if (top < 0)
                        for (int y = h - 1; y >= Math.Max(seaLevel + 1, h - 2); y--)
                            if (IsWater(ba.GetBlock(wx, y, wz, BlockLayersAccess.Fluid))) { top = y; break; }
                    if (top < 0 && inside && (lx & 3) == 0 && (lz & 3) == 0)
                        for (int y = Math.Max(seaLevel + 1, h - 8); y < h; y++)
                            if (IsWater(ba.GetBlock(wx, y, wz, BlockLayersAccess.Fluid))) { hiddenWater = true; break; }
                    return top;
                }
                catch (Exception) { return -1; /* a failing column simply gets no river flag */ }
            }
            for (int lz = -EagerRadius; lz < Size + EagerRadius; lz++)
                for (int lx = -EagerRadius; lx < Size + EagerRadius; lx++)
                    waterTop[(lz + WideRadius) * side + (lx + WideRadius)] = ScanColumn(lx, lz);

            var ex = Compose(rects, grid, Trim(waterTop, WideRadius, RiverRadius), baseX, baseZ, cfg.RuinMargin, seaLevel, cfg.OceanBuffer);
            ex.WaterTopWide = waterTop;
            ex.PondBlockedFn = pondBlocked;
            if (storyRects != null) ex.Story.AddRange(storyRects);
            // The lazy scanner must only be called while this column is being generated (the accessor is positioned on it):
            // LakePass and SwampPass run inside the same handler call, which is the only place it is used.
            ex.Scanner = ScanColumn;

            // Detects a wrong WorldGenTerrainHeightMap / IsLiquid assumption: liquid exists below the height map
            // above sea level, yet no column of this chunk got a river flag. Logged once per process.
            WarnIfWaterButNoRiver(api, hiddenWater, ex);
            return ex;
        }

        /// <summary>
        /// One structure's world-block rectangle as RectNear reads it (min and max both inclusive; pure, game-API-free).
        /// Build fills it from Cuboidi, whose Max is exclusive, so the max sides are one block wider than the structure.
        /// </summary>
        public readonly record struct StructureRect(int MinX, int MinZ, int MaxX, int MaxZ);

        /// <summary>
        /// The pure composition of all three layers for the chunk column whose first world block is
        /// (baseX, baseZ). No game API: inputs are the structure rectangles (margin is applied here), the
        /// height grid, waterTop (side 32+2*RiverRadius, index (z+R)*side+(x+R), local coordinates, -1 = no
        /// river water at that column) and config values. Build() calls this, and the tests drive it directly.
        /// </summary>
        public static ColumnExclusions Compose(IList<StructureRect> rects, HeightGrid grid, int[] waterTop, int baseX, int baseZ, int ruinMargin, int seaLevel, int oceanBuffer)
        {
            var ex = new ColumnExclusions();
            if (rects != null) ex.Structures.AddRange(rects);
            int m = ruinMargin;
            if (rects != null)
                foreach (var r in rects)
                {
                    // Skip structures whose margin-expanded rectangle cannot overlap this chunk column.
                    if (!(r.MinX - m <= baseX + Size - 1 && r.MaxX + m >= baseX && r.MinZ - m <= baseZ + Size - 1 && r.MaxZ + m >= baseZ)) continue;
                    for (int z = 0; z < Size; z++)
                        for (int x = 0; x < Size; x++)
                            if (RectNear(baseX + x, baseZ + z, r.MinX, r.MinZ, r.MaxX, r.MaxZ, m)) ex.NearStructure[z * Size + x] = true;
                }
            ex.NearOcean = ComputeNearOcean(grid, seaLevel, oceanBuffer);
            if (waterTop != null && waterTop.Length == (Size + 2 * RiverRadius) * (Size + 2 * RiverRadius))
                ex.RiverSurfaceY = SpreadRiver(waterTop, RiverRadius);
            return ex;
        }

        static int warnedNoRiver;
        static void WarnIfWaterButNoRiver(ICoreServerAPI api, bool hiddenWater, ColumnExclusions ex)
        {
            if (!hiddenWater) return;
            foreach (int v in ex.RiverSurfaceY) if (v >= 0) return;
            if (System.Threading.Interlocked.Exchange(ref warnedNoRiver, 1) != 0) return;
            api.Logger.Warning("[RiverFeatures] Liquid found below the terrain height map but no river flag was set in this chunk; the WorldGenTerrainHeightMap / IsLiquid assumptions may be wrong (logged once).");
        }

        /// <summary>Any liquid except lava. UNVERIFIED: that water blocks report a LiquidCode that does not start with "lava".</summary>
        static bool IsWater(Block b)
        {
            if (b == null || !b.IsLiquid()) return false;
            string code = b.LiquidCode;
            return code == null || !code.StartsWith("lava", StringComparison.Ordinal);
        }

        static int routeLogged;
        static void LogRouteOnce(ICoreServerAPI api)
        {
            if (System.Threading.Interlocked.Exchange(ref routeLogged, 1) != 0) return;
            api.Logger.Notification("[RiverFeatures] River exclusion: in-chunk water scan (radius {0}); the Rivers API distance query was not used (not inspected).", RiverRadius);
        }
    }
}
