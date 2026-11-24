using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RiverFeatures.Rivers
{
    /// <summary>
    /// Flight test 16: every Rivers channel of the regions built this session, in WORLD coordinates (segment and half-width),
    /// so lakes from lakes.json can keep a real distance from rivers. Filled when Rivers sizes a river (its channels, deltas,
    /// oxbows, traces) and when it adds a source lake. Rivers rebuilds a region's network every time it loads the region,
    /// before any of its chunks reaches the TerrainFeatures pass, so the index is complete for a chunk's own region; a channel
    /// registered twice (region rebuilt) only repeats an entry. Spatial hash of 256-block cells. Thread safe.
    /// </summary>
    public static class RiverIndex
    {
        public readonly record struct Seg(double Ax, double Az, double Bx, double Bz, double Half);

        const int Cell = 256;
        static readonly ConcurrentDictionary<(int, int), List<Seg>> cells = new ConcurrentDictionary<(int, int), List<Seg>>();

        public static void Clear() => cells.Clear();
        public static bool Empty => cells.IsEmpty;

        public static void Add(double ax, double az, double bx, double bz, double half)
        {
            if (double.IsNaN(ax) || double.IsNaN(az) || double.IsNaN(bx) || double.IsNaN(bz)) return;
            var s = new Seg(ax, az, bx, bz, Math.Max(0, half));
            int x0 = Fl(Math.Min(ax, bx) - s.Half), x1 = Fl(Math.Max(ax, bx) + s.Half);
            int z0 = Fl(Math.Min(az, bz) - s.Half), z1 = Fl(Math.Max(az, bz) + s.Half);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    var list = cells.GetOrAdd((cx, cz), _ => new List<Seg>());
                    lock (list) list.Add(s);
                }
        }

        static int Fl(double v) => (int)Math.Floor(v / Cell);

        /// <summary>Blocks from (x, z) to the nearest channel EDGE within <paramref name="reach"/> (negative inside); +inf when none.</summary>
        public static double EdgeDistance(double x, double z, double reach)
        {
            double best = double.PositiveInfinity;
            int x0 = Fl(x - reach), x1 = Fl(x + reach), z0 = Fl(z - reach), z1 = Fl(z + reach);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!cells.TryGetValue((cx, cz), out var list)) continue;
                    lock (list)
                        foreach (var s in list)
                        {
                            double d = DistToSeg(x, z, s) - s.Half;
                            if (d < best) best = d;
                        }
                }
            return best;
        }

        static double DistToSeg(double x, double z, Seg s)
        {
            double dx = s.Bx - s.Ax, dz = s.Bz - s.Az, l2 = dx * dx + dz * dz;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((x - s.Ax) * dx + (z - s.Az) * dz) / l2, 0, 1);
            double px = s.Ax + dx * t - x, pz = s.Az + dz * t - z;
            return Math.Sqrt(px * px + pz * pz);
        }

        /// <summary>
        /// A lake disc (centre, radius) kept <paramref name="gap"/> blocks of land from every channel (pure given the
        /// distance): the radius it may keep, which is the old one when far enough, smaller when a channel is near, and
        /// NaN when that would fall below <paramref name="minRadius"/> (the lake is dropped).
        /// </summary>
        public static double FitRadius(double radius, double edgeDistance, double gap, double minRadius)
        {
            if (double.IsNaN(edgeDistance) || double.IsPositiveInfinity(edgeDistance)) return radius;
            double r = Math.Min(radius, edgeDistance - gap);
            return r >= minRadius ? r : double.NaN;
        }
    }
}
