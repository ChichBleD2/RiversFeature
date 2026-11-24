using System.Collections.Generic;
using OpenTK.Mathematics;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Flight test 10: the coast along a river, and the mouth fade on the hook Rivers' terrain really calls.</summary>
    public class CoastFadeTests
    {
        static List<NetNode> River()
        {
            var net = new List<NetNode>();
            var at = new P2(0, 0);
            for (int i = 0; i < 4; i++) { var end = at + new P2(0, 250); net.Add(new NetNode { Start = at, End = end, Parent = i - 1, StartSize = 40, EndSize = 38 }); at = end; }
            return net;
        }

        [Fact]
        public void Coast_IsWhereTheOceanEnds_AndTheSeaLiesDownstream()
        {
            var net = River(); var h = RiverNet.Classify(net);
            var c = RiverNet.FindCoast(net, h, 0, q => q.Z < 300 ? 255 : 0, 90);
            Assert.NotNull(c);
            Assert.InRange(c.Value.Coast.Z, 300, 308);
            Assert.True(c.Value.SeaDir.Z < -0.99);
            Assert.Null(RiverNet.FindCoast(net, h, 0, q => 0, 90));      // mouth already on land
            Assert.Null(RiverNet.FindCoast(net, h, 0, q => 255, 90));    // never reaches land
        }

        [Fact]
        public void MouthFade_FullDepthAtTheCoast_NoDiggingFarOut()
        {
            // Coast at z 300, sea toward -z, fade 40: Start 40 blocks out at sea, End 40/0.6 blocks back toward land.
            var info = new DryChannels.Info { Cap = 8.0 / 182, RelaxUpstream = true, Start = new Vector2d(0, 260), End = new Vector2d(0, 260 + 40 / 0.6) };
            Assert.Equal(8.0 / 182, DryChannels.CapAt(info, new Vector2d(0, 200)), 9);  // out at sea: the cut ends at sea level
            Assert.Equal(1.0, DryChannels.CapAt(info, new Vector2d(0, 300)), 9);        // at the coast: Rivers' full depth
            Assert.InRange(DryChannels.CapAt(info, new Vector2d(0, 280)), 8.0 / 182, 1.0);
        }
    }
}
