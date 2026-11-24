using System;
using System.Collections.Generic;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Fixes from the 2026-11-22 flight test (swamp walls, lake walls, flow-through lakes, lake shapes, erosion).</summary>
    public class FlightTestFixTests
    {
        static readonly LakeParams P = new LakeParams(SeaLevel: 110, OceanBuffer: 10, MinDepth: 3, MaxCut: 5, MaxRise: 12, MaxSlope: 1.0, MaxRimVariation: 6, FadeMargin: 8);

        static LakeWorld Flat(int height, List<FeatureCell> feats, Func<int, int, int> waterTop = null, Func<int, int, int> riverLake = null)
        {
            return new LakeWorld
            {
                Height = (x, z) => height,
                WaterTop = waterTop ?? ((x, z) => -1),
                RiverLevel = riverLake ?? ((x, z) => -1),
                Feature = (x, z) => WaterRules.BestFeature(feats, x, z),
                NearStructure = (x, z) => false,
                NearOcean = (x, z) => false,
                RiverLake = riverLake,
            };
        }

        static int Depth(LakePlan p) => p.Column.Level - p.Column.Bed;

        // ------------------------------------------------------------------ lake walls (shore ramp)

        [Fact]
        public void OldPlanner_LeavesUnderwaterWallAtShore_NewRampDoesNot()
        {
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16, 16, 14, 116) };
            var planner = new LakePlanner(Flat(116, feats), P, 22);
            var old = planner.DecideAll();
            var now = new LakePlanner(Flat(116, feats), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));

            int oldShoreMin = int.MaxValue, flooded = 0, maxDepth = 0;
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                {
                    var o = old[z * 32 + x];
                    var n = now[z * 32 + x];
                    Assert.Equal(o.Flood, n.Flood); // which columns flood is unchanged
                    if (!n.Flood) continue;
                    flooded++;
                    maxDepth = Math.Max(maxDepth, Depth(n));
                    bool shore = false;
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var nb = now[(z + dz) * 32 + (x + dx)];
                        if (!nb.Flood) shore = true;
                        else Assert.True(Math.Abs(nb.Column.Bed - n.Column.Bed) <= 1, $"underwater step at {x},{z}");
                    }
                    if (shore)
                    {
                        oldShoreMin = Math.Min(oldShoreMin, Depth(o));
                        Assert.Equal(1, Depth(n)); // shore columns hold one block of water
                    }
                }
            Assert.True(flooded > 50);
            Assert.True(oldShoreMin >= 3, "the old planner dug at least MinDepth right at the shore (the wall seen in game)");
            Assert.InRange(maxDepth, 3, 5); // gentle 0.45..0.6 slope: a lake ~19 wide is 3..5 deep in the middle
        }

        [Fact]
        public void ShoreBed_NeverFillsAndNeverDeepens()
        {
            Assert.Equal(115, WaterRules.ShoreBed(116, 116, 111, 1)); // bank column: 1 deep at the shore
            Assert.Equal(112, WaterRules.ShoreBed(112, 116, 111, 1)); // natural basin floor below the ramp: kept
            Assert.Equal(111, WaterRules.ShoreBed(116, 116, 111, 9)); // interior: the planned carve
            Assert.Equal(1, WaterRules.ShoreDepth(0.5));
            Assert.Equal(2, WaterRules.ShoreDepth(Math.Sqrt(5)));
        }

        // ------------------------------------------------------------------ flow-through lakes

        [Fact]
        public void RiverSideLake_JoinsTheRiverAtItsLevel()
        {
            // A river along x = 10 (surface 115, bed 112), flat ground at 115 around it, river-lake mask everywhere.
            Func<int, int, int> top = (x, z) => x == 10 ? 115 : -1;
            Func<int, int, int> riverLake = (x, z) => Math.Abs(x - 10) <= ColumnExclusions.RiverLakeReach ? 115 : -1;
            var world = Flat(115, new List<FeatureCell>(), top, riverLake);
            var baseHeight = world.Height;
            world.Height = (x, z) => x == 10 ? 112 : baseHeight(x, z);
            var plans = new LakePlanner(world, P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));

            for (int z = 0; z < 32; z++)
            {
                Assert.False(plans[z * 32 + 10].Flood); // the river itself is never touched
                for (int x = 11; x <= 10 + ColumnExclusions.RiverLakeReach; x++)
                {
                    var p = plans[z * 32 + x];
                    Assert.True(p.Flood, $"column {x},{z} next to the river should flood");
                    Assert.True(p.RiverLake);
                    Assert.Equal(115, p.Column.Level);
                }
                Assert.False(plans[z * 32 + 11 + ColumnExclusions.RiverLakeReach].Flood);
                // deeper toward the river, 1 deep at the far shore, no underwater steps
                Assert.Equal(1, Depth(plans[z * 32 + 10 + ColumnExclusions.RiverLakeReach]));
                for (int x = 11; x < 10 + ColumnExclusions.RiverLakeReach; x++)
                    Assert.True(Math.Abs(plans[z * 32 + x].Column.Bed - plans[z * 32 + x + 1].Column.Bed) <= 1);
                Assert.True(Depth(plans[z * 32 + 11]) >= 3);
            }
        }

        [Fact]
        public void NearestRiverLevel_PicksTheClosestColumnThenTheLowest()
        {
            var ex = new ColumnExclusions();
            int side = 32 + 2 * ColumnExclusions.WideRadius;
            ex.WaterTopWide = new int[side * side];
            Array.Fill(ex.WaterTopWide, -1);
            void Set(int lx, int lz, int v) => ex.WaterTopWide[(lz + ColumnExclusions.WideRadius) * side + (lx + ColumnExclusions.WideRadius)] = v;
            Set(10, 12, 120); // distance 2
            Set(11, 10, 118); // distance 1
            Assert.Equal(118, ex.NearestRiverLevelAt(10, 10, 9));
            Set(9, 10, 116);  // also distance 1, lower
            Assert.Equal(116, ex.NearestRiverLevelAt(10, 10, 9));
            Assert.Equal(-1, ex.NearestRiverLevelAt(-5, -5, 3));
        }

        // ------------------------------------------------------------------ lake shapes

        [Fact]
        public void Shapes_StayInsideTheDisc_AndVary()
        {
            var fractions = new List<double>();
            for (int c = 0; c < 24; c++)
            {
                var f = FeatureCells.At(12345, c * 144, 0, 144, 110, 12, 16, 48);
                Assert.InRange(f.Aspect, 0.45, 1.0);
                Assert.InRange(f.Wobble, 0.2, 0.6);
                int inside = 0, disc = 0;
                for (int dz = -50; dz <= 50; dz++)
                    for (int dx = -50; dx <= 50; dx++)
                    {
                        double px = f.CenterX + dx, pz = f.CenterZ + dz;
                        WaterRules.ShapeAt(f, px, pz, out double rho, out double limit);
                        bool inShape = rho < limit;
                        bool inDisc = dx * dx + dz * dz < f.Radius * f.Radius;
                        if (inShape) Assert.True(Math.Sqrt(dx * dx + dz * dz) < f.Radius + 1e-9, "shape leaves its disc");
                        if (inDisc) disc++;
                        if (inShape && inDisc) inside++;
                    }
                fractions.Add(inside / (double)disc);
            }
            fractions.Sort();
            Assert.True(fractions[^1] - fractions[0] > 0.2, "lake shapes should differ noticeably");
            Assert.True(fractions[^1] < 0.95, "no lake should be a full disc");
        }

        [Fact]
        public void PlainFeature_IsStillTheDisc()
        {
            var f = new FeatureCell(0, 0, 0, 0, 10, 116);
            var hit = WaterRules.BestFeature(new[] { f }, 3, 0);
            Assert.True(hit.Found);
            Assert.Equal(10 - 3.5355, hit.DistToEdge, 3);
            Assert.False(WaterRules.BestFeature(new[] { f }, 10, 0).Found);
        }

        [Theory]
        [InlineData(987654321L)]
        [InlineData(5L)]
        [InlineData(-42L)]
        [InlineData(1234567890123L)]
        public void ShapeCoverage_DefaultLakeChanceIsReachable(long seed)
        {
            double factor = WaterRules.ShapeAreaFactor(seed, 144, 110, 12, 16, 48);
            Assert.InRange(factor, 0.3, 0.9);
            double coverage = WaterRules.DiscCoverage(144, 16, 48) * factor;
            // Flight test 6: the default (0.12) is above the 3R grid, so the grid densifies; the densest grid (2.25R) reaches it.
            double dense = WaterRules.DiscCoverage(108, 16, 48) * WaterRules.ShapeAreaFactor(seed, 108, 110, 12, 16, 48);
            Assert.True(dense > new TerrainWatersConfig().LakeChance, $"densest coverage {dense} must exceed the default LakeChance");
            Assert.InRange(coverage, 0.07, 0.12); // the README's "roughly 0.08 to 0.1"
        }

        [Fact]
        public void ValueNoise_InRangeAndSmooth()
        {
            for (int i = 0; i < 2000; i++)
            {
                double x = i * 0.37 - 300, z = i * 0.91 + 17;
                double v = WaterRules.ValueNoise(42, x, z, 1);
                Assert.InRange(v, 0.0, 1.0);
                Assert.True(Math.Abs(v - WaterRules.ValueNoise(42, x + 0.001, z, 1)) < 0.01);
            }
        }

        // ------------------------------------------------------------------ swamp walls

        [Fact]
        public void PlacedWater_RoundTrip_FirstWins()
        {
            var flags = new bool[1024];
            flags[0] = flags[33] = flags[1023] = true;
            var back = PlacedWater.Decode(PlacedWater.Encode(flags));
            Assert.Equal(flags, back);
            var store = new PlacedWater(2);
            Assert.True(store.Remember(1, 2, flags));
            Assert.False(store.Remember(1, 2, new bool[1024]));
            Assert.True(store.Get(1, 2)[33]);
            Assert.Null(PlacedWater.Decode(new byte[5]));
        }

        [Fact]
        public void SwampCutAndFade_KeepTheAddedSlopeGentle()
        {
            Assert.Equal(2, SwampPass.SwampCut(5));   // flight test 6 (was 3: a 3-block wall)
            Assert.Equal(0, SwampPass.SwampCut(0));
            Assert.Equal(8, SwampPass.FadeOf(8, 5));
            Assert.Equal(16, SwampPass.FadeOf(40, 5));
            for (int cut = 1; cut <= 32; cut++)
            {
                int sc = SwampPass.SwampCut(cut), fade = SwampPass.FadeOf(0, cut);
                if (fade < 16) Assert.True(1.5 * sc / fade <= 0.5 + 1e-9, $"MaxCut {cut}: steepest added slope too high");
            }
        }

        [Fact]
        public void RiverLakeChance_DefaultAndClamp()
        {
            Assert.Equal(0, new TerrainWatersConfig().RiverLakeChance); // flight test 11: flow-through lakes removed
            Assert.Equal(0, new TerrainWatersConfig { RiverLakeChance = 0.5 }.Resolve(new WorldScale(256, 110, 1.0)).RiverLakeChance);
            // Audit: /riverfeatures set RiverLakeChance is refused (it did nothing).
            Assert.False(ConfigEdit.TrySet(new TerrainWatersConfig(), "riverlakechance", "3", out var res, out _));
            Assert.Null(res);
        }
    }
}
