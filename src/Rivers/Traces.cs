using System;
using System.Collections.Generic;

namespace RiverFeatures.Rivers
{
    /// <summary>Kinds of traces of the past.</summary>
    public enum TraceKind
    {
        /// <summary>An abandoned side channel: a dry, gravelled scar running beside the river, detached from it.</summary>
        Channel,
        /// <summary>A backwater: a short dead-end arm of still water leaving the river (a cut-off bend that stayed connected).</summary>
        Backwater,
        /// <summary>A floodplain pocket: a small still pond beside the river, left by an old flood.</summary>
        Pocket,
    }

    /// <summary>One planned trace: a chain of points (Rivers-local), its half-width, the river node it belongs to.</summary>
    public readonly record struct Trace(TraceKind Kind, List<P2> Points, float Size, int Node);

    public sealed class TraceOptions
    {
        /// <summary>Chance per eligible river node (main stems and first-level branches by default).</summary>
        public double Chance = 0.25;
        public int MaxLevel = 1;
        public float MinRiverSize = 10;
        /// <summary>Least land (blocks between edges) from other channels, lakes and features; the backwater's own join excepted.</summary>
        public double Clearance = 24;
        /// <summary>How strongly the climate shifts the mix (0 = climate-blind: about 41% dry channels, 26% backwaters, 32% pockets).</summary>
        public double ClimateInfluence = 0.7;
    }

    /// <summary>
    /// Stage 2: "traces of the past" for Rivers' model, where all water lies at sea level and channels are carved into the
    /// ground. Nothing here moves a river; each trace is a short extra node, so Rivers carves and smooths it like any channel:
    ///  - Channel: a dry side channel beside the river (dry cap: floor above the sea, gravelled, no water);
    ///  - Backwater: a still dead-end arm joined to the river (water at the river's level);
    ///  - Pocket: one to three small still ponds on the floodplain.
    /// Dry climates favour old dry channels, wet ones backwaters and pockets (soft, by ClimateInfluence). Every trace keeps
    /// <see cref="TraceOptions.Clearance"/> of land from the river tree (except a backwater's own join), from the
    /// <paramref name="extra"/> features already placed, and from the traces planned before it; the caller's
    /// <paramref name="accept"/> adds the terrain check and the other rivers and lakes of the region. Pure, deterministic.
    /// </summary>
    public static class Traces
    {
        public static List<Trace> Plan(IReadOnlyList<NetNode> nodes, Hierarchy h, TraceOptions o, Func<int, ClimateSample> climate,
            Func<int, int, double> rnd, IReadOnlyList<LakeShapes.Obstacle> extra, Func<Trace, bool> accept)
        {
            var result = new List<Trace>();
            if (o == null || o.Chance <= 0 || nodes == null) return result;
            var placed = new List<LakeShapes.Obstacle>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                int pi = n.Parent;
                if (n.IsLake || pi < 0 || pi >= nodes.Count || nodes[pi].IsLake) continue;
                if (h.Level[i] > o.MaxLevel || n.StartSize < o.MinRiverSize || n.Length < 40) continue;
                if (rnd(i, 1) >= o.Chance) continue;

                var kind = PickKind(climate?.Invoke(i) ?? ClimateSample.Unknown, o.ClimateInfluence, rnd(i, 2));
                var t = Build(kind, n, i, rnd);
                if (t == null) continue;
                var tr = t.Value;
                if (!ClearOfTree(tr, nodes, o.Clearance)) continue;
                var pieces = AsPieces(tr);
                var others = new List<LakeShapes.Obstacle>(placed);
                if (extra != null) others.AddRange(extra);
                if (others.Count > 0 && LakeShapes.Clearance(pieces, others, tr.Points[0], 0) < o.Clearance) continue;
                if (accept != null && !accept(tr)) continue;
                result.Add(tr);
                foreach (var p in pieces) placed.Add(new LakeShapes.Obstacle(p.A, p.B, p.SizeA, p.SizeB));
            }
            return result;
        }

        /// <summary>The trace kind for a climate: weights dry 0.2 + aridity, backwater 0.15 + 0.6 wetness, pocket 0.15 + 0.8 wetness.</summary>
        public static TraceKind PickKind(ClimateSample c, double influence, double u)
        {
            double arid = ClimateBias.Soft(ClimateBias.Aridity(c), 0.5, influence);
            double wDry = 0.2 + arid, wBack = 0.15 + 0.6 * (1 - arid), wPocket = 0.15 + 0.8 * (1 - arid);
            double x = Math.Clamp(u, 0, 0.999999) * (wDry + wBack + wPocket);
            return x < wDry ? TraceKind.Channel : x < wDry + wBack ? TraceKind.Backwater : TraceKind.Pocket;
        }

        static Trace? Build(TraceKind kind, NetNode n, int i, Func<int, int, double> rnd)
        {
            double R(int salt, double lo, double hi) => lo + (hi - lo) * rnd(i, salt);
            var dir = n.UpDir;
            if (dir.Length < 0.5) return null;
            var side = new P2(-dir.Z, dir.X) * (rnd(i, 3) < 0.5 ? 1 : -1);
            double L = n.Length, w = n.StartSize;
            switch (kind)
            {
                case TraceKind.Channel:
                {
                    // An old course beside the river: offset past the bank, bulging out in the middle, 5 points.
                    float size = (float)Math.Clamp(w * 0.4, 4, 10);
                    double gap = w + size + R(10, 14, 34), bulge = R(11, 6, 22);
                    double t0 = R(12, 0.1, 0.3), t1 = Math.Min(0.95, t0 + R(13, 0.45, 0.65));
                    var pts = new List<P2>(5);
                    for (int k = 0; k < 5; k++)
                    {
                        double f = k / 4.0, t = t0 + (t1 - t0) * f;
                        pts.Add(n.Start + dir * (L * t) + side * (gap + bulge * Math.Sin(Math.PI * f)));
                    }
                    return new Trace(kind, pts, size, i);
                }
                case TraceKind.Backwater:
                {
                    // Leaves the river at the bank, heading out and slightly downstream, curving; 4 points, 30..70 blocks.
                    float size = (float)Math.Clamp(w * 0.35, 4, 9);
                    double t = R(20, 0.3, 0.7), len = R(21, 30, 70), turn = R(22, 25, 50), bend = R(23, -25, 25);
                    var a0 = n.Start + dir * (L * t);
                    var d = (side * Math.Cos(turn * Math.PI / 180) + dir * (-Math.Sin(turn * Math.PI / 180))).Normalized();
                    var pts = new List<P2> { a0 };
                    var p = a0 + d * (w * 0.5);
                    pts.Add(p);
                    for (int k = 0; k < 2; k++) { d = d.Rotated(bend * 0.5); p = p + d * (len / 2); pts.Add(p); }
                    return new Trace(kind, pts, size, i);
                }
                default:
                {
                    // A small pond on the floodplain: one short node.
                    float size = (float)R(30, 4, 8);
                    double t = R(31, 0.2, 0.8), off = w + size + R(32, 18, 45), len = R(33, 8, 20);
                    var c = n.Start + dir * (L * t) + side * off;
                    var e = dir.Rotated(R(34, -60, 60));
                    return new Trace(kind, new List<P2> { c - e * (len / 2), c + e * (len / 2) }, size, i);
                }
            }
        }

        /// <summary>
        /// Every point keeps clearance from every tree node's edge, except from its own river (see the comment inside); a
        /// backwater's first two points (its join) are exempt from its own river.
        /// </summary>
        static bool ClearOfTree(Trace t, IReadOnlyList<NetNode> nodes, double clearance)
        {
            for (int k = 0; k < t.Points.Count; k++)
            {
                var p = t.Points[k];
                for (int j = 0; j < nodes.Count; j++)
                {
                    var nd = nodes[j];
                    double edge = RiverNet.DistToSegment(p, nd.Start, nd.End) - Math.Max(nd.StartSize, nd.EndSize) - t.Size;
                    // Its own river (the node, the next node down, the nodes joining at its upstream end): a trace lies on
                    // that river's floodplain, so it keeps at least 10 blocks (backwater: 4 past its join), not the full clearance.
                    bool own = j == t.Node || j == nodes[t.Node].Parent || nd.Parent == t.Node;
                    if (t.Kind == TraceKind.Backwater && own)
                    {
                        if (k < 2) continue;
                        if (edge < 4) return false;
                        continue;
                    }
                    if (edge < (own ? Math.Min(clearance, 10) : clearance)) return false;
                }
            }
            return true;
        }

        /// <summary>The trace as capsule pieces (for LakeShapes.Clearance).</summary>
        public static List<LakeShapes.Piece> AsPieces(Trace t)
        {
            var list = new List<LakeShapes.Piece>();
            for (int k = 0; k + 1 < t.Points.Count; k++) list.Add(new LakeShapes.Piece(t.Points[k], t.Points[k + 1], t.Size, t.Size, k - 1));
            return list;
        }
    }
}
