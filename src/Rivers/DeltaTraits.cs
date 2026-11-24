using System;

namespace RiverFeatures.Rivers
{
    /// <summary>What a delta's banks are made of.</summary>
    public enum DeltaBank { Sand, Mud, Gravel, Rock }

    /// <summary>How a delta reads in the world (statistics and /rffind): derived from its traits, never rolled on its own.</summary>
    public enum DeltaLook { Sandy, Marshy, Overgrown, Dry, Intermittent, Rocky }

    /// <summary>
    /// Stage 2: the character of one delta as a few independent parameters instead of fixed types.
    ///  Scale      0.3..2.5: size of the whole delta (apex distance, branch count, channel width).
    ///  Bank       bank and shallow-bed material.
    ///  Moisture   0..1: below DryBelow every distributary is dry (an abandoned delta), below WetFrom some are dry and the
    ///             rest shallow (intermittent), above that all carry water.
    ///  Vegetation 0..1: plant density on the banks.
    ///  Branching  0..1: fewer (0) or more (1) distributaries than the shape's usual count.
    /// The shape (small / carving / shallow, <see cref="DeltaKind"/>) stays a separate axis, so every look exists at every size.
    /// </summary>
    public readonly record struct DeltaTraits(double Scale, DeltaBank Bank, double Moisture, double Vegetation, double Branching)
    {
        public const double DryBelow = 0.22, WetFrom = 0.42;

        public DeltaLook Look => DeltaTraitRules.Classify(this);

        /// <summary>Traits of the deltas before stage 2 (wet, sandy, sparse plants, usual size).</summary>
        public static readonly DeltaTraits Classic = new DeltaTraits(1.0, DeltaBank.Sand, 1.0, 0.2, 0.5);
    }

    /// <summary>Rolling and classifying delta traits (pure, unit tested).</summary>
    public static class DeltaTraitRules
    {
        /// <summary>
        /// Rolls a delta's traits. <paramref name="rnd"/>(salt) is a deterministic value in [0, 1) for this delta;
        /// <paramref name="sizeShare"/> is the river's mouth width over Rivers' maximum width (bigger rivers lean to bigger
        /// deltas). Climate bias, all soft (blended to neutral by 1 - influence):
        ///  - moisture leans to 1 - aridity: dry and intermittent deltas belong to dry land, wet ones to rainy land;
        ///  - vegetation follows moisture and warmth;
        ///  - bank material: sand likes warm and dry, mud likes wet, gravel and rock like cold.
        /// </summary>
        public static DeltaTraits Roll(ClimateSample climate, double influence, double sizeShare, double minScale, double maxScale, Func<int, double> rnd)
        {
            double arid = ClimateBias.Soft(ClimateBias.Aridity(climate), 0.5, influence);
            double warm = ClimateBias.Soft(ClimateBias.Warmth(climate), 0.5, influence);

            // Moisture: a random value pulled toward the climate's (1 - aridity). With no influence, arid = 0.5 and the pull is
            // toward 0.5 only partly, so every moisture class still occurs everywhere.
            double u = rnd(1);
            double moisture = Math.Clamp(0.45 * u + 0.55 * (1 - arid) + (rnd(2) - 0.5) * 0.3, 0, 1);

            double vegetation = Math.Clamp(moisture * (0.45 + 0.55 * warm) * (0.55 + 0.9 * rnd(3)), 0, 1);

            double wSand = 0.6 + 0.8 * warm + 0.6 * arid;
            double wMud = 0.2 + 1.6 * moisture * (1 - arid);
            double wGravel = 0.4 + 0.7 * (1 - warm);
            double wRock = 0.15 + 0.6 * (1 - warm) * (0.5 + arid);
            double pick = rnd(4) * (wSand + wMud + wGravel + wRock);
            var bank = pick < wSand ? DeltaBank.Sand : pick < wSand + wMud ? DeltaBank.Mud : pick < wSand + wMud + wGravel ? DeltaBank.Gravel : DeltaBank.Rock;

            double lo = Math.Clamp(Math.Min(minScale, maxScale), 0.3, 2.5), hi = Math.Clamp(Math.Max(minScale, maxScale), 0.3, 2.5);
            double s01 = Math.Clamp(0.6 * rnd(5) + 0.4 * Math.Clamp(double.IsNaN(sizeShare) ? 0.5 : sizeShare, 0, 1), 0, 1);
            double scale = lo + (hi - lo) * s01;

            return new DeltaTraits(scale, bank, moisture, vegetation, rnd(6));
        }

        /// <summary>
        /// The look: dry below DryBelow moisture, intermittent below WetFrom; otherwise rocky (rock, or gravel with few
        /// plants), overgrown (dense plants), marshy (mud), else sandy.
        /// </summary>
        public static DeltaLook Classify(DeltaTraits t)
        {
            if (t.Moisture < DeltaTraits.DryBelow) return DeltaLook.Dry;
            if (t.Moisture < DeltaTraits.WetFrom) return DeltaLook.Intermittent;
            if (t.Bank == DeltaBank.Rock || (t.Bank == DeltaBank.Gravel && t.Vegetation < 0.3)) return DeltaLook.Rocky;
            if (t.Vegetation >= 0.55) return DeltaLook.Overgrown;
            if (t.Bank == DeltaBank.Mud) return DeltaLook.Marshy;
            return DeltaLook.Sandy;
        }

        /// <summary>Whether distributary <paramref name="b"/> of a delta with these traits carries no water (pure, by hash).</summary>
        public static bool ChannelDry(DeltaTraits t, double h)
        {
            if (t.Moisture < DeltaTraits.DryBelow) return true;
            if (t.Moisture >= DeltaTraits.WetFrom) return false;
            // Intermittent: the drier the delta, the more of its channels are dry (about 30..70%).
            double share = 0.7 - 0.4 * (t.Moisture - DeltaTraits.DryBelow) / (DeltaTraits.WetFrom - DeltaTraits.DryBelow);
            return h < share;
        }

        /// <summary>How far (blocks beyond the channel edge) the bank character reaches: 10 + 14 x scale.</summary>
        public static double BankReach(DeltaTraits t) => 10 + 14 * Math.Clamp(t.Scale, 0.3, 2.5);
    }
}
