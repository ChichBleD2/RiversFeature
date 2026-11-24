using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    /// <summary>Reference sizes in the config are for a 256-high world; this scales them to the real one.</summary>
    public readonly record struct WorldScale(int WorldHeight, int SeaLevel, double LandformScale)
    {
        const int MaxScaled = 100000; // keeps derived sizes (e.g. 4 x radius) far from int overflow

        /// <summary>round(referenceBlocks * WorldHeight / 256), at least 1 for a positive input, 0 for input &lt;= 0. Never throws.</summary>
        public int Scale(int referenceBlocks)
        {
            if (referenceBlocks <= 0) return 0;
            double v = Math.Round(referenceBlocks * (double)Math.Max(0, WorldHeight) / 256.0, MidpointRounding.AwayFromZero);
            if (double.IsNaN(v)) return 1;
            return (int)Math.Clamp(v, 1, MaxScaled);
        }

        /// <summary>
        /// Horizontal distances: Scale(referenceBlocks) x LandformScale (clamped 0.25..4, NaN/infinity = 1), rounded,
        /// at least 1 for a positive input. Vertical heights use Scale only.
        /// The world-creation settings "landcover" and "oceanscale" (keys known from vssurvivalmod 1.22.7
        /// Properties/AssemblyInfo.cs) are known but deliberately not used: they change where land and ocean are, which the
        /// ocean rule already reads from the terrain heights.
        /// </summary>
        public int ScaleHorizontal(int referenceBlocks)
        {
            int baseValue = Scale(referenceBlocks);
            if (baseValue <= 0) return 0;
            double f = LandformScale;
            f = (double.IsNaN(f) || double.IsInfinity(f)) ? 1.0 : Math.Clamp(f, 0.25, 4.0);
            double v = Math.Round(baseValue * f, MidpointRounding.AwayFromZero);
            return (int)Math.Clamp(v, 1, MaxScaled);
        }

        public static WorldScale From(IWorldAccessor world, IWorldManagerAPI wm)
        {
            double landform;
            // Key "landformScale" is a worldgen dropdown in vssurvivalmod 1.22.7 Properties/AssemblyInfo.cs. GetAsString is used
            // because the stored attribute type is UNVERIFIED (a string from the dropdown, or a number set by a command);
            // anything that does not parse as a positive number gives 1.
            string raw = null;
            try { raw = world.Config?.GetAsString("landformScale"); } catch (Exception) { raw = null; }
            landform = ParseLandform(raw);
            return new WorldScale(wm.MapSizeY, world.SeaLevel, landform);
        }

        /// <summary>
        /// The landform scale from its config text: invariant culture first ("1.5"), then the current culture (GetAsString
        /// turns a stored number into text with the current culture, e.g. "1,5"). Null, unparsable, NaN, infinite or &lt;= 0 -> 1.
        /// </summary>
        public static double ParseLandform(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 1.0;
            var st = System.Globalization.NumberStyles.Float;
            if (!double.TryParse(raw, st, System.Globalization.CultureInfo.InvariantCulture, out var v)
                && !double.TryParse(raw, st, System.Globalization.CultureInfo.CurrentCulture, out v))
                return 1.0;
            return double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? 1.0 : v;
        }
    }
}
