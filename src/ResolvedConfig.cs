namespace RiverFeatures
{
    /// <summary>Config values scaled to one world's height. Distances and heights are whole blocks.</summary>
    public class ResolvedConfig
    {
        public bool Enabled { get; init; }
        public double RiverSpawnChance { get; init; }
        public double LakeChance { get; init; }
        /// <summary>Plain lakes on (TerrainWatersConfig.PlainLakes; deferred, off by default).</summary>
        public bool PlainLakes { get; init; }
        public double RiverLakeChance { get; init; }
        public double SwampChance { get; init; }
        public int LakeMaxRadius { get; init; }
        /// <summary>max(8, LakeMaxRadius / 3), never above LakeMaxRadius.</summary>
        public int LakeMinRadius { get; init; }
        /// <summary>3 x LakeMaxRadius, down to 2.25 x when LakeChance needs more lakes; the feature-cell size used by FeatureCells.At.</summary>
        public int CellSize { get; set; }

        /// <summary>A copy with another feature-cell size (RebuildWorldState densifies the lake grid for a high LakeChance).</summary>
        public ResolvedConfig WithCellSize(int cellSize) { var c = (ResolvedConfig)MemberwiseClone(); c.CellSize = cellSize; return c; }
        public int LakeMinDepth { get; init; }
        public int LakeMinArea { get; init; }
        public int OceanBuffer { get; init; }
        public int LakeRiverGap { get; init; }
        public int RuinMargin { get; init; }
        public double SwampWaterCoverage { get; init; }
        public double SwampMinRain { get; init; }
        public int MaxRiseAboveSea { get; init; }
        public double MaxSlope { get; init; }
        public int MaxCut { get; init; }
        public int MaxFill { get; init; }
        public int MaxRimVariation { get; init; }
        public int FadeMargin { get; init; }
        public string[] SwampPlantCodes { get; init; }
        public int SeedOffset { get; init; }
        public int WorldHeight { get; init; }
        public int SeaLevel { get; init; }
    }
}
