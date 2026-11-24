using System;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Flight test 9: mini-lakes are dropped, larger lakes kept.</summary>
    public class FlightTest9Tests
    {
        static readonly LakeParams P = new LakeParams(110, 10, 3, 5, 12, 1.0, 6, 8);

        static LakeWorld World(Func<int, int, int> h) => new LakeWorld
        {
            Height = h,
            WaterTop = (x, z) => -1,
            RiverLevel = (x, z) => -1,
            Feature = (x, z) => WaterRules.BestFeature(new[] { new FeatureCell(0, 0, 16.5, 16.5, 30, 112) }, x, z),
            NearStructure = (x, z) => false,
            NearOcean = (x, z) => false,
        };

        static double R(int x, int z) => Math.Sqrt((x + 0.5 - 16.5) * (x + 0.5 - 16.5) + (z + 0.5 - 16.5) * (z + 0.5 - 16.5));

        static int Flooded(LakePlan[] plans) { int n = 0; foreach (var p in plans) if (p.Flood) n++; return n; }

        [Fact]
        public void SmallHollow_IsDropped_WhenMinAreaIsAbove()
        {
            // A bowl of radius 3 (about 28 columns): a mini-lake.
            Func<int, int, int> h = (x, z) => R(x, z) < 3 ? 114 : 118;
            var keepAll = new LakePlanner(World(h), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.True(Flooded(keepAll) > 0);
            var filtered = new LakePlanner(World(h), P, 22) { MinArea = 80 }.PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.Equal(0, Flooded(filtered));
            Assert.Equal(LakeSkip.Small, filtered[16 * 32 + 16].Reason);
        }

        [Fact]
        public void LargerHollow_IsKept()
        {
            // A bowl of radius 7 (about 150 columns).
            Func<int, int, int> h = (x, z) => R(x, z) < 7 ? 114 : 118;
            var plans = new LakePlanner(World(h), P, 22) { MinArea = 80 }.PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.True(plans[16 * 32 + 16].Flood);
            Assert.True(Flooded(plans) >= 80);
        }

        [Fact]
        public void Default_MinArea_Is80() => Assert.Equal(80, new TerrainWatersConfig().LakeMinArea);
    }
}
