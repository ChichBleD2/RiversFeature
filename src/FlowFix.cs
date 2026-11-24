using System;
using System.Collections.Generic;

namespace RiverFeatures
{
    /// <summary>
    /// Rivers' flow map (flight test 4: "isolated puddles of fast-flowing water"). Read from Rivers 5.0.2 by reflection/IL:
    /// Rivers stores a float[2048] in the bottom chunk's mod data under "flowVectors" (index z*32+x = flow X, 1024 + z*32+x =
    /// flow Z) and its client patches draw ANY water in a column with a non-zero vector as flowing (and push boats and
    /// swimmers). So still water inside Rivers' flow zone, and stretches of a Rivers channel that ended up as separate pools,
    /// looked and behaved like rapids. River Features clears the vector (still water) on columns where it places water and on
    /// small enclosed pools. Pure helpers; the mod-data access is in TerrainWatersSystem.
    /// </summary>
    public static class FlowFix
    {
        public const string ModDataKey = "flowVectors";
        public const int Cells = 32 * 32;
        /// <summary>A water body counts as an isolated pool only up to this many columns ...</summary>
        public const int MaxPoolColumns = 400;
        /// <summary>... and this bounding-box side (blocks), and only if it does not reach the edge of the scanned window.</summary>
        public const int MaxPoolSpan = 28;

        /// <summary>
        /// For the 32x32 own columns (index z*32+x): true when the column holds water (<paramref name="wet"/>, local coordinates)
        /// that belongs to a 4-connected water body which stays inside the window -window..31+window, has at most MaxPoolColumns
        /// columns and a bounding box of at most MaxPoolSpan. A river channel crosses the window edge or is long, so it is never
        /// isolated. Deterministic for every chunk that sees the whole pool.
        /// </summary>
        public static bool[] IsolatedWater(Func<int, int, bool> wet, int window)
        {
            var result = new bool[Cells];
            if (wet == null || window < 1) return result;
            int side = 32 + 2 * window;
            var comp = new int[side * side]; // 0 unvisited, >0 component id
            var isolated = new List<bool> { false };
            var queue = new List<int>();
            int Idx(int x, int z) => (z + window) * side + (x + window);
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                {
                    if (!wet(x, z) || comp[Idx(x, z)] != 0) continue;
                    int id = isolated.Count;
                    bool ok = true;
                    int minX = x, maxX = x, minZ = z, maxZ = z;
                    queue.Clear();
                    comp[Idx(x, z)] = id; queue.Add(Idx(x, z));
                    for (int head = 0; head < queue.Count; head++)
                    {
                        int q = queue[head];
                        int qx = q % side - window, qz = q / side - window;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = qx + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = qz + (k == 2 ? -1 : k == 3 ? 1 : 0);
                            if (nx < -window || nz < -window || nx >= 32 + window || nz >= 32 + window) { ok = false; continue; }
                            int ni = Idx(nx, nz);
                            if (comp[ni] != 0 || !wet(nx, nz)) continue;
                            comp[ni] = id; queue.Add(ni);
                            if (nx < minX) minX = nx; if (nx > maxX) maxX = nx;
                            if (nz < minZ) minZ = nz; if (nz > maxZ) maxZ = nz;
                        }
                    }
                    if (queue.Count > MaxPoolColumns || maxX - minX + 1 > MaxPoolSpan || maxZ - minZ + 1 > MaxPoolSpan) ok = false;
                    isolated.Add(ok);
                }
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                {
                    int c = comp[Idx(x, z)];
                    result[z * 32 + x] = c > 0 && isolated[c];
                }
            return result;
        }

        /// <summary>Zeroes both flow components of every marked column; returns how many columns changed. Wrong layout: untouched, 0.</summary>
        public static int Clear(float[] flow, bool[] mark)
        {
            if (flow == null || mark == null || flow.Length != 2 * Cells || mark.Length != Cells) return 0;
            int changed = 0;
            for (int i = 0; i < Cells; i++)
            {
                if (!mark[i] || (flow[i] == 0f && flow[Cells + i] == 0f)) continue;
                flow[i] = 0f; flow[Cells + i] = 0f;
                changed++;
            }
            return changed;
        }
    }
}
