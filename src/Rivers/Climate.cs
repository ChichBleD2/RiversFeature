using System;

namespace RiverFeatures.Rivers
{
    /// <summary>Local climate at a column: yearly rainfall 0..1 and temperature in degrees C at sea level. NaN = unknown.</summary>
    public readonly record struct ClimateSample(double Rain, double Temp)
    {
        public static readonly ClimateSample Unknown = new ClimateSample(double.NaN, double.NaN);
        public bool Known => !double.IsNaN(Rain);
    }

    /// <summary>Where River Features reads the local climate from (world block coordinates in).</summary>
    public interface IClimateSource
    {
        string Name { get; }
        ClimateSample At(double x, double z);
    }

    /// <summary>
    /// The climate River Features reads. The default source is the game's own climate map layer (GenMaps.climateGen, the
    /// layer climate mods replace), so a climate-overhaul mod that changes that layer is followed with no change here.
    /// A mod with its own climate model can instead register a source (<see cref="Register"/>); a source that throws or
    /// returns unknown values only turns the climate bias off (neutral), it never stops world generation.
    /// </summary>
    public static class ClimateSources
    {
        static volatile IClimateSource registered;

        /// <summary>For other mods: use this source instead of the game's climate map (null = back to the game's).</summary>
        public static void Register(IClimateSource source) => registered = source;

        public static IClimateSource Registered => registered;

        /// <summary>The climate at a column from the registered source, else the fallback; Unknown on any failure.</summary>
        public static ClimateSample At(IClimateSource fallback, double x, double z)
        {
            var s = registered ?? fallback;
            if (s == null) return ClimateSample.Unknown;
            try { return s.At(x, z); } catch (Exception) { return ClimateSample.Unknown; }
        }
    }

    /// <summary>
    /// Soft climate bias (pure). Aridity and warmth are 0..1; an unknown climate is neutral (0.5). Every biased value is
    /// blended toward its neutral value by (1 - influence), so ClimateInfluence 0 makes delta looks and trace kinds
    /// climate-blind (dry rivers follow rainfall separately, through DryRainLimit).
    /// </summary>
    public static class ClimateBias
    {
        /// <summary>0 = wet (rain 0.8 and more), 1 = arid (rain 0.1 and less), linear between; 0.5 when unknown.</summary>
        public static double Aridity(ClimateSample c) =>
            !c.Known ? 0.5 : Math.Clamp((0.8 - c.Rain) / 0.7, 0, 1);

        /// <summary>0 = cold (-5 C and below), 1 = hot (30 C and above), linear between; 0.5 when unknown.</summary>
        public static double Warmth(ClimateSample c) =>
            double.IsNaN(c.Temp) ? 0.5 : Math.Clamp((c.Temp + 5) / 35.0, 0, 1);

        /// <summary>value blended toward neutral by (1 - influence), influence clamped to 0..1.</summary>
        public static double Soft(double value, double neutral, double influence)
        {
            double k = double.IsNaN(influence) ? 0 : Math.Clamp(influence, 0, 1);
            return neutral + (value - neutral) * k;
        }
    }
}
