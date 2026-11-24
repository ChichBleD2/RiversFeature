using System;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    /// <summary>
    /// Terrain heights of one 32x32 chunk column plus its neighbours, in LOCAL coordinates of that column
    /// (0..31 = the column itself). -1 means "height unavailable" everywhere, matching TerrainGuard.Slope.
    /// At() covers the 34x34 grid (-1..32, i.e. a 1-block border). Raw() covers the whole 3x3 chunk block
    /// (-32..63) because the neighbour arrays hold full chunk height maps, which the ocean buffer uses so
    /// that a buffer wider than 1 block is judged identically on both sides of a chunk border.
    ///
    /// NEIGHBOUR SOURCES, per neighbour chunk column, first valid (1024 long) wins (<see cref="Pick"/>):
    ///  1. ORIGINAL heights this mod stored for a chunk it changed (OriginalHeights cache, then the map chunk's mod data);
    ///  2. the neighbour map chunk's WorldGenTerrainHeightMap (a clone), read through the worldgen block accessor;
    ///  3. IChunkColumnGenerateRequest.NeighbourTerrainHeight;
    ///  4. unknown (-1).
    /// Why 2 exists: the game appears to fill NeighbourTerrainHeight only when chunk border smoothing is required
    /// (UNVERIFIED either way: vsapi 1.22.7 declares it next to RequiresChunkBorderSmoothing, no game code was read),
    /// while vsapi EnumWorldGenPass says every neighbour of a chunk in TerrainFeatures is at the same or a higher pass,
    /// so its terrain height map exists. A neighbour this mod already changed has a lowered map, which is why the
    /// stored originals come first; the map fallback is NEVER written into the originals store.
    ///
    /// UNVERIFIED (no game source for it in vsapi/vssurvivalmod, so no use of it could be read): the layout of
    /// IChunkColumnGenerateRequest.NeighbourTerrainHeight is ASSUMED to be ushort[8][] indexed by the Cardinal
    /// index (N=0, NE=1, E=2, SE=3, S=4, SW=5, W=6, NW=7; N = -Z, E = +X), each entry the neighbour column's
    /// 32x32 height map indexed z*32+x, or null when that neighbour is not available. Any other shape
    /// (wrong outer length, wrong inner length) is treated as "unavailable" rather than throwing. If the real
    /// layout differs, only DirectionIndex below needs changing (sources 1 and 2 are asked by chunk coordinates via
    /// Offset and do not depend on it). Own heights come from IMapChunk.WorldGenTerrainHeightMap (documented as the
    /// terrain height before the Vegetation pass).
    /// </summary>
    public class HeightGrid
    {
        public const int Size = 32;
        const int Cells = Size * Size;

        readonly ushort[] own;
        readonly ushort[][] neighbours;
        readonly ushort[][] originals;
        readonly ushort[][] maps;

        /// <param name="neighbourHeights">The request's NeighbourTerrainHeight (Cardinal layout), may be null.</param>
        /// <param name="originalHeights">Optional, Cardinal layout: the ORIGINAL heights of neighbours this mod has already
        /// changed (OriginalHeights cache / mod data). Preferred over every other source.</param>
        /// <param name="mapHeights">Optional, Cardinal layout: copies of the neighbours' map-chunk terrain height maps.
        /// Used when there is no original, before the request's neighbour heights.</param>
        public HeightGrid(ushort[] ownHeights, ushort[][] neighbourHeights, ushort[][] originalHeights = null, ushort[][] mapHeights = null)
        {
            own = ownHeights;
            neighbours = neighbourHeights;
            originals = originalHeights;
            maps = mapHeights;
        }

        /// <param name="originalOf">Original heights of chunk column (chunkX, chunkZ), or null when not known (no change by this mod).</param>
        /// <param name="mapOf">A COPY of chunk column (chunkX, chunkZ)'s map-chunk terrain height map, or null.</param>
        public static HeightGrid From(IChunkColumnGenerateRequest req, Func<int, int, ushort[]> originalOf = null, Func<int, int, ushort[]> mapOf = null)
        {
            ushort[] own = null;
            try { own = req.Chunks?[0]?.MapChunk?.WorldGenTerrainHeightMap; }
            catch (Exception) { /* leave null: every cell unavailable */ }
            // Copy: Commit later rewrites the map in place, and every rule of this column must read the original terrain.
            if (own != null) own = (ushort[])own.Clone();
            ushort[][] reqNb = null;
            try { reqNb = req.NeighbourTerrainHeight; } catch (Exception) { reqNb = null; }
            return FromSources(own, req.ChunkX, req.ChunkZ, originalOf, mapOf, reqNb);
        }

        /// <summary>
        /// Pure assembly of the grid from its sources (see the class doc). For each of the 8 neighbours (Offset), asks
        /// originalOf; on a miss asks mapOf and then originalOf AGAIN (a neighbour's Commit stores its originals before it
        /// lowers its map, so a store that appears meanwhile wins over a map that may already be lowered). Exceptions
        /// from either delegate count as a miss. reqNeighbours is the request's NeighbourTerrainHeight (may be null).
        /// </summary>
        public static HeightGrid FromSources(ushort[] own, int chunkX, int chunkZ, Func<int, int, ushort[]> originalOf,
            Func<int, int, ushort[]> mapOf, ushort[][] reqNeighbours)
        {
            ushort[][] orig = originalOf == null ? null : new ushort[8][];
            ushort[][] map = mapOf == null ? null : new ushort[8][];
            for (int k = 0; k < 8; k++)
            {
                var (dx, dz) = Offset(k);
                int cx = chunkX + dx, cz = chunkZ + dz;
                ushort[] o = null;
                if (originalOf != null) o = Ask(originalOf, cx, cz);
                if (Valid(o)) { orig[k] = o; continue; }
                if (mapOf == null) continue;
                var m = Ask(mapOf, cx, cz);
                if (!Valid(m)) continue;
                if (originalOf != null) o = Ask(originalOf, cx, cz);
                if (Valid(o)) orig[k] = o; else map[k] = m;
            }
            return new HeightGrid(own, reqNeighbours, orig, map);
        }

        static ushort[] Ask(Func<int, int, ushort[]> f, int cx, int cz)
        {
            try { return f(cx, cz); } catch (Exception) { return null; }
        }

        static bool Valid(ushort[] a) => a != null && a.Length == Cells;

        static ushort[] Entry(ushort[][] a, int idx) => a != null && idx >= 0 && idx < a.Length ? a[idx] : null;

        /// <summary>The source used for one neighbour: original, else map, else the request's array (first valid), else null. Pure.</summary>
        public static ushort[] Pick(ushort[] original, ushort[] map, ushort[] req)
        {
            if (Valid(original)) return original;
            if (Valid(map)) return map;
            return Valid(req) ? req : null;
        }

        /// <summary>Chunk offset (dx, dz) of Cardinal index 0..7 (inverse of DirectionIndex).</summary>
        public static (int dx, int dz) Offset(int index) => index switch
        {
            0 => (0, -1), 1 => (1, -1), 2 => (1, 0), 3 => (1, 1),
            4 => (0, 1), 5 => (-1, 1), 6 => (-1, 0), _ => (-1, -1),
        };

        /// <summary>A copy of the own (original) heights, or null when unavailable.</summary>
        public ushort[] OwnCopy() => own == null || own.Length != Cells ? null : (ushort[])own.Clone();

        /// <summary>Cardinal index of the neighbour in direction (dx, dz), each in -1..1, not both 0. Cardinal order (UNVERIFIED use, see class doc).</summary>
        static int DirectionIndex(int dx, int dz)
        {
            if (dx == 0) return dz < 0 ? 0 : 4;
            if (dx > 0) return dz < 0 ? 1 : dz == 0 ? 2 : 3;
            return dz > 0 ? 5 : dz == 0 ? 6 : 7;
        }

        /// <summary>Height at local (x, z) anywhere in the 3x3 chunk block (-32..63), or -1 when unavailable.</summary>
        public int Raw(int localX, int localZ)
        {
            if (localX < -Size || localX >= 2 * Size || localZ < -Size || localZ >= 2 * Size) return -1;
            int dx = localX < 0 ? -1 : localX >= Size ? 1 : 0;
            int dz = localZ < 0 ? -1 : localZ >= Size ? 1 : 0;
            ushort[] src;
            if (dx == 0 && dz == 0) src = own;
            else
            {
                int idx = DirectionIndex(dx, dz);
                src = Pick(Entry(originals, idx), Entry(maps, idx), Entry(neighbours, idx));
            }
            if (src == null || src.Length != Cells) return -1;
            int x = localX - dx * Size, z = localZ - dz * Size; // position inside the source chunk, 0..31
            return src[z * Size + x];
        }

        /// <summary>Height at local -1..32 (the 34x34 grid), -1 outside that range or when unavailable.</summary>
        public int At(int localX, int localZ)
        {
            if (localX < -1 || localX > Size || localZ < -1 || localZ > Size) return -1;
            return Raw(localX, localZ);
        }
    }
}
