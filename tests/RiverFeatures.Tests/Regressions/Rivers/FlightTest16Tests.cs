using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 16: shallow delta fingers and rise, deltas along the coast, lake and branch spacing.</summary>
    public class FlightTest16Tests
    {
        static List<NetNode> River()
        {
            var net = new List<NetNode>();
            var at = new P2(5000, 0);
            for (int i = 0; i < 6; i++) { var end = at + new P2(0, 250); net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 40, EndSize = 38 }); at = end; }
            return net;
        }

        static readonly DeltaOptions Opt = new DeltaOptions { Chance = 1, MinBranches = 2, MaxBranches = 4, MinLength = 150, MaxLength = 260, SmallShare = 0, ShallowShare = 1 };

        [Fact]
        public void Shallow_deltas_have_one_to_many_fingers()
        {
            var net = River(); var h = RiverNet.Classify(net);
            var counts = new Dictionary<int, int>();
            for (int s = 0; s < 400; s++)
            {
                var ch = RiverNet.CoastDelta(net, h, 0, 50, Opt, q => q.Z < 300 ? 255 : 0, 30, null, salt => RiverNet.Hash01(s, 1, 2, salt), 24,
                    0.6 + RiverNet.Hash01(s, 3, 4, 1), RiverNet.Hash01(s, 5, 6, 1));
                if (ch.Count == 0) continue;
                Assert.All(ch, c => Assert.Equal(DeltaKind.Shallow, c.Kind));
                counts[ch.Count] = counts.GetValueOrDefault(ch.Count) + 1;
            }
            string all = string.Join(" ", counts.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value));
            Assert.True(counts.GetValueOrDefault(1) > 0, "a single finger occurs: " + all);
            Assert.True(counts.Keys.Max() >= 5, "many fingers occur: " + all);
            Assert.True(counts.GetValueOrDefault(3) < 0.5 * counts.Values.Sum(), "three is no longer the usual count: " + all);
        }

        [Fact]
        public void Delta_channels_do_not_run_along_the_coast()
        {
            // A coast that runs diagonally (sea where z < 300 + 0.9 (x - 5000)): a channel turned toward it runs along the
            // shore for a long way before reaching the sea. No channel may be longer than twice the apex distance (+ overshoot).
            var net = River(); var h = RiverNet.Classify(net);
            Func<P2, double> ocean = q => q.Z < 300 + 0.9 * (q.X - 5000) ? 255 : 0;
            for (int s = 0; s < 200; s++)
            {
                var o = new DeltaOptions { Chance = 1, MinBranches = 4, MaxBranches = 6, MinLength = 200, MaxLength = 200, SpreadDegrees = 75 };
                var ch = RiverNet.CoastDelta(net, h, 0, 50, o, ocean, 30, null, salt => RiverNet.Hash01(s, 7, 8, salt), 24, 1.0, 0.9);
                foreach (var c in ch)
                {
                    double len = (c.End - c.Start).Length;
                    Assert.True(len <= 2 * 200 + 24 + 8, "channel " + len + " blocks: skims along the coast");
                }
            }
        }

        [Fact]
        public void Shallow_rise_has_riffles_but_keeps_its_ends()
        {
            var plain = new DryChannels.Info { Cap = 0.07, RelaxUpstream = true, Start = new Vector2d(0, 0), End = new Vector2d(0, 400) };
            var riff = new DryChannels.Info { Cap = 0.07, RelaxUpstream = true, Start = new Vector2d(0, 0), End = new Vector2d(0, 400), Amp = 0.3, Waves = 2, Phase = 1 };
            Assert.Equal(0.07, DryChannels.CapAt(riff, new Vector2d(0, -50)), 9);   // seaward of the apex: the shallow cap
            Assert.Equal(1.0, DryChannels.CapAt(riff, new Vector2d(0, 390)), 9);    // past the rise: full depth
            int differ = 0;
            for (int z = 10; z < 240; z += 10)
            {
                double a = DryChannels.CapAt(plain, new Vector2d(0, z)), b = DryChannels.CapAt(riff, new Vector2d(0, z));
                Assert.InRange(b, 0.07, 1.0);
                if (Math.Abs(a - b) > 0.02) differ++;
            }
            Assert.True(differ >= 5, "the rise varies along its length");
        }

        [Fact]
        public void Carving_and_small_deltas_sometimes_get_more_branches()
        {
            var net = River(); var h = RiverNet.Classify(net);
            var o = new DeltaOptions { Chance = 1, MinBranches = 2, MaxBranches = 4, MinLength = 200, MaxLength = 200, SmallShare = 0, ShallowShare = 0 };
            int over4 = 0, n = 0;
            for (int s = 0; s < 400; s++)
            {
                RiverNet.CoastDelta(net, h, 0, 50, o, q => q.Z < 300 ? 255 : 0, 30, null, salt => RiverNet.Hash01(s, 9, 9, salt), out _, out int rolled);
                if (rolled > 0) { n++; if (rolled > 4) over4++; }
            }
            Assert.InRange(over4, n / 10, n / 2);   // a long tail above MaxBranches, not the rule
        }

        [Fact]
        public void A_channel_groove_can_be_its_own_depth()
        {
            // Rivers' ellipse with size 10, riverDepth 0.022, baseDepth 0.1: a bank value at the centre line gets the full groove.
            double full = Math.Max(Math.Sqrt(10) * 0.022, 0.1);
            Assert.Equal(0.05, DryChannels.GrooveAt(full, 10, 0.022, 0.1, 0.05), 9);
            Assert.True(DryChannels.GrooveAt(full * 0.2, 10, 0.022, 0.1, 0.05) == 0, "no groove near the channel edge");
            Assert.True(double.IsNaN(new DryChannels.Info().GrooveBank));   // default: no groove (every groove sets its own)
        }

        // ---------------------------------------------------------------- branch spacing

        [Fact]
        public void A_tributary_running_beside_the_main_stem_is_cut_back()
        {
            // Main stem north from (0,0); a tributary leaving at node 0's end that turns and runs parallel about 45 blocks east
            // of it (17 blocks of land between the edges).
            var net = new List<NetNode>();
            for (int i = 0; i < 6; i++) net.Add(new NetNode { Start = new P2(0, i * 200), End = new P2(0, (i + 1) * 200), Parent = i - 1, StartSize = 20, EndSize = 20 });
            net.Add(new NetNode { Start = new P2(0, 200), End = new P2(50, 260), Parent = 0, StartSize = 8, EndSize = 8 });    // 6: leaves the junction
            net.Add(new NetNode { Start = new P2(50, 260), End = new P2(40, 460), Parent = 6, StartSize = 8, EndSize = 8 });   // 7: alongside
            net.Add(new NetNode { Start = new P2(40, 460), End = new P2(45, 660), Parent = 7, StartSize = 8, EndSize = 8 });   // 8: upstream of it
            var h = RiverNet.Classify(net);
            var cut = RiverNet.CrowdedBranches(net, h, 32);
            Assert.Contains(7, cut); Assert.Contains(8, cut);
            Assert.DoesNotContain(6, cut);                  // the junction node keeps away from everything but its own parent
            for (int i = 0; i < 6; i++) Assert.DoesNotContain(i, cut);   // never the main stem
            Assert.Empty(RiverNet.CrowdedBranches(net, h, 0));
            // Far enough away: nothing is cut.
            net[7].End = new P2(300, 460); net[8].Start = new P2(300, 460); net[8].End = new P2(320, 660);
            Assert.Empty(RiverNet.CrowdedBranches(net, RiverNet.Classify(net), 32));
        }

        // ---------------------------------------------------------------- lakes and rivers

        [Fact]
        public void Lakes_keep_a_gap_from_rivers()
        {
            Assert.Equal(40, RiverIndex.FitRadius(40, 200, 24, 16));          // far: unchanged
            Assert.Equal(26, RiverIndex.FitRadius(40, 50, 24, 16));           // near: shrunk so 24 blocks of land remain
            Assert.True(double.IsNaN(RiverIndex.FitRadius(40, 30, 24, 16)));  // too near: dropped
            Assert.Equal(40, RiverIndex.FitRadius(40, double.PositiveInfinity, 24, 16));

            RiverIndex.Clear();
            try
            {
                RiverIndex.Add(1000, 0, 1000, 2000, 20);   // a river 40 blocks wide along x = 1000
                Assert.Equal(1060 - 1000 - 20, RiverIndex.EdgeDistance(1060, 500, 200), 6);
                Assert.True(RiverIndex.EdgeDistance(1010, 500, 200) < 0);    // inside the channel
                Assert.True(double.IsPositiveInfinity(RiverIndex.EdgeDistance(5000, 500, 200)));
            }
            finally { RiverIndex.Clear(); }
        }
    }
}
