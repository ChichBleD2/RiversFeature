using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    /// <summary>Rivers' oceanThreshold is in blocks of submerged land (RiverConfig.cs); the ocean map is 0..255.</summary>
    public class OceanThresholdTests
    {
        [Fact]
        public void ThirtyBlocks_IsMapValueNinety_OnDefaultHeights()
        {
            Assert.InRange(RiverNet.OceanMapThreshold(30, 256), 89.9, 90.1);
            Assert.InRange(RiverNet.OceanMapThreshold(30, 320), 89.9, 90.1);   // 320 / 256 = 1 (integer division, as Rivers)
            Assert.InRange(RiverNet.OceanMapThreshold(30, 512), 44.9, 45.1);
        }
    }
}
