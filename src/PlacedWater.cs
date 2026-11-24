using System;
using System.Collections.Generic;

namespace RiverFeatures
{
    /// <summary>
    /// Which columns of a chunk column got water from THIS mod (lake water, swamp pockets), 32x32 flags, index z*32+x.
    /// The river scan (ColumnExclusions.Build) ignores those columns in neighbour chunks. Without this, a chunk generated
    /// after its neighbour saw the neighbour's swamp pockets and lake water as river water, locked everything within the
    /// river radius of them, and left its side of the border uncut while the neighbour's side was cut: a straight wall
    /// along the chunk border (flight test 2026-11-22, swamps at -1360,96 and -3056,-292).
    /// Kept like <see cref="OriginalHeights"/>: a bounded in-memory cache, persisted in the map chunk's mod data. Thread-safe.
    /// </summary>
    public sealed class PlacedWater
    {
        public const int Cells = 32 * 32;
        public const int DefaultCapacity = 4096;

        readonly object gate = new object();
        readonly Dictionary<long, bool[]> map = new Dictionary<long, bool[]>();
        readonly Queue<long> order = new Queue<long>();

        public int Capacity { get; }

        public PlacedWater(int capacity = DefaultCapacity) { Capacity = Math.Max(1, capacity); }

        static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

        /// <summary>Stores a copy for (cx, cz) unless one is stored already (first wins). Wrong length or null: ignored.</summary>
        public bool Remember(int cx, int cz, bool[] flags)
        {
            if (flags == null || flags.Length != Cells) return false;
            long k = Key(cx, cz);
            var copy = (bool[])flags.Clone();
            lock (gate)
            {
                if (map.ContainsKey(k)) return false;
                while (map.Count >= Capacity && order.Count > 0) map.Remove(order.Dequeue());
                map[k] = copy;
                order.Enqueue(k);
                return true;
            }
        }

        /// <summary>The stored flags of (cx, cz) (shared, do not modify), or null on a miss.</summary>
        public bool[] Get(int cx, int cz)
        {
            lock (gate) return map.TryGetValue(Key(cx, cz), out var f) ? f : null;
        }

        /// <summary>128 bytes, one bit per column (bit i%8 of byte i/8), or null for a wrong input.</summary>
        public static byte[] Encode(bool[] flags)
        {
            if (flags == null || flags.Length != Cells) return null;
            var b = new byte[Cells / 8];
            for (int i = 0; i < Cells; i++) if (flags[i]) b[i >> 3] |= (byte)(1 << (i & 7));
            return b;
        }

        /// <summary>Inverse of Encode; null unless exactly 128 bytes.</summary>
        public static bool[] Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != Cells / 8) return null;
            var f = new bool[Cells];
            for (int i = 0; i < Cells; i++) f[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
            return f;
        }
    }
}
