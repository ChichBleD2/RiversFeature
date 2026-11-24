using System.Collections.Generic;
using System.Linq;
using RiverFeatures;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>/rffind river: main-stem length, the river log (dedup by mouth), range parsing, the reply text.</summary>
    [Collection("RiverLog")]
    public class RiverLengthTests
    {
        [Fact]
        public void Main_stem_length_follows_the_longest_upstream_path()
        {
            // Stem: 0 -> 1 -> 2 (3 x 200 north). A short tributary 3 joins at node 0's end; a lake at the source.
            var net = new List<NetNode>
            {
                new NetNode { Start = new P2(0, 0), End = new P2(0, 200), Parent = -1, StartSize = 30, EndSize = 30 },
                new NetNode { Start = new P2(0, 200), End = new P2(0, 400), Parent = 0, StartSize = 30, EndSize = 30 },
                new NetNode { Start = new P2(0, 400), End = new P2(0, 600), Parent = 1, StartSize = 30, EndSize = 30 },
                new NetNode { Start = new P2(0, 200), End = new P2(100, 300), Parent = 0, StartSize = 10, EndSize = 10 },
                new NetNode { Start = new P2(0, 600), End = new P2(0, 650), Parent = 2, IsLake = true },
            };
            var h = RiverNet.Classify(net);
            var s = RiverNet.MainStem(net, h, 0).Value;
            Assert.Equal(600, s.Length, 6);
            Assert.Equal(3, s.StemNodes);
            Assert.Equal(4, s.TotalNodes);              // the lake is not a river node
            Assert.Equal(new P2(0, 0), s.Mouth);
            Assert.Equal(new P2(0, 600), s.Source);
            Assert.Null(RiverNet.MainStem(net, h, 4));    // a lake is no river
            Assert.Null(RiverNet.MainStem(net, h, 9));
        }

        [Fact]
        public void A_river_is_recorded_once_per_mouth()
        {
            RiverLog.Clear();
            try
            {
                var r = new RiverLog.River(100, 200, 100, 8200, 8000, 32, 50);
                Assert.True(RiverLog.Add(r));
                Assert.False(RiverLog.Add(r with { Length = 9000 }));   // a region rebuild (same mouth) is not counted again
                Assert.Equal(1, RiverLog.Count);
                Assert.Equal(8000, RiverLog.All().Single().Length);
            }
            finally { RiverLog.Clear(); }
        }

        [Fact]
        public void Range_arguments_parse_as_minimum_or_range()
        {
            Assert.True(RiverLog.TryParseRange(null, null, out var a, out var b, out _)); Assert.Null(a); Assert.Null(b);
            Assert.True(RiverLog.TryParseRange("8000", null, out a, out b, out _)); Assert.Equal(8000, a); Assert.Null(b);
            Assert.True(RiverLog.TryParseRange("9k", "5000", out a, out b, out _)); Assert.Equal(5000, a); Assert.Equal(9000, b);
            Assert.True(RiverLog.TryParseRange("7.5k", null, out a, out _, out _)); Assert.Equal(7500, a);
            Assert.False(RiverLog.TryParseRange("long", null, out _, out _, out var err)); Assert.Contains("long", err);
            Assert.False(RiverLog.TryParseRange("5000", "-3", out _, out _, out _));
        }

        // Four rivers near the origin (mouths along x, sources 4000..10000 north) and one 100000 blocks east.
        static readonly List<RiverLog.River> Sample = new List<RiverLog.River>
        {
            new RiverLog.River(1000, 0, 1000, 4000, 4000, 16, 20),
            new RiverLog.River(5000, 0, 5000, 9000, 9000, 36, 60),
            new RiverLog.River(-3000, 0, -3000, 6000, 6000, 24, 30),
            new RiverLog.River(20000, 0, 20000, 10000, 10000, 40, 70),
            new RiverLog.River(100000, 0, 100000, 12000, 12000, 48, 80),
        };

        [Fact]
        public void Without_bounds_the_longest_rivers_near_the_player_are_listed()
        {
            var text = RiverLog.Reply(Sample, null, null, 0, 0, 512000, 512000);
            Assert.Contains("Rivers within 24000 blocks of you (0,0, map -512000, -512000): 4 rivers, main stem min / avg / max 4000 / 7250 / 10000 blocks.", text);
            Assert.Contains("Whole session: 5 rivers", text);
            var lines = text.Split('\n').Skip(1).ToList();
            Assert.StartsWith("10000 blocks (40 stem nodes, 70 total) mouth 20000,0 (map -492000, -512000), 20000 blocks away", lines[0]);
            Assert.Contains("source 20000,10000 (map -492000, -502000), 22360 blocks away", lines[0]);
            Assert.StartsWith("9000 blocks", lines[1]);
            Assert.DoesNotContain("12000 blocks (", text);   // the river 100000 blocks east is not "near you"
        }

        [Fact]
        public void After_a_far_teleport_the_reply_says_nothing_is_generated_there_yet()
        {
            // At 100000 east the far river is near: it is the only one listed, distances from the player.
            var there = RiverLog.Reply(Sample, null, null, 100000, 5000, 0, 0);
            Assert.Contains("1 river, main stem min / avg / max 12000 / 12000 / 12000 blocks", there);
            Assert.Contains("mouth 100000,0 (map 100000, 0), 5000 blocks away", there);
            // At 300000 east nothing is recorded yet: said plainly, with the distance to the nearest recorded river.
            var empty = RiverLog.Reply(Sample, null, null, 300000, 0, 0, 0);
            Assert.Contains("no rivers", empty);
            Assert.Contains("No river has been generated near you yet", empty);
            Assert.Contains("The nearest recorded river is 200000 blocks away", empty);
            Assert.DoesNotContain("blocks (", empty);
            // 'all' lists the whole session regardless of the player's position.
            var all = RiverLog.Reply(Sample, null, null, 300000, 0, 0, 0, everywhere: true);
            Assert.StartsWith("Rivers this session (everywhere): 5 rivers", all);
            Assert.Contains("12000 blocks (48 stem nodes", all);
        }

        [Fact]
        public void A_range_lists_the_nearest_matching_rivers_near_the_player()
        {
            var text = RiverLog.Reply(Sample, 5000, 9500, 0, 0, 0, 0);
            Assert.Contains("Main stem 5000..9500 blocks near you: 2 rivers, main stem min / avg / max 6000 / 7500 / 9000 blocks.", text);
            var lines = text.Split('\n').Where(l => l.Contains("blocks away")).ToList();
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("6000 blocks", lines[0]);   // 3000 blocks to its mouth, nearer than the 9000 river (5000)
            Assert.Contains("Main stem 8000+ blocks near you: 2 rivers", RiverLog.Reply(Sample, 8000, null, 0, 0, 0, 0));
            Assert.Contains("Main stem 8000+ blocks: 3 rivers", RiverLog.Reply(Sample, 8000, null, 0, 0, 0, 0, everywhere: true));
            Assert.Contains("near you: no rivers", RiverLog.Reply(Sample, 20000, null, 0, 0, 0, 0));
            Assert.Contains("No rivers recorded this session yet", RiverLog.Reply(new List<RiverLog.River>(), null, null, 0, 0, 0, 0));
        }

        [Fact]
        public void Distance_is_to_the_nearer_end_from_the_player()
        {
            var r = new RiverLog.River(0, 0, 0, 10000, 10000, 40, 40);
            Assert.Equal(1000, RiverLog.Distance(r, 0, 11000), 6);   // near the source
            Assert.Equal(500, RiverLog.Distance(r, 0, -500), 6);     // near the mouth
        }
    }
}
