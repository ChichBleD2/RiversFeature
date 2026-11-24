using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 7: varied oxbow shapes, dry oxbows, dry rivers, the dry-channel depth cap.</summary>
    public class DryTests
    {
        static List<NetNode> Meander(int n = 40)
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
        public void Oxbows_ComeInSeveralShapes_AndSomeAreDry()
        {
            var net = Meander(); var h = RiverNet.Classify(net);
            var o = new OxbowOptions { Chance = 1, MinBendDegrees = 12, MinRiverSize = 10, MaxLevel = 2, DryShare = 0.3 };
            var ox = RiverNet.OxbowsAt(net, h, o, (i, s) => RiverNet.Hash01(11, i, s, 7));
            Assert.True(ox.Count >= 10, "oxbows: " + ox.Count);
            // Shape spread: the angle the chain covers around its own centroid differs between oxbows.
            var spans = ox.Select(x =>
            {
                var c = new P2(x.Points.Average(p => p.X), x.Points.Average(p => p.Z));
                var a0 = Math.Atan2(x.Points[0].Z - c.Z, x.Points[0].X - c.X);
                var a1 = Math.Atan2(x.Points[^1].Z - c.Z, x.Points[^1].X - c.X);
                return Math.Round(Math.Abs(Math.IEEERemainder(a1 - a0, 2 * Math.PI)), 1);
            }).Distinct().Count();
            Assert.True(spans >= 4, "shapes too uniform");
            Assert.Contains(ox, x => x.Dry);
            Assert.Contains(ox, x => !x.Dry);
            Assert.True(ox.Select(x => x.Points.Count).Distinct().Count() >= 2);
        }

        [Fact]
        public void DryRivers_FollowDryClimate_AndWholeBranchesDryOut()
        {
            // Stem 0..3; a tributary 4..5 joins node 1.
            var net = new List<NetNode>();
            for (int i = 0; i < 4; i++) net.Add(new NetNode { Start = new P2(0, i * 250), End = new P2(0, (i + 1) * 250), Parent = i - 1, StartSize = 40, EndSize = 38 });
            net.Add(new NetNode { Start = new P2(0, 500), End = new P2(200, 650), Parent = 1, StartSize = 14, EndSize = 14 });
            net.Add(new NetNode { Start = new P2(200, 650), End = new P2(400, 800), Parent = 4, StartSize = 14, EndSize = 14 });
            var h = RiverNet.Classify(net);

            var wet = RiverNet.DryRivers(net, h, 1, 0.4, i => 0.8, i => 0.0);
            Assert.DoesNotContain(true, wet);                                   // wet climate: never dry
            var desert = RiverNet.DryRivers(net, h, 1, 0.4, i => 0.0, i => 0.5);
            Assert.All(desert, d => Assert.True(d));                             // desert: the whole river is dry
            // Only the tributary lies in the desert: it dries from the confluence up, the stem keeps its water.
            var trib = RiverNet.DryRivers(net, h, 1, 0.4, i => i >= 4 ? 0.0 : 0.8, i => 0.5);
            Assert.Equal(new[] { false, false, false, false, true, true }, trib);
            Assert.DoesNotContain(true, RiverNet.DryRivers(net, h, 0, 0.4, i => 0.0, i => 0.0));
        }

        [Fact]
        public void DryCap_KeepsTheFloorAboveTheSea_AndFadesOutTowardADryMouth()
        {
            DryChannels.Cap = 5.0 / 140;   // heightBoost 8 - floor 3, 140 blocks above the sea
            var plain = new DryChannels.Info();
            Assert.Equal(DryChannels.Cap, DryChannels.CapAt(plain, new Vector2d(0, 0)));
            var mouth = new DryChannels.Info { FadeDownstream = true, Start = new Vector2d(0, 0), End = new Vector2d(0, 250) };
            Assert.Equal(0, DryChannels.CapAt(mouth, new Vector2d(0, 0)), 9);            // runs out into nowhere at the mouth end
            Assert.Equal(DryChannels.Cap, DryChannels.CapAt(mouth, new Vector2d(0, 200)), 9);
            Assert.InRange(DryChannels.CapAt(mouth, new Vector2d(0, 75)), 0.0001, DryChannels.Cap - 0.0001);
            // Floor: carving starts above sea + heightBoost - cap x 140 = sea + 3, so no water.
            Assert.Equal(3, 8 - DryChannels.Cap * 140, 9);
        }

        [Fact]
        public void DryPatch_BindsOnRealRivers()
        {
            var asm = RiversBindingTests.LoadRiversForTests();
            if (asm == null) return;
            var a = RiversAdapter.TryBind(asm);
            Assert.True(a.BindDry(asm));
            DryChannels.Bind(a.SegNode);
            typeof(DryChannels.Acc<>).MakeGenericType(a.TSample).GetMethod("Init").Invoke(null, new object[] { a.SampleBank });
            var h = new HarmonyLib.Harmony("riverhierarchy.test.dry");
            try
            {
                h.Patch(a.SampleSelf, postfix: new HarmonyLib.HarmonyMethod(typeof(DryChannels).GetMethod(nameof(DryChannels.SelfPostfix)).MakeGenericMethod(a.TSample)));
                h.Patch(a.SampleConnector, postfix: new HarmonyLib.HarmonyMethod(typeof(DryChannels).GetMethod(nameof(DryChannels.ConnectorPostfix)).MakeGenericMethod(a.TSample)));
            }
            finally { h.UnpatchAll("riverhierarchy.test.dry"); }
        }

        [Fact]
        public void WorldSetting_DryRivers()
        {
            var c = new HierarchyConfig();
            c.ApplyWorldSettings(k => k == "rhDryRiverChance" ? "0.25" : null);
            Assert.Equal(0.25, c.DryRiverChance);
        }
    }
}
