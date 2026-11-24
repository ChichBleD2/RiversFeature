using System;
using System.Collections.Generic;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Second flight test (2026-11-22, 11:20..11:26): lake ring shelves and stepped shores, swamp pits and steps.</summary>
    public class FlightTest2Tests
    {
        static readonly LakeParams P = new LakeParams(110, 10, 3, 5, 12, 1.0, 6, 8);

        static LakeWorld World(Func<int, int, int> height, List<FeatureCell> feats, bool shave = false) => new LakeWorld
        {
            Height = height,
            WaterTop = (x, z) => -1,
            RiverLevel = (x, z) => -1,
            Feature = (x, z) => WaterRules.BestFeature(feats, x, z),
            NearStructure = (x, z) => false,
            NearOcean = (x, z) => false,
            ShaveAllowed = shave ? (x, z) => true : null,
        };

        static int MaxOrthStep(LakePlan[] plans, Func<int, int, bool> where = null)
        {
            int worst = 0;
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 31; x++)
                {
                    foreach (var (ax, az, bx, bz) in new[] { (x, z, x + 1, z), (z, x, z, x + 1) })
                    {
                        var a = plans[az * 32 + ax]; var b = plans[bz * 32 + bx];
                        if (!a.Flood || !b.Flood) continue;
                        if (where != null && !where(ax, az) && !where(bx, bz)) continue;
                        worst = Math.Max(worst, Math.Abs(a.Column.Bed - b.Column.Bed));
                    }
                }
            return worst;
        }

        [Fact]
        public void TerracedGround_NoShelfUnderWater()
        {
            // Two terraces inside one lake: 116 (the level) west of x = 16, 117 east of it.
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16, 16, 14, 116) };
            Func<int, int, int> h = (x, z) => x < 16 ? 116 : 117;
            var old = new LakePlanner(World(h, feats), P, 22).DecideAll();
            var now = new LakePlanner(World(h, feats), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            Assert.True(old[16 * 32 + 15].Flood && old[16 * 32 + 16].Flood);
            Assert.NotEqual(old[16 * 32 + 15].Column.Bed, old[16 * 32 + 16].Column.Bed); // the old shelf along the terrace edge
            Assert.Equal(now[16 * 32 + 15].Column.Bed, now[16 * 32 + 16].Column.Bed);      // gone: depth is measured from the level
            Assert.True(MaxOrthStep(now) <= 1);
        }

        [Fact]
        public void NaturalHumpInDeepBasin_IsSmoothedIntoASlope()
        {
            // A closed natural basin: deep ring (108) around a hump (114), rim 116 = level. The old planner dug the hump to
            // level - 5 = 111 next to the 108 ring: a 3-block ring-shaped shelf (screenshot -475,-858).
            var p = new LakeParams(104, 10, 3, 5, 12, 4.0, 32, 8);
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16.5, 16.5, 20, 116) };
            Func<int, int, int> h = (x, z) =>
            {
                double r = Math.Sqrt((x + 0.5 - 16.5) * (x + 0.5 - 16.5) + (z + 0.5 - 16.5) * (z + 0.5 - 16.5));
                return r < 4 ? 114 : r < 8 ? 108 : 116;
            };
            Func<int, int, bool> hump = (x, z) => h(x, z) == 114;
            var old = new LakePlanner(World(h, feats), p, 22).DecideAll();
            var now = new LakePlanner(World(h, feats), p, 22).PlanWithShore(WaterRules.ShoreRadius(p.MaxCut));
            Assert.True(now[16 * 32 + 16].Flood);
            Assert.True(MaxOrthStep(old, hump) >= 3, "the old planner left the shelf");
            Assert.True(MaxOrthStep(now, hump) <= 1, "the hump must slope into the ring");
            for (int i = 0; i < 1024; i++)
                if (now[i].Flood) Assert.True(now[i].Column.Bed >= h(i % 32, i / 32) - p.MaxCut);
        }

        [Fact]
        public void ShoreShaving_TurnsABankStepIntoASlope()
        {
            // Flat ground at the level (116) open to the west (not a closed hollow, so it floods at the feature level), a
            // 3-block higher bank (119) from x = 25 east.
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16.5, 16.5, 30, 116) };
            Func<int, int, int> h = (x, z) => x < 25 ? 116 : 119;
            var plans = new LakePlanner(World(h, feats, shave: true), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            // Along the row z = 16 east of the lake: water, then 116, 117, 118, then the untouched 119.
            int firstDry = -1;
            for (int x = 17; x < 32; x++) if (!plans[16 * 32 + x].Flood) { firstDry = x; break; }
            Assert.True(firstDry > 0);
            int Top(int x) { var pl = plans[16 * 32 + x]; return pl.ShaveTo >= 0 ? pl.ShaveTo : h(x, 16); }
            Assert.Equal(116, Top(firstDry));
            Assert.Equal(117, Top(firstDry + 1));
            Assert.Equal(118, Top(firstDry + 2));
            Assert.Equal(119, Top(firstDry + 3));
            // Every water column stays walled by ground at or above its level.
            for (int i = 0; i < 1024; i++)
                if (plans[i].ShaveTo >= 0) Assert.True(plans[i].ShaveTo >= 116);
        }

        [Fact]
        public void ShavedSteps_WanderAlongAStraightShore_AndNeverCutDeeper()
        {
            // Flight test 15 (-1210,5268): a straight bank shaved into three steps exactly parallel to the shore looked dug.
            // With the shave noise the profile varies along the shore, and no column is cut below the old staircase.
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16.5, 16.5, 30, 116) };
            Func<int, int, int> h = (x, z) => x < 25 ? 116 : 119;
            var exact = new LakePlanner(World(h, feats, shave: true), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            var profiles = new HashSet<string>();
            int shaved = 0;
            foreach (long seed in new long[] { 1, 2, 3 })
            {
                var world = World(h, feats, shave: true);
                world.ShaveNoise = (x, z) => LakePass.ShaveNoiseAt(seed, x, z);
                var plans = new LakePlanner(world, P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
                for (int z = 4; z < 28; z++)
                {
                    var row = new System.Text.StringBuilder();
                    for (int x = 20; x < 32; x++)
                    {
                        var pl = plans[z * 32 + x]; var ex = exact[z * 32 + x];
                        Assert.Equal(ex.Flood, pl.Flood);                       // which columns flood is unchanged
                        int top = pl.ShaveTo >= 0 ? pl.ShaveTo : h(x, z);
                        int oldTop = ex.ShaveTo >= 0 ? ex.ShaveTo : h(x, z);
                        if (!pl.Flood) Assert.True(top >= oldTop, $"seed {seed} ({x},{z}): {top} below the old staircase {oldTop}");
                        if (pl.ShaveTo >= 0) { Assert.True(pl.ShaveTo >= 116, "every water column keeps its wall"); shaved++; }
                        row.Append(top).Append(',');
                    }
                    profiles.Add(row.ToString());
                }
            }
            Assert.True(shaved > 0, "banks are still eased");
            Assert.True(profiles.Count >= 4, "the shaved steps must not be the same line all along the shore: " + profiles.Count);
        }

        [Fact]
        public void NoShaving_WhenNotAllowed()
        {
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16.5, 16.5, 30, 116) };
            Func<int, int, int> h = (x, z) => Math.Abs(x - 16) < 9 && Math.Abs(z - 16) < 9 ? 116 : 119;
            var plans = new LakePlanner(World(h, feats, shave: false), P, 22).PlanWithShore(WaterRules.ShoreRadius(P.MaxCut));
            foreach (var pl in plans) Assert.Equal(-1, pl.ShaveTo);
        }

        [Fact]
        public void ShoreSlope_NeverSteeperThanOneBlockPerBlock()
        {
            Assert.Equal(0.45, WaterRules.ShoreSlope(0), 9);
            Assert.Equal(0.6, WaterRules.ShoreSlope(1), 9);
            for (int mc = 1; mc <= 8; mc++)
            {
                int r = WaterRules.ShoreRadius(mc);
                Assert.True(WaterRules.ShoreDepth((r + 1) * WaterRules.ShoreSlope(0)) >= mc, $"MaxCut {mc}: ramp must reach the bottom inside the radius");
            }
        }

        // ------------------------------------------------------------------ swamps

        [Fact]
        public void SwampLowest_FadesWithWeight()
        {
            Assert.Equal(120, WaterRules.SwampLowest(120, 111, 3, 0.0));
            Assert.Equal(119, WaterRules.SwampLowest(120, 111, 3, 0.3));
            Assert.Equal(117, WaterRules.SwampLowest(120, 111, 3, 1.0));
            Assert.Equal(111, WaterRules.SwampLowest(112, 111, 3, 1.0));
            Assert.Equal(110, WaterRules.SwampLowest(110, 111, 3, 1.0));
            for (double w = 0; w < 1; w += 0.05) Assert.True(WaterRules.SwampLowest(130, 111, 3, w) >= WaterRules.SwampLowest(130, 111, 3, w + 0.05));
        }

        [Fact]
        public void SwampSmoothing_TurnsATerraceRiserIntoSteps()
        {
            // A 3-block riser (120 | 117) in a row of planned surfaces; smoothing may lower the upper side by up to 3.
            int side = 9;
            var grid = new int[side * side];
            for (int z = 0; z < side; z++) for (int x = 0; x < side; x++) grid[z * side + x] = x < 4 ? 117 : 120;
            var row = new int[side];
            for (int x = 0; x < side; x++) row[x] = x < 4 ? 117 : WaterRules.SmoothedSurface(grid, side, x, 4, 3, 117);
            Assert.Equal(new[] { 117, 117, 117, 117, 118, 119, 120, 120, 120 }, row);
            // never below the faded lowest
            Assert.Equal(119, WaterRules.SmoothedSurface(grid, side, 4, 4, 3, 119));
        }

        [Fact]
        public void Pocket_NeedsLowBanks()
        {
            Assert.True(WaterRules.PocketNeighbourOk(112, new SwampNeighbour(113, 113, false, 113)));
            Assert.False(WaterRules.PocketNeighbourOk(112, new SwampNeighbour(115, 114, false, 114))); // a 2-block hole: no pocket
            Assert.False(WaterRules.PocketNeighbourOk(112, new SwampNeighbour(113, 111, false, 112)));  // may end below the water
        }
    }
}
