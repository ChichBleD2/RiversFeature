using System;

namespace RiverFeatures
{
    /// <summary>
    /// One candidate feature (lake centre) belonging to a square world cell. Shape (WaterRules.ShapeAt): an ellipse with
    /// semi-axes Radius and Radius x Aspect turned by Angle, whose outline is pulled inward by up to Wobble x its size by
    /// smooth value noise (ShapeSeed) at world coordinates: bays, lobes and peninsulas, always inside the Radius disc.
    /// Defaults (Aspect 1, Wobble 0) are the plain disc.
    /// </summary>
    public readonly record struct FeatureCell(int CellX, int CellZ, double CenterX, double CenterZ, double Radius, int WaterLevel,
        double Aspect = 1.0, double Angle = 0.0, double Wobble = 0.0, long ShapeSeed = 0);

    public static class FeatureCells
    {
        /// <summary>
        /// The feature owned by the cell containing WORLD block (blockX, blockZ). Cells are cellSize-block
        /// squares aligned to the world origin (floor division, so negative coordinates work); chunk borders
        /// play no part, so two blocks in the same cell, even in different chunks, get the identical feature.
        /// Pure function of (seed, cellX, cellZ, parameters): every value comes from a SplitMix64 hash of the
        /// seed and cell coordinates with a different salt per value. No shared random state, no call-order dependence.
        /// Centre is in WORLD block coordinates, kept at least Radius from the cell edge when the cell is big
        /// enough (otherwise the cell middle). Radius in [minRadius, maxRadius]. WaterLevel in
        /// [seaLevel + 2, seaLevel + maxRise] (collapsing to seaLevel + 2 if maxRise &lt; 2).
        /// </summary>
        public static FeatureCell At(long seed, int blockX, int blockZ, int cellSize, int seaLevel, int maxRise, int minRadius, int maxRadius)
        {
            if (cellSize < 1) cellSize = 1;
            if (maxRadius < minRadius) maxRadius = minRadius;
            int cx = FloorDiv(blockX, cellSize);
            int cz = FloorDiv(blockZ, cellSize);

            double radius = minRadius + Unit(seed, cx, cz, 1) * (maxRadius - minRadius);
            double lo = radius, hi = cellSize - radius;
            double offX, offZ;
            if (hi <= lo) { offX = offZ = cellSize / 2.0; }
            else
            {
                offX = lo + Unit(seed, cx, cz, 2) * (hi - lo);
                offZ = lo + Unit(seed, cx, cz, 3) * (hi - lo);
            }
            int loLevel = seaLevel + 2;
            int hiLevel = Math.Max(loLevel, seaLevel + maxRise);
            int level = loLevel + (int)Math.Min(hiLevel - loLevel, Math.Floor(Unit(seed, cx, cz, 4) * (hiLevel - loLevel + 1)));
            // Shape (flight test: discs read as predictable circles): elongation 0.45..1, any orientation, outline wobble 0.2..0.6.
            double aspect = 0.45 + 0.55 * Unit(seed, cx, cz, 5);
            double angle = Math.PI * Unit(seed, cx, cz, 6);
            double wobble = 0.2 + 0.4 * Unit(seed, cx, cz, 7);
            long shapeSeed = (long)(Mix((ulong)seed ^ ((ulong)(uint)cx << 32 | (uint)cz) ^ 0x5DEECE66DUL) >> 1);
            return new FeatureCell(cx, cz, (double)cx * cellSize + offX, (double)cz * cellSize + offZ, radius, level, aspect, angle, wobble, shapeSeed);
        }

        /// <summary>Floor division: -1 / 64 gives -1, not 0.</summary>
        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
        }

        /// <summary>Hash of (seed, cx, cz, salt) mapped to [0, 1).</summary>
        private static double Unit(long seed, int cx, int cz, int salt)
        {
            ulong h = Mix((ulong)seed + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1));
            h = Mix(h ^ (ulong)(uint)cx);
            h = Mix(h ^ ((ulong)(uint)cz << 32) ^ 0xD1B54A32D192ED03UL);
            return (h >> 11) * (1.0 / 9007199254740992.0); // top 53 bits / 2^53
        }

        /// <summary>SplitMix64 finalizer.</summary>
        private static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
