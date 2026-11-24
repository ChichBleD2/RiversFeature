using RiverFeatures;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Pre-release step 2: erosion switch, no writes to other mods' configs, the settings reference sheet.</summary>
    public class Step2Tests
    {
        [Fact]
        public void Erosion_is_removed_and_its_old_keys_are_dropped_from_the_file()
        {
            Assert.Null(typeof(TerrainWatersConfig).Assembly.GetType("RiverFeatures.ErosionPass"));
            Assert.DoesNotContain(TerrainWatersConfig.KnownKeys(), k => k.StartsWith("Erosion"));
            var logs = new System.Collections.Generic.List<string>();
            var o = Newtonsoft.Json.Linq.JObject.Parse("{ \"SwampChance\": 0.2, \"Erosion\": false, \"ErosionStrength\": 0.5, \"Typo\": 1 }");
            var cfg = TerrainWatersConfig.Parse(o.ToString(), logs.Add);
            Assert.Equal(0.2, cfg.SwampChance);
            Assert.Contains(logs, l => l.Contains("removed setting(s) dropped") && l.Contains("ErosionStrength"));
            Assert.Equal(new[] { "Typo" }, TerrainWatersConfig.UnknownKeys(o));   // erosion keys are not "unknown" (not kept)
            var written = TerrainWatersConfig.ToFileObject(cfg, o);
            Assert.Null(written["Erosion"]); Assert.Null(written["ErosionStrength"]);
            Assert.NotNull(written["Typo"]);
            Assert.False(ConfigEdit.TrySet(new TerrainWatersConfig(), "ErosionStrength", "0.3", out _, out _));
        }

        [Fact]
        public void River_spawn_chance_is_never_mirrored_into_Rivers_file()
        {
            Assert.Null(typeof(TerrainWatersConfig).Assembly.GetType("RiverFeatures.RiversMirrorSystem"));
            Assert.False(ConfigEdit.TrySet(new TerrainWatersConfig(), "RiverSpawnChance", "0.5", out var r, out var msg));
            Assert.Null(r);
            Assert.Contains("never writes another mod's config", msg);
        }

        [Fact]
        public void The_settings_sheet_covers_every_key_of_both_files()
        {
            var (m1, s1) = SettingsSheet.Check(typeof(TerrainWatersConfig), SettingsSheet.Lakes);
            var (m2, s2) = SettingsSheet.Check(typeof(HierarchyConfig), SettingsSheet.Rivers);
            Assert.True(m1.Count == 0, "lakes keys without an entry: " + string.Join(", ", m1));
            Assert.True(s1.Count == 0, "stale lakes entries: " + string.Join(", ", s1));
            Assert.True(m2.Count == 0, "rivers keys without an entry: " + string.Join(", ", m2));
            Assert.True(s2.Count == 0, "stale rivers entries: " + string.Join(", ", s2));

            var text = SettingsSheet.Build(new TerrainWatersConfig { SwampChance = 0.3 }, new HierarchyConfig());
            Assert.Contains("SwampChance = 0.3   (default 0.15)", text);
            Assert.Contains("DeltaChance = 0.35   (default)", text);
            Assert.Contains("never writes any other mod's config", text);
        }
    }
}
