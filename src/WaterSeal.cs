using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace RiverFeatures
{
    /// <summary>
    /// Flight test 7 (seed 12345, 40,141,209): with Realistic Water installed, water is finite and drains toward any open path
    /// below it (its BFS looks MaxPathDistance = 15 blocks for a way down). This mod places lakes and pockets in the
    /// TerrainFeatures pass, AFTER the game's caves, so a lake bed can sit on a cave ceiling or a lake can touch a cave or an
    /// overhang from the side. Vanilla water would just stand there; Realistic Water drains it, leaving dry pits and stepped
    /// surfaces. After Commit, every column this mod flooded is checked: the block under the water (the bed) and every
    /// horizontal neighbour block beside the water that is empty (no solid, no fluid) is filled with the column's rock (or
    /// the bed block). Only empty blocks are filled, never water or solid ground; neighbour chunks that are not loaded are
    /// skipped. Rivers' own water is not touched (the game's caves avoid it).
    /// </summary>
    public static class WaterSeal
    {
        const int N = ChunkCtx.Size;

        public static int Apply(ChunkCtx c)
        {
            if (c?.Ba == null || c.Req == null || Array.IndexOf(c.WaterPlaced, true) < 0) return 0;
            var mc = c.Req.Chunks?[0]?.MapChunk;
            var th = mc?.WorldGenTerrainHeightMap;
            var rh = mc?.RainHeightMap;
            if (th == null || rh == null || th.Length != N * N || rh.Length != N * N) return 0;
            var ba = c.Ba;
            var pos = new BlockPos(0, 0, 0);
            int sealed_ = 0;
            for (int i = 0; i < N * N; i++)
            {
                if (!c.WaterPlaced[i]) continue;
                int bed = th[i], level = rh[i];
                if (level <= bed || bed < 2) continue;
                int wx = c.BaseX + i % N, wz = c.BaseZ + i / N;
                int fill = FillBlock(c, wx, bed, wz);
                if (fill <= 0) continue;
                if (Empty(ba, wx, bed, wz)) { pos.Set(wx, bed, wz); ba.SetBlock(fill, pos); sealed_++; }
                for (int y = bed + 1; y <= level; y++)
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = wx + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = wz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (ba.GetChunkAtBlockPos(nx, y, nz) == null) continue;
                        if (!Empty(ba, nx, y, nz)) continue;
                        pos.Set(nx, y, nz);
                        ba.SetBlock(fill, pos);
                        sealed_++;
                    }
            }
            c.Stats.WaterSealed += sealed_;
            return sealed_;
        }

        static bool Empty(IBlockAccessor ba, int x, int y, int z)
        {
            try
            {
                return (ba.GetBlock(x, y, z, BlockLayersAccess.Solid)?.BlockId ?? 0) == 0
                    && (ba.GetBlock(x, y, z, BlockLayersAccess.Fluid)?.BlockId ?? 0) == 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The column's rock (rock-&lt;name&gt;) when one is found below the bed, else the bed block itself (0 = none).</summary>
        static int FillBlock(ChunkCtx c, int wx, int bed, int wz)
        {
            string rock = LakePass.RockBelow(c, wx, bed, wz);
            int id = rock == null ? 0 : c.Blocks.IdOf("rock-" + rock, false);
            if (id > 0) return id;
            try
            {
                var b = c.Ba.GetBlock(wx, bed, wz, BlockLayersAccess.Solid);
                return b != null && b.BlockId != 0 && b.BlockMaterial != EnumBlockMaterial.Plant ? b.BlockId : 0;
            }
            catch (Exception) { return 0; }
        }
    }
}
