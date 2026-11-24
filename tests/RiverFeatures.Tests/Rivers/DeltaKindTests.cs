using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 9: three delta types and the shallow cap.</summary>
    public class DeltaKindTests
    {
        static List<NetNode> River()
        {
            var net = new List<NetNode>();
            var at = new P2(5000, 0);
            for (int i = 0; i < 6; i++)
            {
                var end = at + new P2(0, 250);
                net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 40, EndSize = 38 });
                at = end;
            }
            return net;
        }

        static double Ocean(P2 q) => q.Z < 300 ? 255 : 0;

        static List<Channel> Run(double kindRoll)
        {
            var net = River(); var h = RiverNet.Classify(net);
            var o = new DeltaOptions { Chance = 1, MinBranches = 3, MaxBranches = 3, MinLength = 200, MaxLength = 200, SmallShare = 0.35, ShallowShare = 0.3 };
            return RiverNet.CoastDelta(net, h, 0, 50, o, Ocean, 30, null, s => s == 4 ? kindRoll : 0.4);
        }

        [Fact]
        public void Small_IsShortWithTwoNarrowChannels()
        {
            var ch = Run(0.1);
            Assert.Equal(2, ch.Count);
            Assert.All(ch, c => Assert.Equal(DeltaKind.Small, c.Kind));
            Assert.All(ch, c => Assert.InRange(c.End.Z, 370, 395));   // apex 80 blocks inland
            Assert.All(ch, c => Assert.True(c.Size < 20));
        }

        [Fact]
        public void Shallow_IsLongWideAndSlow()
        {
            var ch = Run(0.5);
            Assert.InRange(ch.Count, 1, 8);   // flight test 16: 1..many fingers (was MaxBranches + 1)
            Assert.All(ch, c => Assert.Equal(DeltaKind.Shallow, c.Kind));
            Assert.All(ch, c => Assert.Equal(0.3f, c.Speed));
            Assert.All(ch, c => Assert.InRange(c.End.Z, 550, 570));   // apex 260 blocks inland
            Assert.All(ch, c => Assert.Equal(12f, c.Size));   // flight test 16b: half the old width (was 24)
        }

        [Fact]
        public void Carving_IsTheFullDelta()
        {
            var ch = Run(0.9);
            Assert.Equal(3, ch.Count);
            Assert.All(ch, c => Assert.Equal(DeltaKind.Carving, c.Kind));
            Assert.All(ch, c => Assert.Equal(1f, c.Speed));
        }

        [Fact]
        public void ShallowCap_HoldsDownstreamOfTheApex_AndRelaxesUpstream()
        {
            var info = new DryChannels.Info { Cap = 0.07, RelaxUpstream = true, Start = new Vector2d(0, 0), End = new Vector2d(0, 333) };
            Assert.Equal(0.07, DryChannels.CapAt(info, new Vector2d(0, -100)), 9);     // toward the sea: shallow
            Assert.Equal(1.0, DryChannels.CapAt(info, new Vector2d(0, 300)), 9);       // well upstream: no cap
            Assert.InRange(DryChannels.CapAt(info, new Vector2d(0, 100)), 0.07, 1.0);
        }
    }
}
