using System;
using System.Collections.Generic;
using System.Linq;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Code audit fixes: sibling branch spacing, island veto on a straight shore, config fallbacks and unknown keys.</summary>
    public class AuditTests
    {
        static List<NetNode> Stem()
        {
            var net = new List<NetNode>();
            for (int i = 0; i < 6; i++) net.Add(new NetNode { Start = new P2(0, i * 200), End = new P2(0, (i + 1) * 200), Parent = i - 1, StartSize = 20, EndSize = 20 });
            return net;
        }

        [Fact]
        public void A_branch_leaving_parallel_to_the_stem_is_cut_but_a_normal_fork_is_not()
        {
            // Leaves node 0's end (the junction with node 1, the stem's continuation) almost straight up beside it.
            var net = Stem();
            net.Add(new NetNode { Start = new P2(0, 200), End = new P2(15, 400), Parent = 0, StartSize = 8, EndSize = 8 });
            var cut = RiverNet.CrowdedBranches(net, RiverNet.Classify(net), 32);
            Assert.Contains(6, cut);
            for (int i = 0; i < 6; i++) Assert.DoesNotContain(i, cut);

            // The same branch at 45 degrees: a normal fork, kept.
            net[6].End = new P2(141, 341);
            Assert.Empty(RiverNet.CrowdedBranches(net, RiverNet.Classify(net), 32));
        }

        // A 4x4 hut at (100..103, 128, 200..203), floor 128; water one block below the floor where `wet` says.
        static bool Hut(Func<int, int, bool> wet) =>
            StructureGuard.WaterVeto(100, 128, 200, 104, 133, 204, 128, "ruin-hut", (x, y, z) => y == 127 && wet(x, z));

        [Fact]
        public void A_small_building_on_a_straight_shore_is_allowed_but_not_on_a_spit()
        {
            Assert.False(Hut((x, z) => x >= 105));                              // shore on one side
            Assert.False(Hut((x, z) => x >= 105 || z >= 205));                  // a corner of the shore: two sides
            Assert.True(Hut((x, z) => x >= 105 || z < 199 || z >= 205));       // a spit: three sides
            Assert.True(Hut((x, z) => x < 99 || x >= 105 || z < 199 || z >= 205)); // an island
        }

        [Fact]
        public void Unreadable_numbers_fall_back_to_the_real_defaults()
        {
            var defaults = new HierarchyConfig();
            var cfg = new HierarchyConfig();
            var fields = typeof(HierarchyConfig).GetFields().Where(f => f.FieldType == typeof(double) || f.FieldType == typeof(float)).ToList();
            foreach (var f in fields) f.SetValue(cfg, f.FieldType == typeof(double) ? (object)double.NaN : float.NaN);
            cfg.Sanitize();
            foreach (var f in fields)
                Assert.True(Convert.ToDouble(f.GetValue(defaults)) == Convert.ToDouble(f.GetValue(cfg)),
                    f.Name + ": default " + f.GetValue(defaults) + ", NaN fell back to " + f.GetValue(cfg));
        }

        [Fact]
        public void Unknown_keys_in_the_rivers_config_are_kept()
        {
            var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<HierarchyConfig>("{ \"DeltaChance\": 0.5, \"SomeFutureKey\": 7 }");
            Assert.Equal(0.5, cfg.DeltaChance);
            var back = Newtonsoft.Json.JsonConvert.SerializeObject(cfg);
            Assert.Contains("\"SomeFutureKey\":7", back);
        }

        [Fact]
        public void Setting_the_removed_river_lake_chance_is_refused()
        {
            Assert.False(ConfigEdit.TrySet(new TerrainWatersConfig(), "RiverLakeChance", "0.4", out var result, out var msg));
            Assert.Null(result);
            Assert.Contains("no longer used", msg);
        }
    }
}
