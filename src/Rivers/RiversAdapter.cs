using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenTK.Mathematics;
using Vintagestory.API.MathTools;

namespace RiverFeatures.Rivers
{
    /// <summary>
    /// Reflection access to Rivers 5.0.2 internals (checked against the installed Rivers.dll: types, fields, constructor
    /// signatures and the IL of River.AssignRiverSize, RiverRegion.GenerateRiver / GenerateZones / AddLake, RiverNode..ctor).
    /// No compile-time reference to Rivers: if any member is missing, <see cref="TryBind"/> fails and the mod stays off.
    /// </summary>
    public sealed class RiversAdapter
    {
        public Type TRiver, TNode, TSegment, TRegion, TConfig, TRequest;
        public FieldInfo RiverNodes, NodeStart, NodeEnd, NodeParent, NodeStartSize, NodeEndSize, NodeEndFlag, NodeIsLake, NodeSpeed, NodeSegments;
        public FieldInfo SegParent, SegChildren, SegParentInvalid;
        public FieldInfo RegionConfig, CfgMaxNodes, CfgMinNodes, CfgMinLength, CfgLengthVariation, CfgMaxSize, CfgLakeChance;
        public FieldInfo ReqStage, ReqStartPos, ReqRiver;
        public PropertyInfo CfgLoaded, CfgRegionSize;
        public ConstructorInfo NodeCtor, SegCtor;
        public MethodInfo GenerateZones, GenerateRiver, AssignRiverSizes;
        public string Missing;

        public static RiversAdapter TryBind(Assembly rivers)
        {
            var a = new RiversAdapter();
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            T Need<T>(T v, string what) where T : class { if (v == null && a.Missing == null) a.Missing = what; return v; }
            a.TRiver = Need(rivers.GetType("Rivers.River"), "Rivers.River");
            a.TNode = Need(rivers.GetType("Rivers.RiverNode"), "Rivers.RiverNode");
            a.TSegment = Need(rivers.GetType("Rivers.RiverSegment"), "Rivers.RiverSegment");
            a.TRegion = Need(rivers.GetType("Rivers.RiverRegion"), "Rivers.RiverRegion");
            a.TConfig = Need(rivers.GetType("Rivers.RiverConfig"), "Rivers.RiverConfig");
            a.TRequest = Need(rivers.GetType("Rivers.RiverRegion+GenerationRequest"), "RiverRegion+GenerationRequest");
            if (a.Missing != null) return a;
            a.RiverNodes = Need(a.TRiver.GetField("nodes", I), "River.nodes");
            a.NodeStart = Need(a.TNode.GetField("startPos", I), "RiverNode.startPos");
            a.NodeEnd = Need(a.TNode.GetField("endPos", I), "RiverNode.endPos");
            a.NodeParent = Need(a.TNode.GetField("parentNode", I), "RiverNode.parentNode");
            a.NodeStartSize = Need(a.TNode.GetField("startSize", I), "RiverNode.startSize");
            a.NodeEndSize = Need(a.TNode.GetField("endSize", I), "RiverNode.endSize");
            a.NodeEndFlag = Need(a.TNode.GetField("end", I), "RiverNode.end");
            a.NodeIsLake = Need(a.TNode.GetField("isLake", I), "RiverNode.isLake");
            a.NodeSpeed = Need(a.TNode.GetField("speed", I), "RiverNode.speed");
            a.NodeSegments = Need(a.TNode.GetField("segments", I), "RiverNode.segments");
            a.SegParent = Need(a.TSegment.GetField("parent", I), "RiverSegment.parent");
            a.SegChildren = Need(a.TSegment.GetField("children", I), "RiverSegment.children");
            a.SegParentInvalid = Need(a.TSegment.GetField("parentInvalid", I), "RiverSegment.parentInvalid");
            a.RegionConfig = Need(a.TRegion.GetField("config", I), "RiverRegion.config");
            a.CfgMaxNodes = Need(a.TConfig.GetField("maxNodes", I), "RiverConfig.maxNodes");
            a.CfgMinLength = Need(a.TConfig.GetField("minLength", I), "RiverConfig.minLength");
            a.CfgLengthVariation = Need(a.TConfig.GetField("lengthVariation", I), "RiverConfig.lengthVariation");
            a.CfgMaxSize = Need(a.TConfig.GetField("maxSize", I), "RiverConfig.maxSize");
            a.CfgLakeChance = a.TConfig.GetField("lakeChance", I); // optional: source lakes (null = setting unavailable)
            a.CfgMinNodes = a.TConfig.GetField("minNodes", I);     // optional: the floor for maxNodes (Rivers drops rivers with fewer nodes)
            a.CfgLoaded = Need(a.TConfig.GetProperty("Loaded", S), "RiverConfig.Loaded");
            a.CfgRegionSize = Need(a.TConfig.GetProperty("RegionSize", I), "RiverConfig.RegionSize");
            a.ReqStage = Need(a.TRequest.GetField("stage", I), "GenerationRequest.stage");
            a.ReqStartPos = Need(a.TRequest.GetField("startPos", I), "GenerationRequest.startPos");
            a.ReqRiver = Need(a.TRequest.GetField("river", I), "GenerationRequest.river");
            a.NodeCtor = Need(a.TNode.GetConstructor(I, null, new[] { typeof(Vector2d), typeof(Vector2d), a.TRiver, a.TNode, typeof(LCGRandom), a.TSegment.MakeArrayType() }, null), "RiverNode(Vector2d, Vector2d, River, RiverNode, LCGRandom, RiverSegment[])");
            a.SegCtor = Need(a.TSegment.GetConstructor(I, null, new[] { typeof(Vector2d), typeof(Vector2d), a.TNode }, null), "RiverSegment(Vector2d, Vector2d, RiverNode)");
            a.GenerateZones = Need(a.TRegion.GetMethod("GenerateZones", I), "RiverRegion.GenerateZones");
            a.GenerateRiver = Need(a.TRegion.GetMethod("GenerateRiver", I), "RiverRegion.GenerateRiver");
            a.AssignRiverSizes = Need(a.TRiver.GetMethod("AssignRiverSizes", I), "River.AssignRiverSizes");
            a.AddLake = a.TRegion.GetMethod("AddLake", I); // optional: lake shapes (null = Rivers' teardrops)
            a.RegionRivers = a.TRegion.GetField("rivers", I); // optional: lake clearance from other rivers
            if (a.Missing == null && a.GenerateRiver.GetParameters().Length != 7) a.Missing = "RiverRegion.GenerateRiver has " + a.GenerateRiver.GetParameters().Length + " parameters (expected 7)";
            return a;
        }

        public bool Ok => Missing == null;

        // Rivers' sample type and its fields, read by the dry-channel patch.
        public Type TSample;
        public FieldInfo SampleDistance, SampleBank;

        // Dry channels (optional): RiverGenerator.SampleSelf / SampleConnector and RiverSegment.riverNode.
        public MethodInfo SampleSelf, SampleConnector;
        public FieldInfo SegNode;

        /// <summary>Binds the members the dry-channel patch needs; false when any is missing.</summary>
        public bool BindDry(Assembly rivers)
        {
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var gen = rivers.GetType("Rivers.RiverGenerator");
            TSample ??= rivers.GetType("Rivers.RiverSample");
            SampleSelf = gen?.GetMethod("SampleSelf", I);
            SampleConnector = gen?.GetMethod("SampleConnector", I);
            SegNode = TSegment?.GetField("riverNode", I);
            SampleBank ??= TSample?.GetField("bankFactor", I);
            SampleDistance ??= TSample?.GetField("riverDistance", I);
            return SampleSelf != null && SampleConnector != null && SegNode != null && SampleBank != null
                && SampleSelf.ReturnType == TSample && SampleConnector.ReturnType == TSample
                && SampleSelf.GetParameters().Length == 5 && SampleConnector.GetParameters().Length == 7;
        }

        /// <summary>World position of a region's local origin (RiverRegion.GlobalRegionStart); node positions are region-local.</summary>
        public P2 RegionOrigin(object region)
        {
            var p = TRegion.GetProperty("GlobalRegionStart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null || region == null ? new P2(double.NaN, double.NaN) : P((Vector2d)p.GetValue(region));
        }

        /// <summary>
        /// Removes a source-tip node: unlinks its segments from their parents (the parent node's last segment lists the tip's
        /// first segment as a child) and drops it from river.nodes. If the parent node has no other child left it becomes a
        /// source (end = true), so Rivers' own source-lake roll (lakeChance) applies to it.
        /// </summary>
        public void RemoveTip(object river, object node)
        {
            foreach (var seg in (Array)NodeSegments.GetValue(node))
            {
                var p = seg == null ? null : SegParent.GetValue(seg);
                if (p != null) ((IList)SegChildren.GetValue(p)).Remove(seg);
            }
            var nodes = Nodes(river);
            nodes.Remove(node);
            var parent = NodeParent.GetValue(node);
            if (parent == null) return;
            foreach (var n in nodes) if (ReferenceEquals(NodeParent.GetValue(n), parent)) return;
            NodeEndFlag.SetValue(parent, true);
        }

        public IList Nodes(object river) => (IList)RiverNodes.GetValue(river);

        static P2 P(Vector2d v) => new P2(v.X, v.Y);
        static Vector2d V(P2 p) => new Vector2d(p.X, p.Z);

        /// <summary>Copies a river's nodes into the pure model (same order; Parent as an index into it).</summary>
        public List<NetNode> Read(IList nodes)
        {
            var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < nodes.Count; i++) index[nodes[i]] = i;
            var list = new List<NetNode>(nodes.Count);
            foreach (var n in nodes)
            {
                var p = NodeParent.GetValue(n);
                list.Add(new NetNode
                {
                    Start = P((Vector2d)NodeStart.GetValue(n)),
                    End = P((Vector2d)NodeEnd.GetValue(n)),
                    Parent = p != null && index.TryGetValue(p, out int pi) ? pi : -1,
                    StartSize = (float)NodeStartSize.GetValue(n),
                    EndSize = (float)NodeEndSize.GetValue(n),
                    IsLake = (bool)NodeIsLake.GetValue(n),
                });
            }
            return list;
        }

        public void WriteSizes(IList nodes, List<NetNode> net)
        {
            for (int i = 0; i < nodes.Count && i < net.Count; i++)
            {
                NodeStartSize.SetValue(nodes[i], net[i].StartSize);
                NodeEndSize.SetValue(nodes[i], net[i].EndSize);
            }
        }

        /// <summary>
        /// Adds a channel node to a river: Rivers builds its jittered segments (customSegments null, like a normal node), the node
        /// has no parent (it is not part of the tree), end = false (so no source lake is added to it), and the given width. The
        /// node is appended to river.nodes; Rivers indexes its segments for terrain sampling at the end of GenerateZones.
        /// </summary>
        public object AddChannel(object river, P2 start, P2 end, float size, float speed, long seed)
        {
            var rand = new LCGRandom(seed);
            rand.InitPositionSeed((int)start.X, (int)start.Z);
            var node = NodeCtor.Invoke(new object[] { V(start), V(end), river, null, rand, null });
            NodeStartSize.SetValue(node, size);
            NodeEndSize.SetValue(node, size);
            NodeEndFlag.SetValue(node, false);
            NodeSpeed.SetValue(node, speed);
            Nodes(river).Add(node);
            return node;
        }

        public MethodInfo AddLake; // RiverRegion.AddLake(int minSize, int maxSize, River river, RiverNode parent, double angle)

        /// <summary>
        /// The lake node AddLake just appended for <paramref name="parent"/>, or null. Rivers' AddLake (IL) builds it with
        /// parentNode = NULL, startPos = parent.endPos, isLake = true, and adds it LAST to river.nodes (flight test 12: matching
        /// on parentNode found nothing, so no lake was ever reshaped).
        /// </summary>
        public object NewLakeNode(object river, object parent)
        {
            var nodes = Nodes(river);
            if (nodes.Count == 0) return null;
            var n = nodes[nodes.Count - 1];
            if (!(bool)NodeIsLake.GetValue(n)) return null;
            if (parent != null)
            {
                var pe = (Vector2d)NodeEnd.GetValue(parent);
                var ls = (Vector2d)NodeStart.GetValue(n);
                if (Math.Abs(pe.X - ls.X) > 0.5 || Math.Abs(pe.Y - ls.Y) > 0.5) return null;
            }
            return n;
        }

        /// <summary>RiverRegion.rivers (the region's rivers generated so far); optional.</summary>
        public FieldInfo RegionRivers;
        FieldInfo segStart, segEnd;

        /// <summary>
        /// River channels and lakes near a new source lake, as obstacles: every segment of every node of the region's rivers
        /// (and this river) within <paramref name="reach"/> of the start, except the lake node itself. Segments of this river
        /// are marked Own: LakeShapes.Clearance exempts them only at the join; other rivers are never exempt.
        /// </summary>
        public List<LakeShapes.Obstacle> ObstaclesNear(object region, object river, object lake, P2 start, double reach, float unsizedWidth, bool lakesOnly = false)
        {
            var list = new List<LakeShapes.Obstacle>();
            segStart ??= TSegment.GetField("startPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            segEnd ??= TSegment.GetField("endPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (segStart == null || segEnd == null) return list;
            var rivers = new List<object>();
            if (RegionRivers != null && region != null && RegionRivers.GetValue(region) is IList rl)
                foreach (var r in rl) if (r != null) rivers.Add(r);
            if (river != null && !rivers.Exists(r => ReferenceEquals(r, river))) rivers.Add(river);
            double box = reach + 120;
            foreach (var r in rivers)
                foreach (var n in Nodes(r))
                {
                    if (ReferenceEquals(n, lake)) continue;
                    if (lakesOnly && !(bool)NodeIsLake.GetValue(n)) continue;
                    var a = P((Vector2d)NodeStart.GetValue(n)); var b = P((Vector2d)NodeEnd.GetValue(n));
                    if (Math.Min(a.X, b.X) > start.X + box || Math.Max(a.X, b.X) < start.X - box
                        || Math.Min(a.Z, b.Z) > start.Z + box || Math.Max(a.Z, b.Z) < start.Z - box) continue;
                    float s0 = (float)NodeStartSize.GetValue(n), s1 = (float)NodeEndSize.GetValue(n);
                    // Rivers assigns sizes river by river (AssignRiverSizes, then that river's lakes): rivers later in the
                    // region still have none, so they count at the widest Rivers size (flight test 13: a source lake cut into
                    // a big river whose width read as 0).
                    if (s0 <= 1 && s1 <= 1) { s0 = unsizedWidth; s1 = unsizedWidth; }
                    bool own = ReferenceEquals(r, river);
                    var segs = (Array)NodeSegments.GetValue(n);
                    if (segs == null) continue;
                    int cnt = segs.Length;
                    for (int i = 0; i < cnt; i++)
                    {
                        var sg = segs.GetValue(i);
                        if (sg == null) continue;
                        var sa = P((Vector2d)segStart.GetValue(sg)); var sb = P((Vector2d)segEnd.GetValue(sg));
                        var o = new LakeShapes.Obstacle(sa, sb, s0 + (s1 - s0) * i / (float)cnt, s0 + (s1 - s0) * (i + 1) / (float)cnt, own);
                        list.Add(o);
                    }
                }
            return list;
        }

        public P2 StartOf(object node) => P((Vector2d)NodeStart.GetValue(node));
        public P2 EndOf(object node) => P((Vector2d)NodeEnd.GetValue(node));

        /// <summary>
        /// Gives Rivers' lake node the first piece's geometry (a fresh segment that keeps the old one's link to the river) and
        /// appends the other pieces as still lake nodes, chained where ChainTo says. Returns the number of nodes added.
        /// </summary>
        public int ReshapeLake(object river, object lake, List<LakeShapes.Piece> pieces, long seed)
        {
            if (pieces.Count == 0) return 0;
            // All-or-nothing: every new node and segment is built and linked among themselves FIRST; Rivers' lake node, its
            // parent segment and river.nodes are touched only after that succeeded. Any exception before then leaves Rivers'
            // own lake exactly as AddLake made it.
            var p0 = pieces[0];
            var oldSegs = (Array)NodeSegments.GetValue(lake);
            object oldSeg = oldSegs.Length > 0 ? oldSegs.GetValue(0) : null;
            var seg0 = SegCtor.Invoke(new object[] { V(p0.A), V(p0.B), lake });
            var segsArr = new List<object> { seg0 };
            var nodeOf = new List<object> { lake };
            var newNodes = new List<object>();
            for (int k = 1; k < pieces.Count; k++)
            {
                var pc = pieces[k];
                var rand = new LCGRandom(seed);
                rand.InitPositionSeed((int)pc.A.X, (int)pc.A.Z);
                var segs = Array.CreateInstance(TSegment, 1);
                object parentNode = pc.ChainTo >= 0 ? nodeOf[pc.ChainTo] : lake;
                var node = NodeCtor.Invoke(new object[] { V(pc.A), V(pc.B), river, parentNode, rand, segs });
                var seg = SegCtor.Invoke(new object[] { V(pc.A), V(pc.B), node });
                segs.SetValue(seg, 0);
                if (pc.ChainTo >= 0)
                {
                    var prev = segsArr[pc.ChainTo];
                    SegParent.SetValue(seg, prev);
                    ((IList)SegChildren.GetValue(prev)).Add(seg);
                }
                NodeStartSize.SetValue(node, pc.SizeA);
                NodeEndSize.SetValue(node, pc.SizeB);
                NodeEndFlag.SetValue(node, false);
                NodeSpeed.SetValue(node, 0f);
                NodeIsLake.SetValue(node, true);
                segsArr.Add(seg); nodeOf.Add(node); newNodes.Add(node);
            }
            var newSegs = Array.CreateInstance(TSegment, 1);
            newSegs.SetValue(seg0, 0);
            object par = oldSeg == null ? null : SegParent.GetValue(oldSeg);
            if (oldSeg != null)
            {
                SegParent.SetValue(seg0, par);
                SegParentInvalid.SetValue(seg0, SegParentInvalid.GetValue(oldSeg));
            }

            // Commit (plain field writes and list operations only).
            NodeStart.SetValue(lake, V(p0.A));
            NodeEnd.SetValue(lake, V(p0.B));
            NodeStartSize.SetValue(lake, p0.SizeA);
            NodeEndSize.SetValue(lake, p0.SizeB);
            NodeSegments.SetValue(lake, newSegs);
            if (par != null)
            {
                var ch = (IList)SegChildren.GetValue(par);
                int at = ch.IndexOf(oldSeg);
                if (at >= 0) ch[at] = seg0;
            }
            var all = Nodes(river);
            foreach (var n in newNodes) all.Add(n);
            return newNodes.Count;
        }

        /// <summary>
        /// Adds a still-water chain (oxbow): one node per pair of points, each with a single straight segment (as Rivers' own
        /// AddLake does), chained so Rivers' connector sampling joins them smoothly; speed 0 (still water), end = false.
        /// </summary>
        public int AddChain(object river, List<P2> pts, float size, long seed, float speed = 0f)
        {
            object prevNode = null, prevSeg = null;
            int added = 0;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                var rand = new LCGRandom(seed);
                rand.InitPositionSeed((int)pts[k].X, (int)pts[k].Z);
                var segs = Array.CreateInstance(TSegment, 1);
                var node = NodeCtor.Invoke(new object[] { V(pts[k]), V(pts[k + 1]), river, prevNode, rand, segs });
                var seg = SegCtor.Invoke(new object[] { V(pts[k]), V(pts[k + 1]), node });
                segs.SetValue(seg, 0);
                if (prevSeg != null)
                {
                    SegParent.SetValue(seg, prevSeg);
                    ((IList)SegChildren.GetValue(prevSeg)).Add(seg);
                }
                NodeStartSize.SetValue(node, size);
                NodeEndSize.SetValue(node, size);
                NodeEndFlag.SetValue(node, false);
                NodeSpeed.SetValue(node, speed);
                Nodes(river).Add(node);
                prevNode = node; prevSeg = seg; added++;
            }
            return added;
        }
    }
}
