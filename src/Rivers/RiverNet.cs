using System;
using System.Collections.Generic;

namespace RiverFeatures.Rivers
{
    /// <summary>A 2D point/vector in Rivers' region-local block coordinates (X, Z).</summary>
    public readonly record struct P2(double X, double Z)
    {
        public static P2 operator +(P2 a, P2 b) => new P2(a.X + b.X, a.Z + b.Z);
        public static P2 operator -(P2 a, P2 b) => new P2(a.X - b.X, a.Z - b.Z);
        public static P2 operator *(P2 a, double k) => new P2(a.X * k, a.Z * k);
        public double Length => Math.Sqrt(X * X + Z * Z);
        public P2 Normalized() { double l = Length; return l > 1e-12 ? new P2(X / l, Z / l) : new P2(0, 0); }
        public P2 Rotated(double degrees)
        {
            double r = degrees * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r);
            return new P2(X * c - Z * s, X * s + Z * c);
        }
        public static double Cross(P2 a, P2 b) => a.X * b.Z - a.Z * b.X;
        public static double Dot(P2 a, P2 b) => a.X * b.X + a.Z * b.Z;
    }

    /// <summary>
    /// One Rivers node as this mod sees it. Rivers' convention (read from Rivers 5.0.2 IL): a river grows UPSTREAM from the
    /// coast, so <see cref="Start"/> is the node's downstream end and <see cref="End"/> its upstream end; <see cref="Parent"/> is
    /// the next node downstream (-1 at the mouth); <see cref="StartSize"/> (downstream) &gt;= <see cref="EndSize"/> (upstream).
    /// </summary>
    public sealed class NetNode
    {
        public P2 Start, End;
        public int Parent = -1;
        public float StartSize, EndSize;
        public bool IsLake;
        public double Length => (End - Start).Length;
        public P2 UpDir => (End - Start).Normalized();
    }

    /// <summary>The hierarchy of one river tree.</summary>
    public sealed class Hierarchy
    {
        /// <summary>0 = main stem from the mouth; +1 for every confluence where the branch was the secondary one.</summary>
        public int[] Level;
        /// <summary>The child that continues the main stem at each node (-1 = none).</summary>
        public int[] MainChild;
        /// <summary>Longest path (blocks) from the node's downstream end up to a source.</summary>
        public double[] Upstream;
        public List<int>[] Children;
    }

    /// <summary>Delta type (flight test 9): a simple mouth with a small delta, a carving delta, or a slow, shallow, flat delta.</summary>
    public enum DeltaKind { Small, Carving, Shallow }

    /// <summary>A delta distributary to add, fork to sea: Start = downstream (seaward) end, End = the fork.</summary>
    public readonly record struct Channel(P2 Start, P2 End, float Size, DeltaKind Kind = DeltaKind.Carving, float Speed = 1f);

    /// <summary>
    /// Pure river-network rules (no game or Rivers types), unit tested. The Harmony adapter copies Rivers' nodes in and the
    /// results back out.
    /// </summary>
    public static class RiverNet
    {
        public static List<int>[] Children(IReadOnlyList<NetNode> nodes)
        {
            var ch = new List<int>[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) ch[i] = new List<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                int p = nodes[i].Parent;
                if (p >= 0 && p < nodes.Count && p != i) ch[p].Add(i);
            }
            return ch;
        }

        /// <summary>
        /// Main stem and levels. At every confluence the child with the longest upstream path continues the main stem (ties:
        /// the straighter continuation of the node's own direction, then the lower index); every other child starts a branch
        /// one level higher, and the rule cascades up every branch. Lakes count with length 0. Iterative (no recursion).
        /// </summary>
        public static Hierarchy Classify(IReadOnlyList<NetNode> nodes)
        {
            int n = nodes.Count;
            var h = new Hierarchy { Level = new int[n], MainChild = new int[n], Upstream = new double[n], Children = Children(nodes) };
            Array.Fill(h.MainChild, -1);
            var order = TopDown(nodes, h.Children);
            for (int k = order.Count - 1; k >= 0; k--) // bottom-up: children before parents
            {
                int i = order[k];
                double best = 0;
                foreach (int c in h.Children[i]) best = Math.Max(best, h.Upstream[c]);
                h.Upstream[i] = (nodes[i].IsLake ? 0 : nodes[i].Length) + best;
            }
            foreach (int i in order)
            {
                int main = -1;
                double bestUp = -1, bestDot = double.NegativeInfinity;
                var dir = nodes[i].UpDir;
                foreach (int c in h.Children[i])
                {
                    double up = h.Upstream[c], dot = P2.Dot(dir, nodes[c].UpDir);
                    bool better = up > bestUp + 1e-6 || (Math.Abs(up - bestUp) <= 1e-6 && (dot > bestDot + 1e-9 || (Math.Abs(dot - bestDot) <= 1e-9 && (main < 0 || c < main))));
                    if (better) { main = c; bestUp = up; bestDot = dot; }
                }
                h.MainChild[i] = main;
                foreach (int c in h.Children[i]) h.Level[c] = h.Level[i] + (c == main ? 0 : 1);
            }
            return h;
        }

        /// <summary>Nodes ordered from the roots (Parent -1 or invalid) downward in the tree; nodes in a cycle are left out.</summary>
        static List<int> TopDown(IReadOnlyList<NetNode> nodes, List<int>[] children)
        {
            var order = new List<int>(nodes.Count);
            var seen = new bool[nodes.Count];
            var queue = new Queue<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                int p = nodes[i].Parent;
                if (p < 0 || p >= nodes.Count || p == i) { queue.Enqueue(i); seen[i] = true; }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                order.Add(i);
                foreach (int c in children[i]) if (!seen[c]) { seen[c] = true; queue.Enqueue(c); }
            }
            return order;
        }

        /// <summary>
        /// Width hierarchy: a secondary branch is never wider than min(TributaryMaxSize, share x the width of the river it joins
        /// at the junction), at least TributaryMinSize (and never wider than it already was); the cap cascades up the branch and
        /// halves again at every further secondary confluence. The main stem keeps Rivers' widths. Lake nodes keep their sizes
        /// (their size is the lake's radius) but pass the cap on. Returns the number of nodes narrowed.
        /// </summary>
        public static int CapTributaries(IReadOnlyList<NetNode> nodes, Hierarchy h, float maxSize, float share, float minSize)
        {
            int n = nodes.Count, narrowed = 0;
            var limit = new float[n];
            Array.Fill(limit, float.PositiveInfinity);
            foreach (int i in TopDown(nodes, h.Children))
            {
                foreach (int c in h.Children[i])
                {
                    float lim = limit[i];
                    if (c != h.MainChild[i])
                    {
                        float atJunction = nodes[i].IsLake ? limit[i] : nodes[i].EndSize;
                        float cap = Math.Max(minSize, Math.Min(maxSize, atJunction * share));
                        lim = Math.Min(lim, cap);
                    }
                    limit[c] = lim;
                    var node = nodes[c];
                    if (node.IsLake || float.IsPositiveInfinity(lim)) continue;
                    float s = Math.Min(node.StartSize, lim), e = Math.Min(node.EndSize, lim);
                    if (s < node.StartSize || e < node.EndSize) narrowed++;
                    node.StartSize = s;
                    node.EndSize = Math.Min(e, s);
                }
            }
            return narrowed;
        }

        /// <summary>
        /// Stage given to the SECONDARY branch of a fork so it keeps only <paramref name="share"/> of the main branch's remaining
        /// node budget (Rivers stops a branch once its stage exceeds maxNodes). share 1 = Rivers' symmetric forks.
        /// </summary>
        public static int SecondaryStage(int nextStage, int maxNodes, double share)
        {
            if (double.IsNaN(share)) share = 1;
            share = Math.Clamp(share, 0.0, 1.0);
            int remaining = Math.Max(0, maxNodes - nextStage);
            int stage = nextStage + (int)Math.Round(remaining * (1.0 - share), MidpointRounding.AwayFromZero);
            return Math.Min(stage, maxNodes + 1);
        }

        /// <summary>
        /// Rivers' node budget (maxNodes) for a target longest-stem length: round(length / mean node length), 1..400, and at
        /// least <paramref name="minNodes"/>. Rivers (GenerateZones, read from its IL) removes every river with fewer than
        /// minNodes nodes before sizing it, so a budget below minNodes (a length under about 2000 blocks with Rivers'
        /// defaults) would remove every river: the length setting must never change how many rivers there are.
        /// </summary>
        public static int MaxNodesFor(int lengthBlocks, int minLength, int lengthVariation, int minNodes = 1)
        {
            double mean = Math.Max(1.0, minLength + lengthVariation / 2.0);
            int n = Math.Clamp((int)Math.Round(lengthBlocks / mean, MidpointRounding.AwayFromZero), 1, 400);
            return Math.Max(n, Math.Clamp(minNodes, 1, 400));
        }

        /// <summary>
        /// Delta distributaries at a river mouth. <paramref name="mouth"/> is the root node (Parent -1, not a lake); its Start is
        /// the mouth and the sea lies opposite its UpDir. Chance = deltaChance x (0.5 + mouthSize / riversMaxSize), so large rivers
        /// get deltas more often; none below minRiverSize. 2..maxBranches channels fan out over +-spread degrees (jittered), each
        /// minLength..maxLength long and widthShare x the mouth's width (at least minBranchSize). rnd(salt) must be a
        /// deterministic value in [0, 1) for this mouth.
        /// </summary>
        public static List<Channel> Delta(NetNode mouth, float riversMaxSize, DeltaOptions o, Func<int, double> rnd)
        {
            var list = new List<Channel>();
            if (mouth == null || mouth.IsLake || mouth.StartSize < o.MinRiverSize || o.Chance <= 0) return list;
            double p = Math.Clamp(o.Chance * (0.5 + mouth.StartSize / Math.Max(1f, riversMaxSize)), 0, 1);
            if (rnd(1) >= p) return list;
            var sea = mouth.UpDir * -1;
            if (sea.Length < 0.5) return list;
            int k = Math.Clamp(o.MinBranches + (int)(rnd(2) * (o.MaxBranches - o.MinBranches + 1)), 2, 8);
            float size = Math.Max(o.MinBranchSize, mouth.StartSize * o.WidthShare);
            for (int b = 0; b < k; b++)
            {
                double t = k == 1 ? 0.5 : b / (double)(k - 1);
                double angle = -o.SpreadDegrees + t * 2 * o.SpreadDegrees + (rnd(10 + b) - 0.5) * (o.SpreadDegrees / k);
                double len = o.MinLength + rnd(20 + b) * (o.MaxLength - o.MinLength);
                list.Add(new Channel(mouth.Start + sea.Rotated(angle) * len, mouth.Start, size));
            }
            return list;
        }

        /// <summary>
        /// Where the river crosses the coast: walking from the mouth node's Start (out at sea, Rivers starts rivers in ocean
        /// zones) up the main stem in 8-block steps, the first point where <paramref name="ocean"/> drops below the threshold.
        /// Returns the coast point and the seaward direction (the river's own direction there, reversed), or null when the
        /// river never reaches land within 2000 blocks or starts on land. Pure.
        /// </summary>
        public static (P2 Coast, P2 SeaDir)? FindCoast(IReadOnlyList<NetNode> nodes, Hierarchy h, int mouth, Func<P2, double> ocean, double threshold)
        {
            if (mouth < 0 || mouth >= nodes.Count || ocean == null || nodes[mouth].IsLake) return null;
            var path = new List<P2> { nodes[mouth].Start };
            for (int i = mouth, k = 0; i >= 0 && k < 12; i = h.MainChild[i], k++) path.Add(nodes[i].End);
            if (!(ocean(path[0]) >= threshold)) return null;   // the mouth is not at sea
            double walked = 0;
            for (int s = 0; s + 1 < path.Count; s++)
            {
                var a = path[s]; var b = path[s + 1];
                double len = (b - a).Length;
                if (len < 1e-6) continue;
                for (double t = 0; t < len; t += 8)
                {
                    var q = a + (b - a) * (t / len);
                    if (ocean(q) < threshold) return (q, ((a - b) * (1 / len)).Normalized());
                    if ((walked += 8) > 2000) return null;
                }
            }
            return null;
        }

        /// <summary>Flight test 17b: least angle (degrees) between two delta channels, and between a channel and the main stem.</summary>
        public const double MinBranchSeparation = 12;

        /// <summary>
        /// Delta on LAND at the coast (flight test 7). Rivers starts a river in an ocean zone, so its mouth node begins out at
        /// sea and the 0.1.x deltas, which fanned seaward from there, lay entirely in the ocean: invisible, or trenches in the
        /// shelf. Here the coast is found along the main stem (<see cref="FindCoast"/>); the apex lies minLength..maxLength
        /// further upstream (x scale); distributaries leave the apex on both sides of the main stem (which stays the central
        /// channel), at most 65 degrees from the sea direction and at least <see cref="MinBranchSeparation"/> from each other
        /// and the stem, and each runs until it reaches the sea, plus <paramref name="overshoot"/> blocks. A channel whose
        /// ground is too high (<paramref name="tooHigh"/>, given the channel's points) or that does not reach the sea within
        /// min(2, 1.4 / cos(angle)) x its apex distance is dropped. Chance as <see cref="Delta"/>. Positions are Rivers-local;
        /// ocean/tooHigh take local points.
        /// </summary>
        public static List<Channel> CoastDelta(IReadOnlyList<NetNode> nodes, Hierarchy h, int mouth, float riversMaxSize, DeltaOptions o,
            Func<P2, double> ocean, double threshold, Func<List<P2>, bool> tooHigh, Func<int, double> rnd, double overshoot = 24,
            double scale = 1.0, double branching = 0.5)
            => CoastDelta(nodes, h, mouth, riversMaxSize, o, ocean, threshold, tooHigh, rnd, out _, out _, overshoot, scale, branching);

        /// <summary>
        /// As above; <paramref name="apexOut"/> is the delta's apex on the main stem (the furthest fork inland) and
        /// <paramref name="rolled"/> the number of distributaries rolled (placed or not).
        /// </summary>
        public static List<Channel> CoastDelta(IReadOnlyList<NetNode> nodes, Hierarchy h, int mouth, float riversMaxSize, DeltaOptions o,
            Func<P2, double> ocean, double threshold, Func<List<P2>, bool> tooHigh, Func<int, double> rnd, out P2 apexOut, out int rolled,
            double overshoot = 24, double scale = 1.0, double branching = 0.5)
        {
            apexOut = default; rolled = 0;
            // Stage 2 (DeltaTraits): scale stretches the delta (apex distance x scale, width x sqrt(scale), spread x 0.7..1.3)
            // and branching adds or removes one distributary; scale 1 / branching 0.5 is exactly the delta before stage 2.
            scale = double.IsNaN(scale) ? 1 : Math.Clamp(scale, 0.3, 2.5);
            branching = double.IsNaN(branching) ? 0.5 : Math.Clamp(branching, 0, 1);
            var list = new List<Channel>();
            if (mouth < 0 || mouth >= nodes.Count || ocean == null) return list;
            var m = nodes[mouth];
            if (m.IsLake || m.StartSize < o.MinRiverSize || o.Chance <= 0) return list;
            double p = Math.Clamp(o.Chance * (0.5 + m.StartSize / Math.Max(1f, riversMaxSize)), 0, 1);
            if (rnd(1) >= p) return list;

            // The main stem as a polyline from the mouth upstream.
            var path = new List<P2> { m.Start };
            for (int i = mouth, k = 0; i >= 0 && k < 12; i = h.MainChild[i], k++) path.Add(nodes[i].End);
            // Delta type: small (short channels from an apex just inland), carving (the full delta) or shallow (long, wide, slow).
            double kr = rnd(4);
            var kind = kr < o.SmallShare ? DeltaKind.Small : kr < o.SmallShare + o.ShallowShare ? DeltaKind.Shallow : DeltaKind.Carving;
            double lenMul = kind == DeltaKind.Small ? 0.4 : kind == DeltaKind.Shallow ? 1.3 : 1.0;
            double apexDist = (o.MinLength + rnd(3) * (o.MaxLength - o.MinLength)) * lenMul * scale;
            P2? coast = null, apex = null;
            double walked = 0, sinceCoast = 0;
            const double Step = 8;
            var stem = new List<P2>();   // main-stem points from the coast up to the apex, Step apart
            for (int s = 0; s + 1 < path.Count && apex == null; s++)
            {
                var a = path[s]; var b = path[s + 1];
                double len = (b - a).Length;
                for (double t = 0; t < len; t += Step)
                {
                    var q = a + (b - a) * (t / len);
                    if (coast == null)
                    {
                        if (ocean(q) < threshold) { coast = q; stem.Add(q); }
                        else if ((walked += Step) > 2000) return list; // no coast near the mouth
                    }
                    else { stem.Add(q); if ((sinceCoast += Step) >= apexDist) { apex = q; break; } }
                }
            }
            if (coast == null || apex == null) return list;
            var seaDir = (coast.Value - apex.Value).Normalized();
            if (seaDir.Length < 0.5) return list;
            apexOut = apex.Value;

            int extra = (int)Math.Round((branching - 0.5) * 2, MidpointRounding.AwayFromZero) + (scale >= 1.6 ? 1 : 0);
            // Flight test 16: shallow deltas had 3 fingers too often. Their count now runs from 1 to 8, mostly few, more with
            // branching and size (1 + floor(u^1.25 x 6 x (0.6 + 0.8 branching) x sqrt(scale))).
            // Flight test 16b: carving deltas rolled only MinBranches..MaxBranches (2..4) and small ones always 2; a quarter of
            // them now get 1..3 more (a long tail), small ones sometimes 3.
            int tail = rnd(8) < 0.25 ? 1 + (int)(rnd(7) * 3) : 0;
            int k2 = kind == DeltaKind.Small ? 2 + (scale >= 1.6 ? 1 : 0) + (rnd(8) < 0.3 ? 1 : 0)
                : kind == DeltaKind.Shallow ? Math.Clamp(1 + (int)Math.Floor(Math.Pow(rnd(2), 1.25) * 6 * (0.6 + 0.8 * branching) * Math.Sqrt(scale)), 1, 8)
                : Math.Clamp(o.MinBranches + (int)(rnd(2) * (o.MaxBranches - o.MinBranches + 1)) + extra + tail, 2, 8);
            rolled = k2;
            bool flip = k2 == 1 && rnd(9) < 0.5;   // a lone distributary leaves on either side
            double spread = o.SpreadDegrees * (kind == DeltaKind.Small ? 0.55 : kind == DeltaKind.Shallow ? 1.25 : 1.0) * Math.Clamp(0.7 + 0.3 * scale, 0.7, 1.3);
            // Flight test 16b: shallow channels were 1.2 x the width share, a 1-block-deep body about 50 blocks wide along every
            // finger (too broad and flat); now 0.6 x, so the deep groove in its middle reads as the channel.
            float size = Math.Max(o.MinBranchSize, m.StartSize * o.WidthShare * (kind == DeltaKind.Small ? 0.7f : kind == DeltaKind.Shallow ? 0.6f : 1f) * (float)Math.Sqrt(scale));
            float speed = kind == DeltaKind.Shallow ? 0.3f : 1f;
            int pairs = (k2 + 1) / 2;
            // Flight test 17b: start points ACROSS the river plus smaller-angle retries (16b/17) crammed nearly parallel channels
            // into the mouth, crossing and overlapping (-6955,-4352 and -10313,11290). Rolled back to ONE start, the apex centre
            // line, with spacing enforced: every channel stays at least MinBranchSeparation degrees from every other channel
            // and from the main stem's own course to the sea (angle 0). A channel whose rolled angle fails gets one alternative
            // at 0.75 x that angle, only if it still keeps that spacing; otherwise it is dropped, never squeezed in.
            var placedAng = new List<double> { 0.0 };   // the main stem continues along seaDir
            for (int b = 0; b < k2; b++)
            {
                double side = (b % 2 == 0) != flip ? 1 : -1;
                int ring = b / 2 + 1;
                double mag = spread * (0.35 + 0.65 * ring / pairs) + (rnd(10 + b) - 0.5) * (spread / (2.0 * k2));
                // Flight test 16 (-17511,30106): at most 65 degrees from the sea direction, and the sea must come within 1.4 x
                // the straight distance at that angle (at most 2 x the apex distance); a channel along the coast is dropped.
                double ang0 = Math.Min(mag, 65);
                foreach (double af in new[] { 1.0, 0.75 })
                {
                    double ang = ang0 * af;
                    double sa = side * ang;
                    bool tooClose = false;
                    foreach (double pa in placedAng) if (Math.Abs(pa - sa) < MinBranchSeparation) { tooClose = true; break; }
                    if (tooClose) continue;
                    var dir = seaDir.Rotated(sa);
                    double maxRun = Math.Min(2.0, 1.4 / Math.Max(0.4, Math.Cos(ang * Math.PI / 180))) * apexDist;
                    var pts = new List<P2> { apex.Value };
                    P2 end = apex.Value; bool reached = false;
                    for (double t = Step; t <= maxRun; t += Step)
                    {
                        end = apex.Value + dir * t;
                        pts.Add(end);
                        if (ocean(end) >= threshold) { reached = true; end = apex.Value + dir * (t + overshoot); pts.Add(end); break; }
                    }
                    if (!reached || (tooHigh != null && tooHigh(pts))) continue;
                    list.Add(new Channel(end, apex.Value, size, kind, speed));
                    placedAng.Add(sa);
                    break;
                }
            }
            return list;
        }

        /// <summary>
        /// Oxbow lakes: at a bend between a node and its downstream parent (both rivers, branch level &lt;= maxLevel, width at least
        /// minRiverSize, turn at least minBendDegrees) with probability chance, a horseshoe of 6..8 points is
        /// placed on the OUTER side of the bend (where a meander is cut off), radius minRadius..maxRadius, its opening facing the
        /// river, clear of every river node by at least the bend's width + 6 blocks. Returns the point chains.
        /// </summary>
        public static List<List<P2>> Oxbows(IReadOnlyList<NetNode> nodes, Hierarchy h, OxbowOptions o, Func<int, int, double> rnd)
        {
            var result = new List<List<P2>>();
            foreach (var x in OxbowsAt(nodes, h, o, rnd)) result.Add(x.Points);
            return result;
        }

        /// <summary>One oxbow: its point chain, the bend node it belongs to, and whether it is a dry (abandoned) oxbow.</summary>
        public readonly record struct Oxbow(List<P2> Points, int Node, bool Dry, int Attempt = 0);

        /// <summary>Placements tried per oxbow (stage 2): radius factor, mirrored turn, how close to the river (factor on the gap).</summary>
        public static readonly (double Radius, bool Mirror, double Gap)[] OxbowAttempts =
        {
            (1.0, false, 1.0), (0.75, false, 1.0), (0.75, true, 1.0), (0.55, false, 0.7), (0.55, true, 0.7),
        };

        /// <summary>
        /// Oxbow lakes (see <see cref="Oxbows"/>). Flight test 7: the shape varies per oxbow instead of one fixed horseshoe:
        /// a horseshoe (20%: arc 230..270 degrees, nearly round), a crescent (30%: arc 120..170), a long flattened arc (25%:
        /// arc 180..240, squashed toward the river to 40..65%) or an irregular meander scar (25%: arc 160..260, radius wobble
        /// +-30%); every shape is turned up to +-25 degrees, its radius wobbles +-15% point by point, and it has 6..8 points.
        /// With probability o.DryShare the oxbow is dry (an old channel above the water level, decided here, carved by the caller).
        /// </summary>
        public static List<Oxbow> OxbowsAt(IReadOnlyList<NetNode> nodes, Hierarchy h, OxbowOptions o, Func<int, int, double> rnd)
            => OxbowsAt(nodes, h, o, rnd, null, out _);

        /// <summary>
        /// Stage 2: as <see cref="OxbowsAt(IReadOnlyList{NetNode}, Hierarchy, OxbowOptions, Func{int, int, double})"/>, but
        /// every rolled oxbow tries the placements of <see cref="OxbowAttempts"/> in order (smaller, mirrored, closer to the
        /// river) and keeps the first that clears its own river and passes <paramref name="accept"/> (the caller's terrain
        /// slope and clearance checks against other rivers and lakes); it is left out only when none does.
        /// <paramref name="rejected"/> counts rolled oxbows for which no placement passed.
        /// </summary>
        public static List<Oxbow> OxbowsAt(IReadOnlyList<NetNode> nodes, Hierarchy h, OxbowOptions o, Func<int, int, double> rnd,
            Func<List<P2>, int, bool> accept, out int rejected)
        {
            rejected = 0;
            var result = new List<Oxbow>();
            if (o.Chance <= 0) return result;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                int pi = n.Parent;
                if (pi < 0 || pi >= nodes.Count || n.IsLake || nodes[pi].IsLake) continue;
                if (h.Level[i] > o.MaxLevel || n.StartSize < o.MinRiverSize) continue;
                var dp = nodes[pi].UpDir; var dn = n.UpDir;
                double cross = P2.Cross(dp, dn), dot = P2.Dot(dp, dn);
                double turn = Math.Atan2(Math.Abs(cross), dot) * 180.0 / Math.PI;
                if (turn < o.MinBendDegrees) continue;
                if (rnd(i, 1) >= o.Chance) continue;
                var bis = (dp + dn).Normalized();
                if (bis.Length < 0.5) continue;
                var left = new P2(-bis.Z, bis.X);
                var outer = cross > 0 ? left * -1 : left;   // the river turns toward `left` when cross > 0
                double radius = o.MinRadius + rnd(i, 2) * (o.MaxRadius - o.MinRadius);
                double width = n.StartSize;

                double kind = rnd(i, 3), span, ratio, wobble = 0.15;
                if (kind < 0.2) { span = 230 + 40 * rnd(i, 5); ratio = 0.85 + 0.15 * rnd(i, 6); }
                else if (kind < 0.5) { span = 120 + 50 * rnd(i, 5); ratio = 0.6 + 0.4 * rnd(i, 6); }
                else if (kind < 0.75) { span = 180 + 60 * rnd(i, 5); ratio = 0.4 + 0.25 * rnd(i, 6); }
                else { span = 160 + 100 * rnd(i, 5); ratio = 0.7 + 0.3 * rnd(i, 6); wobble = 0.3; }
                double rot = (rnd(i, 7) - 0.5) * 50;
                int points = 6 + Math.Min(2, (int)(rnd(i, 4) * 3));

                bool dryOx = rnd(i, 60) < o.DryShare;
                bool placed = false;
                int tries = accept == null ? 1 : OxbowAttempts.Length;
                for (int at = 0; at < tries && !placed; at++)
                {
                    var (rf, mirror, gf) = OxbowAttempts[at];
                    double rr = Math.Max(o.MinRadius * 0.5, radius * rf);
                    var centre = n.Start + outer * (width + rr * ratio + 8 * gf);
                    var u = (outer * -1).Rotated(mirror ? -rot : rot);   // toward the river (the opening side)
                    var v = new P2(-u.Z, u.X);
                    var chain = new List<P2>(points);
                    for (int k = 0; k < points; k++)
                    {
                        double a = (180 - span / 2 + span * k / (points - 1)) * Math.PI / 180.0;
                        double r = rr * (1 + (rnd(i, 10 + k) - 0.5) * 2 * wobble);
                        chain.Add(centre + u * (Math.Cos(a) * r * ratio) + v * (Math.Sin(a) * r));
                    }
                    if (!Clear(chain, nodes, width + 6)) continue;
                    if (accept != null && !accept(chain, i)) continue;
                    result.Add(new Oxbow(chain, i, dryOx, at));
                    placed = true;
                }
                if (!placed) rejected++;
            }
            return result;
        }

        /// <summary>
        /// Dry rivers (flight test 7): whole branches without water, likelier in dry climates. Going down from the mouth, a
        /// branch start (the mouth node, or a secondary child at a confluence) becomes dry with probability
        /// chance x clamp((rainLimit - rain) / rainLimit, 0, 1) (rain 0..1 at the node's middle), and everything upstream of a dry
        /// node is dry too. Lakes are never dry. Returns one flag per node.
        /// </summary>
        public static bool[] DryRivers(IReadOnlyList<NetNode> nodes, Hierarchy h, double chance, double rainLimit, Func<int, double> rainAt, Func<int, double> rnd)
        {
            var dry = new bool[nodes.Count];
            if (chance <= 0 || rainLimit <= 0 || rainAt == null) return dry;
            foreach (int i in TopDown(nodes, h.Children))
            {
                if (nodes[i].IsLake) continue;
                int p = nodes[i].Parent;
                bool parentDry = p >= 0 && p < nodes.Count && dry[p];
                if (parentDry) { dry[i] = true; continue; }
                bool branchStart = p < 0 || p >= nodes.Count || h.MainChild[p] != i;
                if (!branchStart) continue;
                double rain = rainAt(i);
                if (double.IsNaN(rain)) continue;
                double pr = chance * Math.Clamp((rainLimit - rain) / rainLimit, 0, 1);
                if (rnd(i) < pr) dry[i] = true;
            }
            return dry;
        }

        /// <summary>Points along a tributary node (0 = its downstream start, 1 = its upstream end) tested by <see cref="CrowdedBranches"/>.</summary>
        static readonly double[] SpacingSamples = { 0.25, 0.5, 0.75, 1.0 };
        /// <summary>Siblings at one junction closer than about this angle (degrees) count as running alongside each other.</summary>
        public const double MinForkDegrees = 20;

        /// <summary>
        /// Flight test 16: tributaries running right alongside another channel (base Rivers' branching keeps whole nodes
        /// riverPaddingBlocks apart only from the node a river grows from, so a branch can run beside its own main stem or
        /// another branch). A tributary node (level &gt;= 1) is crowded when a point at 25/50/75/100% along it comes within
        /// <paramref name="minGap"/> blocks of land of a channel that is not its parent or a child. A sibling at the same
        /// junction (usually the main stem's continuation) counts too, beyond half + minGap blocks from the junction, when the
        /// land between them is less than min(minGap, s x sin(MinForkDegrees) - half) at distance s: a branch leaving almost
        /// parallel to the stem, not a normal fork.
        /// Of two crowded channels the more minor one goes (higher level; same level: the shorter upstream). Returns
        /// the nodes to remove: each crowded node and everything upstream of it. The main stem (level 0) is never removed. Pure.
        /// </summary>
        public static HashSet<int> CrowdedBranches(IReadOnlyList<NetNode> nodes, Hierarchy h, double minGap)
        {
            var remove = new HashSet<int>();
            if (minGap <= 0 || nodes == null) return remove;
            int n = nodes.Count;
            for (int i = 0; i < n; i++)
            {
                var a = nodes[i];
                if (a.IsLake || h.Level[i] < 1 || remove.Contains(i)) continue;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    var b = nodes[j];
                    if (b.IsLake) continue;
                    if (j == a.Parent || b.Parent == i) continue;
                    // Audit: a sibling at the same junction (typically the main stem's continuation) is checked too, except
                    // within half + minGap blocks of the shared junction, where two channels always touch.
                    bool sibling = b.Parent == a.Parent && a.Parent >= 0;
                    if (h.Level[j] > h.Level[i]) continue;                           // the more minor one is tested itself
                    if (h.Level[j] == h.Level[i] && (h.Upstream[j] < h.Upstream[i] || (h.Upstream[j] == h.Upstream[i] && j > i))) continue;
                    double half = Math.Max(b.StartSize, b.EndSize) + Math.Max(a.StartSize, a.EndSize);
                    bool crowded = false;
                    foreach (double t in SpacingSamples)
                    {
                        var p = a.Start + (a.End - a.Start) * t;
                        double gap = DistToSegment(p, b.Start, b.End) - half, need = minGap;
                        if (sibling)
                        {
                            // Two channels leaving one junction start touching and part by s x sin(angle): only a sibling
                            // running within about MinForkDegrees of the other is crowded (a normal fork is not).
                            double s = (p - a.Start).Length;
                            if (s < half + minGap) continue;
                            need = Math.Min(minGap, s * Math.Sin(MinForkDegrees * Math.PI / 180) - half);
                        }
                        if (gap < need) { crowded = true; break; }
                    }
                    if (!crowded) continue;
                    // Crowded: this node and its whole upstream subtree.
                    var stack = new Stack<int>(); stack.Push(i);
                    while (stack.Count > 0) { int k = stack.Pop(); if (!remove.Add(k)) continue; foreach (int c in h.Children[k]) stack.Push(c); }
                    break;
                }
            }
            return remove;
        }

        /// <summary>
        /// Smallest angle (degrees) between channel <paramref name="i"/>'s direction (fork to sea) and every other channel's,
        /// and <paramref name="stemDir"/> (the main stem toward the sea). 180 when alone. Pure.
        /// </summary>
        public static double NearestChannelAngle(IReadOnlyList<Channel> channels, int i, P2 stemDir)
        {
            var d = (channels[i].Start - channels[i].End).Normalized();
            double best = 180;
            void Check(P2 o)
            {
                if (o.Length < 0.5) return;
                double a = Math.Acos(Math.Clamp(P2.Dot(d, o.Normalized()), -1, 1)) * 180 / Math.PI;
                if (a < best) best = a;
            }
            Check(stemDir);
            for (int k = 0; k < channels.Count; k++) if (k != i) Check(channels[k].Start - channels[k].End);
            return best;
        }

        /// <summary>
        /// The meander's sideways cap for a channel whose nearest neighbour is <paramref name="gapDegrees"/> away: 0.22 x
        /// sin(gap), at most 0.12. Two channels at that angle, each bending toward the other, stay apart (their straight lines
        /// are 0.5 x sin(gap) x length apart halfway). Pure.
        /// </summary>
        public static double MeanderCap(double gapDegrees) =>
            Math.Clamp(0.22 * Math.Sin(Math.Clamp(gapDegrees, 0, 90) * Math.PI / 180), 0, 0.12);

        /// <summary>
        /// Flight test 17: a delta channel as a gently meandering path instead of one straight node (straight canals through
        /// land at -18280,-18235). Points about every 32 blocks (flight test 18) from <paramref name="from"/> to
        /// <paramref name="to"/>, pushed sideways by A x sin(pi x waves x t + phase) x sin(pi x t): A is
        /// length x maxFrac x (0.42..1), 1..2 waves, and the sin(pi t) taper keeps both ends exactly in place (the join with
        /// the river and the mouth at sea). The caller builds it as one linked chain, so Rivers rounds the joints. Pure.
        /// </summary>
        public static List<P2> Meander(P2 from, P2 to, Func<int, double> rnd, double maxFrac = 0.12)
        {
            var d = to - from;
            double len = d.Length;
            var pts = new List<P2> { from };
            if (len < 60) { pts.Add(to); return pts; }
            var dir = d * (1 / len); var perp = new P2(-dir.Z, dir.X);
            // Flight test 17b: the sideways reach is capped by maxFrac (the caller limits it by the angle to the neighbouring channels).
            double frac = Math.Clamp(double.IsNaN(maxFrac) ? 0.12 : maxFrac, 0, 0.12);
            double amp = len * frac * (0.42 + 0.58 * rnd(1)) * (rnd(2) < 0.5 ? 1 : -1);
            double waves = 1 + rnd(3), phase = rnd(4) * Math.PI;
            int n = Math.Max(2, (int)Math.Ceiling(len / 32));   // flight test 18: denser (was 50), gentler turn at each joint
            for (int k = 1; k < n; k++)
            {
                double t = k / (double)n;
                double off = amp * Math.Sin(Math.PI * waves * t + phase) * Math.Sin(Math.PI * t);
                pts.Add(from + d * t + perp * off);
            }
            pts.Add(to);
            return pts;
        }

        /// <summary>
        /// Flight test 17: the main-stem nodes a shallow delta's rise covers, with each node's offset T0 along the rise
        /// (= distance ALONG THE RIVER from the apex to the node's downstream end, over the rise length; negative downstream
        /// of the apex). Walks the main stem from the mouth (at most 24 nodes) and stops at the first node that starts past
        /// the rise (T0 &gt;= 1). Pure.
        /// </summary>
        public static List<(int node, double t0)> StemRise(IReadOnlyList<NetNode> nodes, Hierarchy h, int mouth, P2 apex, double rise)
        {
            var result = new List<(int, double)>();
            if (mouth < 0 || mouth >= nodes.Count || !(rise > 0)) return result;
            var idx = new List<int>(); var dist = new List<double>();
            double acc = 0;
            for (int j = mouth, k = 0; j >= 0 && k < 24; j = h.MainChild[j], k++) { idx.Add(j); dist.Add(acc); acc += nodes[j].Length; }
            double apexPath = 0, best = double.MaxValue;
            for (int q = 0; q < idx.Count; q++)
            {
                var nd = nodes[idx[q]];
                double d = DistToSegment(apex, nd.Start, nd.End);
                if (d < best) { best = d; apexPath = dist[q] + Math.Min((apex - nd.Start).Length, nd.Length); }
            }
            for (int q = 0; q < idx.Count; q++)
            {
                double t0 = (dist[q] - apexPath) / rise;
                if (t0 >= 1) break;
                result.Add((idx[q], t0));
            }
            return result;
        }

        /// <summary>Flight test 17: extra plain depth (blocks below ShallowDeltaDepth) from u in [0, 1): 0, 1, 2 at 45 / 35 / 20%.</summary>
        public static int PlainExtra(double u) => u < 0.45 ? 0 : u < 0.8 ? 1 : 2;

        /// <summary>One river's main stem, for /rffind river: length in blocks along its nodes, its node count, mouth and source.</summary>
        public readonly record struct Stem(P2 Mouth, P2 Source, double Length, int StemNodes, int TotalNodes);

        /// <summary>
        /// The main stem from root <paramref name="mouth"/> (a node with no parent, not a lake) up to its source, following
        /// <see cref="Hierarchy.MainChild"/> (the longest upstream path): summed node lengths (straight node lines; Rivers'
        /// jitter adds a little in the world), the nodes on it, and the river's total river nodes (lakes excluded). Null
        /// for a lake or an invalid index. Guards against cycles. Pure.
        /// </summary>
        public static Stem? MainStem(IReadOnlyList<NetNode> nodes, Hierarchy h, int mouth)
        {
            if (nodes == null || mouth < 0 || mouth >= nodes.Count || nodes[mouth].IsLake) return null;
            double len = 0; int count = 0; int last = mouth;
            var seen = new HashSet<int>();
            for (int i = mouth; i >= 0 && i < nodes.Count && !nodes[i].IsLake && seen.Add(i); i = h.MainChild[i])
            {
                len += nodes[i].Length; count++; last = i;
            }
            int total = 0;
            foreach (int i in TopDown(nodes, h.Children)) if (!nodes[i].IsLake && RootOf(nodes, i) == mouth) total++;
            return new Stem(nodes[mouth].Start, nodes[last].End, len, count, total);
        }

        static int RootOf(IReadOnlyList<NetNode> nodes, int i)
        {
            for (int k = 0; k <= nodes.Count; k++)
            {
                int p = nodes[i].Parent;
                if (p < 0 || p >= nodes.Count) return i;
                i = p;
            }
            return -1;   // cycle
        }

        /// <summary>Depth below the root (mouth = 0) per node, for removing tips before their parents.</summary>
        public static int[] Depths(IReadOnlyList<NetNode> nodes, Hierarchy h)
        {
            var d = new int[nodes.Count];
            foreach (int i in TopDown(nodes, h.Children)) { int p = nodes[i].Parent; d[i] = p >= 0 && p < nodes.Count && p != i ? d[p] + 1 : 0; }
            return d;
        }

        static bool Clear(List<P2> chain, IReadOnlyList<NetNode> nodes, double minDist)
        {
            foreach (var p in chain)
                foreach (var n in nodes)
                    if (DistToSegment(p, n.Start, n.End) < minDist + n.StartSize * 0.5) return false;
            return true;
        }

        /// <summary>
        /// Rivers' oceanThreshold (blocks of submerged land) in ocean-map units (0..255): Rivers marks a zone as ocean when
        /// mapValue x (mapSizeY / 256, integer division as in Rivers' IL) x 0.33333 &gt; threshold. Pure.
        /// </summary>
        public static double OceanMapThreshold(double thresholdBlocks, int mapSizeY)
        {
            double perUnit = Math.Max(1, mapSizeY / 256) * 0.33333;
            return thresholdBlocks / perUnit;
        }

        public static double DistToSegment(P2 p, P2 a, P2 b)
        {
            var ab = b - a; double l2 = P2.Dot(ab, ab);
            if (l2 < 1e-12) return (p - a).Length;
            double t = Math.Clamp(P2.Dot(p - a, ab) / l2, 0, 1);
            return (p - (a + ab * t)).Length;
        }

        /// <summary>Deterministic hash of (seed, x, z, salt) to [0, 1) (SplitMix64).</summary>
        public static double Hash01(long seed, long x, long z, int salt)
        {
            unchecked
            {
                ulong h = Mix((ulong)seed ^ (0xA0761D6478BD642FUL * (ulong)(salt + 1)));
                h = Mix(h ^ (ulong)x);
                h = Mix(h ^ ((ulong)z << 29) ^ 0xE7037ED1A0B428DBUL);
                return (h >> 11) * (1.0 / 9007199254740992.0);
            }
        }

        static ulong Mix(ulong z)
        {
            unchecked
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }

    public sealed class DeltaOptions
    {
        public double Chance = 0.35;
        public float MinRiverSize = 30;
        public int MinBranches = 2, MaxBranches = 4;
        public double SpreadDegrees = 45;
        public double MinLength = 120, MaxLength = 260;
        public float WidthShare = 0.5f, MinBranchSize = 8;
        /// <summary>Shares of the small and the shallow delta types (the rest carve); 0 / 0 = every delta carves.</summary>
        public double SmallShare = 0, ShallowShare = 0;
    }

    public sealed class OxbowOptions
    {
        public double Chance = 0.2;
        public double MinBendDegrees = 20;
        public float MinRiverSize = 16;
        public int MaxLevel = 1;
        public double MinRadius = 30, MaxRadius = 60;
        public float WidthShare = 0.5f;
        /// <summary>Share of oxbows that are dry (old channels above the water level).</summary>
        public double DryShare = 0;
    }
}
