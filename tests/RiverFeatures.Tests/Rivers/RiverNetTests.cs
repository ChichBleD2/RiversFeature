using System;
using System.Collections.Generic;
using System.Linq;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    public class RiverNetTests
    {
        // A mouth node going north (upstream = +Z), then a fork into a long branch (west) and a short branch (east).
        static List<NetNode> Fork(int westNodes = 4, int eastNodes = 1)
        {
            var n = new List<NetNode> { new NetNode { Start = new P2(0, 0), End = new P2(0, 200), StartSize = 40, EndSize = 37 } };
            int parent = 0; var at = new P2(0, 200);
            for (int k = 0; k < westNodes; k++)
            {
                var end = at + new P2(-120, 150);
                n.Add(new NetNode { Start = at, End = end, Parent = parent, StartSize = 37 - 3 * k, EndSize = 34 - 3 * k });
                parent = n.Count - 1; at = end;
            }
            parent = 0; at = new P2(0, 200);
            for (int k = 0; k < eastNodes; k++)
            {
                var end = at + new P2(120, 150);
                n.Add(new NetNode { Start = at, End = end, Parent = parent, StartSize = 37 - 3 * k, EndSize = 34 - 3 * k });
                parent = n.Count - 1; at = end;
            }
            return n;
        }

        [Fact]
        public void LongestUpstreamBranch_IsTheMainStem()
        {
            var net = Fork(westNodes: 4, eastNodes: 1);
            var h = RiverNet.Classify(net);
            Assert.Equal(1, h.MainChild[0]);           // the first west node
            Assert.Equal(0, h.Level[1]);
            Assert.Equal(1, h.Level[5]);               // the east branch is secondary
            Assert.True(h.Upstream[0] > h.Upstream[5]);
        }

        [Fact]
        public void EqualBranches_PickOneMain_Deterministically()
        {
            var net = Fork(westNodes: 2, eastNodes: 2);
            var a = RiverNet.Classify(net); var b = RiverNet.Classify(net);
            Assert.Equal(a.MainChild[0], b.MainChild[0]);
            int levelOne = Enumerable.Range(1, net.Count - 1).Count(i => a.Level[i] == 1);
            Assert.Equal(2, levelOne); // exactly one of the two equal branches (2 nodes) became secondary
        }

        [Fact]
        public void Tributaries_AreCapped_AndTheCapCascades()
        {
            // main stem 0 -> 1 -> 2; secondary 3 joins node 0's upstream end; tertiary 4 joins secondary 3.
            var net = new List<NetNode>
            {
                new NetNode { Start = new P2(0, 0), End = new P2(0, 300), StartSize = 50, EndSize = 47 },
                new NetNode { Start = new P2(0, 300), End = new P2(0, 600), Parent = 0, StartSize = 47, EndSize = 44 },
                new NetNode { Start = new P2(0, 600), End = new P2(0, 900), Parent = 1, StartSize = 44, EndSize = 41 },
                new NetNode { Start = new P2(0, 300), End = new P2(250, 450), Parent = 0, StartSize = 44, EndSize = 41 },
                new NetNode { Start = new P2(250, 450), End = new P2(400, 650), Parent = 3, StartSize = 41, EndSize = 38 },
                new NetNode { Start = new P2(250, 450), End = new P2(260, 520), Parent = 3, StartSize = 20, EndSize = 17 },
            };
            var h = RiverNet.Classify(net);
            Assert.Equal(1, h.MainChild[0]);
            int narrowed = RiverNet.CapTributaries(net, h, maxSize: 28, share: 0.6f, minSize: 8);
            Assert.True(narrowed >= 2);
            Assert.Equal(47, net[0].EndSize);                    // main stem unchanged
            Assert.Equal(47, net[1].StartSize);                  // main stem continues unchanged
            Assert.True(net[3].StartSize <= 28);                  // secondary: min(28, 0.6 x 47)
            Assert.True(net[3].StartSize <= 0.6f * 47 + 1e-4);
            Assert.True(net[5].StartSize <= 0.6f * net[3].EndSize + 1e-4); // tertiary joins the (capped) secondary
            foreach (var n in net) Assert.True(n.EndSize <= n.StartSize);
        }

        [Fact]
        public void SecondaryStage_KeepsOnlyItsShareOfTheBudget()
        {
            Assert.Equal(5 + 14, RiverNet.SecondaryStage(5, 40, 0.6)); // 35 remaining, keeps 21 -> stage advanced by 14
            Assert.Equal(5, RiverNet.SecondaryStage(5, 40, 1.0));      // Rivers' symmetric forks
            Assert.Equal(40, RiverNet.SecondaryStage(39, 40, 0.0));     // keeps nothing of the remaining budget: last node
            Assert.Equal(41, RiverNet.SecondaryStage(45, 40, 0.5));     // never beyond maxNodes + 1
        }

        [Fact]
        public void MaxNodes_ForTenThousandBlocks()
        {
            Assert.Equal(40, RiverNet.MaxNodesFor(10000, 150, 200)); // mean node 250 blocks
            Assert.Equal(20, RiverNet.MaxNodesFor(5000, 150, 200));
            // Never below Rivers' minNodes (it removes shorter rivers): a 1000-block length keeps 8 nodes, not 4.
            Assert.Equal(4, RiverNet.MaxNodesFor(1000, 150, 200));
            Assert.Equal(8, RiverNet.MaxNodesFor(1000, 150, 200, 8));
            Assert.Equal(40, RiverNet.MaxNodesFor(10000, 150, 200, 8));
            foreach (int len in new[] { 5000, 7500, 10000, 15000, 20000 }) Assert.True(RiverNet.MaxNodesFor(len, 150, 200, 8) == len / 250, "world setting " + len);
        }

        [Fact]
        public void Delta_FansSeaward_FromTheMouth()
        {
            var mouth = new NetNode { Start = new P2(1000, 1000), End = new P2(1000, 1300), StartSize = 50, EndSize = 47 };
            var o = new DeltaOptions { Chance = 1, MinBranches = 3, MaxBranches = 3, SpreadDegrees = 40, MinLength = 150, MaxLength = 150, WidthShare = 0.5f };
            var ch = RiverNet.Delta(mouth, 50, o, salt => 0.3);
            Assert.Equal(3, ch.Count);
            foreach (var c in ch)
            {
                Assert.Equal(mouth.Start, c.End);                 // upstream end at the mouth
                Assert.True(c.Start.Z < mouth.Start.Z);           // the sea lies opposite the river (south here)
                Assert.InRange((c.Start - c.End).Length, 149.9, 150.1);
                Assert.Equal(25f, c.Size);
            }
            Assert.True(ch.Select(c => Math.Round(c.Start.X)).Distinct().Count() == 3); // fanned, not stacked
        }

        [Fact]
        public void Delta_RespectsChanceAndMinimumSize()
        {
            var small = new NetNode { Start = new P2(0, 0), End = new P2(0, 300), StartSize = 14, EndSize = 14 };
            Assert.Empty(RiverNet.Delta(small, 50, new DeltaOptions { Chance = 1, MinRiverSize = 30 }, s => 0));
            var big = new NetNode { Start = new P2(0, 0), End = new P2(0, 300), StartSize = 50, EndSize = 47 };
            Assert.Empty(RiverNet.Delta(big, 50, new DeltaOptions { Chance = 0.1 }, s => 0.99));
        }

        [Fact]
        public void Oxbow_OnTheOuterSideOfABend_ClearOfTheRiver()
        {
            // Parent goes north, the child turns 45 degrees east (right): the outer side is west.
            var net = new List<NetNode>
            {
                new NetNode { Start = new P2(0, 0), End = new P2(0, 300), StartSize = 30, EndSize = 28 },
                new NetNode { Start = new P2(0, 300), End = new P2(212, 512), Parent = 0, StartSize = 28, EndSize = 25 },
            };
            var h = RiverNet.Classify(net);
            var ox = RiverNet.Oxbows(net, h, new OxbowOptions { Chance = 1, MinBendDegrees = 20, MinRiverSize = 16, MinRadius = 40, MaxRadius = 40 }, (i, s) => 0.1);
            Assert.Single(ox);
            var chain = ox[0];
            Assert.Equal(6, chain.Count);
            double cx = chain.Average(p => p.X);
            Assert.True(cx < 0, "the oxbow lies on the outer (west) side");
            foreach (var p in chain)
                foreach (var n in net)
                    Assert.True(RiverNet.DistToSegment(p, n.Start, n.End) > n.StartSize);
        }

        [Fact]
        public void Oxbow_NotOnStraightReaches()
        {
            var net = new List<NetNode>
            {
                new NetNode { Start = new P2(0, 0), End = new P2(0, 300), StartSize = 30, EndSize = 28 },
                new NetNode { Start = new P2(0, 300), End = new P2(20, 600), Parent = 0, StartSize = 28, EndSize = 25 },
            };
            Assert.Empty(RiverNet.Oxbows(net, RiverNet.Classify(net), new OxbowOptions { Chance = 1 }, (i, s) => 0.1));
        }

        [Fact]
        public void Hash_IsDeterministicAndInRange()
        {
            for (int i = 0; i < 1000; i++)
            {
                double v = RiverNet.Hash01(123, i * 37, -i * 11, 5);
                Assert.InRange(v, 0.0, 1.0);
                Assert.Equal(v, RiverNet.Hash01(123, i * 37, -i * 11, 5));
            }
        }

        [Fact]
        public void Config_MigratesOldOxbowDefaults_KeepsCustomValues()
        {
            var old = Newtonsoft.Json.JsonConvert.DeserializeObject<HierarchyConfig>("{\"OxbowChance\":0.2,\"OxbowMinBendDegrees\":20,\"OxbowMinRiverSize\":16,\"OxbowMaxLevel\":1}");
            Assert.True(old.Migrate());
            Assert.Equal(0.65, old.OxbowChance);
            Assert.Equal(12, old.OxbowMinBendDegrees);
            Assert.Equal(HierarchyConfig.CurrentVersion, old.ConfigVersion);
            Assert.False(old.Migrate());
            var custom = Newtonsoft.Json.JsonConvert.DeserializeObject<HierarchyConfig>("{\"OxbowChance\":0.05}");
            custom.Migrate();
            Assert.Equal(0.05, custom.OxbowChance); // a value the player chose stays
        }

        [Fact]
        public void Oxbows_MoreOftenWithNewDefaults()
        {
            // A meandering stem: 30 nodes turning +-18 degrees alternately (Rivers' normalAngle is 20).
            var net = new List<NetNode>();
            var at = new P2(1000, 1000); double ang = 90;
            for (int i = 0; i < 30; i++)
            {
                ang += (i % 2 == 0 ? 18 : -18);
                var end = at + new P2(Math.Cos(ang * Math.PI / 180), Math.Sin(ang * Math.PI / 180)) * 250;
                net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 30, EndSize = 28 });
                at = end;
            }
            var h = RiverNet.Classify(net);
            var oldCount = RiverNet.Oxbows(net, h, new OxbowOptions { Chance = 0.2, MinBendDegrees = 20 }, (i, s) => RiverNet.Hash01(7, i, s, 3)).Count;
            var newCount = RiverNet.Oxbows(net, h, new HierarchyConfig().Oxbow(), (i, s) => RiverNet.Hash01(7, i, s, 3)).Count;
            Assert.Equal(0, oldCount);      // 18-degree bends were below the old 20-degree threshold
            Assert.True(newCount >= 8, "expected many oxbows along a meandering river, got " + newCount);
        }

        [Fact]
        public void Config_SanitizeClamps()
        {
            var c = new HierarchyConfig { MaxRiverLengthBlocks = 99999, TributaryLengthShare = 5, DeltaMinBranches = 6, DeltaMaxBranches = 3, OxbowMinRadius = 100, OxbowMaxRadius = 50 };
            c.Sanitize();
            Assert.Equal(30000, c.MaxRiverLengthBlocks);
            Assert.Equal(1.0, c.TributaryLengthShare);
            Assert.True(c.DeltaMaxBranches >= c.DeltaMinBranches);
            Assert.True(c.OxbowMaxRadius >= c.OxbowMinRadius);
        }
    }
}
