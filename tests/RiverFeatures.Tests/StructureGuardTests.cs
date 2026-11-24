using System.Collections.Generic;
using System.Linq;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Flight test 2.5: structures (trader camps, BetterRuins ruins and story structures) and TerraWaters.</summary>
    public class StructureGuardTests
    {
        [Fact]
        public void OwnCodes_AreRecognised()
        {
            Assert.True(StructureGuard.IsOwn(StructureGuard.LakeCode));
            Assert.True(StructureGuard.IsOwn(StructureGuard.CutCode));
            Assert.False(StructureGuard.IsOwn("game:trader-forest"));
            Assert.False(StructureGuard.IsOwn("betterruins:ruin"));
            Assert.False(StructureGuard.IsOwn(null));
        }

        [Fact]
        public void Tiles_TightBoxesPerTile()
        {
            var flags = new bool[1024];
            var low = new int[1024];
            var high = new int[1024];
            void Set(int x, int z, int lo, int hi) { flags[z * 32 + x] = true; low[z * 32 + x] = lo; high[z * 32 + x] = hi; }
            Set(2, 3, 110, 117); Set(5, 6, 112, 118);   // tile (0,0)
            Set(9, 1, 115, 116);                        // tile (8,0)
            Set(31, 31, 100, 101);                      // tile (24,24)
            var boxes = StructureGuard.Tiles(flags, low, high);
            Assert.Equal(3, boxes.Count);
            Assert.Contains(new StructureGuard.Box(2, 3, 5, 6, 110, 118), boxes);
            Assert.Contains(new StructureGuard.Box(9, 1, 9, 1, 115, 116), boxes);
            Assert.Contains(new StructureGuard.Box(31, 31, 31, 31, 100, 101), boxes);
            Assert.Empty(StructureGuard.Tiles(new bool[1024], low, high));
        }

        [Fact]
        public void RegionsAround_CoversNeighbourRegionsAtBorders()
        {
            Assert.Single(StructureGuard.RegionsAround(5, 5, 16));                 // inside one region
            var corner = StructureGuard.RegionsAround(0, 0, 16);                    // corner of a region: 4 regions
            Assert.Equal(4, corner.Count);
            Assert.Contains((-1, -1), corner);
            Assert.Equal(2, StructureGuard.RegionsAround(15, 7, 16).Count);         // east edge
        }

        [Fact]
        public void StoryFootprint_ExcludesColumnsLikeAStructure()
        {
            // A story structure footprint passed as a structure rectangle excludes its own area plus RuinMargin, like any ruin.
            var grid = HeightGrid.FromSources(Enumerable.Repeat((ushort)120, 1024).ToArray(), 0, 0, null, null, null);
            var story = new List<ColumnExclusions.StructureRect> { new ColumnExclusions.StructureRect(40, 0, 60, 20) };
            var ex = ColumnExclusions.Compose(story, grid, null, 0, 0, 12, 110, 10);
            Assert.True(ex.NearStructure[0 * 32 + 31]);  // 9 blocks west of the footprint: inside the margin
            Assert.False(ex.NearStructure[0 * 32 + 20]); // 20 blocks away: free
            Assert.True(ex.NearStructureAt(70, 10, 12));
        }

        [Fact]
        public void PondKeepOut_BlocksLakesThroughTheQuery_AndIsCached()
        {
            int calls = 0;
            var ex = new ColumnExclusions { PondBlockedFn = (x, z) => { calls++; return x < 0; } };
            Assert.True(ex.PondBlockedAt(-5, 3));
            Assert.True(ex.PondBlockedAt(-5, 3));
            Assert.False(ex.PondBlockedAt(5, 3));
            Assert.Equal(2, calls);
            var failing = new ColumnExclusions { PondBlockedFn = (x, z) => throw new System.InvalidOperationException() };
            Assert.True(failing.PondBlockedAt(0, 0)); // a failing query keeps lakes out (safe side)
            Assert.False(new ColumnExclusions().PondBlockedAt(0, 0));
        }

        // Box of a 12x12 camp at (100..111, 128, 200..211), floor 128, 6 high.
        static bool Veto(System.Func<int, int, int, bool> liquid, string code = "trader-forest", int terrain = 128, int y1 = 128, int y2 = 134) =>
            StructureGuard.WaterVeto(100, y1, 200, 112, y2, 212, terrain, code, liquid);

        [Fact]
        public void WaterVeto_CampOnABasinFloorUnderWater()
        {
            // -649,144: floor 128, water up to 138 over the whole area.
            Assert.True(Veto((x, y, z) => y <= 138));
        }

        [Fact]
        public void WaterVeto_WaterOnlyBesideTheFootprint()
        {
            // Dry footprint, but water two blocks east of it standing above the floor: a hole next to water.
            Assert.True(Veto((x, y, z) => x == 113 && y == 130));
            // Water three blocks away (outside the ring) or only at floor level: allowed.
            Assert.False(Veto((x, y, z) => x == 115 && y == 130));
            // Flight test 17: water at floor level on ONE side (a shore) is allowed; all around (an island) is not.
            Assert.False(Veto((x, y, z) => y <= 128 && x >= 115));
            Assert.True(Veto((x, y, z) => y <= 128 && (x < 100 || x > 111 || z < 200 || z > 211)));
        }

        [Fact]
        public void WaterVeto_IslandsAndHalfSubmergedFootprints()
        {
            // -1500,144,11749: a ruin on a small island, water one block below its floor all around.
            Assert.True(Veto((x, y, z) => y == 127 && (x < 100 || x > 111 || z < 200 || z > 211)));
            // A third of the footprint is water at floor level: half-submerged.
            Assert.True(Veto((x, y, z) => y == 128 && x >= 108));
            // Water at floor level beside one edge only, outside the footprint: a riverbank, allowed.
            Assert.False(Veto((x, y, z) => y == 127 && x >= 113));
        }

        [Fact]
        public void WaterVeto_ExemptionsAndDryGround()
        {
            Assert.False(Veto((x, y, z) => false));                                       // dry: place
            Assert.False(Veto((x, y, z) => y <= 138, code: "trader-shallow"));            // meant for shallow water
            Assert.False(Veto((x, y, z) => y <= 138, terrain: 150, y1: 120, y2: 130));    // underground: box ends below the surface
            Assert.False(Veto(null));
        }

        [Fact]
        public void WaterVeto_SamplesTheLastRowAndColumn()
        {
            // Water only in the far corner of the ring (x = 113, z = 213) must still be found.
            Assert.True(Veto((x, y, z) => x == 113 && z == 213 && y == 129));
        }

        [Fact]
        public void Lake_DoesNotFloodInsidePondKeepOut()
        {
            var feats = new List<FeatureCell> { new FeatureCell(0, 0, 16, 16, 14, 116) };
            var world = new LakeWorld
            {
                Height = (x, z) => 116,
                WaterTop = (x, z) => -1,
                RiverLevel = (x, z) => -1,
                Feature = (x, z) => WaterRules.BestFeature(feats, x, z),
                NearStructure = (x, z) => x >= 16, // e.g. the story structure's pond keep-out covers the east half
                NearOcean = (x, z) => false,
            };
            var plans = new LakePlanner(world, new LakeParams(110, 10, 3, 5, 12, 1.0, 6, 8), 22).PlanWithShore(5);
            for (int z = 0; z < 32; z++)
                for (int x = 16; x < 32; x++)
                    Assert.False(plans[z * 32 + x].Flood);
            Assert.Contains(plans, p => p.Flood); // the west half still floods
        }
    }
}
