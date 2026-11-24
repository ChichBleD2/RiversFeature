using System;
using System.Collections.Generic;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Flight test 4: natural hollows fill to their spill level; Rivers' flow on still and isolated water.</summary>
    public class FlightTest4Tests
    {
        static readonly LakeParams P = new LakeParams(110, 10, 3, 5, 12, 1.0, 6, 8);

        static LakeWorld World(Func<int, int, int> h, int featureLevel) => new LakeWorld
        {
            Height = h,
            WaterTop = (x, z) => -1,
            RiverLevel = (x, z) => -1,
            Feature = (x, z) => WaterRules.BestFeature(new[] { new FeatureCell(0, 0, 16.5, 16.5, 30, featureLevel) }, x, z),
            NearStructure = (x, z) => false,
            NearOcean = (x, z) => false,
        };

        static double R(int x, int z) => Math.Sqrt((x + 0.5 - 16.5) * (x + 0.5 - 16.5) + (z + 0.5 - 16.5) * (z + 0.5 - 16.5));

        [Fact]
        public void Hollow_FillsToItsSpillLevel_NotTheFeatureLevel()
        {
            // A bowl: floor 113 in the middle rising to a rim of 118 at r = 8; the feature's own level is 112 (below the floor).
            Func<int, int, int> h = (x, z) => { double r = R(x, z); return r < 8 ? 113 + (int)(r * 0.6) : 118; };
            var plans = new LakePlanner(World(h, 112), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            var centre = plans[16 * 32 + 16];
            Assert.True(centre.Flood, "the hollow should fill");
            Assert.True(centre.Basin);
            Assert.Equal(118, centre.Column.Level);              // spill level = the rim
            Assert.Equal(113, centre.Column.Bed);                // natural floor kept
            foreach (var pl in plans) if (pl.Flood) Assert.Equal(118, pl.Column.Level);
        }

        [Fact]
        public void Hollow_WithALowNotch_FillsOnlyToTheNotch()
        {
            // Floor 111 inside r < 6, a ring of 117 to r = 8, 118 outside; a notch at 114 cuts the ring on the east side and runs
            // on to the edge of the window: the water stops at the notch (114).
            Func<int, int, int> h = (x, z) =>
            {
                if (z == 16 && x >= 22) return 114;
                double r = R(x, z);
                return r < 6 ? 111 : r < 8 ? 117 : 118;
            };
            var plans = new LakePlanner(World(h, 112), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.True(plans[16 * 32 + 16].Flood);
            Assert.Equal(114, plans[16 * 32 + 16].Column.Level);
        }

        [Fact]
        public void OpenSlope_IsNoHollow_AndDeepShaftsStayDry()
        {
            // A plain slope (no closed region): nothing floods as a basin.
            var slope = new LakePlanner(World((x, z) => 112 + x / 4, 112), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            foreach (var pl in slope) Assert.False(pl.Flood && pl.Basin);
            // A 20-deep shaft (deeper than MaxBasinDepth 10) inside a rim at 121: not filled.
            var plans = new LakePlanner(World((x, z) => R(x, z) < 3 ? 100 : 121, 112), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.False(plans[16 * 32 + 16].Flood);
        }

        [Fact]
        public void Hollow_TooShallow_StaysDry()
        {
            // A 2-block dip (LakeMinDepth 3): no lake.
            var plans = new LakePlanner(World((x, z) => R(x, z) < 6 ? 116 : 118, 112), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.False(plans[16 * 32 + 16].Flood);
        }

        // ------------------------------------------------------------------ Rivers flow vectors

        [Fact]
        public void IsolatedPool_IsFound_ButAChannelThroughTheWindowIsNot()
        {
            // A pool of 5x4 inside the chunk, and a river channel crossing the whole scanned window along z = 28.
            Func<int, int, bool> wet = (x, z) => (x >= 10 && x < 15 && z >= 10 && z < 14) || z == 28;
            var iso = FlowFix.IsolatedWater(wet, window: 30);
            Assert.True(iso[12 * 32 + 12]);
            Assert.False(iso[28 * 32 + 5]);
            Assert.False(iso[0]);
        }

        [Fact]
        public void LargePool_IsNotTreatedAsIsolated()
        {
            Func<int, int, bool> wet = (x, z) => x >= 2 && x < 30 && z >= 2 && z < 30; // 784 columns: a lake, not a puddle
            var iso = FlowFix.IsolatedWater(wet, window: 30);
            Assert.False(iso[16 * 32 + 16]);
        }

        [Fact]
        public void ClearFlow_ZeroesBothComponentsOfMarkedColumns()
        {
            var flow = new float[2048];
            for (int i = 0; i < flow.Length; i++) flow[i] = 0.7f;
            var mark = new bool[1024];
            mark[5] = mark[1023] = true;
            Assert.Equal(2, FlowFix.Clear(flow, mark));
            Assert.Equal(0f, flow[5]); Assert.Equal(0f, flow[1024 + 5]);
            Assert.Equal(0f, flow[1023]); Assert.Equal(0f, flow[2047]);
            Assert.Equal(0.7f, flow[6]);
            Assert.Equal(0, FlowFix.Clear(flow, mark)); // already still
            Assert.Equal(0, FlowFix.Clear(new float[10], mark)); // wrong layout: untouched
        }
    }
}
