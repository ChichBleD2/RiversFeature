using System;
using System.Collections.Generic;
using System.Linq;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 5: terrain estimate, oxbows only on flat ground, steep headwaters trimmed, calm flow near sources.</summary>
    public class TerrainTests
    {
        const int MapY = 256, Sea = 110;

        // A landform whose median surface is at `surface` and whose noise band (probability 0.75..0.25) is `band` blocks.
        static float[] Landform(double surface, double band)
        {
            float lo = (float)((surface - band) / MapY), hi = (float)((surface + band) / MapY);
            return TerrainModel.KeyThresholds(new[] { lo, hi }, new[] { 1f, 0f }, MapY);
        }

        static TerrainModel Model(Func<double, double, float[]> land, double strength = 0.02) => new TerrainModel
        {
            SeaLevel = Sea, Land = land, River = Landform(Sea + 2, 1), Strength = (x, z) => strength, NoiseShare = 1,
        };

        static List<NetNode> Stem(int count, double len = 250, float size = 20)
        {
            var net = new List<NetNode>();
            var at = new P2(1000, 1000);
            for (int i = 0; i < count; i++)
            {
                var end = at + new P2(0, len);
                net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = size, EndSize = size });
                at = end;
            }
            return net;
        }

        [Fact]
        public void KeyThresholds_InterpolateLinearly()
        {
            var t = TerrainModel.KeyThresholds(new[] { 0.25f, 0.75f }, new[] { 1f, 0f }, 100);
            Assert.Equal(1f, t[10]);
            Assert.Equal(0.5f, t[50], 3);
            Assert.Equal(0f, t[90]);
        }

        [Fact]
        public void Estimate_FollowsLandformAwayFromRivers_AndRiverLandformInStrongValleys()
        {
            var hills = Landform(Sea + 40, 6);
            var m = Model((x, z) => hills);
            Assert.True(m.At(0, 0, 500, out _, out double farHi));        // beyond the valley: the landform itself
            Assert.InRange(farHi, Sea + 38, Sea + 44);
            Assert.True(m.At(0, 0, 0, out _, out double inHi));          // in a strong valley: Rivers' flat river landform
            Assert.InRange(inHi, Sea, Sea + 5);
            var weak = Model((x, z) => hills, strength: 0.6);              // weak valley: 36% of the hill remains at the channel
            Assert.True(weak.At(0, 0, 0, out _, out double weakHi));
            Assert.True(weakHi > Sea + 12);
        }

        [Fact]
        public void Oxbow_AcceptedOnLowFlatGround_RejectedOnHillsAndHighGround()
        {
            var net = Stem(2);
            var chain = Enumerable.Range(0, 6).Select(k => new P2(1060 + 40 * Math.Cos(k * 0.8), 1250 + 40 * Math.Sin(k * 0.8))).ToList();
            var origin = new P2(0, 0);

            var flat = Model((x, z) => Landform(Sea + 3, 2));
            Assert.InRange(TerrainRules.OxbowRelief(chain, 10, 12, net, flat, origin), 0, 7);

            var high = Model((x, z) => Landform(Sea + 30, 2), strength: 0.5);    // flat but far above the water: a pit
            Assert.True(TerrainRules.OxbowRelief(chain, 10, 12, net, high, origin) > 7);

            var hills = Model((x, z) => x > 1060 ? Landform(Sea + 45, 20) : Landform(Sea + 3, 2), strength: 0.5);
            Assert.True(TerrainRules.OxbowRelief(chain, 10, 12, net, hills, origin) > 7);

            Assert.True(double.IsNaN(TerrainRules.OxbowRelief(chain, 10, 12, net, Model((x, z) => null), origin)));
        }

        [Fact]
        public void Headwaters_SteepTipsAreTrimmed_UpToTheLimit()
        {
            var net = Stem(6);
            // Ground rises toward the source: z above 2000 (nodes 4 and 5) is high and weakly valleyed.
            var m = Model((x, z) => z > 2000 ? Landform(Sea + 50, 10) : Landform(Sea + 3, 2), strength: 0.5);
            var origin = new P2(0, 0);
            bool Steep(int i, ICollection<int> removed) => TerrainRules.HeadwaterRise(net, i, 10, m, origin, removed) > 8;

            var trimmed = TerrainRules.HeadwatersToTrim(net, Steep, maxPerTip: 3, minKeep: 2);
            Assert.Equal(new[] { 4, 5 }, trimmed.OrderBy(i => i).ToArray());   // node 3 (ends at z 2000) is low enough

            var limited = TerrainRules.HeadwatersToTrim(net, (i, r) => true, maxPerTip: 3, minKeep: 2);
            Assert.Equal(3, limited.Count);                                      // never more than maxPerTip per source
            var keep = TerrainRules.HeadwatersToTrim(Stem(3), (i, r) => true, maxPerTip: 3, minKeep: 2);
            Assert.Single(keep);                                                // a river keeps minKeep nodes
        }

        [Fact]
        public void Headwaters_ForkParentIsNotTrimmedWhileItHasAnotherBranch()
        {
            var net = Stem(2);
            net.Add(new NetNode { Start = net[1].End, End = net[1].End + new P2(200, 150), Parent = 1, StartSize = 14, EndSize = 14 }); // 2
            net.Add(new NetNode { Start = net[1].End, End = net[1].End + new P2(-200, 150), Parent = 1, StartSize = 14, EndSize = 14 }); // 3
            var trimmed = TerrainRules.HeadwatersToTrim(net, (i, r) => i == 2 || i == 1, maxPerTip: 3, minKeep: 1);
            Assert.Equal(new[] { 2 }, trimmed.ToArray());                        // node 1 still feeds branch 3
        }

        [Fact]
        public void Speeds_RampUpFromTheSource()
        {
            var net = Stem(6);
            var h = RiverNet.Classify(net);
            var sp = TerrainRules.HeadwaterSpeeds(net, h, 800, 0.25);
            Assert.Equal(1f, sp[0]);                                   // far from the source: Rivers' full speed
            Assert.InRange(sp[5], 0.25f, 0.45f);                        // the source node flows gently
            for (int i = 1; i < 6; i++) Assert.True(sp[i] <= sp[i - 1]); // never faster upstream
            Assert.All(TerrainRules.HeadwaterSpeeds(net, h, 0, 0.25), s => Assert.Equal(1f, s));
        }

        [Fact]
        public void WorldSetting_OxbowChance_OverridesOrKeeps()
        {
            var c = new HierarchyConfig();
            Assert.False(c.ApplyWorldOxbow("config"));
            Assert.False(c.ApplyWorldOxbow(null));
            Assert.False(c.ApplyWorldOxbow("2"));
            Assert.Equal(0.65, c.OxbowChance);
            Assert.True(c.ApplyWorldOxbow("0.85"));
            Assert.Equal(0.85, c.OxbowChance);
        }

        [Fact]
        public void Config_V2FileGetsNewKeys_AndKeepsItsOxbowChoices()
        {
            var v2 = Newtonsoft.Json.JsonConvert.DeserializeObject<HierarchyConfig>("{\"ConfigVersion\":2,\"OxbowChance\":0.2}");
            Assert.True(v2.Migrate());
            Assert.Equal(0.2, v2.OxbowChance);           // chosen in a version-2 file: kept
            Assert.Equal(7, v2.OxbowMaxRelief);
            Assert.Equal(8, v2.HeadwaterMaxRise);
            Assert.Equal(HierarchyConfig.CurrentVersion, v2.ConfigVersion);
        }
    }
}
