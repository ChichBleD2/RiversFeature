using System;
using System.Collections.Generic;

namespace RiverFeatures.Rivers
{
    /// <summary>
    /// Estimate of the terrain Rivers will generate, available while the river NETWORK is built (before any chunk exists).
    /// It mirrors the two inputs Rivers' terrain uses (read from Rivers 5.0.2 IL, NewGenTerra / RiversApi.ModifyLandformWeights):
    ///  - the landform at the column (the game's landform map; per-Y solid probability from its terrainYKey thresholds), and
    ///  - Rivers' valley: within maxValleyWidth of a channel every landform weight is multiplied by
    ///    W = lerp(strength, 1, riverDistance / maxValleyWidth)^2 and Rivers' flat "riverlandform" gets 1 - W
    ///    (strength from Rivers' valley noise, 0.02..1 - valleyStrengthMin).
    /// Ground spans the heights where the blended solid probability is between 0.5 +- 0.3 x NoiseShare (0.2..0.8 at 1): the
    /// landform's noise turns such a band into plateaus and terraces. Not exact (no 3D noise, no upheaval): a conservative screen
    /// for steep or high ground.
    /// Rivers' water is ALWAYS at sea level (NewGenTerra fills carved air below seaLevel only), so "rise" = ground above sea level.
    /// </summary>
    public sealed class TerrainModel
    {
        public int SeaLevel = 110;
        /// <summary>World (x, z) -> per-Y solid probability of the landform there (length = map height); null = unknown.</summary>
        public Func<double, double, float[]> Land;
        /// <summary>Rivers' riverlandform per-Y solid probability (null = a flat step at sea level + 4).</summary>
        public float[] River;
        /// <summary>World (x, z) -> Rivers' valley strength (0.02..1).</summary>
        public Func<double, double, double> Strength = (x, z) => 0.3;
        public double MaxValleyWidth = 150;
        public double NoiseShare = 1;

        /// <summary>Estimated lowest and highest ground (block Y) near world (x, z), <paramref name="edgeDistance"/> blocks from the nearest channel edge.</summary>
        public bool At(double x, double z, double edgeDistance, out double lo, out double hi)
        {
            lo = hi = SeaLevel;
            var land = Land?.Invoke(x, z);
            if (land == null || land.Length < 4) return false;
            double w = 1;
            if (edgeDistance < MaxValleyWidth)
            {
                double s = Math.Clamp(Strength?.Invoke(x, z) ?? 0.3, 0.02, 1.0);
                double v = s + (1 - s) * Math.Clamp(edgeDistance / MaxValleyWidth, 0, 1);
                w = v * v;
            }
            int n = land.Length;
            double Blend(int y)
            {
                double r = River != null && y < River.Length ? River[y] : (y <= SeaLevel + 4 ? 1 : 0);
                return w * land[y] + (1 - w) * r;
            }
            // Landform noise is smooth in space, so a height band where the solid probability is between 0.2 and 0.8 becomes
            // plateaus and terraces, not an average: count ground up to probability 0.5 - 0.3 x NoiseShare.
            double q = 0.3 * Math.Clamp(NoiseShare, 0, 1);
            hi = Cross(Blend, n, 0.5 - q); lo = Cross(Blend, n, 0.5 + q);
            return true;
        }

        /// <summary>Highest Y (interpolated) at which the solid probability is still at least <paramref name="v"/>.</summary>
        static double Cross(Func<int, double> t, int n, double v)
        {
            for (int y = n - 2; y >= 0; y--)
            {
                double a = t(y);
                if (a < v) continue;
                double b = t(y + 1);
                return a - b > 1e-9 ? y + (a - v) / (a - b) : y;
            }
            return 0;
        }

        /// <summary>Per-Y solid probability from a landform's terrainYKeyPositions (fractions of map height) / thresholds, linearly interpolated.</summary>
        public static float[] KeyThresholds(float[] positions, float[] thresholds, int mapSizeY)
        {
            var t = new float[mapSizeY];
            if (positions == null || thresholds == null || positions.Length == 0 || positions.Length != thresholds.Length) return null;
            for (int y = 0; y < mapSizeY; y++)
            {
                double p = y / (double)mapSizeY;
                if (p <= positions[0]) { t[y] = thresholds[0]; continue; }
                if (p >= positions[^1]) { t[y] = thresholds[^1]; continue; }
                int k = 1;
                while (k < positions.Length - 1 && positions[k] < p) k++;
                double span = positions[k] - positions[k - 1];
                double f = span > 1e-9 ? (p - positions[k - 1]) / span : 1;
                t[y] = (float)(thresholds[k - 1] + (thresholds[k] - thresholds[k - 1]) * f);
            }
            return t;
        }
    }

    /// <summary>Terrain checks on a river tree (positions are Rivers-local; <c>origin</c> converts them to world coordinates).</summary>
    public static class TerrainRules
    {
        /// <summary>Blocks from p to the nearest channel EDGE (0 inside a channel) over the tree and any extra chains.</summary>
        public static double EdgeDistance(P2 p, IReadOnlyList<NetNode> nodes, ICollection<int> skip = null, IReadOnlyList<(List<P2> pts, double size)> extra = null)
        {
            double best = double.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (skip != null && skip.Contains(i)) continue;
                var n = nodes[i];
                var ab = n.End - n.Start; double l2 = P2.Dot(ab, ab);
                double t = l2 < 1e-12 ? 0 : Math.Clamp(P2.Dot(p - n.Start, ab) / l2, 0, 1);
                double size = n.StartSize + (n.EndSize - n.StartSize) * t;
                best = Math.Min(best, (p - (n.Start + ab * t)).Length - size);
            }
            if (extra != null)
                foreach (var (pts, size) in extra)
                    for (int k = 0; k + 1 < pts.Count; k++)
                        best = Math.Min(best, RiverNet.DistToSegment(p, pts[k], pts[k + 1]) - size);
            return Math.Max(0, best);
        }

        /// <summary>
        /// Ground above the water (sea level) and the spread of ground heights around an oxbow: samples every ~10 blocks along
        /// the horseshoe, on its centre line and on both banks <paramref name="bank"/> blocks beyond its edge. Returns
        /// max(ground, sea) - min(ground, sea): the height difference across the oxbow footprint and its banks, water included.
        /// NaN when the terrain is unknown.
        /// </summary>
        public static double OxbowRelief(List<P2> chain, double size, double bank, IReadOnlyList<NetNode> nodes, TerrainModel m, P2 origin)
        {
            var extra = new List<(List<P2>, double)> { (chain, size) };
            double max = m.SeaLevel, min = m.SeaLevel;
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                var a = chain[k]; var b = chain[k + 1];
                var dir = (b - a).Normalized(); var side = new P2(-dir.Z, dir.X);
                int steps = Math.Max(1, (int)Math.Ceiling((b - a).Length / 10));
                for (int s = 0; s <= steps; s++)
                {
                    var c = a + (b - a) * (s / (double)steps);
                    foreach (double off in new[] { 0.0, size + bank, -(size + bank) })
                    {
                        var p = c + side * off;
                        if (!m.At(origin.X + p.X, origin.Z + p.Z, EdgeDistance(p, nodes, null, extra), out double lo, out double hi)) return double.NaN;
                        max = Math.Max(max, hi); min = Math.Min(min, lo);
                    }
                }
            }
            return max - min;
        }

        /// <summary>
        /// Highest ground above the water (sea level) along the upstream (source) half of node i and on both banks
        /// <paramref name="bank"/> blocks beyond its edge. NaN when the terrain is unknown.
        /// </summary>
        public static double HeadwaterRise(IReadOnlyList<NetNode> nodes, int i, double bank, TerrainModel m, P2 origin, ICollection<int> removed)
        {
            var n = nodes[i];
            var dir = n.UpDir; var side = new P2(-dir.Z, dir.X);
            double max = double.NegativeInfinity;
            for (int s = 0; s <= 5; s++)
            {
                double t = 0.5 + 0.1 * s;
                var c = n.Start + (n.End - n.Start) * t;
                double size = n.StartSize + (n.EndSize - n.StartSize) * t;
                foreach (double off in new[] { 0.0, size + bank, -(size + bank) })
                {
                    var p = c + side * off;
                    if (!m.At(origin.X + p.X, origin.Z + p.Z, EdgeDistance(p, nodes, removed), out _, out double hi)) return double.NaN;
                    max = Math.Max(max, hi);
                }
            }
            return max - m.SeaLevel;
        }

        /// <summary>
        /// Headwater tips to remove: starting at every source tip (a river node, not the mouth, with no children), the tip is
        /// removed while <paramref name="tooSteep"/> says so, then its parent becomes the tip if it has no other children; at
        /// most <paramref name="maxPerTip"/> nodes per tip, and the river keeps at least <paramref name="minKeep"/> nodes.
        /// </summary>
        public static HashSet<int> HeadwatersToTrim(IReadOnlyList<NetNode> nodes, Func<int, ICollection<int>, bool> tooSteep, int maxPerTip, int minKeep)
        {
            var removed = new HashSet<int>();
            if (maxPerTip <= 0) return removed;
            var children = RiverNet.Children(nodes);
            var alive = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) alive[i] = children[i].Count;
            int left = nodes.Count;
            for (int tip = 0; tip < nodes.Count; tip++)
            {
                if (nodes[tip].IsLake || alive[tip] != 0 || removed.Contains(tip)) continue;
                int cur = tip;
                for (int k = 0; k < maxPerTip; k++)
                {
                    int p = nodes[cur].Parent;
                    if (p < 0 || p >= nodes.Count || left <= minKeep || alive[cur] != 0 || nodes[cur].IsLake) break;
                    if (!tooSteep(cur, removed)) break;
                    removed.Add(cur); left--;
                    alive[p]--;
                    cur = p;
                }
            }
            return removed;
        }

        /// <summary>
        /// Flow speed per node (Rivers' node.speed, 1 everywhere by default; the client draws and pushes with flow x riverSpeed):
        /// minSpeed at a source, rising linearly with the distance from the node's middle up to the farthest source, reaching 1
        /// at <paramref name="calmBlocks"/>. Lakes keep 0. Returns one value per node.
        /// </summary>
        public static float[] HeadwaterSpeeds(IReadOnlyList<NetNode> nodes, Hierarchy h, double calmBlocks, double minSpeed)
        {
            var sp = new float[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].IsLake) { sp[i] = 0; continue; }
                if (calmBlocks <= 0) { sp[i] = 1; continue; }
                double up = h.Upstream[i] - nodes[i].Length / 2;
                sp[i] = (float)Math.Clamp(minSpeed + (1 - minSpeed) * up / calmBlocks, minSpeed, 1.0);
            }
            return sp;
        }
    }
}
