using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 17 / 17b: spaced channels from the apex, meandering channels, the shallow rise along the path, plain depth.</summary>
    public class FlightTest17Tests
    {
        static List<NetNode> River()
        {
            var net = new List<NetNode>();
            var at = new P2(5000, 0);
            for (int i = 0; i < 6; i++) { var end = at + new P2(0, 250); net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 40, EndSize = 38 }); at = end; }
            return net;
        }

        [Fact]
        public void Delta_channels_start_at_the_apex_and_stay_spaced()
        {
            // Flight test 17b: one start (the apex centre line), every channel at least MinBranchSeparation from the others
            // and from the main stem, even when many are rolled.
            var net = River(); var h = RiverNet.Classify(net);
            Func<P2, double> ocean = q => q.Z < 300 ? 255 : 0;
            for (int s = 0; s < 200; s++)
            {
                var o = new DeltaOptions { Chance = 1, MinBranches = 6, MaxBranches = 8, MinLength = 200, MaxLength = 260, SpreadDegrees = 45 };
                var ch = RiverNet.CoastDelta(net, h, 0, 50, o, ocean, 30, null, salt => RiverNet.Hash01(s, 4, 4, salt), out P2 apex, out _);
                var stem = new P2(0, -1);
                for (int i = 0; i < ch.Count; i++)
                {
                    Assert.True((ch[i].End - apex).Length < 1e-6, "every channel starts at the apex");
                    Assert.True(RiverNet.NearestChannelAngle(ch, i, stem) >= RiverNet.MinBranchSeparation - 1e-6, "seed " + s + ": channels too close");
                }
            }
        }

        [Fact]
        public void Meander_turns_gently_at_every_joint()
        {
            // Flight test 18: points every ~32 blocks, so no joint turns sharply (Rivers' connector rounds the rest).
            double worst = 0;
            for (int s = 0; s < 200; s++)
            {
                var pts = RiverNet.Meander(new P2(0, 0), new P2(0, 500), salt => RiverNet.Hash01(s, 2, 2, salt));
                for (int k = 1; k + 1 < pts.Count; k++)
                {
                    var a = (pts[k] - pts[k - 1]).Normalized(); var b = (pts[k + 1] - pts[k]).Normalized();
                    worst = Math.Max(worst, Math.Acos(Math.Clamp(P2.Dot(a, b), -1, 1)) * 180 / Math.PI);
                }
            }
            Assert.True(worst < 30, "sharpest joint " + worst + " degrees");
        }

        [Fact]
        public void Meander_bend_shrinks_with_the_gap_to_the_neighbour()
        {
            Assert.Equal(0.22 * Math.Sin(12 * Math.PI / 180), RiverNet.MeanderCap(12), 9);
            Assert.Equal(0.12, RiverNet.MeanderCap(60), 9);
            Assert.Equal(0, RiverNet.MeanderCap(0), 9);
            var pts = RiverNet.Meander(new P2(0, 0), new P2(0, 400), s => 0.99, RiverNet.MeanderCap(12));
            Assert.True(pts.Max(p => Math.Abs(p.X)) <= 400 * RiverNet.MeanderCap(12) + 1e-6);
        }

        [Fact]
        public void Meander_keeps_its_ends_and_bends_moderately()
        {
            var a = new P2(0, 0); var b = new P2(0, 400);
            int bent = 0;
            for (int s = 0; s < 50; s++)
            {
                var pts = RiverNet.Meander(a, b, salt => RiverNet.Hash01(s, 1, 1, salt));
                Assert.Equal(a, pts[0]); Assert.Equal(b, pts[^1]);
                Assert.True(pts.Count >= 12, "pieces of about 32 blocks");
                double maxOff = pts.Max(p => Math.Abs(p.X));
                Assert.True(maxOff <= 400 * 0.12 + 1e-6, "off the straight line by at most 12%: " + maxOff);
                if (maxOff > 8) bent++;
            }
            Assert.True(bent > 40, "most channels visibly bend");
            Assert.Equal(2, RiverNet.Meander(a, new P2(0, 40), s => 0.5).Count);   // short: straight
        }

        [Fact]
        public void Shallow_rise_follows_a_bending_river_and_ends()
        {
            // Mouth heading north, then the river turns east: measured along the mouth's direction, the eastward nodes never
            // got past the rise (the old bug kept up to 16 nodes shallow).
            var net = new List<NetNode>();
            var at = new P2(0, 0);
            var dirs = new[] { new P2(0, 1), new P2(0, 1), new P2(1, 0), new P2(1, 0), new P2(1, 0), new P2(1, 0), new P2(1, 0), new P2(1, 0) };
            for (int i = 0; i < dirs.Length; i++) { var end = at + dirs[i] * 200; net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 30, EndSize = 30 }); at = end; }
            var h = RiverNet.Classify(net);
            var apex = new P2(0, 300);                  // 300 blocks up the river, in node 1
            var stem = RiverNet.StemRise(net, h, 0, apex, 250);
            // Node 3 starts 300 blocks along the river past the apex, beyond the 250-block rise: not covered.
            Assert.Equal(new[] { 0, 1, 2 }, stem.Select(s => s.node).ToArray());
            Assert.Equal(-300 / 250.0, stem[0].t0, 6);
            Assert.Equal(-100 / 250.0, stem[1].t0, 6);
            Assert.Equal(100 / 250.0, stem[2].t0, 6);
            // The cap per node, on node 2 (heading east): full depth by 250 blocks past the apex along the river.
            var info = new DryChannels.Info { Cap = 0.1, RelaxUpstream = true, Start = new Vector2d(0, 400), End = new Vector2d(250 / 0.6, 400), T0 = stem[2].t0 };
            Assert.True(DryChannels.CapAt(info, new Vector2d(10, 400)) < 1);                // 110 past the apex: still rising
            Assert.Equal(1.0, DryChannels.CapAt(info, new Vector2d(160, 400)), 9);          // 260 past: full depth
        }

        [Fact]
        public void Shallow_plain_depth_varies_one_to_three()
        {
            var counts = new int[3];
            for (int k = 0; k < 1000; k++) counts[RiverNet.PlainExtra(RiverNet.Hash01(3, k, 0, 1))]++;
            Assert.InRange(counts[0], 380, 520);
            Assert.InRange(counts[1], 280, 420);
            Assert.InRange(counts[2], 140, 260);
        }
    }
}
