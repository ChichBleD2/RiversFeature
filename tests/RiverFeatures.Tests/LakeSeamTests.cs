using System;
using System.Collections.Generic;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Flight test 11 (square lake pits, -2428,-8368): a lake beside a finished neighbour chunk that stayed dry.</summary>
    public class LakeSeamTests
    {
        static readonly LakeParams P = new LakeParams(110, 10, 3, 5, 12, 1.0, 6, 8);

        static LakeWorld World(List<FeatureCell> feats, Func<int, int, int> finished) => new LakeWorld
        {
            Height = (x, z) => 116,          // flat meadow at the lake level
            WaterTop = (x, z) => -1,
            RiverLevel = (x, z) => -1,
            Feature = (x, z) => WaterRules.BestFeature(feats, x, z),
            NearStructure = (x, z) => false,
            NearOcean = (x, z) => false,
            Finished = finished,
        };

        [Fact]
        public void Shore_ramps_up_to_a_finished_dry_neighbour()
        {
            // A big lake disc over the whole chunk and beyond; the west neighbour chunk (x < 0) is finished and DRY.
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16, 16, 60, 116) };
            var planOnly = new LakePlanner(World(feats, null), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            var seamAware = new LakePlanner(World(feats, (x, z) => x < 0 ? 0 : -1), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            int z0 = 16;
            Assert.True(planOnly[z0 * 32].Flood && seamAware[z0 * 32].Flood);
            int depthPlanOnly = 116 - planOnly[z0 * 32].Column.Bed, depthSeam = 116 - seamAware[z0 * 32].Column.Bed;
            Assert.True(depthPlanOnly >= 3, "without the neighbour's real state the border column is dug deep (the wall)");
            Assert.Equal(1, depthSeam);  // next to the dry neighbour: 1 deep, then deeper away from it
            Assert.True(116 - seamAware[z0 * 32 + 3].Column.Bed > depthSeam);
        }

        [Fact]
        public void Finished_neighbour_with_water_is_not_shore()
        {
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16, 16, 60, 116) };
            var a = new LakePlanner(World(feats, null), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            var b = new LakePlanner(World(feats, (x, z) => x < 0 ? 1 : -1), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.Equal(a[16 * 32].Column.Bed, b[16 * 32].Column.Bed);
        }

        [Fact]
        public void View_round_trips_and_compare_finds_a_disagreement()
        {
            var flood = new LakePlan(new LakeColumn(true, 116, 2, 114, LakeSkip.None), LakeSkip.None, false);
            var dry = new LakePlan(LakeColumn.No(LakeSkip.Ocean, 116), LakeSkip.Ocean, false);
            // Earlier chunk (1,0) thinks its x 0..2 (world 32..34) stay dry; the later chunk (0,0) thinks they flood.
            byte[] earlier = LakeSeamDiag.Encode((x, z) => x >= 0 && x < 3 ? dry : flood, (x, z) => 116);
            byte[] later = LakeSeamDiag.Encode((x, z) => flood, (x, z) => 116);
            var c = LakeSeamDiag.Read(earlier, 1, 5);
            Assert.True(c.Known);
            Assert.Equal(LakeSkip.Ocean, c.Reason);
            Assert.Equal(116, c.Height);
            var lines = new List<string>();
            int n = LakeSeamDiag.Compare(later, 0, 0, (cx, cz) => cx == 1 && cz == 0 ? earlier : null, lines.Add);
            Assert.True(n > 0);
            Assert.Contains(lines, l => l.Contains("Ocean"));
        }
    }
}
