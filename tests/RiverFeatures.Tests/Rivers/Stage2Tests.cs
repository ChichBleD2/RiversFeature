using System;
using System.Collections.Generic;
using System.Linq;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Stage 2: delta traits and looks, climate bias, delta scale, oxbow placement, traces of the past, /rffind.</summary>
    public class Stage2Tests
    {
        static Func<int, double> Rnd(int seed) => salt => RiverNet.Hash01(seed, 31, 47, salt);
        static readonly ClimateSample Arid = new ClimateSample(0.05, 26), Wet = new ClimateSample(0.9, 18), Cold = new ClimateSample(0.5, -8);

        static Dictionary<DeltaLook, int> Looks(ClimateSample c, double influence, int n = 3000)
        {
            var d = new Dictionary<DeltaLook, int>();
            foreach (DeltaLook l in Enum.GetValues(typeof(DeltaLook))) d[l] = 0;
            for (int s = 0; s < n; s++) d[DeltaTraitRules.Roll(c, influence, 0.5, 0.6, 1.6, Rnd(s)).Look]++;
            return d;
        }

        [Fact]
        public void Climate_leans_delta_looks_softly()
        {
            var arid = Looks(Arid, 0.7); var wet = Looks(Wet, 0.7);
            int aridDry = arid[DeltaLook.Dry] + arid[DeltaLook.Intermittent], wetDry = wet[DeltaLook.Dry] + wet[DeltaLook.Intermittent];
            int aridLush = arid[DeltaLook.Marshy] + arid[DeltaLook.Overgrown], wetLush = wet[DeltaLook.Marshy] + wet[DeltaLook.Overgrown];
            Assert.True(aridDry > 3 * Math.Max(1, wetDry), $"dry land: dry/intermittent {aridDry} vs wet land {wetDry}");
            Assert.True(wetLush > 3 * Math.Max(1, aridLush), $"wet land: marshy/overgrown {wetLush} vs dry land {aridLush}");
            // Soft: across climates every look occurs, and a dry delta can still turn up on moderately wet land.
            foreach (DeltaLook l in Enum.GetValues(typeof(DeltaLook)))
                Assert.True(arid[l] + wet[l] + Looks(Cold, 0.7)[l] > 20, l + " too rare");
            Assert.True(Looks(new ClimateSample(0.5, 15), 0.7)[DeltaLook.Dry] > 0);
        }

        [Fact]
        public void Influence_zero_is_climate_blind_and_unknown_climate_is_neutral()
        {
            static void Same(DeltaTraits a, DeltaTraits b)
            {
                Assert.Equal(a.Scale, b.Scale, 9); Assert.Equal(a.Moisture, b.Moisture, 9); Assert.Equal(a.Vegetation, b.Vegetation, 9);
                Assert.Equal(a.Bank, b.Bank); Assert.Equal(a.Branching, b.Branching, 9);
            }
            for (int s = 0; s < 200; s++)
            {
                Same(DeltaTraitRules.Roll(Arid, 0, 0.5, 0.6, 1.6, Rnd(s)), DeltaTraitRules.Roll(Wet, 0, 0.5, 0.6, 1.6, Rnd(s)));
                Same(DeltaTraitRules.Roll(ClimateSample.Unknown, 1, 0.5, 0.6, 1.6, Rnd(s)), DeltaTraitRules.Roll(new ClimateSample(0.45, 12.5), 1, 0.5, 0.6, 1.6, Rnd(s)));
            }
        }

        [Fact]
        public void Cold_land_leans_to_gravel_and_rock_banks_warm_dry_land_to_sand()
        {
            int coldStone = 0, hotSand = 0;
            for (int s = 0; s < 2000; s++)
            {
                var c = DeltaTraitRules.Roll(Cold, 1, 0.5, 0.6, 1.6, Rnd(s)).Bank;
                if (c == DeltaBank.Gravel || c == DeltaBank.Rock) coldStone++;
                if (DeltaTraitRules.Roll(Arid, 1, 0.5, 0.6, 1.6, Rnd(s)).Bank == DeltaBank.Sand) hotSand++;
            }
            Assert.True(coldStone > 900, "cold: " + coldStone);
            Assert.True(hotSand > 1000, "hot and dry: " + hotSand);
        }

        [Fact]
        public void Delta_scale_stays_in_range_and_bigger_rivers_lean_bigger()
        {
            double small = 0, big = 0;
            for (int s = 0; s < 1000; s++)
            {
                var a = DeltaTraitRules.Roll(Wet, 0.7, 0.1, 0.6, 1.6, Rnd(s)); var b = DeltaTraitRules.Roll(Wet, 0.7, 1.0, 0.6, 1.6, Rnd(s));
                Assert.InRange(a.Scale, 0.6, 1.6); Assert.InRange(b.Scale, 0.6, 1.6);
                small += a.Scale; big += b.Scale;
            }
            Assert.True(big > small * 1.15);
        }

        [Fact]
        public void Dry_and_intermittent_deltas_dry_their_channels()
        {
            var dry = DeltaTraits.Classic with { Moisture = 0.1 };
            var mid = DeltaTraits.Classic with { Moisture = 0.3 };
            var wet = DeltaTraits.Classic with { Moisture = 0.8 };
            int dryN = 0, midN = 0, wetN = 0;
            for (int b = 0; b < 1000; b++)
            {
                double h = RiverNet.Hash01(5, b, 0, 1);
                if (DeltaTraitRules.ChannelDry(dry, h)) dryN++;
                if (DeltaTraitRules.ChannelDry(mid, h)) midN++;
                if (DeltaTraitRules.ChannelDry(wet, h)) wetN++;
            }
            Assert.Equal(1000, dryN); Assert.Equal(0, wetN);
            Assert.InRange(midN, 250, 750);   // intermittent: some dry, some shallow
        }

        // ---------------------------------------------------------------- delta shape

        static List<NetNode> CoastRiver()
        {
            var net = new List<NetNode>();
            var at = new P2(5000, 0);
            for (int i = 0; i < 6; i++) { var end = at + new P2(0, 250); net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 40, EndSize = 38 }); at = end; }
            return net;
        }

        [Fact]
        public void Delta_scale_one_is_the_old_delta_and_bigger_scale_reaches_further()
        {
            var net = CoastRiver(); var h = RiverNet.Classify(net);
            var o = new DeltaOptions { Chance = 1, MinBranches = 3, MaxBranches = 3, MinLength = 150, MaxLength = 150 };
            Func<P2, double> ocean = q => q.Z < 300 ? 255 : 0;
            var old = RiverNet.CoastDelta(net, h, 0, 50, o, ocean, 30, null, s => 0.4);
            var same = RiverNet.CoastDelta(net, h, 0, 50, o, ocean, 30, null, s => 0.4, 24, 1.0, 0.5);
            Assert.Equal(old, same);
            var big = RiverNet.CoastDelta(net, h, 0, 50, o, ocean, 30, null, s => 0.4, 24, 2.0, 0.9);
            var tiny = RiverNet.CoastDelta(net, h, 0, 50, o, ocean, 30, null, s => 0.4, 24, 0.5, 0.1);
            Assert.True(big[0].End.Z > old[0].End.Z + 100, "the apex of a big delta lies further inland");
            Assert.True(tiny[0].End.Z < old[0].End.Z - 50);
            Assert.True(big.Count > tiny.Count, $"branches {big.Count} vs {tiny.Count}");
            Assert.True(big[0].Size > tiny[0].Size);
        }

        // ---------------------------------------------------------------- oxbows

        static List<NetNode> Meander(int n = 30)
        {
            var net = new List<NetNode>();
            var at = new P2(1000, 1000); double ang = 90;
            for (int i = 0; i < n; i++)
            {
                ang += (i % 2 == 0 ? 25 : -25);
                var end = at + new P2(Math.Cos(ang * Math.PI / 180), Math.Sin(ang * Math.PI / 180)) * 250;
                net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 30, EndSize = 28 });
                at = end;
            }
            return net;
        }

        [Fact]
        public void Oxbows_try_another_spot_before_giving_up()
        {
            var net = Meander(); var h = RiverNet.Classify(net);
            var o = new OxbowOptions { Chance = 1, MinBendDegrees = 12, MinRiverSize = 10, MaxLevel = 2, MinRadius = 40, MaxRadius = 60 };
            Func<int, int, double> rnd = (i, s) => RiverNet.Hash01(11, i, s, 7);
            var plain = RiverNet.OxbowsAt(net, h, o, rnd);
            var again = RiverNet.OxbowsAt(net, h, o, rnd, null, out int r0);
            Assert.Equal(plain.Count, again.Count);
            for (int k = 0; k < plain.Count; k++) { Assert.Equal(plain[k].Node, again[k].Node); Assert.Equal(plain[k].Points, again[k].Points); }
            // "Steep" wherever the oxbow reaches far from the river: only the smaller, closer placements pass.
            bool Near(List<P2> chain, int node) => chain.All(p => RiverNet.DistToSegment(p, net[node].Start, net[node].End) < 30 + 8 + 2 * 60 * 0.62);
            var moved = RiverNet.OxbowsAt(net, h, o, rnd, Near, out int rejected);
            Assert.Contains(moved, x => x.Attempt > 0);
            Assert.All(moved, x => Assert.True(Near(x.Points, x.Node)));
            Assert.True(moved.Count > plain.Count(p => Near(p.Points, p.Node)), "moving finds spots the first placement missed");
            // Accept nothing: every rolled oxbow is counted as rejected, none placed.
            var none = RiverNet.OxbowsAt(net, h, o, rnd, (c, i) => false, out int all);
            Assert.Empty(none);
            Assert.True(all >= plain.Count);
        }

        // ---------------------------------------------------------------- traces of the past

        static List<NetNode> Stem(int n = 12)
        {
            var net = new List<NetNode>();
            var at = new P2(2000, 2000);
            for (int i = 0; i < n; i++)
            {
                double ang = 90 + (i % 3 - 1) * 8;
                var end = at + new P2(Math.Cos(ang * Math.PI / 180), Math.Sin(ang * Math.PI / 180)) * 200;
                net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 20, EndSize = 19 });
                at = end;
            }
            return net;
        }

        [Fact]
        public void Traces_follow_the_climate_and_keep_clear_of_the_river()
        {
            int aridDry = 0, aridWet = 0, wetDry = 0, wetWet = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                var net = Stem(); var h = RiverNet.Classify(net);
                var o = new TraceOptions { Chance = 1, Clearance = 24, ClimateInfluence = 1 };
                Func<int, int, double> rnd = (i, s) => RiverNet.Hash01(seed, i, s, 3);
                foreach (var (clim, isArid) in new[] { (Arid, true), (Wet, false) })
                {
                    var ts = Traces.Plan(net, h, o, i => clim, rnd, null, null);
                    foreach (var t in ts)
                    {
                        bool dryKind = t.Kind == TraceKind.Channel;
                        if (isArid) { if (dryKind) aridDry++; else aridWet++; } else { if (dryKind) wetDry++; else wetWet++; }
                        // Never inside the river: every point outside the channel (backwater join excepted).
                        for (int k = t.Kind == TraceKind.Backwater ? 2 : 0; k < t.Points.Count; k++)
                            foreach (var n in net)
                                Assert.True(RiverNet.DistToSegment(t.Points[k], n.Start, n.End) - n.StartSize - t.Size >= 4 - 1e-6, t.Kind + " cuts the river");
                    }
                }
            }
            Assert.True(aridDry > 2 * Math.Max(1, aridWet), $"dry land: dry channels {aridDry}, wet traces {aridWet}");
            Assert.True(wetWet > 2 * Math.Max(1, wetDry), $"wet land: wet traces {wetWet}, dry channels {wetDry}");
        }

        [Fact]
        public void Traces_keep_clear_of_features_already_placed_and_respect_accept()
        {
            var net = Stem(); var h = RiverNet.Classify(net);
            var o = new TraceOptions { Chance = 1, Clearance = 24, ClimateInfluence = 0 };
            Func<int, int, double> rnd = (i, s) => RiverNet.Hash01(9, i, s, 3);
            var free = Traces.Plan(net, h, o, i => ClimateSample.Unknown, rnd, null, null);
            Assert.NotEmpty(free);
            // A wide feature covering the floodplain on both sides of the river: nothing fits beside it.
            var block = new List<LakeShapes.Obstacle>();
            foreach (var n in net) block.Add(new LakeShapes.Obstacle(n.Start, n.End, 150, 150));
            Assert.Empty(Traces.Plan(net, h, o, i => ClimateSample.Unknown, rnd, block, null));
            Assert.Empty(Traces.Plan(net, h, o, i => ClimateSample.Unknown, rnd, null, t => false));
            // Traces never overlap each other.
            for (int a = 0; a < free.Count; a++)
                for (int b = a + 1; b < free.Count; b++)
                {
                    var obs = Traces.AsPieces(free[b]).Select(p => new LakeShapes.Obstacle(p.A, p.B, p.SizeA, p.SizeB)).ToList();
                    Assert.True(LakeShapes.Clearance(Traces.AsPieces(free[a]), obs, free[a].Points[0], 0) >= o.Clearance - 1e-6);
                }
        }

        // ---------------------------------------------------------------- delta banks

        [Fact]
        public void Delta_banks_fade_with_distance_and_stay_on_low_ground()
        {
            var t = new DeltaTraits(1.0, DeltaBank.Sand, 0.8, 0.5, 0.5);
            double reach = DeltaTraitRules.BankReach(t);
            Assert.Equal((DeltaSurface.Surface.None, DeltaSurface.Plant.None), DeltaSurface.Decide(t, reach + 1, 140, 138, 1, 0, 0));
            Assert.Equal((DeltaSurface.Surface.None, DeltaSurface.Plant.None), DeltaSurface.Decide(t, 2, 150, 138, 1, 0, 0)); // hillside
            Assert.Equal((DeltaSurface.Surface.None, DeltaSurface.Plant.None), DeltaSurface.Decide(t, -1, 139, 138, 1, 0, 0)); // in the channel
            int Count(double edge)
            {
                int c = 0;
                for (int k = 0; k < 2000; k++)
                    if (DeltaSurface.Decide(t, edge, 140, 138, RiverNet.Hash01(1, k, 0, 1), RiverNet.Hash01(1, k, 0, 2), 0.99).surface != DeltaSurface.Surface.None) c++;
                return c;
            }
            int near = Count(1), mid = Count(reach * 0.5), far = Count(reach * 0.9);
            Assert.True(near > mid && mid > far && far > 0, $"{near} > {mid} > {far}");
        }

        [Fact]
        public void Delta_looks_pick_their_materials_and_plants()
        {
            var marsh = new DeltaTraits(1, DeltaBank.Mud, 0.8, 0.4, 0.5);
            Assert.Equal(DeltaLook.Marshy, marsh.Look);
            var (s, p) = DeltaSurface.Decide(marsh, 1, 139, 138, 1, 0, 0);
            Assert.Equal(DeltaSurface.Surface.Mud, s); Assert.Equal(DeltaSurface.Plant.Reeds, p);
            var rocky = new DeltaTraits(1, DeltaBank.Rock, 0.8, 0.9, 0.5);
            Assert.Equal(DeltaLook.Rocky, rocky.Look);
            Assert.Equal(DeltaSurface.Surface.Rock, DeltaSurface.Decide(rocky, 1, 139, 138, 1, 0, 0).surface);
            Assert.Equal(DeltaSurface.Plant.None, DeltaSurface.Decide(rocky, 1, 139, 138, 1, 0, 0).plant); // nothing grows out of rock
            var dryMud = new DeltaTraits(1, DeltaBank.Mud, 0.1, 0.1, 0.5);
            Assert.Equal(DeltaLook.Dry, dryMud.Look);
            Assert.Equal(DeltaSurface.Surface.Sand, DeltaSurface.Decide(dryMud, 1, 139, 138, 1, 0, 0).surface); // dried out: no mud
            Assert.Equal(DeltaLook.Overgrown, new DeltaTraits(1, DeltaBank.Sand, 0.9, 0.8, 0.5).Look);
            Assert.Equal(DeltaLook.Sandy, new DeltaTraits(1, DeltaBank.Sand, 0.9, 0.2, 0.5).Look);
            Assert.Equal(DeltaLook.Intermittent, new DeltaTraits(1, DeltaBank.Sand, 0.3, 0.2, 0.5).Look);
        }

        // ---------------------------------------------------------------- /rffind

        [Fact]
        public void Rffind_matches_delta_shapes_and_looks()
        {
            Assert.True(FeatureLog.Matches("delta-shallow-marshy", "delta"));
            Assert.True(FeatureLog.Matches("delta-shallow-marshy", "delta-shallow"));
            Assert.True(FeatureLog.Matches("delta-shallow-marshy", "delta-marshy"));
            Assert.True(FeatureLog.Matches("delta-shallow-marshy", "delta-shallow-marshy"));
            Assert.False(FeatureLog.Matches("delta-shallow-marshy", "delta-sandy"));
            Assert.False(FeatureLog.Matches("delta-carving-dry", "delta-shallow"));
            Assert.True(FeatureLog.Matches("lake-source-oval", "lake"));
            Assert.True(FeatureLog.Matches("oxbow-dry", "oxbow"));
            Assert.False(FeatureLog.Matches("oxbow-dry", "dry-river"));
            Assert.True(FeatureLog.Matches("trace-backwater", "trace"));
            Assert.Equal("trace", FeatureLog.Normalize("traces"));
            Assert.Equal("delta-marshy", FeatureLog.Normalize("swampy"));
            Assert.Equal("delta", FeatureLog.Normalize(null));
            Assert.Equal("dry-river", FeatureLog.Normalize("Wadi "));
        }
    }
}
