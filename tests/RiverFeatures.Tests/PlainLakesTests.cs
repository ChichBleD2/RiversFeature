using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>1.0.0: plain lakes (lakes config) are deferred behind PlainLakes; source lakes and swamps do not depend on it.</summary>
    public class PlainLakesTests
    {
        static readonly WorldScale W = new WorldScale(256, 110, 1.0);

        [Fact]
        public void Plain_lakes_are_off_by_default_and_the_lake_keys_are_kept()
        {
            var cfg = new TerrainWatersConfig();
            Assert.False(cfg.PlainLakes);
            var r = cfg.Resolve(W);
            Assert.False(r.PlainLakes);
            Assert.Equal(0.12, r.LakeChance);   // kept for when plain lakes come back
            Assert.Equal(cfg.SwampChance, r.SwampChance);
        }

        [Fact]
        public void Plain_lakes_can_be_switched_back_on()
        {
            var parsed = TerrainWatersConfig.Parse("{ \"PlainLakes\": true }", null);
            Assert.True(parsed.PlainLakes);
            Assert.True(parsed.Resolve(W).PlainLakes);
            Assert.True(ConfigEdit.TrySet(new TerrainWatersConfig(), "PlainLakes", "true", out var set, out _));
            Assert.True(set.PlainLakes);
            Assert.False(TerrainWatersConfig.Parse("{ \"LakeChance\": 0.2 }", null).PlainLakes);   // an old file stays deferred
        }
    }
}
