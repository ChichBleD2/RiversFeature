using System;
using System.Collections.Generic;
using System.Linq;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    public class LakeShapeTests
    {
        static Func<int, double> Rnd(int seed) => salt => RiverNet.Hash01(seed, 17, 23, salt);
        static readonly P2 Start = new P2(1000, 2000), Dir = new P2(1, 0);

        [Fact]
        public void Procedural_lakes_vary_and_every_kind_occurs()
        {
            var counts = new Dictionary<LakeShape, int>();
            var signatures = new HashSet<string>();
            for (int seed = 0; seed < 1000; seed++)
            {
                var plan = LakeShapes.Procedural(Start, Dir, 7, 60, Rnd(seed));
                var k = LakeShapes.Classify(plan);
                counts[k] = counts.GetValueOrDefault(k) + 1;
                signatures.Add(string.Join(";", plan.Select(p => (int)p.B.X + "," + (int)p.B.Z + "," + (int)p.SizeB)));
            }
            string all = string.Join(" ", counts.Select(kv => kv.Key + "=" + kv.Value));
            foreach (LakeShape s in Enum.GetValues(typeof(LakeShape))) Assert.True(counts.GetValueOrDefault(s) > 30, s + " too rare: " + all);
            Assert.True(counts[LakeShape.Teardrop] < 250, "teardrops stay a minority");
            Assert.True(signatures.Count > 900, "shapes are not a few templates: " + signatures.Count);
        }

        [Fact]
        public void Pieces_are_well_formed()
        {
            for (int seed = 0; seed < 300; seed++)
            {
                var pieces = LakeShapes.Procedural(Start, Dir, 7, 60, Rnd(seed));
                Assert.Equal(Start, pieces[0].A);
                Assert.Equal(-1, pieces[0].ChainTo);
                for (int k = 0; k < pieces.Count; k++)
                {
                    var p = pieces[k];
                    Assert.True(p.ChainTo < k);
                    if (p.ChainTo >= 0) Assert.Equal(pieces[p.ChainTo].B, p.A);
                    Assert.InRange(p.SizeA, 1, 80);
                    Assert.InRange(p.SizeB, 1, 80);
                    Assert.True((p.B - p.A).Length >= 19.9);
                    Assert.True((p.B - Start).Length < LakeShapes.MaxReach, "stays inside Rivers' river radius margin");
                }
            }
        }

        [Fact]
        public void Fit_keeps_land_between_the_lake_and_a_nearby_river()
        {
            // A wide river running parallel to the lake's direction, 60 blocks to the side.
            var river = new List<LakeShapes.Obstacle>();
            for (int x = 800; x < 1500; x += 40) river.Add(new LakeShapes.Obstacle(new P2(x, 2060), new P2(x + 40, 2060), 20, 20));
            int clipped = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var raw = LakeShapes.Procedural(Start, Dir, 7, 60, Rnd(seed));
                if (LakeShapes.Clearance(raw, river, Start, 30) < 14) clipped++;
                var (plan, how) = LakeShapes.Fit(Start, Dir, 7, 60, Rnd(seed), river, 14, 30);
                if (how != LakeShapes.FitResult.Pond) Assert.True(LakeShapes.Clearance(plan, river, Start, 30) >= 14, "seed " + seed + " " + how);
            }
            Assert.True(clipped > 20, "the test river is close enough to matter");
        }

        [Fact]
        public void Fit_without_obstacles_is_the_procedural_plan()
        {
            var (plan, how) = LakeShapes.Fit(Start, Dir, 7, 60, Rnd(5), new List<LakeShapes.Obstacle>(), 14, 30);
            Assert.Equal(LakeShapes.FitResult.Fits, how);
            Assert.Equal(LakeShapes.Procedural(Start, Dir, 7, 60, Rnd(5)), plan);
        }

        [Fact]
        public void Another_river_at_the_source_is_never_exempt()
        {
            // Flight test 14 (299,2212): a big river passing right by the source. Its channel is not the lake's own river,
            // so the join zone does not hide it: the lake is dropped rather than cut into it.
            var other = new List<LakeShapes.Obstacle>();
            for (int z = 1800; z < 2300; z += 40) other.Add(new LakeShapes.Obstacle(new P2(1030, z), new P2(1030, z + 40), 25, 25));
            var (plan, how) = LakeShapes.Fit(Start, Dir, 7, 60, Rnd(1), other, 32, 30);
            Assert.Equal(LakeShapes.FitResult.Dropped, how);
            Assert.Null(plan);
            // The same channel marked as the lake's own river would be exempt at the join.
            var own = other.ConvertAll(o => o with { Own = true });
            Assert.True(LakeShapes.Clearance(LakeShapes.Pond(Start, Dir, 60), own, Start, 30) > -1000);
        }

        [Fact]
        public void Fit_keeps_32_blocks_from_a_parallel_river()
        {
            // Flight test 14 (2989,3242): elongated lakes beside a river are fine, with a wide strip of land between.
            var river = new List<LakeShapes.Obstacle>();
            for (int x = 800; x < 1500; x += 40) river.Add(new LakeShapes.Obstacle(new P2(x, 2110), new P2(x + 40, 2110), 22, 22));
            for (int seed = 0; seed < 200; seed++)
            {
                var (plan, how) = LakeShapes.Fit(Start, Dir, 7, 60, Rnd(seed), river, 32, 30);
                if (how == LakeShapes.FitResult.Dropped) continue;
                Assert.True(LakeShapes.Clearance(plan, river, Start, 30) >= 32, "seed " + seed + " " + how);
            }
        }

        [Fact]
        public void Join_zone_is_exempt()
        {
            // The lake's own river ends right at the start: never an obstacle there.
            var own = new List<LakeShapes.Obstacle> { new LakeShapes.Obstacle(new P2(900, 2000), new P2(1000, 2000), 7, 7, Own: true) };
            var plan = new List<LakeShapes.Piece> { new LakeShapes.Piece(Start, Start + new P2(60, 0), 7, 40, -1) };
            Assert.True(LakeShapes.Clearance(plan, own, Start, 30) > 10);
        }
    }
}
