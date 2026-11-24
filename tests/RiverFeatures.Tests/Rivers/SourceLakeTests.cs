using System.Collections.Generic;
using RiverFeatures.Rivers;
using Xunit;

namespace RiverFeatures.Tests.Rivers
{
    public class SourceLakeTests
    {
        [Fact]
        public void Default_leaves_Rivers_own_lakeChance()
        {
            var c = new HierarchyConfig();
            c.Sanitize();
            Assert.Equal(-1, c.SourceLakeChance);
        }

        [Fact]
        public void World_setting_sets_the_source_lake_chance()
        {
            var c = new HierarchyConfig();
            var set = new Dictionary<string, string> { ["rhSourceLakeChance"] = "0.3" };
            var applied = c.ApplyWorldSettings(k => set.TryGetValue(k, out var v) ? v : null);
            Assert.Equal(0.3, c.SourceLakeChance);
            Assert.Contains(applied, l => l.StartsWith("rhSourceLakeChance"));

            var keep = new HierarchyConfig();
            keep.ApplyWorldSettings(k => k == "rhSourceLakeChance" ? "config" : null);
            Assert.Equal(-1, keep.SourceLakeChance);
        }

        [Fact]
        public void Lake_shapes_world_setting_opts_out()
        {
            var c = new HierarchyConfig();
            Assert.True(c.VariedSourceLakes);
            c.ApplyWorldSettings(k => k == "rhLakeShapes" ? "0" : null);
            Assert.False(c.VariedSourceLakes);
            c.ApplyWorldSettings(k => k == "rhLakeShapes" ? "1" : null);
            Assert.True(c.VariedSourceLakes);
            c.ApplyWorldSettings(k => k == "rhLakeShapes" ? "config" : null);
            Assert.True(c.VariedSourceLakes);
        }

        [Fact]
        public void Sanitize_clamps()
        {
            var c = new HierarchyConfig { SourceLakeChance = 5 };
            c.Sanitize();
            Assert.Equal(1, c.SourceLakeChance);
            c = new HierarchyConfig { SourceLakeChance = -7 };
            c.Sanitize();
            Assert.Equal(-1, c.SourceLakeChance);
        }
    }
}
