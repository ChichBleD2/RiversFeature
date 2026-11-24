using System;
using System.Collections.Generic;

namespace RiverFeatures
{
    /// <summary>
    /// Structure compatibility (flight test 2.5, a trader camp on a lake bed at -649,144; BetterRuins). Facts from the 1.22.7
    /// game DLLs (VSEssentials / VSSurvivalMod, read by reflection):
    ///  - GenStructures (ruins, BetterRuins' structures.json, villages) runs in TerrainFeatures at order 0.3, the post-pass
    ///    structures (trader camps, "postPass": true) in GenStructuresPosPass at 0.5: both BEFORE this mod (0.55) in the same
    ///    chunk, and both register every placed schematic in IMapRegion.GeneratedStructures, which ColumnExclusions reads.
    ///  - A chunk generated LATER may place a structure that reaches into a chunk this mod already changed. Surface placement
    ///    takes its Y from the terrain height map (a lake column's map points at the lake BED) and is rejected only when its
    ///    box intersects an entry of GeneratedStructures (WorldGenStructure.WouldOverlapAt, WorldGenVillage.overlapsExistingStructure).
    ///    So this mod now registers its own footprints there: lake water (and eased shores) and ground lowered by 2 or more.
    ///  - Story structures (GenStoryStructures, BetterRuins' storystructures.json) are placed in the Vegetation pass, AFTER this
    ///    mod, at locations fixed at world start (GenStoryStructures.Structures). Their footprints are protected like any
    ///    structure, and their "pond" keep-out (skipGenerationCategories) is honoured for lakes and swamps through the same
    ///    query vanilla ponds use (ModSystemWorldGenStructures.GetIntersectingStructure with ModStdWorldGen.PondsHashCode).
    /// Pure helpers only; the game calls are in ChunkCtx / ColumnExclusions / TerrainWatersSystem.
    /// </summary>
    public static class StructureGuard
    {
        /// <summary>Code prefix of this mod's own GeneratedStructures entries; ColumnExclusions ignores them.</summary>
        public const string CodePrefix = "riverfeatures:";
        public const string LakeCode = CodePrefix + "lake";
        public const string CutCode = CodePrefix + "cut";
        public const string Group = "riverfeatures";

        /// <summary>Ground lowered by at least this many blocks is registered as a footprint (a structure placed over it later would be sheared).</summary>
        public const int MinRegisteredCut = 2;

        /// <summary>Footprint tile size (blocks): one box per tile with flagged columns keeps boxes tight around irregular lakes.</summary>
        public const int Tile = 8;

        public static bool IsOwn(string code) => code != null && code.StartsWith(CodePrefix, StringComparison.Ordinal);

        /// <summary>One footprint box in local chunk coordinates, all bounds inclusive.</summary>
        public readonly record struct Box(int X1, int Z1, int X2, int Z2, int Y1, int Y2);

        /// <summary>
        /// Bounding boxes of the flagged columns of a 32x32 chunk column (index z*32+x), one per Tile x Tile tile that has any:
        /// X/Z the flagged columns' extent in that tile, Y from the lowest lowY to the highest highY of those columns. Pure.
        /// </summary>
        public static List<Box> Tiles(bool[] flags, int[] lowY, int[] highY, int size = 32, int tile = Tile)
        {
            var list = new List<Box>();
            if (flags == null || lowY == null || highY == null || flags.Length != size * size) return list;
            tile = Math.Clamp(tile, 1, size);
            for (int tz = 0; tz < size; tz += tile)
                for (int tx = 0; tx < size; tx += tile)
                {
                    int x1 = int.MaxValue, z1 = int.MaxValue, x2 = int.MinValue, z2 = int.MinValue, y1 = int.MaxValue, y2 = int.MinValue;
                    for (int z = tz; z < Math.Min(size, tz + tile); z++)
                        for (int x = tx; x < Math.Min(size, tx + tile); x++)
                        {
                            int i = z * size + x;
                            if (!flags[i]) continue;
                            if (x < x1) x1 = x; if (x > x2) x2 = x;
                            if (z < z1) z1 = z; if (z > z2) z2 = z;
                            if (lowY[i] < y1) y1 = lowY[i];
                            if (highY[i] > y2) y2 = highY[i];
                        }
                    if (x1 != int.MaxValue) list.Add(new Box(x1, z1, x2, z2, y1, Math.Max(y1, y2)));
                }
            return list;
        }

        /// <summary>The distinct map regions holding chunk column (cx, cz) and its 8 neighbours (a structure whose origin is in a
        /// neighbouring chunk checks its own region). regionChunks = region size in chunks. Pure.</summary>
        public static List<(int rx, int rz)> RegionsAround(int cx, int cz, int regionChunks)
        {
            var list = new List<(int, int)>();
            if (regionChunks <= 0) return list;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var r = (FloorDiv(cx + dx, regionChunks), FloorDiv(cz + dz, regionChunks));
                    if (!list.Contains(r)) list.Add(r);
                }
            return list;
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        /// <summary>Horizontal ring (blocks) around a structure's footprint checked for water by <see cref="WaterVeto"/>.</summary>
        public const int VetoRing = 2;
        /// <summary>Water this many blocks above the structure's floor (floor + 1 .. floor + VetoRise) counts.</summary>
        public const int VetoRise = 3;

        /// <summary>
        /// Placement veto for GenStructures.OnPreventSchematicPlaceAt (flight test 2.5: a trader camp built ~10 blocks below sea
        /// level on the floor of a water-filled basin at -649,144; not River Features water, but the same kind of failure). Vanilla
        /// surface placement only tests the centre and the four corners one block above the ground for liquid. This vetoes a
        /// surface structure when any sampled column of its footprint, or of a VetoRing ring around it, holds liquid between
        /// floor + 1 and floor + VetoRise: the structure would stand in water, or in a hole next to water that floods it.
        /// Not vetoed: structures meant for water (code containing "shallow" or "underwater", e.g. vanilla trader-shallow), and
        /// underground structures (box top more than 2 below the terrain height at the footprint centre). Box max X/Z exclusive.
        /// Samples every 2 blocks including the last row/column. Pure.
        /// </summary>
        public static bool WaterVeto(int x1, int y1, int z1, int x2Excl, int y2, int z2Excl, int terrainYAtCentre, string code,
            Func<int, int, int, bool> isLiquid)
        {
            if (isLiquid == null) return false;
            if (code != null && (code.IndexOf("shallow", StringComparison.OrdinalIgnoreCase) >= 0 || code.IndexOf("underwater", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if (y2 < terrainYAtCentre - 2) return false; // underground: water above it does not matter
            int xa = x1 - VetoRing, xb = Math.Max(x1, x2Excl - 1) + VetoRing;
            int za = z1 - VetoRing, zb = Math.Max(z1, z2Excl - 1) + VetoRing;
            if ((long)(xb - xa) * (zb - za) > 128L * 128L) return false; // absurd box: do not scan
            for (int x = xa; ; x = Math.Min(x + 2, xb))
            {
                for (int z = za; ; z = Math.Min(z + 2, zb))
                {
                    for (int y = y1 + 1; y <= y1 + VetoRise; y++)
                        if (isLiquid(x, y, z)) return true;
                    if (z == zb) break;
                }
                if (x == xb) break;
            }
            return IslandVeto(x1, y1, z1, x2Excl, z2Excl, isLiquid);
        }

        /// <summary>Ring (blocks) around the footprint checked for surface water at floor level by <see cref="IslandVeto"/>.</summary>
        public const int IslandRing = 6;
        /// <summary>Veto when more than this share of the whole ring holds water at the floor or one block below it.</summary>
        public const double IslandShare = 0.6;
        /// <summary>A side band (west, east, north, south of the footprint) counts as wet above this share of water.</summary>
        public const double SideWetShare = 0.5;
        /// <summary>Veto when this many of the four sides are wet (an island: 4, a spit or headland tip: 3).</summary>
        public const int IslandSides = 3;
        /// <summary>Veto when more than this share of the footprint holds water at the floor itself.</summary>
        public const double FloorWaterShare = 0.2;

        /// <summary>
        /// Flight test 17 (a BetterRuins house on a tiny island in a shallow delta at -1500,144,11749): the floor stood one
        /// block above the water, so the "water above the floor" rule passed it. Also vetoed: a structure whose footprint
        /// is partly water at floor level (more than FloorWaterShare of it: half-submerged), or whose surroundings (a band of
        /// IslandRing blocks, water at the floor or one block below) are wet on IslandSides or more of its four sides, or wet
        /// over more than IslandShare of the whole band: an island or a spit. Audit: the old single 35% share also caught a
        /// small (4x4) building on a straight shore, where one wet side alone is about 40% of the band; one wet side (a
        /// riverbank or shore), or two (a corner of the shore), is allowed. Samples every 2 blocks. Pure.
        /// </summary>
        public static bool IslandVeto(int x1, int y1, int z1, int x2Excl, int z2Excl, Func<int, int, int, bool> isLiquid)
        {
            int fx2 = Math.Max(x1, x2Excl - 1), fz2 = Math.Max(z1, z2Excl - 1);
            int inFoot = 0, wetFoot = 0, inRing = 0, wetRing = 0;
            Span<int> side = stackalloc int[4], sideWet = stackalloc int[4];   // west, east, north, south
            for (int x = x1 - IslandRing; x <= fx2 + IslandRing; x += 2)
                for (int z = z1 - IslandRing; z <= fz2 + IslandRing; z += 2)
                {
                    bool foot = x >= x1 && x <= fx2 && z >= z1 && z <= fz2;
                    if (foot) { inFoot++; if (isLiquid(x, y1, z)) wetFoot++; continue; }
                    bool wet = isLiquid(x, y1, z) || isLiquid(x, y1 - 1, z);
                    inRing++; if (wet) wetRing++;
                    // Corners belong to both of their sides.
                    if (x < x1) { side[0]++; if (wet) sideWet[0]++; }
                    if (x > fx2) { side[1]++; if (wet) sideWet[1]++; }
                    if (z < z1) { side[2]++; if (wet) sideWet[2]++; }
                    if (z > fz2) { side[3]++; if (wet) sideWet[3]++; }
                }
            if (inFoot > 0 && wetFoot > FloorWaterShare * inFoot) return true;
            int wetSides = 0;
            for (int k = 0; k < 4; k++) if (side[k] > 0 && sideWet[k] > SideWetShare * side[k]) wetSides++;
            return wetSides >= IslandSides || (inRing > 0 && wetRing > IslandShare * inRing);
        }
    }
}
