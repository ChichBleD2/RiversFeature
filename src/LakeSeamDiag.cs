using System;
using System.Text;
using System.Threading;

namespace RiverFeatures
{
    /// <summary>
    /// Flight test 11 diagnostics (square lake pits at -2428,-8368): two neighbouring chunk columns disagreed about which
    /// columns along their border flood. Every chunk column with a lake plan stores its VIEW of the window -R..31+R (the
    /// decision, the height it read and the level, per column) in its map chunk. A chunk column generated later compares
    /// its own decisions with the earlier neighbour's view of them and the other way round, and logs where they differ,
    /// with both sides' reason, height and level, so the input that differs can be named. Logs are capped.
    /// </summary>
    public static class LakeSeamDiag
    {
        public const int R = 4, Side = 32 + 2 * R, Cells = Side * Side;
        const int Header = 8;
        static long seq;
        static int logged;
        public static long Mismatches;
        public const int MaxLogged = 40;

        /// <summary>Encodes this planner's view: per column reason (byte, 255 = none), height (ushort), level (ushort); then a sequence number.</summary>
        public static byte[] Encode(Func<int, int, LakePlan> decide, Func<int, int, int> height)
        {
            var b = new byte[Header + Cells * 5];
            long s = Interlocked.Increment(ref seq);
            for (int k = 0; k < 8; k++) b[k] = (byte)(s >> (8 * k));
            for (int lz = -R; lz < 32 + R; lz++)
                for (int lx = -R; lx < 32 + R; lx++)
                {
                    int i = (lz + R) * Side + (lx + R), o = Header + i * 5;
                    LakePlan p;
                    try { p = decide(lx, lz); } catch (Exception) { b[o] = 255; continue; }
                    b[o] = (byte)p.Reason;
                    int h = height(lx, lz), l = p.Column.Level;
                    h = h < 0 ? 0xFFFF : h; l = l < 0 ? 0xFFFF : l;
                    b[o + 1] = (byte)h; b[o + 2] = (byte)(h >> 8); b[o + 3] = (byte)l; b[o + 4] = (byte)(l >> 8);
                }
            return b;
        }

        public readonly record struct Cell(bool Known, LakeSkip Reason, int Height, int Level);

        public static Cell Read(byte[] view, int lx, int lz)
        {
            if (view == null || view.Length != Header + Cells * 5 || lx < -R || lz < -R || lx >= 32 + R || lz >= 32 + R) return default;
            int o = Header + ((lz + R) * Side + (lx + R)) * 5;
            if (view[o] == 255) return default;
            int h = view[o + 1] | (view[o + 2] << 8), l = view[o + 3] | (view[o + 4] << 8);
            return new Cell(true, (LakeSkip)view[o], h == 0xFFFF ? -1 : h, l == 0xFFFF ? -1 : l);
        }

        public static long Seq(byte[] view)
        {
            if (view == null || view.Length < Header) return -1;
            long s = 0;
            for (int k = 0; k < 8; k++) s |= (long)view[k] << (8 * k);
            return s;
        }

        /// <summary>
        /// Compares this chunk's view with the stored views of its 4 finished cardinal neighbours, over the columns both
        /// views cover. Returns the number of columns where the flood decision differs; logs the first few per call.
        /// </summary>
        public static int Compare(byte[] mine, int chunkX, int chunkZ, Func<int, int, byte[]> viewOf, Action<string> log)
        {
            if (mine == null || viewOf == null) return 0;
            int total = 0;
            for (int d = 0; d < 4; d++)
            {
                int dx = d == 0 ? 1 : d == 1 ? -1 : 0, dz = d == 2 ? 1 : d == 3 ? -1 : 0;
                byte[] other;
                try { other = viewOf(chunkX + dx, chunkZ + dz); } catch (Exception) { other = null; }
                if (other == null) continue;
                int n = 0;
                var sb = new StringBuilder();
                for (int lz = -R; lz < 32 + R; lz++)
                    for (int lx = -R; lx < 32 + R; lx++)
                    {
                        int ox = lx - dx * 32, oz = lz - dz * 32; // same column in the neighbour's local coordinates
                        var a = Read(mine, lx, lz);
                        var b = Read(other, ox, oz);
                        if (!a.Known || !b.Known) continue;
                        bool ownA = lx >= 0 && lx < 32 && lz >= 0 && lz < 32, ownB = ox >= 0 && ox < 32 && oz >= 0 && oz < 32;
                        if (!ownA && !ownB) continue; // judge only columns one of the two actually owns
                        if ((a.Reason == LakeSkip.None) == (b.Reason == LakeSkip.None)) continue;
                        n++;
                        if (n <= 3)
                            sb.Append(" [").Append(chunkX * 32 + lx).Append(',').Append(chunkZ * 32 + lz).Append(ownA ? " owned by later" : " owned by earlier")
                              .Append(": later ").Append(a.Reason).Append(" h").Append(a.Height).Append(" L").Append(a.Level)
                              .Append(" | earlier ").Append(b.Reason).Append(" h").Append(b.Height).Append(" L").Append(b.Level).Append(']');
                    }
                if (n == 0) continue;
                total += n;
                Interlocked.Add(ref Mismatches, n);
                if (Interlocked.Increment(ref logged) <= MaxLogged)
                    log("lake seam mismatch: chunk " + chunkX + "," + chunkZ + " (seq " + Seq(mine) + ") vs neighbour " + (chunkX + dx) + "," + (chunkZ + dz)
                        + " (seq " + Seq(other) + "): " + n + " column(s) decided differently;" + sb);
            }
            return total;
        }
    }
}
