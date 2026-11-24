using System;
using System.Collections.Generic;

namespace RiverFeatures
{
    /// <summary>
    /// Original terrain heights (32x32, index z*32+x) of chunk columns this mod has changed, kept so that the height
    /// maps can be updated to the new ground (Vegetation then places plants and trees on it) while every chunk still
    /// judges its neighbours by the ORIGINAL terrain (seam agreement: all rules read original heights).
    /// Bounded: at most Capacity chunk columns; the oldest entry is evicted first. Thread-safe (one lock). The first
    /// Remember of a chunk column wins, so a later call can never replace the original by modified data. Pure (no game
    /// types). A miss means "not cached": the caller falls back to the persisted copy in the map chunk's mod data, then
    /// to the game's neighbour heights.
    /// </summary>
    public sealed class OriginalHeights
    {
        public const int Cells = 32 * 32;
        public const int DefaultCapacity = 2048;

        readonly object gate = new object();
        readonly Dictionary<long, ushort[]> map = new Dictionary<long, ushort[]>();
        readonly Queue<long> order = new Queue<long>();

        public int Capacity { get; }

        public OriginalHeights(int capacity = DefaultCapacity) { Capacity = Math.Max(1, capacity); }

        static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

        public int Count { get { lock (gate) return map.Count; } }

        /// <summary>
        /// Stores a copy of heights (length 1024) for chunk column (cx, cz) unless one is stored already. Returns true when
        /// stored. Evicts the oldest entries first so Count never exceeds Capacity. Wrong length or null: ignored.
        /// </summary>
        public bool Remember(int cx, int cz, ushort[] heights)
        {
            if (heights == null || heights.Length != Cells) return false;
            long k = Key(cx, cz);
            var copy = (ushort[])heights.Clone();
            lock (gate)
            {
                if (map.ContainsKey(k)) return false;
                while (map.Count >= Capacity && order.Count > 0) map.Remove(order.Dequeue());
                map[k] = copy;
                order.Enqueue(k);
                return true;
            }
        }

        /// <summary>A copy of the stored original heights of (cx, cz), or null on a miss.</summary>
        public ushort[] Get(int cx, int cz)
        {
            lock (gate)
                return map.TryGetValue(Key(cx, cz), out var h) ? (ushort[])h.Clone() : null;
        }

        /// <summary>Little-endian bytes of a 1024-entry height map (2048 bytes), or null for a wrong input.</summary>
        public static byte[] Encode(ushort[] heights)
        {
            if (heights == null || heights.Length != Cells) return null;
            var b = new byte[Cells * 2];
            for (int i = 0; i < Cells; i++) { b[2 * i] = (byte)(heights[i] & 0xFF); b[2 * i + 1] = (byte)(heights[i] >> 8); }
            return b;
        }

        /// <summary>Inverse of Encode; null unless exactly 2048 bytes.</summary>
        public static ushort[] Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != Cells * 2) return null;
            var h = new ushort[Cells];
            for (int i = 0; i < Cells; i++) h[i] = (ushort)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            return h;
        }

        /// <summary>
        /// The new rain height of a column whose terrain top moved from oldTerrain to newTop (pure). The rain map follows
        /// the ground when it was at or below the old ground (nothing rain-impermeable stood above it); otherwise it keeps
        /// its value unless the new top is higher. Unknown current value (&lt; 0) -> newTop.
        /// </summary>
        public static int RainAfter(int currentRain, int oldTerrain, int newTop)
        {
            if (currentRain < 0) return newTop;
            if (currentRain <= oldTerrain) return newTop;
            return Math.Max(currentRain, newTop);
        }
    }
}
