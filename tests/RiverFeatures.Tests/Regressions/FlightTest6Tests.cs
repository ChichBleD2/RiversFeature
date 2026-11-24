using System.Collections.Generic;
using RiverFeatures;
using Xunit;

namespace RiverFeatures.Tests
{
    /// <summary>Flight test 6: swamp walls at chunk borders, more lakes, world-creation settings.</summary>
    public class FlightTest6Tests
    {
        static WaterRules.SeamNeighbour N(int orig, int now, int d) => new WaterRules.SeamNeighbour(orig, now, d);

        [Fact]
        public void Seam_OwnCutRampsUpToAnUncutFinishedNeighbour()
        {
            // Own ground 120 planned down to 118; the neighbour chunk is finished and stayed at 120.
            Assert.Equal(119, WaterRules.SeamFit(120, 118, 2, new[] { N(120, 120, 1) }));
            Assert.Equal(118, WaterRules.SeamFit(120, 118, 2, new[] { N(120, 120, 2) }));
            Assert.Equal(118, WaterRules.SeamFit(120, 118, 2, new[] { N(120, 120, 3) }));
        }

        [Fact]
        public void Seam_UncutOwnGroundRampsDownToANeighbourThisModLowered()
        {
            // Neighbour was 120, this mod lowered it to 118; own ground 120 not planned to change.
            Assert.Equal(119, WaterRules.SeamFit(120, 120, 2, new[] { N(120, 118, 1) }));
            Assert.Equal(120, WaterRules.SeamFit(120, 120, 2, new[] { N(120, 118, 2) }));
        }

        [Fact]
        public void Seam_NaturalStepsAndUnfinishedNeighboursAreLeftAlone()
        {
            // A natural cliff: own 130 next to a lowered neighbour that was 120 -> not touched.
            Assert.Equal(130, WaterRules.SeamFit(130, 130, 2, new[] { N(120, 118, 1) }));
            // A natural drop toward own lower ground: never raised above the own ground.
            Assert.Equal(110, WaterRules.SeamFit(110, 110, 2, new[] { N(125, 125, 1) }));
            // Unknown / not finished (-1): nothing changes.
            Assert.Equal(118, WaterRules.SeamFit(120, 118, 2, new[] { N(120, -1, 1) }));
            // Lowering never goes more than maxLower below the own ground.
            Assert.Equal(118, WaterRules.SeamFit(120, 120, 2, new[] { N(120, 110, 1) }));
        }

        [Fact]
        public void Seam_AcrossABorderTheStepIsAtMostOnePerBlock()
        {
            // Chunk A cut its border row to 118 (finished); chunk B's columns at distance 1..3 from that row stay uncut.
            int[] fitted = new int[3];
            for (int d = 1; d <= 3; d++) fitted[d - 1] = WaterRules.SeamFit(120, 120, 2, new[] { N(120, 118, d) });
            Assert.Equal(new[] { 119, 120, 120 }, fitted);
            Assert.True(fitted[0] - 118 <= 1);
        }

        [Fact]
        public void WorldSettings_OverrideOrKeepTheModConfig()
        {
            var baseCfg = new TerrainWatersConfig { LakeChance = 0.1, SwampChance = 0.2, RiverLakeChance = 0.3 };
            var set = new Dictionary<string, string> { ["twLakeChance"] = "0.15", ["twSwampChance"] = "lots" };
            var logs = new List<string>();
            var c = baseCfg.WithWorldSettings(k => set.TryGetValue(k, out var v) ? v : null, logs.Add);
            Assert.Equal(0.15, c.LakeChance);
            Assert.Equal(0.2, c.SwampChance);       // unreadable: the mod config value, reported
            Assert.Equal(0.3, c.RiverLakeChance);   // flow-through lakes have no world setting any more
            Assert.Equal(0.1, baseCfg.LakeChance);  // the original is untouched
            Assert.Contains(logs, l => l.Contains("twSwampChance"));
            Assert.Equal(0.1, baseCfg.WithWorldSettings(null, null).LakeChance);
        }

        [Fact]
        public void WorldConfigFile_ListsTheThreeSettings()
        {
            string dir = System.AppContext.BaseDirectory;
            string path = null;
            for (var d = new System.IO.DirectoryInfo(dir); d != null; d = d.Parent)
                if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "worldconfig.json")) && System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "RiverFeatures.csproj")))
                { path = System.IO.Path.Combine(d.FullName, "worldconfig.json"); break; }
            Assert.NotNull(path);
            var o = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
            var codes = new List<string>();
            foreach (var a in o["worldConfigAttributes"]) codes.Add((string)a["code"]);
            foreach (var (code, _) in TerrainWatersConfig.WorldSettingCodes)
                if (System.Array.IndexOf(TerrainWatersConfig.HiddenWorldSettings, code) < 0) Assert.Contains(code, codes);
                else Assert.DoesNotContain(code, codes);   // 1.0.0: plain lakes deferred, their dropdown is hidden
            // Flight test 7: the game reads <mod>/assets/game/lang/worldconfig-en.json; every label and dropdown name needs a key.
            var lang = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "assets", "game", "lang", "worldconfig-en.json")));
            foreach (var a in o["worldConfigAttributes"])
            {
                string code = (string)a["code"];
                Assert.NotNull(lang["worldattribute-" + code]);
                Assert.NotNull(lang["worldconfig-category-" + (string)a["category"]]);
                foreach (var n in a["names"]) Assert.NotNull(lang["worldconfig-" + code + "-" + (string)n]);
            }
            foreach (var a in o["worldConfigAttributes"])
                foreach (var v in a["values"])
                {
                    string s = (string)v;
                    if (s == "config" || (string)a["code"] == "rhRiverLength") continue;   // river length is in blocks
                    Assert.True(double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d >= 0 && d <= 1, s);
                }
        }

        [Fact]
        public void Lakes_DenserGridRaisesTheCeiling()
        {
            int r = 48, min = 16;
            double at3 = WaterRules.DiscCoverage(3 * r, min, r), at225 = WaterRules.DiscCoverage((int)(2.25 * r), min, r);
            Assert.True(at225 > 1.7 * at3);
            Assert.True(0.12 > at3 * 0.6);   // the new default is above what the 3R grid gives (shape factor about 0.5..0.6)
        }
    }
}
