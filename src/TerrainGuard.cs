using System;

namespace RiverFeatures
{
    /// <summary>
    /// Mountain-protection rules. All heights are block Y values; all positions are WORLD coordinates.
    /// "Locked" means the column is not touched at all (false / 0). "Attenuated" means the change is
    /// scaled down smoothly. Config names are from the spec: MaxRiseAboveSea, MaxSlope, MaxCut, MaxFill, FadeMargin.
    /// </summary>
    public static class TerrainGuard
    {
        /// <summary>
        /// Slope in blocks of height per block of distance, max over the two axes of:
        /// both neighbours present -> |h1 - h2| / 2 (central difference); exactly one missing -> the full
        /// one-sided difference |h - other| (not halved, so a chunk-edge column is judged like an interior one);
        /// both missing -> 0 for that axis. A neighbour height of -1 means unavailable (edge chunk). Never throws.
        /// Inputs are heights at the four orthogonal neighbours of the column, one block away.
        /// </summary>
        public static double Slope(int h, int hE, int hW, int hN, int hS)
        {
            return Math.Max(AxisSlope(h, hE, hW), AxisSlope(h, hN, hS));
        }

        private static double AxisSlope(int h, int a, int b)
        {
            bool hasA = a != -1, hasB = b != -1;
            if (hasA && hasB) return Math.Abs(a - b) / 2.0;
            if (hasA) return Math.Abs(h - a);
            if (hasB) return Math.Abs(h - b);
            return 0.0;
        }

        /// <summary>
        /// Smoothstep weight in [0, 1] for a column distToEdge blocks inside a feature edge. Exactly 0 when
        /// distToEdge &lt;= 0 (the edge or outside: nothing is changed), exactly 1 when distToEdge &gt;= margin,
        /// smooth (3t^2 - 2t^3) and monotonic non-decreasing between. margin &lt;= 0 (possible on tiny scaled
        /// worlds) gives 1 when distToEdge &gt; 0 and 0 otherwise; NaN input gives 0; infinities are handled.
        /// </summary>
        public static double Fade(double distToEdge, double margin)
        {
            if (double.IsNaN(distToEdge) || double.IsNaN(margin)) return 0.0;
            if (distToEdge <= 0) return 0.0;
            if (margin <= 0) return 1.0;
            if (distToEdge >= margin) return 1.0;
            double t = distToEdge / margin;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
        /// May a lake be carved at this column? surfaceY is the terrain height, waterLevel the lake surface.
        /// LOCKED (false): surfaceY &gt; seaLevel + maxRise (MaxRiseAboveSea: highlands keep their terrain);
        /// slope &gt; maxSlope or NaN slope (MaxSlope: steep ground is never changed); surfaceY &lt;= seaLevel (ocean or beach, never a lake); the surface is more than
        /// maxCut above waterLevel (a cut of at most maxCut cannot reach the water); the reachable depth
        /// waterLevel - (surfaceY - maxCut) is below minDepth for a column above the water (too shallow to hold a lake).
        /// ALLOWED: columns at or below waterLevel (carving may deepen them) that are above sea level.
        /// oceanBuffer is NOT checked here: it is a horizontal distance, enforced by NearOcean in the caller;
        /// the parameter is kept so call sites state the buffer they use.
        /// </summary>
        public static bool LakeColumnAllowed(int surfaceY, int waterLevel, int seaLevel, int oceanBuffer, int minDepth, int maxCut, int maxRise, double slope, double maxSlope)
        {
            if (surfaceY <= seaLevel) return false;
            if (surfaceY > seaLevel + maxRise) return false;
            if (!(slope <= maxSlope)) return false;
            if (surfaceY <= waterLevel) return true;
            if (surfaceY - waterLevel > maxCut) return false;
            int reachableDepth = waterLevel - (surfaceY - maxCut);
            return reachableDepth >= minDepth;
        }

        /// <summary>
        /// May a swamp be painted at this column? LOCKED (false): surfaceY more than maxRise above seaLevel
        /// (mountains and highlands keep their terrain) or slope &gt; maxSlope (steep ground is never changed).
        /// Everything else is allowed.
        /// </summary>
        public static bool SwampColumnAllowed(int surfaceY, int seaLevel, int maxRise, double slope, double maxSlope)
        {
            if (surfaceY > seaLevel + maxRise) return false;
            if (slope > maxSlope) return false;
            return true;
        }

        /// <summary>
        /// How many blocks to cut at a column. ATTENUATED: desiredCut is scaled by fade (0..1, from Fade), so
        /// cuts shrink smoothly to 0 over FadeMargin at the feature edge. The desired cut is capped at maxCut first and then scaled by fade, so the cut ramps 0..maxCut with fade and never forms a wall. Never negative:
        /// a non-positive desiredCut, fade of 0 or NaN gives 0 (locked).
        /// </summary>
        public static int LimitCut(int desiredCut, int maxCut, double fade)
        {
            if (desiredCut <= 0 || maxCut <= 0 || double.IsNaN(fade) || fade <= 0) return 0;
            if (fade > 1) fade = 1;
            int cut = (int)Math.Floor(Math.Min(desiredCut, maxCut) * fade);
            return Math.Max(0, cut);
        }

        /// <summary>How many blocks to fill: desired capped to [0, maxFill]. Fills are never negative and never exceed MaxFill.</summary>
        public static int LimitFill(int desired, int maxFill)
        {
            if (desired <= 0 || maxFill <= 0) return 0;
            return Math.Min(desired, maxFill);
        }
    }
}
