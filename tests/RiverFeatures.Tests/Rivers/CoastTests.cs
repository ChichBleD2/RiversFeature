using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 7: deltas on land at the coast, world settings, the river-mouth patch.</summary>
    public class CoastTests
    {
        // A wide river whose mouth node starts out at sea (z 0) and runs north; the coast is at z 300.
        static List<NetNode> River()
        {
            var net = new List<NetNode>();
            var at = new P2(5000, 0);
            for (int i = 0; i < 6; i++)
            {
                var end = at + new P2(0, 250);
                net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 40, EndSize = 38 });
                at = end;
            }
            return net;
        }

        static double Ocean(P2 q) => q.Z < 300 ? 255 : 0;

        [Fact]
        public void Delta_ForksOnLand_AndEachChannelReachesTheSea()
        {
            var net = River(); var h = RiverNet.Classify(net);
            var o = new DeltaOptions { Chance = 1, MinBranches = 3, MaxBranches = 3, MinLength = 200, MaxLength = 200, SpreadDegrees = 40 };
            var ch = RiverNet.CoastDelta(net, h, 0, 50, o, Ocean, 30, null, s => 0.4);
            Assert.Equal(3, ch.Count);
            foreach (var c in ch)
            {
                Assert.InRange(c.End.Z, 490, 515);              // the apex: 200 blocks upstream of the coast, on land
                Assert.True(c.Start.Z < 300);                    // each channel ends in the sea
                Assert.True(c.Start.Z > 300 - 60, "ends just past the coast, not far out on the shelf: " + c.Start.Z);
            }
            Assert.Equal(3, ch.Select(c => Math.Round(c.Start.X)).Distinct().Count());
            Assert.Contains(ch, c => c.Start.X < 5000);
            Assert.Contains(ch, c => c.Start.X > 5000);          // both sides of the main stem
        }

        [Fact]
        public void Delta_NoneWithoutACoast_OrOnHighGround_OrByChance()
        {
            var net = River(); var h = RiverNet.Classify(net);
            var o = new DeltaOptions { Chance = 1, MinBranches = 2, MaxBranches = 2 };
            Assert.Empty(RiverNet.CoastDelta(net, h, 0, 50, o, q => 255, 30, null, s => 0.4));       // all sea: no coast
            Assert.Empty(RiverNet.CoastDelta(net, h, 0, 50, o, Ocean, 30, pts => true, s => 0.4));  // every channel too high
            Assert.Empty(RiverNet.CoastDelta(net, h, 0, 50, new DeltaOptions { Chance = 0.1 }, Ocean, 30, null, s => 0.99));
            var small = River(); foreach (var n in small) { n.StartSize = 14; n.EndSize = 14; }
            Assert.Empty(RiverNet.CoastDelta(small, RiverNet.Classify(small), 0, 50, o, Ocean, 30, null, s => 0.4));
        }

        [Fact]
        public void WorldSettings_RiverLengthDeltaOxbow()
        {
            var c = new HierarchyConfig();
            var set = new Dictionary<string, string> { ["rhRiverLength"] = "15000", ["rhDeltaChance"] = "1", ["rhOxbowChance"] = "config" };
            var lines = c.ApplyWorldSettings(k => set.TryGetValue(k, out var v) ? v : null);
            Assert.Equal(15000, c.MaxRiverLengthBlocks);
            Assert.Equal(1.0, c.DeltaChance);
            Assert.Equal(0.65, c.OxbowChance);
            Assert.Equal(2, lines.Count);
            var d = new HierarchyConfig();
            d.ApplyWorldSettings(k => k == "rhRiverLength" ? "0" : "x");
            Assert.Equal(0, d.MaxRiverLengthBlocks);            // "Rivers default": Rivers' own length
            Assert.Equal(0.35, d.DeltaChance);
        }

        [Fact]
        public void WorldConfigFile_CodesAndLangKeysMatch()
        {
            string root = null;
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RiverFeatures.csproj"))) { root = dir.FullName; break; }
            Assert.NotNull(root);
            var wc = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "worldconfig.json")));
            var lang = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "assets", "game", "lang", "worldconfig-en.json")));
            foreach (var a in wc["worldConfigAttributes"])
            {
                string code = (string)a["code"];
                Assert.NotNull(lang["worldattribute-" + code]);
                Assert.NotNull(lang["worldconfig-category-" + (string)a["category"]]);
                foreach (var n in a["names"]) Assert.NotNull(lang["worldconfig-" + code + "-" + (string)n]);
            }
        }

    }
}
