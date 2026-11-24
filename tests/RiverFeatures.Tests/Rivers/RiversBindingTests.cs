using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using OpenTK.Mathematics;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>
    /// Binds the adapter against the REAL installed Rivers.dll (unpacked by the game into VintagestoryData/Cache/unpack) and
    /// builds channel and oxbow nodes through Rivers' own constructors, segment builder and connector code. Skipped silently
    /// (passes) when the DLL is not on this machine.
    /// </summary>
    public class RiversBindingTests
    {
        public static Assembly LoadRiversForTests() => LoadRivers();

        static Assembly LoadRivers()
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData", "Cache", "unpack");
            if (!Directory.Exists(cache)) return null;
            var dll = Directory.GetDirectories(cache, "rivers_5.0.2*").SelectMany(d => Directory.GetFiles(d, "Rivers.dll", SearchOption.AllDirectories)).FirstOrDefault();
            if (dll == null) return null;
            var already = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rivers");
            return already ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        }

        [Fact]
        public void Adapter_BindsToInstalledRivers()
        {
            var asm = LoadRivers();
            if (asm == null) return;
            var a = RiversAdapter.TryBind(asm);
            Assert.True(a.Ok, "missing: " + a.Missing);
        }

        [Fact]
        public void Adapter_BuildsNodesWithRiversOwnCode()
        {
            var asm = LoadRivers();
            if (asm == null) return;
            var a = RiversAdapter.TryBind(asm);
            Assert.True(a.Ok, "missing: " + a.Missing);
            if (a.CfgLoaded.GetValue(null) == null) a.CfgLoaded.SetValue(null, Activator.CreateInstance(a.TConfig)); // defaults (segmentsInRiver etc.)

            var river = Activator.CreateInstance(a.TRiver, new object[] { new Vector2d(5000, 5000) });
            Assert.NotNull(a.Nodes(river));

            var node = a.AddChannel(river, new P2(5000, 4800), new P2(5000, 5000), 20f, 1f, 1234);
            var segs = (Array)a.NodeSegments.GetValue(node);
            Assert.True(segs.Length >= 1);
            Assert.Equal(20f, (float)a.NodeStartSize.GetValue(node));
            Assert.False((bool)a.NodeEndFlag.GetValue(node));

            var chain = new System.Collections.Generic.List<P2> { new P2(6000, 6000), new P2(6030, 6040), new P2(6070, 6050), new P2(6100, 6020) };
            Assert.Equal(3, a.AddChain(river, chain, 12f, 99));
            var nodes = a.Nodes(river);
            Assert.Equal(4, nodes.Count);
            var last = nodes[3];
            Assert.Equal(0f, (float)a.NodeSpeed.GetValue(last));
            Assert.Same(nodes[2], a.NodeParent.GetValue(last));
            var lastSeg = ((Array)a.NodeSegments.GetValue(last)).GetValue(0);
            var prevSeg = ((Array)a.NodeSegments.GetValue(nodes[2])).GetValue(0);
            Assert.Same(prevSeg, a.SegParent.GetValue(lastSeg));
            Assert.Contains(lastSeg, ((IList)a.SegChildren.GetValue(prevSeg)).Cast<object>());

            // Members the terrain checks read: region origin, valley noise (static, GetNoise with 2 args).
            Assert.NotNull(a.TRegion.GetProperty("GlobalRegionStart"));
            var vf = asm.GetType("Rivers.RiversApi").GetField("valleyNoise", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            // Round trip through the pure model.
            var net = a.Read(nodes);
            Assert.Equal(4, net.Count);
            Assert.Equal(2, net[3].Parent);
            Assert.Equal(new P2(5000, 4800), net[0].Start);
            Assert.NotNull(vf);
            Assert.Contains(vf.FieldType.GetMethods(), m => m.Name == "GetNoise" && m.GetParameters().Length == 2);

            // Removing the chain's last node unlinks its segment and makes its parent a source (end = true).
            var tipSeg = ((Array)a.NodeSegments.GetValue(nodes[3])).GetValue(0);
            var parentNode = nodes[2];
            var parentSeg = ((Array)a.NodeSegments.GetValue(parentNode)).GetValue(0);
            a.RemoveTip(river, nodes[3]);
            Assert.Equal(3, a.Nodes(river).Count);
            Assert.DoesNotContain(tipSeg, ((IList)a.SegChildren.GetValue(parentSeg)).Cast<object>());
            Assert.True((bool)a.NodeEndFlag.GetValue(parentNode));
        }

        [Fact]
        public void A_delta_chain_flows_and_is_linked()
        {
            // Flight test 18: delta channels are one linked chain (parent to child segments, so Rivers rounds the joints) that
            // keeps its flow speed, unlike the still oxbow chains.
            var asm = LoadRivers();
            if (asm == null) return;
            var a = RiversAdapter.TryBind(asm);
            Assert.True(a.Ok, "missing: " + a.Missing);
            if (a.CfgLoaded.GetValue(null) == null) a.CfgLoaded.SetValue(null, Activator.CreateInstance(a.TConfig));
            var river = Activator.CreateInstance(a.TRiver, new object[] { new Vector2d(5000, 5000) });
            var pts = RiverNet.Meander(new P2(6000, 6000), new P2(6000, 6300), s => 0.7);
            Assert.Equal(pts.Count - 1, a.AddChain(river, pts, 10f, 3, 0.3f));
            var nodes = a.Nodes(river);
            for (int k = 0; k < nodes.Count; k++)
            {
                Assert.Equal(0.3f, (float)a.NodeSpeed.GetValue(nodes[k]));
                if (k == 0) continue;
                var seg = ((Array)a.NodeSegments.GetValue(nodes[k])).GetValue(0);
                var prev = ((Array)a.NodeSegments.GetValue(nodes[k - 1])).GetValue(0);
                Assert.Same(prev, a.SegParent.GetValue(seg));
            }
        }

        [Fact]
        public void Rivers_own_AddLake_is_found_and_reshaped()
        {
            var asm = LoadRivers();
            if (asm == null) return;
            var a = RiversAdapter.TryBind(asm);
            Assert.True(a.Ok, "missing: " + a.Missing);
            var cfgObj = a.CfgLoaded.GetValue(null) ?? Activator.CreateInstance(a.TConfig);
            a.CfgLoaded.SetValue(null, cfgObj);

            var region = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(a.TRegion);
            a.RegionConfig.SetValue(region, cfgObj);
            var randField = a.TRegion.GetField("rand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var rand = new Vintagestory.API.MathTools.LCGRandom(7); rand.InitPositionSeed(1, 2);
            randField.SetValue(region, rand);

            var river = Activator.CreateInstance(a.TRiver, new object[] { new Vector2d(5000, 5000) });
            var stem = a.AddChannel(river, new P2(5000, 4800), new P2(5000, 5000), 20f, 1f, 1);
            int before = a.Nodes(river).Count;
            a.AddLake.Invoke(region, new object[] { 50, 75, river, stem, 90.0 });   // Rivers' real code
            Assert.Equal(before + 1, a.Nodes(river).Count);

            var lake = a.NewLakeNode(river, stem);
            Assert.NotNull(lake);   // flight test 12: this was null (lookup by parentNode), so nothing was reshaped
            Assert.Equal(new P2(5000, 5000), a.StartOf(lake));

            // Obstacles from Rivers' real segments: the stem's far segments count, the ones at the join do not.
            var obs = a.ObstaclesNear(region, river, lake, a.StartOf(lake), 380, 50f);
            Assert.NotEmpty(obs);
            Assert.All(obs, o => Assert.True(o.Own)); // only the lake's own river is in this region

            var dir = (a.EndOf(lake) - a.StartOf(lake)).Normalized();
            var pieces = LakeShapes.Procedural(a.StartOf(lake), dir, 7, 60, s => RiverNet.Hash01(5, 1, 2, s));
            Assert.Equal(pieces.Count - 1, a.ReshapeLake(river, lake, pieces, 3));
            // The reshaped first segment keeps Rivers' link: it is a child of the stem's last segment.
            var stemSegs = (Array)a.NodeSegments.GetValue(stem);
            var lastStemSeg = stemSegs.GetValue(stemSegs.Length - 1);
            var lakeSeg = ((Array)a.NodeSegments.GetValue(lake)).GetValue(0);
            Assert.Contains(lakeSeg, ((IList)a.SegChildren.GetValue(lastStemSeg)).Cast<object>());
            Assert.Same(lastStemSeg, a.SegParent.GetValue(lakeSeg));
        }

        [Fact]
        public void ReshapeLake_turns_Rivers_lake_into_a_winding_chain()
        {
            var asm = LoadRivers();
            if (asm == null) return;
            var a = RiversAdapter.TryBind(asm);
            Assert.True(a.Ok, "missing: " + a.Missing);
            Assert.NotNull(a.AddLake);
            Assert.Equal(5, a.AddLake.GetParameters().Length);
            if (a.CfgLoaded.GetValue(null) == null) a.CfgLoaded.SetValue(null, Activator.CreateInstance(a.TConfig));

            var river = Activator.CreateInstance(a.TRiver, new object[] { new Vector2d(5000, 5000) });
            var stem = a.AddChannel(river, new P2(5000, 4800), new P2(5000, 5000), 20f, 1f, 1);
            // A lake node as AddLake builds it: one straight segment, parent = the river node, isLake.
            int before = a.Nodes(river).Count;
            a.AddChain(river, new System.Collections.Generic.List<P2> { new P2(5000, 5000), new P2(5000, 5100) }, 30f, 2);
            var lake = a.Nodes(river)[before];
            Assert.Null(a.NodeParent.GetValue(lake)); // as Rivers' AddLake builds it: no parent node, start = parent end, last in the list
            a.NodeIsLake.SetValue(lake, true);
            Assert.Same(lake, a.NewLakeNode(river, stem));

            var pieces = LakeShapes.Procedural(new P2(5000, 5000), new P2(0, 1), 7, 60, s => RiverNet.Hash01(9, 1, 2, s));
            Assert.True(pieces.Count >= 2, "seed gives a multi-piece lake");
            int added = a.ReshapeLake(river, lake, pieces, 3);
            Assert.Equal(pieces.Count - 1, added);
            Assert.Equal(before + pieces.Count, a.Nodes(river).Count);
            Assert.Equal(pieces[0].B, a.EndOf(lake));
            for (int k = 1; k < pieces.Count; k++)
            {
                var n = a.Nodes(river)[before + k];
                Assert.True((bool)a.NodeIsLake.GetValue(n));
                Assert.Equal(0f, (float)a.NodeSpeed.GetValue(n));
                var seg = ((Array)a.NodeSegments.GetValue(n)).GetValue(0);
                var prev = ((Array)a.NodeSegments.GetValue(a.Nodes(river)[pieces[k].ChainTo == 0 ? before : before + pieces[k].ChainTo])).GetValue(0);
                Assert.Same(prev, a.SegParent.GetValue(seg));
            }
        }
    }
}
