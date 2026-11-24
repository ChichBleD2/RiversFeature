using System;
using Newtonsoft.Json.Linq;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Smoke tests of the documented design rulings (README) against the pure rule functions.</summary>
    public class TerrainGuardTests
    {
        [Fact] public void Slope_CentralDifferenceWhenBothNeighbours() => Assert.Equal(2.0, TerrainGuard.Slope(10, 12, 8, 10, 10));
        [Fact] public void Slope_OneSidedNotHalvedAtMissingNeighbour() => Assert.Equal(2.0, TerrainGuard.Slope(10, 12, -1, -1, -1));
        [Fact] public void Slope_AllMissingIsZero() => Assert.Equal(0.0, TerrainGuard.Slope(10, -1, -1, -1, -1));

        [Fact]
        public void LimitCut_RampsFromZeroToMaxCut()
        {
            Assert.Equal(0, TerrainGuard.LimitCut(5, 5, 0.0));
            Assert.Equal(5, TerrainGuard.LimitCut(5, 5, 1.0));
            Assert.Equal(2, TerrainGuard.LimitCut(10, 5, 0.5)); // capped at maxCut first, then faded
            Assert.Equal(0, TerrainGuard.LimitCut(5, 5, double.NaN));
            int prev = 0;
            for (int i = 0; i <= 20; i++)
            {
                int c = TerrainGuard.LimitCut(5, 5, TerrainGuard.Fade(i, 8));
                Assert.True(c >= prev);
                prev = c;
            }
            Assert.Equal(5, prev);
        }

        [Fact]
        public void Fade_EdgesExact()
        {
            Assert.Equal(0.0, TerrainGuard.Fade(0, 8));
            Assert.Equal(1.0, TerrainGuard.Fade(8, 8));
            Assert.Equal(0.5, TerrainGuard.Fade(4, 8), 9);
        }

        [Fact]
        public void LakeColumnAllowed_UsesMaxRiseAndMaxSlope()
        {
            const int sea = 110;
            Assert.True(TerrainGuard.LakeColumnAllowed(115, 116, sea, 10, 3, 5, 12, 0.5, 1.0));
            Assert.False(TerrainGuard.LakeColumnAllowed(sea + 13, sea + 13, sea, 10, 3, 5, 12, 0.5, 1.0)); // above maxRise
            Assert.False(TerrainGuard.LakeColumnAllowed(115, 116, sea, 10, 3, 5, 12, 1.5, 1.0));          // steeper than maxSlope
            Assert.False(TerrainGuard.LakeColumnAllowed(sea, sea + 2, sea, 10, 3, 5, 12, 0.0, 1.0));      // at sea level
        }

        [Fact]
        public void LakeColumnAllowed_ReachableDepth()
        {
            // 116 - (118 - 5) = 3 >= minDepth 3 -> allowed; 116 - (119 - 5) = 2 -> not.
            Assert.True(TerrainGuard.LakeColumnAllowed(118, 116, 110, 10, 3, 5, 12, 0.0, 1.0));
            Assert.False(TerrainGuard.LakeColumnAllowed(119, 116, 110, 10, 3, 5, 12, 0.0, 1.0));
        }
    }

    public class WorldScaleTests
    {
        [Fact]
        public void LandformScale_HorizontalOnly_Clamped()
        {
            var s = new WorldScale(256, 110, 10.0);
            Assert.Equal(10, s.Scale(10));               // vertical: no landform factor
            Assert.Equal(40, s.ScaleHorizontal(10));     // clamped to 4
            Assert.Equal(3, new WorldScale(256, 110, 0.01).ScaleHorizontal(10)); // clamped to 0.25 -> 2.5 -> 3
            Assert.Equal(10, new WorldScale(256, 110, double.NaN).ScaleHorizontal(10));
        }

        [Fact]
        public void HeightScaling()
        {
            Assert.Equal(20, new WorldScale(512, 110, 1).Scale(10));
            Assert.Equal(1, new WorldScale(16, 8, 1).Scale(1));
        }

        [Fact]
        public void ParseLandform_DotThenLocale()
        {
            Assert.Equal(1.5, WorldScale.ParseLandform("1.5"));
            Assert.Equal(1.0, WorldScale.ParseLandform(null));
            Assert.Equal(1.0, WorldScale.ParseLandform("abc"));
            Assert.Equal(1.0, WorldScale.ParseLandform("-2"));
        }
    }

    public class LakeChanceTests
    {
        // Flight test 6: the user asked for more lakes; the default was 0.05.
        [Fact] public void Default_Is_0_12() => Assert.Equal(0.12, new TerrainWatersConfig().LakeChance);

        [Fact]
        public void Ceiling_And_ActiveShare_MatchReadme()
        {
            double cov = WaterRules.DiscCoverage(192, 16, 48);
            Assert.InRange(cov, 0.094, 0.0955);
            Assert.InRange(WaterRules.LakeActiveShare(0.05, 192, 16, 48), 0.52, 0.535);
            Assert.Null(WaterRules.LakeChanceCeilingNote(0.05, cov));
            Assert.NotNull(WaterRules.LakeChanceCeilingNote(0.5, cov));
        }
    }

    public class ConfigTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("{ not json")]
        [InlineData("[1]")]
        [InlineData("\"x\"")]
        [InlineData("null")]
        public void Reload_RejectsMalformed(string text)
        {
            Assert.False(ConfigEdit.TryParseCandidate(text, out var cfg, out _, out var err));
            Assert.Null(cfg);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void Set_RejectsBadValue_KeepsCurrent()
        {
            var cur = new TerrainWatersConfig();
            Assert.False(ConfigEdit.TrySet(cur, "LakeChance", "lots", out var res, out _));
            Assert.Null(res);
            Assert.Equal(0.12, cur.LakeChance);
        }

        [Fact]
        public void Set_CaseInsensitiveKey_ClampsAndReports()
        {
            Assert.True(ConfigEdit.TrySet(new TerrainWatersConfig(), "lakechance", "5", out var res, out var msg));
            Assert.Equal(1.0, res.LakeChance);
            Assert.Contains("LakeChance is now 1", msg);
        }

        [Fact]
        public void UnknownKey_KeptAndNamed()
        {
            var o = JObject.Parse("{\"lakeChance\": 0.2}");
            var file = TerrainWatersConfig.ToFileObject(new TerrainWatersConfig(), o);
            Assert.Equal(0.2, (double)file["lakeChance"]);
            Assert.Contains("did you mean 'LakeChance'?", TerrainWatersConfig.UnknownKeysNote(TerrainWatersConfig.UnknownKeys(o)));
        }

        [Fact]
        public void Show_ListsTypes()
        {
            var lines = ConfigEdit.Describe(new TerrainWatersConfig());
            Assert.Contains(lines, l => l.StartsWith("LakeChance = 0.12 (double)"));
            Assert.Contains(lines, l => l.StartsWith("Enabled = true (bool)"));
        }

        [Fact]
        public void SeamWarnings_OnSeedAndRadius()
        {
            Assert.NotEmpty(SeamCheck.Warnings(1, 192, 48, 16, 2, 192, 48, 16));
            Assert.NotEmpty(SeamCheck.Warnings(1, 192, 48, 16, 1, 240, 60, 20));
            Assert.Empty(SeamCheck.Warnings(1, 192, 48, 16, 1, 192, 48, 16));
        }

        [Fact]
        public void RiverSpawnChance_DefaultLeavesRiversAlone() => Assert.Equal(-1, new TerrainWatersConfig().RiverSpawnChance);
    }

    public class OriginalHeightsTests
    {
        [Fact]
        public void EncodeDecode_RoundTrip()
        {
            var h = new ushort[32 * 32];
            for (int i = 0; i < h.Length; i++) h[i] = (ushort)(i % 300);
            Assert.Equal(h, OriginalHeights.Decode(OriginalHeights.Encode(h)));
        }
    }
}
