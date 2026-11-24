using System;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    public class GrooveTests
    {
        // Rivers' profile: bank = max(sqrt(size) * riverDepth, baseDepth) * sqrt(1 - (d / size)^2)
        static double Bank(double d, double size, double rd, double bd) => Math.Max(Math.Sqrt(size) * rd, bd) * Math.Sqrt(Math.Max(0, 1 - (d / size) * (d / size)));

        [Fact]
        public void Groove_is_deepest_on_the_centre_line_and_narrow()
        {
            double size = 16, rd = 0.03, bd = 0.1, g = 3.0 / 210;
            double centre = DryChannels.GrooveAt(Bank(0, size, rd, bd), size, rd, bd, g);
            Assert.Equal(g, centre, 6);
            double edge = DryChannels.GrooveAt(Bank(3.0, size, rd, bd), size, rd, bd, g);
            Assert.True(edge > 0 && edge < centre);
            Assert.Equal(0, DryChannels.GrooveAt(Bank(5, size, rd, bd), size, rd, bd, g));
            Assert.Equal(0, DryChannels.GrooveAt(Bank(12, size, rd, bd), size, rd, bd, g));
        }

        [Fact]
        public void Groove_off_or_without_depths_does_nothing()
        {
            Assert.Equal(0, DryChannels.GrooveAt(0.5, 16, 0.03, 0.1, 0));
            Assert.Equal(0, DryChannels.GrooveAt(0.5, 16, 0, 0, 0.01));
        }

        [Fact]
        public void Delta_types_default_to_a_third_each()
        {
            var c = new HierarchyConfig();
            Assert.InRange(c.DeltaSmallShare, 0.33, 0.34);
            Assert.InRange(c.DeltaShallowShare, 0.33, 0.34);
            Assert.InRange(1 - c.DeltaSmallShare - c.DeltaShallowShare, 0.33, 0.34);
            Assert.Equal(3, c.ShallowDeltaGrooveDepth);
        }
    }
}
