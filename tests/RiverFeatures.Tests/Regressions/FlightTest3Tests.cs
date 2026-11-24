using System;
using System.Collections.Generic;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Flight test 3: Rivers lakes and islands, stepped funnels, chasms (landforms).</summary>
    public class FlightTest3Tests
    {
        static readonly LakeParams P = new LakeParams(110, 10, 3, 5, 12, 1.0, 6, 8);

        [Fact]
        public void RiversLakeIsland_IsNotFlooded_ButAChannelBankIs()
        {
            // An island at 115 inside a Rivers lake (water top 115 all around it), and the river-lake mask everywhere.
            Func<int, int, bool> island = (x, z) => x >= 12 && x <= 19 && z >= 12 && z <= 19;
            Func<int, int, int> top = (x, z) => island(x, z) ? -1 : 115;
            var world = new LakeWorld
            {
                Height = (x, z) => island(x, z) ? 115 : 108,
                WaterTop = top,
                RiverLevel = (x, z) => 115,
                Feature = (x, z) => default,
                NearStructure = (x, z) => false,
                NearOcean = (x, z) => false,
                RiverLake = (x, z) => 115,
                WaterShare = (x, z) => 0.8, // mostly water around: a lake, not a channel
            };
            var plans = new LakePlanner(world, P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            foreach (var pl in plans) Assert.False(pl.Flood, "an island of a Rivers lake must stay an island");

            world.WaterShare = (x, z) => 0.25; // the same ground beside a channel joins it
            var channel = new LakePlanner(world, P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.Contains(channel, pl => pl.Flood);
        }

        [Fact]
        public void BedNextToAFlushRiverChannel_SlopesIntoIt()
        {
            // River channel along x = 0..3 (bed 105, water 115); flat ground 115 east of it joins at 115.
            var world = new LakeWorld
            {
                Height = (x, z) => x <= 3 ? 105 : 115,
                WaterTop = (x, z) => x <= 3 ? 115 : -1,
                RiverLevel = (x, z) => Math.Abs(x - 3) <= 9 ? 115 : -1,
                Feature = (x, z) => default,
                NearStructure = (x, z) => false,
                NearOcean = (x, z) => false,
                RiverLake = (x, z) => x > 3 && x - 3 <= ColumnExclusions.RiverLakeReach ? 115 : -1,
                WaterShare = (x, z) => 0.3,
            };
            var plans = new LakePlanner(world, P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            var nextToRiver = plans[16 * 32 + 4];
            Assert.True(nextToRiver.Flood, "reason " + nextToRiver.Reason + ", candidate reason " + nextToRiver.Column.Reason);
            // Without the river as a smoothing source this column would be at most BottomDepth (5) deep: bed 110; the channel
            // bed (105) pulls it down to 106 (one block above the river bed), capped at MaxCut below its ground (110).
            Assert.True(nextToRiver.Column.Bed <= 110);
            for (int x = 4; x < 12; x++)
                if (plans[16 * 32 + x].Flood && plans[16 * 32 + x + 1].Flood)
                    Assert.True(Math.Abs(plans[16 * 32 + x].Column.Bed - plans[16 * 32 + x + 1].Column.Bed) <= 1);
        }

        [Fact]
        public void GentleShore_NoFunnel()
        {
            // A small round pond (radius 7 disc on flat ground at the level): at most 0.6 block of depth per block from shore.
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16.5, 16.5, 12, 116) };
            var world = new LakeWorld
            {
                Height = (x, z) => 116,
                WaterTop = (x, z) => -1,
                RiverLevel = (x, z) => -1,
                Feature = (x, z) => WaterRules.BestFeature(feats, x, z),
                NearStructure = (x, z) => false,
                NearOcean = (x, z) => false,
            };
            var plans = new LakePlanner(world, P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            int maxDepth = 0;
            foreach (var pl in plans) if (pl.Flood) maxDepth = Math.Max(maxDepth, pl.Column.Level - pl.Column.Bed);
            Assert.InRange(maxDepth, 1, 3); // the old 1-per-block ramp dug this pond to 4..5: a stepped funnel
        }

        [Fact]
        public void Landforms_NeverReachBelowSeaLevel()
        {
            string path = System.IO.Path.Combine(FindRoot(), "assets", "riverfeatures", "patches", "landforms.json");
            var arr = Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(path));
            foreach (var op in arr)
            {
                var keys = op["value"]["terrainYKeyPositions"];
                Assert.True((double)keys[0] >= 0.43, (string)op["value"]["code"] + " reaches below sea level");
            }
        }

        static string FindRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "modinfo.json"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("mod root not found");
        }
    }
}
