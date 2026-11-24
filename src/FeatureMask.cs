using System;

namespace RiverFeatures
{
    /// <summary>
    /// Turns an injected noise function into a yes/no mask over the world. The noise delegate is supplied by
    /// the caller (the game's NormalizedSimplexNoise in the mod, a seeded value noise in tests), so this class
    /// references no game API and holds no mutable state.
    /// </summary>
    public sealed class FeatureMask
    {
        private readonly Func<double, double, double> noise;
        private readonly double frequency;

        /// <param name="noise">Noise in [-1, 1] taking (x, z) in noise space.</param>
        /// <param name="frequency">Noise units per block: the noise is sampled at (blockX * frequency, blockZ * frequency).</param>
        public FeatureMask(Func<double, double, double> noise, double frequency)
        {
            this.noise = noise ?? throw new ArgumentNullException(nameof(noise));
            this.frequency = frequency;
        }

        /// <summary>Noise value at a point. x and z are WORLD block coordinates (never chunk-local).</summary>
        public double Sample(double x, double z) => noise(x * frequency, z * frequency);

        /// <summary>
        /// The threshold above which about <paramref name="chance"/> of the world lies. Measured on a fixed
        /// deterministic grid (samples x samples-worth of points, spaced 3.7 noise units apart, starting at the
        /// origin), so the result depends only on the noise function and frequency, not on call order.
        /// chance &lt;= 0 gives +infinity (covers nothing); chance &gt;= 1 gives -infinity (covers everything).
        /// </summary>
        public double ThresholdFor(double chance, int samples = 4096)
        {
            if (double.IsNaN(chance) || chance <= 0) return double.PositiveInfinity;
            if (chance >= 1) return double.NegativeInfinity;
            int side = Math.Max(1, (int)Math.Sqrt(Math.Max(1, samples)));
            double step = 3.7 / frequency; // world blocks between grid points
            var values = new double[side * side];
            for (int i = 0; i < side; i++)
                for (int j = 0; j < side; j++)
                    values[i * side + j] = Sample(i * step, j * step);
            Array.Sort(values);
            int k = (int)Math.Floor((1.0 - chance) * values.Length);
            if (k < 0) k = 0;
            if (k > values.Length - 1) k = values.Length - 1;
            return values[k];
        }

        /// <summary>True when the mask noise at WORLD block position (x, z) is at or above the threshold.</summary>
        public bool Covers(double x, double z, double threshold) => Sample(x, z) >= threshold;
    }
}
