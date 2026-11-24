using System;
using System.Collections.Generic;

namespace RiverFeatures.Rivers
{
    /// <summary>What a generated source lake ended up looking like (statistics and /rffind only).</summary>
    public enum LakeShape { Teardrop, Oval, Finger, Winding, Lobed }

    /// <summary>
    /// Source lakes (flight tests 12, 13). Rivers' AddLake makes every source lake ONE straight node 100 blocks long whose width
    /// grows from minSize/2 to 50..75: a teardrop every time. This builds a procedural lake instead: a curved spine of 1..4
    /// pieces (each turn drawn from a shared curvature plus jitter, sometimes flipping into an S), a width at every spine
    /// vertex, and 0..2 side lobes of 1..2 curved pieces. Pieces are capsules (A to B, half-width SizeA to SizeB) that Rivers
    /// samples as still lake water; ChainTo &gt;= 0 continues that piece through Rivers' connector sampling (smooth joins),
    /// ChainTo = -1 for the first piece (keeps Rivers' link to the river) and for lobes, which start inside the lake.
    /// <see cref="Fit"/> then keeps the lake clear of other river channels. Pure.
    /// </summary>
    public static class LakeShapes
    {
        public readonly record struct Piece(P2 A, P2 B, float SizeA, float SizeB, int ChainTo);

        /// <summary>A river channel (or another lake) the new lake must keep clear of: a capsule with half-widths.</summary>
        /// <summary>Own = part of the lake's own river (the only kind the join zone at the start exempts).</summary>
        public readonly record struct Obstacle(P2 A, P2 B, float SizeA, float SizeB, bool Own = false);

        /// <summary>Furthest any piece reaches from the start (Rivers' river radius margin is +512).</summary>
        public const double MaxReach = 300;

        /// <param name="start">Rivers' lake start (the river's source end).</param>
        /// <param name="dir">Unit direction Rivers gave the lake.</param>
        /// <param name="s0">Rivers' start width (minSize / 2).</param>
        /// <param name="e">Rivers' end width (50..75).</param>
        /// <param name="rnd">Deterministic random in [0, 1) per salt.</param>
        public static List<Piece> Procedural(P2 start, P2 dir, float s0, float e, Func<int, double> rnd)
        {
            double R(int salt, double lo, double hi) => lo + (hi - lo) * rnd(salt);
            var list = new List<Piece>();

            // Spine.
            double u = rnd(1);
            int n = u < 0.25 ? 1 : u < 0.55 ? 2 : u < 0.8 ? 3 : 4;
            double curve = R(2, -35, 35);
            bool roundStart = n == 1 && rnd(3) < 0.6;          // a single wide piece: round or oval
            var p = start;
            var d = dir;
            float w = roundStart ? e * (float)R(4, 0.45, 0.8) : s0;
            double spine = 0;
            var verts = new List<(P2 at, P2 dir, float w)> { (p, d, w) };
            for (int k = 0; k < n; k++)
            {
                if (k > 0 && rnd(10 + k) < 0.35) curve = -curve;  // S-bend
                d = d.Rotated(curve * R(20 + k, 0.5, 1.2) + R(30 + k, -15, 15));
                double len = n == 1 ? R(40 + k, 45, 110) : R(40 + k, 35, 90);
                len = Math.Min(len, Math.Max(20, 230 - spine));
                var q = p + d * len;
                float w2 = e * (float)R(50 + k, 0.35, 0.85);
                list.Add(new Piece(p, q, w, w2, k - 1));
                spine += len;
                p = q; w = w2;
                verts.Add((p, d, w));
            }

            // Side lobes.
            double lu = rnd(60);
            int lobes = lu < 0.45 ? 0 : lu < 0.8 ? 1 : 2;
            double side = rnd(61) < 0.5 ? -1 : 1;
            for (int j = 0; j < lobes; j++)
            {
                // Root somewhere along the spine (never at the river end), turned out to one side.
                int seg = Math.Min(list.Count - 1, (int)(R(70 + j, 0.2, 1.0) * list.Count));
                var sp = list[seg];
                double t = R(72 + j, 0.4, 1.0);
                var root = sp.A + (sp.B - sp.A) * t;
                float rootW = sp.SizeA + (sp.SizeB - sp.SizeA) * (float)t;
                var ld = (sp.B - sp.A).Normalized().Rotated((j == 0 ? side : -side) * R(74 + j, 40, 110));
                int parts = rnd(76 + j) < 0.5 ? 1 : 2;
                double lcurve = R(78 + j, -30, 30);
                var lp = root;
                float lw = rootW * 0.6f;
                int prev = -1;
                for (int k = 0; k < parts; k++)
                {
                    if (k > 0) ld = ld.Rotated(lcurve);
                    var lq = lp + ld * R(80 + 2 * j + k, 25, 70);
                    float lw2 = e * (float)R(90 + 2 * j + k, 0.3, 0.6);
                    list.Add(new Piece(lp, lq, lw, lw2, prev));
                    prev = list.Count - 1;
                    lp = lq; lw = lw2;
                }
            }
            return list;
        }

        /// <summary>A small round lake at the source: the last resort when nothing larger keeps clear of the rivers.</summary>
        public static List<Piece> Pond(P2 start, P2 dir, float e) =>
            new List<Piece> { new Piece(start, start + dir * 18, e * 0.3f, e * 0.32f, -1) };

        /// <summary>The plan scaled about the start (lengths and widths).</summary>
        public static List<Piece> Scaled(List<Piece> plan, P2 start, double k)
        {
            var r = new List<Piece>(plan.Count);
            foreach (var p in plan)
                r.Add(new Piece(start + (p.A - start) * k, start + (p.B - start) * k, (float)(p.SizeA * k), (float)(p.SizeB * k), p.ChainTo));
            return r;
        }

        /// <summary>Classifies a plan for statistics and /rffind.</summary>
        public static LakeShape Classify(List<Piece> plan)
        {
            int spine = 0; bool lobe = false;
            for (int i = 0; i < plan.Count; i++)
            {
                if (i == 0 || plan[i].ChainTo == i - 1 && !lobe) spine++;
                else lobe = true;
            }
            if (lobe) return LakeShape.Lobed;
            if (spine == 1)
            {
                var p = plan[0];
                return p.SizeA >= 0.55 * p.SizeB ? LakeShape.Oval : LakeShape.Teardrop;
            }
            double turn = 0;
            for (int i = 1; i < spine; i++)
            {
                var a = (plan[i - 1].B - plan[i - 1].A).Normalized();
                var b = (plan[i].B - plan[i].A).Normalized();
                turn += Math.Acos(Math.Clamp(a.X * b.X + a.Z * b.Z, -1, 1)) * 180 / Math.PI;
            }
            return turn >= 45 ? LakeShape.Winding : LakeShape.Finger;
        }

        /// <summary>
        /// Smallest gap (blocks between the edges) from any point of the plan to any obstacle. Only the lake's OWN river is
        /// exempt at the join: its segments whose axis passes within <paramref name="joinRadius"/> of the start, and plan
        /// points within joinRadius of the start against its other segments. Other rivers are checked everywhere, also right
        /// at the start. +inf without obstacles.
        /// </summary>
        public static double Clearance(List<Piece> plan, IReadOnlyList<Obstacle> obstacles, P2 start, double joinRadius)
        {
            double best = double.PositiveInfinity;
            // Only the lake's OWN river is exempt, and only where its axis passes within joinRadius of the start (the join).
            // Flight test 13 (299,2212): every channel near the start was exempt, so a source next to a big river clipped it.
            var near = new List<Obstacle>(obstacles.Count);
            foreach (var o in obstacles)
                if (!o.Own || DistToCapsule(start, o with { SizeA = 0, SizeB = 0 }) >= joinRadius) near.Add(o);
            obstacles = near;
            foreach (var p in plan)
            {
                double len = (p.B - p.A).Length;
                int steps = Math.Max(2, (int)Math.Ceiling(len / 8));
                for (int s = 0; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    var at = p.A + (p.B - p.A) * t;
                    bool atJoin = (at - start).Length < joinRadius;
                    double w = p.SizeA + (p.SizeB - p.SizeA) * t;
                    foreach (var o in obstacles)
                    {
                        if (atJoin && o.Own) continue;
                        double gap = DistToCapsule(at, o) - w;
                        if (gap < best) best = gap;
                    }
                }
            }
            return best;
        }

        /// <summary>Distance from a point to the edge of an obstacle capsule (negative inside).</summary>
        public static double DistToCapsule(P2 q, Obstacle o)
        {
            var ab = o.B - o.A;
            double l2 = ab.X * ab.X + ab.Z * ab.Z;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((q.X - o.A.X) * ab.X + (q.Z - o.A.Z) * ab.Z) / l2, 0, 1);
            var c = o.A + ab * t;
            return (q - c).Length - (o.SizeA + (o.SizeB - o.SizeA) * t);
        }

        public enum FitResult { Fits, Varied, Shrunk, Pond, Dropped }

        /// <summary>
        /// The first plan that keeps at least <paramref name="clear"/> blocks of land between the lake and every obstacle:
        /// the plan itself, else up to 4 other procedural variants, else the first plan shrunk (0.75, 0.55, 0.4), else a
        /// small pond at the source. The join with the lake's own river (within joinRadius of the start) is exempt.
        /// </summary>
        public static (List<Piece> plan, FitResult how) Fit(P2 start, P2 dir, float s0, float e, Func<int, double> rnd,
            IReadOnlyList<Obstacle> obstacles, double clear, double joinRadius)
        {
            var first = Procedural(start, dir, s0, e, rnd);
            if (obstacles == null || obstacles.Count == 0 || Clearance(first, obstacles, start, joinRadius) >= clear) return (first, FitResult.Fits);
            for (int v = 1; v <= 4; v++)
            {
                int off = 1000 * v;
                var alt = Procedural(start, dir, s0, e, salt => rnd(salt + off));
                if (Clearance(alt, obstacles, start, joinRadius) >= clear) return (alt, FitResult.Varied);
            }
            foreach (double k in new[] { 0.75, 0.55, 0.4 })
            {
                var sc = Scaled(first, start, k);
                if (Clearance(sc, obstacles, start, joinRadius) >= clear) return (sc, FitResult.Shrunk);
            }
            var pond = Pond(start, dir, e);
            if (Clearance(pond, obstacles, start, joinRadius) >= clear) return (pond, FitResult.Pond);
            return (null, FitResult.Dropped); // the source itself is too close to another river: no lake at all
        }
    }
}
