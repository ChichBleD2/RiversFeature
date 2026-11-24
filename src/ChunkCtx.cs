using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    /// <summary>
    /// Everything one chunk-column handler call needs. Passes (lakes, swamps) only PLAN block
    /// changes through <see cref="Set"/>; <see cref="Commit"/> writes them after every pass has planned, so an
    /// exception while planning leaves the column untouched.
    /// </summary>
    public class ChunkCtx
    {
        public const int Size = 32;

        public IChunkColumnGenerateRequest Req;
        public IWorldGenBlockAccessor Ba;
        public HeightGrid Heights;
        public ColumnExclusions Excl;
        public ResolvedConfig Cfg;
        public WorldScale Scale;
        public long Seed;
        public FeatureMask LakeMask;
        public FeatureMask SwampMask;
        public double LakeThreshold;
        public double SwampThreshold;

        // --- additions beyond the plan's list
        public ICoreServerAPI Api;
        /// <summary>Clumpy noise deciding which swamp columns hold a shallow water pocket (about SwampWaterCoverage of them).</summary>
        public FeatureMask SwampWaterMask;
        public double SwampWaterThreshold;
        /// <summary>Gully noise (Task 7): sampled at WORLD coordinates, seeded from the world seed + SeedOffset.</summary>
        public BlockCodes Blocks;
        /// <summary>Active lake features (mask covers the centre) whose disc can reach this chunk column or its swamp window.</summary>
        public List<FeatureCell> LakeFeatures = new List<FeatureCell>();
        public int BaseX, BaseZ;
        /// <summary>Own columns changed by a pass (index z*32+x). Later passes (swamps, delta banks) skip them.</summary>
        public readonly bool[] Touched = new bool[Size * Size];
        /// <summary>Own columns inside an active lake disc plus WaterRules.LakeKeepOut blocks. Swamps and delta banks never act
        /// here, so they cannot change the ground that walls a lake in the neighbouring chunk.</summary>
        public readonly bool[] NearLake = new bool[Size * Size];
        public readonly GenStats Stats = new GenStats();

        /// <summary>Per-world store of original heights of changed chunk columns (TerrainWatersSystem); null = do not remember.</summary>
        public OriginalHeights Originals;

        /// <summary>Map-chunk mod data key holding this chunk column's original terrain heights (OriginalHeights.Encode).</summary>
        public const string OriginalHeightsKey = "riverfeatures:origheights";

        /// <summary>Own columns that get water from this mod (lake or swamp pocket); persisted so later neighbours' river scans ignore it.</summary>
        public readonly bool[] WaterPlaced = new bool[Size * Size];
        /// <summary>Own columns changed by the lake pass (flooded or shore-eased): registered as a "riverfeatures:lake" footprint.</summary>
        public readonly bool[] LakeEdit = new bool[Size * Size];
        /// <summary>Per-world store of WaterPlaced flags (TerrainWatersSystem); null = do not remember.</summary>
        public PlacedWater PlacedStore;
        /// <summary>Map-chunk mod data key holding <see cref="WaterPlaced"/> (PlacedWater.Encode).</summary>
        public const string PlacedWaterKey = "riverfeatures:water";
        /// <summary>River-side lake mask (flow-through lakes); null = off.</summary>
        public FeatureMask RiverLakeMask;
        public double RiverLakeThreshold;
        /// <summary>
        /// Current terrain height of a LOCAL column outside this chunk when its chunk column has already been through this
        /// mod (seam fitting, WaterRules.SeamFit), else -1. Null = unknown everywhere.
        /// </summary>
        public System.Func<int, int, int> NeighbourNow;
        /// <summary>
        /// For a LOCAL column outside this chunk: -1 = its chunk column is not finished by this mod yet, 0 = finished and no
        /// water of this mod there, 1 = finished with this mod's water (lake or pocket). Null = unknown everywhere.
        /// </summary>
        public System.Func<int, int, int> NeighbourWater;
        /// <summary>The lake seam view (LakeSeamDiag) a finished neighbour chunk column stored, or null.</summary>
        public System.Func<int, int, byte[]> LakeViewOf;
        /// <summary>This chunk column's lake seam view (LakeSeamDiag), stored in the map chunk at Commit; null = none.</summary>
        public byte[] LakeView;
        public const string LakeViewKey = "riverfeatures:lakeview";
        /// <summary>Map-chunk mod data key marking a chunk column this mod has finished (changed or not).</summary>
        public const string DoneKey = "riverfeatures:done";

        readonly List<BlockEdit> edits = new List<BlockEdit>();
        readonly int[] newTerrain = NewMinusOne(), newRain = NewMinusOne(), oldTerrain = NewMinusOne();
        static int[] NewMinusOne() { var a = new int[Size * Size]; Array.Fill(a, -1); return a; }

        /// <summary>
        /// Records that own column (lx, lz) gets a new top: terrainY is its new top solid ground (the lake bed, the swamp
        /// ground under a pocket), rainY the new top rain-impermeable block (water surface
        /// or ground). Commit writes them into WorldGenTerrainHeightMap / RainHeightMap so Vegetation uses the new ground.
        /// </summary>
        public void SetTop(int lx, int lz, int oldTerrainY, int terrainY, int rainY)
        {
            if (lx < 0 || lx >= Size || lz < 0 || lz >= Size || terrainY < 0 || (terrainY == oldTerrainY && rainY <= oldTerrainY)) return;
            int i = lz * Size + lx;
            oldTerrain[i] = oldTerrainY;
            newTerrain[i] = terrainY;
            newRain[i] = Math.Max(terrainY, rainY);
        }

        /// <summary>Plans one block write at WORLD position (x, y, z). blockId 0 = air (clears solid and fluid layers).</summary>
        public void Set(int x, int y, int z, int blockId)
        {
            if (y < 1 || y > Cfg.WorldHeight - 2) return;
            edits.Add(new BlockEdit(x, y, z, blockId));
        }

        public int PlannedEdits => edits.Count;

        /// <summary>
        /// Plans air at (x, plantY, z) when the solid layer there holds a loose plant (PlantLayer.IsLoose: tall grass and
        /// the like, which the block-layers pass places at terrain height + 1, vsapi EnumWorldGenPass). Call it FIRST for
        /// a column whose ground is lowered or flooded, before that column's other edits: Commit writes in order, so a later
        /// planned block at the same position (water, swamp fill, a swamp plant) replaces the air. A failed read plans
        /// nothing. Returns true when an edit was planned.
        /// </summary>
        public bool ClearPlantAt(int x, int plantY, int z)
        {
            try
            {
                var b = Ba.GetBlock(x, plantY, z, BlockLayersAccess.Solid);
                if (b == null || !PlantLayer.IsLoose(b.BlockId, b.BlockMaterial == EnumBlockMaterial.Plant, b.Replaceable)) return false;
            }
            catch (Exception) { return false; }
            Set(x, plantY, z, 0);
            Stats.PlantsCleared++;
            return true;
        }

        /// <summary>
        /// Writes every planned edit with the worldgen block accessor. SetBlock with a fluid block puts it in the fluid
        /// layer and empties the solid layer (vsapi IBlockAccessor.SetBlock doc); id 0 clears both layers.
        /// When any column top changed: first the ORIGINAL own heights are remembered (OriginalHeights cache, and the map
        /// chunk's mod data so it survives a restart; first write wins), then the blocks are written, then
        /// WorldGenTerrainHeightMap and RainHeightMap of the changed columns are set to the new tops (vsapi: the terrain map
        /// is "the last block before the first air block before world gen pass Vegetation", so Vegetation plants on it).
        /// Neighbours generated later read this column's original heights from the cache / mod data (HeightGrid prefers
        /// them), so every rule still sees the original terrain on both sides of a border.
        /// UNVERIFIED: that RainHeightMap should hold the water surface for lake/pocket columns (vsapi: last block that is
        /// not rain permeable; water assumed not permeable); that writing the map arrays in place during TerrainFeatures is
        /// honoured (they are get-only arrays, written in place); a failure there is logged once and the blocks stay.
        /// </summary>
        public void Commit()
        {
            bool tops = false;
            for (int i = 0; i < newTerrain.Length; i++) if (newTerrain[i] >= 0) { tops = true; break; }
            IMapChunk mc = null;
            try { mc = Req?.Chunks?[0]?.MapChunk; } catch (Exception) { mc = null; }
            if (tops) RememberOriginals(mc);
            RememberWater(mc);
            RememberPlan(mc);
            try { if (mc != null && LakeView != null && mc.GetModdata(LakeViewKey) == null) mc.SetModdata(LakeViewKey, LakeView); } catch (Exception) { }

            var pos = new BlockPos(0, 0, 0);
            // Flight test 8 (/rfprobe at 42,208): SetBlock(id, pos) with a water block put the water into the SOLID layer of
            // the worldgen accessor, where the game renders faces between it and Rivers' fluid-layer water (zigzag water
            // walls) and Realistic Water drains it oddly. Liquids now go to the fluid layer with the solid layer cleared;
            // air clears both layers; solid blocks go to the solid layer and clear any fluid there.
            foreach (var e in edits)
            {
                pos.Set(e.X, e.Y, e.Z);
                if (e.BlockId == 0)
                {
                    Ba.SetBlock(0, pos, BlockLayersAccess.Solid);
                    Ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
                }
                else if (IsLiquid(e.BlockId))
                {
                    Ba.SetBlock(0, pos, BlockLayersAccess.Solid);
                    Ba.SetBlock(e.BlockId, pos, BlockLayersAccess.Fluid);
                }
                else
                {
                    Ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
                    Ba.SetBlock(e.BlockId, pos, BlockLayersAccess.Solid);
                }
            }
            edits.Clear();

            if (tops && mc != null) ApplyTops(mc);
            if (tops) RegisterFootprints();
            try { if (mc != null && mc.GetModdata(DoneKey) == null) mc.SetModdata(DoneKey, new byte[] { 1 }); }
            catch (Exception) { /* seam fitting then treats this column as not generated yet: the old behaviour */ }
        }

        /// <summary>
        /// Registers this chunk column's changed ground in IMapRegion.GeneratedStructures (StructureGuard), in the own region and
        /// the regions of the 8 neighbouring chunk columns, so a structure, village or trader camp that a LATER chunk places
        /// refuses to overlap a lake (it would be put on the lake bed and submerged) or deeply lowered ground (sheared).
        /// Failures are logged once; blocks are already written.
        /// </summary>
        void RegisterFootprints()
        {
            try
            {
                var lakeLow = new int[Size * Size];
                var high = new int[Size * Size];
                var cut = new bool[Size * Size];
                bool anyLake = false, anyCut = false;
                for (int i = 0; i < Size * Size; i++)
                {
                    if (newTerrain[i] < 0) continue;
                    lakeLow[i] = newTerrain[i];
                    high[i] = Math.Max(oldTerrain[i], newRain[i]) + 1;
                    if (LakeEdit[i]) anyLake = true;
                    else if (oldTerrain[i] - newTerrain[i] >= StructureGuard.MinRegisteredCut) { cut[i] = true; anyCut = true; }
                }
                if (!anyLake && !anyCut) return;
                var boxes = new List<(string code, StructureGuard.Box box)>();
                if (anyLake) foreach (var b in StructureGuard.Tiles(LakeEdit, lakeLow, high)) boxes.Add((StructureGuard.LakeCode, b));
                if (anyCut) foreach (var b in StructureGuard.Tiles(cut, lakeLow, high)) boxes.Add((StructureGuard.CutCode, b));

                int regionSize = Ba.RegionSize;
                if (regionSize <= 0 || Req == null) return;
                foreach (var (rx, rz) in StructureGuard.RegionsAround(Req.ChunkX, Req.ChunkZ, Math.Max(1, regionSize / Size)))
                {
                    IMapRegion region = null;
                    try { region = Ba.GetMapRegion(rx, rz); } catch (Exception) { region = null; }
                    if (region == null) continue;
                    foreach (var (code, b) in boxes)
                        region.AddGeneratedStructure(new GeneratedStructure
                        {
                            Code = code,
                            Group = StructureGuard.Group,
                            // Cuboidi max is exclusive in 1.22.7 (Cuboidi.Contains uses <), as for vanilla structure boxes.
                            Location = new Cuboidi(BaseX + b.X1, b.Y1, BaseZ + b.Z1, BaseX + b.X2 + 1, b.Y2 + 1, BaseZ + b.Z2 + 1),
                        });
                    Stats.FootprintsRegistered += boxes.Count;
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref footprintWarned, 1) == 0)
                    Api?.Logger.Warning("[RiverFeatures] {0}", "could not register lake/cut footprints as generated structures (" + e.Message + "); structures placed later by neighbouring chunks may overlap lakes (logged once).");
            }
        }

        static int footprintWarned;

        void RememberOriginals(IMapChunk mc)
        {
            var orig = Heights?.OwnCopy();
            if (orig == null || Req == null) return;
            Originals?.Remember(Req.ChunkX, Req.ChunkZ, orig);
            try
            {
                if (mc != null && mc.GetModdata(OriginalHeightsKey) == null) mc.SetModdata(OriginalHeightsKey, OriginalHeights.Encode(orig));
            }
            catch (Exception e)
            {
                // The cache still covers this session; after a restart neighbours fall back to the (lowered) map heights.
                if (Interlocked.Exchange(ref modDataWarned, 1) == 0)
                    Api?.Logger.Warning("[RiverFeatures] {0}", "could not store original heights in the map chunk's mod data (" + e.Message + "); they are kept in memory for this session only, so after a restart chunks next to changed ones may show seams (logged once).");
            }
        }
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> liquid = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();

        /// <summary>Whether a block id is a liquid (belongs in the fluid layer). Cached per id.</summary>
        bool IsLiquid(int id) => liquid.GetOrAdd(id, k =>
        {
            try { var b = Api?.World?.GetBlock(k); return b != null && (b.ForFluidsLayer || b.IsLiquid()); }
            catch (Exception) { return false; }
        });

        /// <summary>Map-chunk mod data key holding the planned new top and water surface of every changed column (diagnostics, /rfprobe).</summary>
        public const string PlanKey = "riverfeatures:plan";

        /// <summary>Stores newTerrain / newRain (0xFFFF = unchanged) so /rfprobe can compare the plan with what the world holds now.</summary>
        void RememberPlan(IMapChunk mc)
        {
            try
            {
                if (mc == null || mc.GetModdata(PlanKey) != null) return;
                bool any = false;
                var b = new byte[Size * Size * 4];
                for (int i = 0; i < Size * Size; i++)
                {
                    int t = newTerrain[i] < 0 ? 0xFFFF : newTerrain[i], r = newRain[i] < 0 ? 0xFFFF : newRain[i];
                    if (newTerrain[i] >= 0) any = true;
                    b[4 * i] = (byte)t; b[4 * i + 1] = (byte)(t >> 8); b[4 * i + 2] = (byte)r; b[4 * i + 3] = (byte)(r >> 8);
                }
                if (any) mc.SetModdata(PlanKey, b);
            }
            catch (Exception) { }
        }

        void RememberWater(IMapChunk mc)
        {
            if (Req == null || Array.IndexOf(WaterPlaced, true) < 0) return;
            PlacedStore?.Remember(Req.ChunkX, Req.ChunkZ, WaterPlaced);
            try
            {
                if (mc != null && mc.GetModdata(PlacedWaterKey) == null) mc.SetModdata(PlacedWaterKey, PlacedWater.Encode(WaterPlaced));
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref modDataWarned, 1) == 0)
                    Api?.Logger.Warning("[RiverFeatures] {0}", "could not store placed-water flags in the map chunk's mod data (" + e.Message + "); after a restart neighbours may read this mod's water as river water (logged once).");
            }
        }

        static int mapWarned, modDataWarned;

        void ApplyTops(IMapChunk mc)
        {
            try
            {
                var th = mc.WorldGenTerrainHeightMap;
                var rh = mc.RainHeightMap;
                int top = Math.Max(1, Cfg.WorldHeight - 1);
                for (int i = 0; i < newTerrain.Length; i++)
                {
                    if (newTerrain[i] < 0) continue;
                    if (th != null && th.Length == Size * Size) th[i] = (ushort)Math.Clamp(newTerrain[i], 0, top);
                    if (rh != null && rh.Length == Size * Size)
                        rh[i] = (ushort)Math.Clamp(OriginalHeights.RainAfter(rh[i], oldTerrain[i], newRain[i]), 0, top);
                }
                mc.MarkDirty();
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref mapWarned, 1) == 0)
                    Api?.Logger.Warning("[RiverFeatures] could not update the height maps ({0}); plants may float over changed ground (logged once).", e.Message);
            }
        }
    }

    public readonly record struct BlockEdit(int X, int Y, int Z, int BlockId);

    /// <summary>The plant-layer decision (pure).</summary>
    public static class PlantLayer
    {
        /// <summary>vsapi Block.Replaceable: "any block with replaceable value above 5000 will be washed away by water" (tallgrass 6000).</summary>
        public const int WashAway = 5000;

        /// <summary>
        /// Whether the block above a lowered or flooded column's old surface is a loose plant to clear: not air (id 0) and
        /// either of plant material (EnumBlockMaterial.Plant) or Replaceable &gt;= WashAway. Read from the SOLID layer only,
        /// so water never counts. Checked against vsapi 1.22.7 Block.cs (Replaceable, BlockMaterial).
        /// </summary>
        public static bool IsLoose(int blockId, bool plantMaterial, int replaceable) =>
            blockId > 0 && (plantMaterial || replaceable >= WashAway);
    }

    /// <summary>Generation counters; Add is thread-safe so per-chunk counts can be folded into a per-world total.</summary>
    public class GenStats
    {
        public long FootprintsRegistered, FlowCleared;
        public long WaterSealed;
        public long SkipSmall;
        public long DeltaColumns, DeltaPlants;
        public long Chunks, LakeColumns, RiverLakeColumns, ShoreShaved, SwampColumns, SwampPockets, SeamFitted, SeamMismatch, Plants, PlantsCleared, SkipStructure, SkipOcean, SkipRiver, SkipProtected, SkipLeak;
        /// <summary>Chunk columns whose handler threw (left as generated); only the first is logged.</summary>
        public long Errors;

        public void Add(GenStats o)
        {
            Interlocked.Add(ref Chunks, o.Chunks);
            Interlocked.Add(ref LakeColumns, o.LakeColumns);
            Interlocked.Add(ref RiverLakeColumns, o.RiverLakeColumns);
            Interlocked.Add(ref ShoreShaved, o.ShoreShaved);
            Interlocked.Add(ref FootprintsRegistered, o.FootprintsRegistered);
            Interlocked.Add(ref SwampColumns, o.SwampColumns);
            Interlocked.Add(ref SwampPockets, o.SwampPockets);
            Interlocked.Add(ref SeamFitted, o.SeamFitted);
            Interlocked.Add(ref SeamMismatch, o.SeamMismatch);
            Interlocked.Add(ref WaterSealed, o.WaterSealed);
            Interlocked.Add(ref SkipSmall, o.SkipSmall);
            Interlocked.Add(ref DeltaColumns, o.DeltaColumns);
            Interlocked.Add(ref DeltaPlants, o.DeltaPlants);
            Interlocked.Add(ref Plants, o.Plants);
            Interlocked.Add(ref PlantsCleared, o.PlantsCleared);
            Interlocked.Add(ref SkipStructure, o.SkipStructure);
            Interlocked.Add(ref SkipOcean, o.SkipOcean);
            Interlocked.Add(ref SkipRiver, o.SkipRiver);
            Interlocked.Add(ref SkipProtected, o.SkipProtected);
            Interlocked.Add(ref SkipLeak, o.SkipLeak);
            Interlocked.Add(ref Errors, o.Errors);
        }

        /// <summary>Counts one skipped feature column by the ColumnExclusions precedence (Structure &gt; River &gt; Ocean).</summary>
        public void CountExclusion(ExclusionKind k)
        {
            if (k == ExclusionKind.Structure) SkipStructure++;
            else if (k == ExclusionKind.River) SkipRiver++;
            else if (k == ExclusionKind.Ocean) SkipOcean++;
        }

        public override string ToString() =>
            $"chunk columns {Chunks}, lake columns {LakeColumns} ({RiverLakeColumns} river-side/flow-through, {ShoreShaved} shore columns eased), swamp columns {SwampColumns} ({SwampPockets} water pockets, {Plants} plants, {SeamFitted} fitted to finished neighbour chunks), blocks sealed beside or under placed water {WaterSealed}, mini-lake columns dropped {SkipSmall}, lake columns decided differently from a finished neighbour {SeamMismatch} (see the lake seam mismatch lines), loose plants cleared {PlantsCleared}, delta bank columns {DeltaColumns} ({DeltaPlants} plants); " +
            $"feature columns skipped: structure or story keep-out {SkipStructure}, ocean {SkipOcean}, river {SkipRiver}, protected (height/slope/rim) {SkipProtected}, would leak or leave a bank {SkipLeak}; errors {Errors}; footprints registered for later structures {FootprintsRegistered}; Rivers flow cleared on still or isolated water columns {FlowCleared}";
    }

    /// <summary>
    /// Block ids looked up by code once per world, with fallbacks; a missing code is never an error (logged once, skipped).
    /// Code sources: "water-still-7" and the "tallplant-&lt;type&gt;-land-harvested-free" / "tallgrass-tall-free" patterns
    /// appear in vssurvivalmod source (BlockReeds.cs, ItemCattailRoots.cs, BlockSoil.cs); "muddygravel" and
    /// "soil-low-normal" too (BehaviorFiniteSpreadingLiquid.cs, BlockForestFloor.cs); "gravel-&lt;rock&gt;" is the pattern
    /// the Rivers mod builds from rockstrata. UNVERIFIED (no game asset JSON available): "peat-none" and "sand-&lt;rock&gt;".
    /// </summary>
    public sealed class BlockCodes
    {
        public static readonly string[] WaterCodes = { "water-still-7" };
        /// <summary>Swamp surface, first existing wins: peat, then muddy gravel, then plain low-fertility soil.</summary>
        public static readonly string[] SwampSurfaceCodes = { "peat-none", "muddygravel", "soil-low-normal" };

        readonly ICoreServerAPI api;
        readonly ConcurrentDictionary<string, int> ids = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        public int WaterId { get; private set; }
        public int SwampSurfaceId { get; private set; }
        public string SwampSurfaceCode { get; private set; }
        /// <summary>Peat with a thin grass cover (peat-verysparse) when the surface is peat, else the surface block.</summary>
        public int SwampGrassId { get; private set; }
        /// <summary>Grassy low-fertility soil (soil-low-normal), 0 when unknown: the top over peat where the old top was not soil.</summary>
        public int GrassSoilId { get; private set; }
        public int[] PlantIds { get; private set; } = Array.Empty<int>();
        public List<string> PlantCodes { get; private set; } = new List<string>();

        BlockCodes(ICoreServerAPI api) { this.api = api; }

        public static BlockCodes Resolve(ICoreServerAPI api, ResolvedConfig cfg)
        {
            var b = new BlockCodes(api);
            string w = WaterRules.FirstKnown(WaterCodes, c => b.IdOf(c, false) > 0);
            b.WaterId = w == null ? 0 : b.IdOf(w, false);
            if (b.WaterId == 0) api.Logger.Warning("[RiverFeatures] no water block found (tried {0}); lakes and swamp pockets are disabled.", string.Join(", ", WaterCodes));
            b.SwampSurfaceCode = WaterRules.FirstKnown(SwampSurfaceCodes, c => b.IdOf(c, false) > 0);
            b.SwampSurfaceId = b.SwampSurfaceCode == null ? 0 : b.IdOf(b.SwampSurfaceCode, false);
            // Grass over the bog (flight test 6): peat with a thin grass cover beside pockets, grassy low-fertility soil elsewhere.
            b.SwampGrassId = b.SwampSurfaceCode == "peat-none" ? b.IdOf("peat-verysparse", false) : 0;
            if (b.SwampGrassId == 0) b.SwampGrassId = b.SwampSurfaceId;
            b.GrassSoilId = b.IdOf("soil-low-normal", false);
            if (b.SwampSurfaceId == 0) api.Logger.Warning("[RiverFeatures] none of the swamp surface blocks exist ({0}); swamps keep their top block.", string.Join(", ", SwampSurfaceCodes));
            b.PlantCodes = WaterRules.ResolvePlants(cfg.SwampPlantCodes, c => b.IdOf(c, false) > 0,
                c => api.Logger.Warning("[RiverFeatures] swamp plant code '{0}' is not a known block; skipped.", c));
            var pl = new List<int>();
            foreach (var c in b.PlantCodes) pl.Add(b.IdOf(c, false));
            b.PlantIds = pl.ToArray();
            return b;
        }

        /// <summary>Block id of a code, 0 when unknown. Cached; an unknown code is logged once when logMissing is set.</summary>
        public int IdOf(string code, bool logMissing)
        {
            if (string.IsNullOrEmpty(code)) return 0;
            if (ids.TryGetValue(code, out int id)) return id;
            id = 0;
            try { id = api.World.GetBlock(new AssetLocation(code))?.BlockId ?? 0; }
            catch (Exception) { id = 0; }
            if (ids.TryAdd(code, id) && id == 0 && logMissing)
                api.Logger.Notification("[RiverFeatures] block '{0}' not found; using a fallback (logged once).", code);
            return id;
        }

        /// <summary>Lake bed block for a column of this rock at this water depth: WaterRules.BedCodes order, 0 = keep the existing block.</summary>
        public int BedId(string rock, int depth)
        {
            foreach (var c in WaterRules.BedCodes(rock, depth))
            {
                int id = IdOf(c, true);
                if (id > 0) return id;
            }
            return 0;
        }
    }
}
