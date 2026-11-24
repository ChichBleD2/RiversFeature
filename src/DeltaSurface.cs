using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RiverFeatures.Rivers;
using Vintagestory.API.Common;

namespace RiverFeatures
{
    /// <summary>One delta as the chunk pass sees it: its channels in WORLD coordinates (segment, half-width) and its traits.</summary>
    public sealed class DeltaZone
    {
        public readonly List<(double ax, double az, double bx, double bz, double half)> Segments = new List<(double, double, double, double, double)>();
        public DeltaTraits Traits;
        public long Salt;
        public double MinX = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxZ = double.MinValue;

        public void AddSegment(double ax, double az, double bx, double bz, double half)
        {
            Segments.Add((ax, az, bx, bz, half));
            double r = half + DeltaTraitRules.BankReach(Traits);
            MinX = Math.Min(MinX, Math.Min(ax, bx) - r); MaxX = Math.Max(MaxX, Math.Max(ax, bx) + r);
            MinZ = Math.Min(MinZ, Math.Min(az, bz) - r); MaxZ = Math.Max(MaxZ, Math.Max(az, bz) + r);
        }

        /// <summary>Blocks from (x, z) to the nearest channel EDGE (negative inside a channel); +inf without segments.</summary>
        public double EdgeDistance(double x, double z)
        {
            double best = double.PositiveInfinity;
            foreach (var s in Segments)
            {
                double dx = s.bx - s.ax, dz = s.bz - s.az, l2 = dx * dx + dz * dz;
                double t = l2 < 1e-9 ? 0 : Math.Clamp(((x - s.ax) * dx + (z - s.az) * dz) / l2, 0, 1);
                double px = s.ax + dx * t - x, pz = s.az + dz * t - z;
                double d = Math.Sqrt(px * px + pz * pz) - s.half;
                if (d < best) best = d;
            }
            return best;
        }
    }

    /// <summary>
    /// Deltas of the regions Rivers has built this session (stage 2). Rivers rebuilds a region's network every time it loads
    /// the region (only its zone map is cached on disk), so the delta hook re-registers every delta before any chunk of
    /// that region reaches the TerrainFeatures pass; a delta registered twice (region rebuilt) replaces itself.
    /// </summary>
    public static class DeltaZones
    {
        static readonly ConcurrentDictionary<(long, long), DeltaZone> zones = new ConcurrentDictionary<(long, long), DeltaZone>();

        public static void Add(double keyX, double keyZ, DeltaZone z) => zones[((long)Math.Round(keyX), (long)Math.Round(keyZ))] = z;

        public static void Clear() => zones.Clear();

        public static int Count => zones.Count;

        public static List<DeltaZone> Near(double minX, double minZ, double maxX, double maxZ)
        {
            List<DeltaZone> list = null;
            foreach (var z in zones.Values)
                if (z.MaxX >= minX && z.MinX <= maxX && z.MaxZ >= minZ && z.MinZ <= maxZ) (list ??= new List<DeltaZone>()).Add(z);
            return list;
        }
    }

    /// <summary>
    /// Stage 2: delta banks get their character. Per own column near a delta channel (within the delta's bank reach beyond the
    /// channel edge), on low dry ground (sea level - 1 .. sea + 6) whose top is soil, sand or gravel: the top block becomes
    /// the delta's bank material and plants grow at its vegetation density. Both thin out with distance from the channel
    /// (patchy, by noise), so the delta blends into the land around it. Heights are never changed (no trench, no step, no
    /// seam: every decision is a function of the world position and the delta). Columns already used by lakes or swamps,
    /// near structures, or holding water are left alone; the columns it changes are kept from swamps.
    /// </summary>
    public static class DeltaSurface
    {
        public enum Surface { None, Sand, Mud, Gravel, Rock }
        public enum Plant { None, Reeds, TallGrass, SparseGrass }

        /// <summary>
        /// The pure per-column rule. edge = blocks beyond the channel edge; h = ground height; n = patch noise 0..1;
        /// hm, hp = per-column hashes 0..1 for the material and the plant.
        /// </summary>
        public static (Surface surface, Plant plant) Decide(DeltaTraits t, double edge, int h, int sea, double n, double hm, double hp)
        {
            double reach = DeltaTraitRules.BankReach(t);
            if (double.IsNaN(edge) || edge < 0 || edge > reach || h < sea - 1 || h > sea + 6) return (Surface.None, Plant.None);
            double f = 1 - edge / reach;
            double p = Math.Pow(f, 1.3) * (0.45 + 0.55 * Math.Clamp(n, 0, 1));
            var surface = Surface.None;
            if (hm < p)
            {
                switch (t.Bank)
                {
                    case DeltaBank.Sand: surface = Surface.Sand; break;
                    case DeltaBank.Mud: surface = t.Moisture >= DeltaTraits.WetFrom ? Surface.Mud : Surface.Sand; break;
                    case DeltaBank.Gravel: surface = Surface.Gravel; break;
                    default: surface = hm < p * 0.5 ? Surface.Rock : Surface.Gravel; break;
                }
            }
            var look = t.Look;
            double veg = Math.Clamp(t.Vegetation, 0, 1) * (0.3 + 0.7 * f) * 0.7;
            var plant = Plant.None;
            if (hp < veg && surface != Surface.Rock)
            {
                if (look == DeltaLook.Marshy || (look == DeltaLook.Overgrown && edge < reach * 0.35)) plant = Plant.Reeds;
                else if (look == DeltaLook.Overgrown) plant = Plant.TallGrass;
                else plant = Plant.SparseGrass;
            }
            return (surface, plant);
        }

        public static void Apply(ChunkCtx c)
        {
            if (DeltaZones.Count == 0 || c.Blocks == null) return;
            const int N = ChunkCtx.Size;
            var near = DeltaZones.Near(c.BaseX, c.BaseZ, c.BaseX + N - 1, c.BaseZ + N - 1);
            if (near == null) return;
            int sea = c.Cfg.SeaLevel;
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    int i = z * N + x;
                    if (c.Touched[i] || c.NearLake[i] || c.Excl.NearStructure[i] || c.Excl.WaterTopAt(x, z) >= 0) continue;
                    int h = c.Heights.Raw(x, z);
                    if (h < 0) continue;
                    int wx = c.BaseX + x, wz = c.BaseZ + z;
                    DeltaZone zone = null; double edge = double.PositiveInfinity;
                    foreach (var zn in near)
                    {
                        double e = zn.EdgeDistance(wx + 0.5, wz + 0.5);
                        if (e < edge) { edge = e; zone = zn; }
                    }
                    if (zone == null) continue;
                    double nz = WaterRules.ValueNoise(c.Seed ^ zone.Salt, wx / 9.0, wz / 9.0, 7);
                    var (surf, plant) = Decide(zone.Traits, edge, h, sea, nz, WaterRules.Hash01(c.Seed, wx, wz, 71), WaterRules.Hash01(c.Seed, wx, wz, 73));
                    if (surf == Surface.None && plant == Plant.None) continue;
                    Block top;
                    try { top = c.Ba.GetBlock(new Vintagestory.API.MathTools.BlockPos(wx, h, wz), BlockLayersAccess.Solid); } catch (Exception) { continue; }
                    var mat = top?.BlockMaterial ?? EnumBlockMaterial.Air;
                    if (mat != EnumBlockMaterial.Soil && mat != EnumBlockMaterial.Sand && mat != EnumBlockMaterial.Gravel) continue;
                    int id = 0;
                    if (surf != Surface.None)
                    {
                        string rock = LakePass.RockBelow(c, wx, h, wz);
                        id = SurfaceId(c.Blocks, surf, rock);
                    }
                    int pid = PlantId(c.Blocks, plant, WaterRules.Hash01(c.Seed, wx, wz, 79));
                    if (id == 0 && pid == 0) continue;
                    // Loose plants on the old top first (Commit writes in order; a planned plant replaces the air).
                    c.ClearPlantAt(wx, h + 1, wz);
                    if (id > 0) c.Set(wx, h, wz, id);
                    if (pid > 0) { c.Set(wx, h + 1, wz, pid); c.Stats.DeltaPlants++; }
                    c.Touched[i] = true;
                    c.Stats.DeltaColumns++;
                }
        }

        static int SurfaceId(BlockCodes b, Surface s, string rock)
        {
            string r = string.IsNullOrEmpty(rock) ? "granite" : rock;
            switch (s)
            {
                case Surface.Sand: return First(b, "sand-" + r, "sand-granite");
                case Surface.Mud: return First(b, "muddygravel", "peat-none", "sand-" + r);
                case Surface.Gravel: return First(b, "gravel-" + r, "gravel-granite");
                case Surface.Rock: return First(b, "rock-" + r, "gravel-" + r);
                default: return 0;
            }
        }

        static int PlantId(BlockCodes b, Plant p, double h)
        {
            switch (p)
            {
                case Plant.Reeds:
                    if (b.PlantIds.Length > 0) return b.PlantIds[Math.Min(b.PlantIds.Length - 1, (int)(h * b.PlantIds.Length))];
                    return First(b, "tallgrass-tall-free");
                case Plant.TallGrass: return h < 0.7 ? First(b, "tallgrass-tall-free", "tallgrass-medium-free") : First(b, "tallgrass-medium-free", "tallgrass-tall-free");
                case Plant.SparseGrass: return First(b, "tallgrass-short-free", "tallgrass-veryshort-free", "tallgrass-medium-free");
                default: return 0;
            }
        }

        static int First(BlockCodes b, params string[] codes)
        {
            foreach (var code in codes) { int id = b.IdOf(code, false); if (id > 0) return id; }
            return 0;
        }
    }
}
