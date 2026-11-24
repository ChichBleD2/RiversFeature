using System;
using System.Collections.Generic;

namespace RiverFeatures
{
    /// <summary>Why a lake column was not flooded (or None when it was / may be).</summary>
    public enum LakeSkip { None = 0, Outside, Unknown, Structure, Ocean, RiverWater, LevelOutOfRange, Protected, Shore, Leak, Cave, Shallow, Bank, Small }

    /// <summary>The config values the lake rules need, in world blocks (from ResolvedConfig).</summary>
    public readonly record struct LakeParams(int SeaLevel, int OceanBuffer, int MinDepth, int MaxCut, int MaxRise, double MaxSlope, int MaxRimVariation, int FadeMargin)
    {
        public static LakeParams From(ResolvedConfig c) =>
            new LakeParams(c.SeaLevel, c.OceanBuffer, c.LakeMinDepth, c.MaxCut, c.MaxRiseAboveSea, c.MaxSlope, c.MaxRimVariation, c.FadeMargin);
    }

    /// <summary>
    /// Result of the per-column lake rule. Candidate = this column would be carved and flooded if its own
    /// exclusion flags and its neighbours allow it (see <see cref="WaterRules.LakeFinal"/>). Level is the water
    /// top Y (water fills Bed+1..Level), Bed the new top solid block, Cut the blocks removed.
    /// </summary>
    public readonly record struct LakeColumn(bool Candidate, int Level, int Cut, int Bed, LakeSkip Reason)
    {
        public static LakeColumn No(LakeSkip why, int level = -1) => new LakeColumn(false, level, 0, -1, why);
    }

    /// <summary>The feature a column belongs to: the active feature whose disc contains it most deeply.</summary>
    public readonly record struct FeatureHit(bool Found, FeatureCell Cell, double DistToEdge);

    /// <summary>
    /// One orthogonal neighbour as the lake containment rule sees it. Candidate/Level: it may be flooded to Level (not
    /// known for sure). Flooded: it is known to be flooded to Level (an accepted basin column, LakePlanner).
    /// </summary>
    public readonly record struct LakeNeighbour(int SurfaceY, int WaterTop, bool Candidate, int Level, bool Flooded = false);

    /// <summary>One orthogonal neighbour as the swamp pocket rule sees it.</summary>
    public readonly record struct SwampNeighbour(int SurfaceY, int MinFinal, bool PocketCapable, int ExpectedFinal);

    /// <summary>
    /// Pure lake/swamp decisions: no game types, no state. Every input is a WORLD coordinate, the world seed or
    /// a terrain height taken from the generated terrain height map (never one this mod has changed), so two
    /// chunks that look at the same world column reach the same answer whichever is generated first.
    ///
    /// No-water-wall invariant (lakes): a column is flooded to Level only if each of its 4 neighbours either has
    /// original terrain at or above Level (and is not a candidate of a different level), already holds water
    /// whose top is exactly Level, or is known to be flooded to the same Level (an accepted basin, LakePlanner).
    /// Neighbours are never lowered by a lake unless they are flooded to the same Level, and swamps never act within
    /// LakeKeepOut blocks of an active lake disc, so every flooded column is walled by ground at least Level high or
    /// by water of the same Level: no water face is left hanging in the air.
    /// No-bank-wall invariant (fix round 1): ground above Level is only removed from a column whose every neighbour
    /// ends at most one block above the water (BankNeighbourLow), so a lake never leaves a cut bank higher than 1.
    /// </summary>
    public static class WaterRules
    {
        /// <summary>Width of the rainfall ramp above SwampMinRain over which the swamp fades in (no hard edge on the rain contour).</summary>
        public const double RainBand = 0.05;
        /// <summary>Share of eligible swamp land columns that get a plant (fixed; the config only lists the codes).</summary>
        public const double PlantChance = 0.3;

        /// <summary>
        /// Swamps (via ChunkCtx.NearLake) keep this far outside every active lake disc. Equal to
        /// ColumnExclusions.RiverRadius because the neighbour chunk's water scan sees this mod's lake water as river water
        /// and excludes RiverRadius around it; locking the same zone from the (pure) feature list in every chunk makes the
        /// lake-making chunk agree with it whichever is generated first (fix round 1, finding 5b).
        /// </summary>
        public const int LakeKeepOut = 6;
        // Was ColumnExclusions.RiverRadius: a later neighbour read this mod's lake water as river water. Since the placed-water
        // flags (PlacedWater) the river scan ignores that water, so the keep-out only has to protect the lake's walls.

        /// <summary>Largest shore ramp radius (blocks): the distance over which lake beds deepen from the shoreline (LakePlanner).</summary>
        public const int MaxShoreRadius = 18;

        /// <summary>
        /// A column whose level would come from nearby river water joins it only when at most this share of the columns
        /// within ColumnExclusions.WaterShareRadius already holds water: river channels yes, Rivers lakes (their shores and
        /// islands) and very wide rivers no (flight test 3).
        /// </summary>
        public const double MaxJoinWaterShare = 0.4;

        /// <summary>A natural hollow fills only where it is at most this deep below its spill level: 2 x MaxCut (no water-filled shafts).</summary>
        public static int MaxBasinDepth(int maxCut) => Math.Max(4, 2 * maxCut);

        /// <summary>Radius (Manhattan) of the lake-bed smoothing envelope (LakePlanner.PlanWithShore).</summary>
        public const int BedSmoothRadius = 3;

        /// <summary>
        /// Shore shaving reaches this far (Euclidean) from water. A shaved column must also lie within LakeKeepOut - ShaveRadius - 1
        /// of a lake disc or RiverRadius - ShaveRadius - 1 of river water, so no swamp pocket can be within ShaveRadius of it.
        /// </summary>
        public const int ShaveRadius = 3;

        /// <summary>
        /// Shore slope multiplier from depth noise n in [0, 1]: 0.45..0.6 blocks of depth per block from the shore (was
        /// 0.8..1.0; flight test 3: one block per block read as a stepped funnel, 1455,2798). Small lakes are therefore
        /// shallow, as natural ponds are; the depth contours wander with the noise.
        /// </summary>
        public static double ShoreSlope(double n) => 0.45 + 0.15 * (double.IsNaN(n) ? 0.5 : Math.Clamp(n, 0.0, 1.0));

        /// <summary>Deepest water below the level from depth noise n in [0, 1]: round(MaxCut x (0.6..1.0)), at least 1. Undulating bottom.</summary>
        public static int BottomDepth(int maxCut, double n) =>
            Math.Max(1, (int)Math.Round(Math.Max(0, maxCut) * (0.6 + 0.4 * (double.IsNaN(n) ? 1.0 : Math.Clamp(n, 0.0, 1.0))), MidpointRounding.AwayFromZero));

        /// <summary>
        /// The shore ramp radius for a config: ceil(MaxCut / slowest ShoreSlope), so the ramp reaches the full depth before the
        /// search ends (no jump where "no dry column within the radius" begins); 1..MaxShoreRadius.
        /// </summary>
        public static int ShoreRadius(int maxCut) => Math.Clamp((int)Math.Ceiling(Math.Max(1, maxCut) / ShoreSlope(0.0)), 1, MaxShoreRadius);

        /// <summary>
        /// Water depth of a flooded column whose nearest dry column is <paramref name="distToDry"/> blocks away (Euclidean):
        /// floor(distance) x 1 block per block, at least 1. A shore column next to dry ground holds 1 block of water, the
        /// next 2, and so on until the column's own carve depth (MaxCut, faded) takes over: a sloping lake bed instead of
        /// a sheer underwater wall (flight test, lakes at -4796,2237 and -2194,-238). Pure.
        /// </summary>
        public static int ShoreDepth(double distToDry)
        {
            if (double.IsNaN(distToDry) || distToDry < 1) return 1;
            return (int)Math.Min(64, Math.Floor(distToDry));
        }

        /// <summary>
        /// The bed of a flooded column after the shore ramp: never deeper than <paramref name="bed"/> (the planned carve),
        /// never above the original ground s (lakes never fill), and the water at least ShoreDepth deep where the ground
        /// allows: max(bed, min(s, level - shoreDepth)). A natural basin floor below level - shoreDepth stays as it is. Pure.
        /// </summary>
        public static int ShoreBed(int s, int level, int bed, int shoreDepth) => Math.Max(bed, Math.Min(s, level - Math.Max(1, shoreDepth)));

        /// <summary>Heights a column's lake rule reads around it: RimSpan radius (the slope reads radius 1).</summary>
        public const int RimRadius = 2;

        /// <summary>
        /// Largest bounding-box side (blocks) of a below-level basin that may be flooded (fix round 1, finding 2). Chosen so
        /// that for any basin touching the 34x34 window -1..32 of some chunk, that chunk sees the whole basin, its closing
        /// ring, the rim/slope heights of every basin column (HeightGrid reaches 32 blocks out), the ocean buffer around
        /// them (same) and the water tops of the ring (the scan reaches ColumnExclusions.WideRadius out). So every chunk
        /// that looks at a basin column computes the identical basin and the identical decision. With the defaults
        /// (OceanBuffer 10) this is 22; the spec's 32x32 window is the upper bound.
        /// </summary>
        public static int BasinSpan(int oceanBuffer)
        {
            int b = Math.Clamp(oceanBuffer, 0, 32);
            int span = Math.Min(ColumnExclusions.WideRadius - 1, 32 - Math.Max(RimRadius, b));
            return Math.Clamp(span, 1, 32);
        }

        /// <summary>
        /// Expected share of the map inside lake discs if every feature were active: E[pi r^2] / cellSize^2 with r uniform
        /// in [minRadius, maxRadius] (as FeatureCells draws it): E[r^2] = (a^2 + ab + b^2) / 3. Capped at 1.
        /// With ResolvedConfig (CellSize = 4 x LakeMaxRadius, LakeMinRadius = LakeMaxRadius / 3) this is about 0.0945.
        /// </summary>
        public static double DiscCoverage(int cellSize, int minRadius, int maxRadius)
        {
            if (cellSize < 1) cellSize = 1;
            double a = Math.Max(0, minRadius), b = Math.Max(a, maxRadius);
            double er2 = (a * a + a * b + b * b) / 3.0;
            return Math.Min(1.0, Math.PI * er2 / ((double)cellSize * cellSize));
        }

        /// <summary>
        /// Share of lake features to activate so that LakeChance is the share of the MAP inside active lake discs (spec),
        /// not the share of features: min(1, lakeChance / DiscCoverage). The achievable ceiling is DiscCoverage itself
        /// (about 9.5% with the defaults): any LakeChance above it activates every feature. Water covers less than the
        /// disc area because only suitable ground inside a disc floods. NaN or &lt;= 0 gives 0.
        /// </summary>
        public static double LakeActiveShare(double lakeChance, int cellSize, int minRadius, int maxRadius)
        {
            if (double.IsNaN(lakeChance) || lakeChance <= 0) return 0.0;
            double cov = DiscCoverage(cellSize, minRadius, maxRadius);
            if (!(cov > 0)) return 0.0;
            return Math.Min(1.0, lakeChance / cov);
        }

        /// <summary>
        /// Message for the startup log and the /riverfeatures reply when <paramref name="lakeChance"/> is above the achievable
        /// ceiling <paramref name="ceiling"/> (= DiscCoverage): then every lake feature is already active and the knob does
        /// nothing further. States the ceiling and the effective share. Null when the value is within range (or NaN).
        /// Pure; the caller supplies the ceiling.
        /// </summary>
        public static string LakeChanceCeilingNote(double lakeChance, double ceiling)
        {
            if (double.IsNaN(lakeChance) || double.IsNaN(ceiling) || !(lakeChance > ceiling)) return null;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "LakeChance " + lakeChance.ToString("0.###", ic) + " is above the achievable ceiling of " + ceiling.ToString("0.###", ic)
                + " (the share of the map inside lake discs when every lake feature is active), so every feature is active and the effective share is "
                + ceiling.ToString("0.###", ic) + "; values above the ceiling change nothing.";
        }

        // ---------------------------------------------------------------- features

        /// <summary>
        /// Every feature whose cell lies in the cell range that can reach the world rectangle minX..maxX, minZ..maxZ
        /// (inclusive) expanded by maxRadius, and whose centre passes <paramref name="active"/> (the lake mask at the
        /// feature centre). Ordered by cell Z then X, so the order is the same in every chunk.
        /// Looking at all these cells (not only the column's own cell) makes membership correct even when a disc
        /// spills into a neighbouring cell. With ResolvedConfig.CellSize = 4 x LakeMaxRadius a disc never spills
        /// (FeatureCells keeps the centre Radius from the cell edge whenever cellSize &gt; 2 x Radius), but this
        /// does not rely on it.
        /// </summary>
        public static List<FeatureCell> FeaturesNear(long seed, int minX, int minZ, int maxX, int maxZ, int cellSize,
            int seaLevel, int maxRise, int minRadius, int maxRadius, Func<double, double, bool> active)
        {
            var list = new List<FeatureCell>();
            if (cellSize < 1) cellSize = 1;
            int r = Math.Max(0, Math.Max(minRadius, maxRadius)) + 1;
            int cx0 = FloorDiv((long)minX - r, cellSize), cx1 = FloorDiv((long)maxX + r, cellSize);
            int cz0 = FloorDiv((long)minZ - r, cellSize), cz1 = FloorDiv((long)maxZ + r, cellSize);
            // Guard against a pathological config (tiny cells): never enumerate more than 32x32 cells.
            if ((long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1) > 1024) return list;
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    var f = FeatureCells.At(seed, (int)((long)cx * cellSize), (int)((long)cz * cellSize), cellSize, seaLevel, maxRise, minRadius, maxRadius);
                    if (active == null || active(f.CenterX, f.CenterZ)) list.Add(f);
                }
            return list;
        }

        static int FloorDiv(long a, int b) => (int)Math.Floor(a / (double)b);

        /// <summary>
        /// The feature of WORLD block (x, z): among features whose disc (distance from the block centre x+0.5, z+0.5
        /// to the feature centre below Radius) contains it, the one with the smallest distance / Radius (first in list
        /// order on a tie). DistToEdge = Radius - distance (blocks inside the shoreline circle).
        /// </summary>
        public static FeatureHit BestFeature(IReadOnlyList<FeatureCell> feats, int x, int z)
        {
            FeatureHit best = default;
            double bestRel = double.MaxValue;
            if (feats == null) return best;
            double px = x + 0.5, pz = z + 0.5;
            for (int i = 0; i < feats.Count; i++)
            {
                var f = feats[i];
                if (!(f.Radius > 0)) continue;
                double dx = px - f.CenterX, dz = pz - f.CenterZ;
                if (dx * dx + dz * dz >= f.Radius * f.Radius) continue; // the shape lies inside the disc
                ShapeAt(f, px, pz, out double rho, out double limit);
                if (!(rho < limit)) continue;
                double rel = rho / limit;
                if (rel < bestRel) { bestRel = rel; best = new FeatureHit(true, f, (limit - rho) * f.Radius * Math.Min(1.0, f.Aspect)); }
            }
            return best;
        }

        /// <summary>
        /// The lake outline test at point (px, pz) (WORLD coordinates, block centre). rho = ellipse-normalised distance from the
        /// centre (the point is rotated by -Angle, the minor axis stretched by 1 / Aspect, divided by Radius); limit = 1 -
        /// Wobble x n with n in [0, 1] from two octaves of value noise at world coordinates. Inside when rho &lt; limit. Because
        /// Aspect &lt;= 1 and limit &lt;= 1 every inside point is closer than Radius to the centre (shape within the disc), so the
        /// disc-based bounds (cell placement, keep-out, coverage ceiling) still hold. Pure.
        /// </summary>
        public static void ShapeAt(in FeatureCell f, double px, double pz, out double rho, out double limit)
        {
            double aspect = f.Aspect > 0.05 && f.Aspect <= 1.0 ? f.Aspect : 1.0;
            double dx = px - f.CenterX, dz = pz - f.CenterZ;
            double ca = Math.Cos(f.Angle), sa = Math.Sin(f.Angle);
            double u = dx * ca + dz * sa, v = (-dx * sa + dz * ca) / aspect;
            rho = Math.Sqrt(u * u + v * v) / f.Radius;
            double wob = Math.Clamp(f.Wobble, 0.0, 0.9);
            if (wob <= 0) { limit = 1.0; return; }
            double wl = Math.Max(6.0, f.Radius * 0.7);
            double n = (2.0 * ValueNoise(f.ShapeSeed, px / wl, pz / wl, 1) + ValueNoise(f.ShapeSeed, px / (wl * 0.4), pz / (wl * 0.4), 2)) / 3.0;
            limit = 1.0 - wob * n;
        }

        /// <summary>Smooth value noise in [0, 1]: hashed lattice values, smoothstep-interpolated. Pure, continuous.</summary>
        public static double ValueNoise(long seed, double x, double z, int salt)
        {
            if (double.IsNaN(x) || double.IsNaN(z) || double.IsInfinity(x) || double.IsInfinity(z)) return 0.5;
            double fx0 = Math.Floor(x), fz0 = Math.Floor(z);
            int ix = (int)fx0, iz = (int)fz0;
            double fx = x - fx0, fz = z - fz0;
            double sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
            double a = Hash01(seed, ix, iz, salt), b = Hash01(seed, ix + 1, iz, salt);
            double c = Hash01(seed, ix, iz + 1, salt), d = Hash01(seed, ix + 1, iz + 1, salt);
            double top = a + (b - a) * sx, bot = c + (d - c) * sx;
            return top + (bot - top) * sz;
        }

        /// <summary>
        /// Share of the disc area the shaped outline covers, averaged over 64 features of this world (area-weighted), so the
        /// LakeChance calibration (LakeActiveShare) and its ceiling account for the shapes. Pure, deterministic, about 65k samples.
        /// </summary>
        public static double ShapeAreaFactor(long seed, int cellSize, int seaLevel, int maxRise, int minRadius, int maxRadius)
        {
            double shape = 0, disc = 0;
            const int G = 32;
            for (int cz = 0; cz < 8; cz++)
                for (int cx = 0; cx < 8; cx++)
                {
                    var f = FeatureCells.At(seed, cx * Math.Max(1, cellSize), cz * Math.Max(1, cellSize), cellSize, seaLevel, maxRise, minRadius, maxRadius);
                    if (!(f.Radius > 0)) continue;
                    for (int j = 0; j < G; j++)
                        for (int i = 0; i < G; i++)
                        {
                            double ox = ((i + 0.5) / G * 2 - 1) * f.Radius, oz = ((j + 0.5) / G * 2 - 1) * f.Radius;
                            if (ox * ox + oz * oz >= f.Radius * f.Radius) continue;
                            disc++;
                            ShapeAt(f, f.CenterX + ox, f.CenterZ + oz, out double rho, out double limit);
                            if (rho < limit) shape++;
                        }
                }
            return disc > 0 ? Math.Clamp(shape / disc, 0.05, 1.0) : 1.0;
        }

        /// <summary>True when WORLD block (x, z) is within Radius + extra of any feature centre (swamps keep out of this zone).</summary>
        /// <summary>A soil block code path: "soil-*" (vanilla soil, incl. its grass variants) or "forestfloor-*".</summary>
        public static bool IsSoil(string codePath)
        {
            if (string.IsNullOrEmpty(codePath)) return false;
            return codePath.StartsWith("soil-", StringComparison.Ordinal) || codePath.StartsWith("forestfloor-", StringComparison.Ordinal);
        }

        public static bool NearAnyFeature(IReadOnlyList<FeatureCell> feats, int x, int z, double extra)
        {
            if (feats == null) return false;
            double px = x + 0.5, pz = z + 0.5;
            for (int i = 0; i < feats.Count; i++)
            {
                var f = feats[i];
                double dx = px - f.CenterX, dz = pz - f.CenterZ, r = f.Radius + extra;
                if (dx * dx + dz * dz < r * r) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- lakes

        /// <summary>River-surface matching: next to river water the lake uses the river's surface Y, else the feature's fixed level.</summary>
        public static int LevelFor(int riverSurfaceY, int featureLevel) => riverSurfaceY >= 0 ? riverSurfaceY : featureLevel;

        /// <summary>
        /// max - min of the available heights (h != -1) within a (2*radius+1) square around (x, z); 0 when none.
        /// This is the per-column form of MaxRimVariation (spec: applied to the neighbouring heights actually available).
        /// </summary>
        public static int RimSpan(Func<int, int, int> height, int x, int z, int radius)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int h = height(x + dx, z + dz);
                    if (h == -1) continue;
                    if (h < lo) lo = h;
                    if (h > hi) hi = h;
                }
            return hi < lo ? 0 : hi - lo;
        }

        /// <summary>
        /// The per-column lake rule (independent of exclusions and neighbours; those are LakeFinal).
        /// Unknown height or existing water in the column (a Rivers channel: never filled, dammed or re-carved) -> skip.
        /// Level must lie in the lake band sea+2 .. sea+MaxRiseAboveSea (a river level outside it is not matched).
        /// TerrainGuard.LakeColumnAllowed (rise, slope, sea, MaxCut reach, LakeMinDepth reach) and the rim span
        /// (MaxRimVariation) lock the column. The cut aims at Level - MaxCut, is capped at MaxCut and faded to 0 at the
        /// disc edge (LimitCut + Fade over FadeMargin), so the bowl follows the terrain and the shore ramps out.
        /// A column whose faded bed stays at or above Level is shore: left dry and untouched. A column whose water would
        /// be shallower than LakeMinDepth (Level - Bed &lt; MinDepth) is left dry too (Shallow, fix round 1 finding 4):
        /// the step down to the first flooded column is under water, so it is not a wall, and the bank above the water
        /// stays within one block (BankNeighbourLow).
        /// </summary>
        public static LakeColumn LakeCandidate(int surfaceY, int existingWaterTop, int level, double slope, int rimSpan, double distToEdge, in LakeParams p)
        {
            if (surfaceY < 0) return LakeColumn.No(LakeSkip.Unknown, level);
            if (existingWaterTop >= 0) return LakeColumn.No(LakeSkip.RiverWater, level);
            if (level < p.SeaLevel + 2 || level > p.SeaLevel + p.MaxRise) return LakeColumn.No(LakeSkip.LevelOutOfRange, level);
            if (!TerrainGuard.LakeColumnAllowed(surfaceY, level, p.SeaLevel, p.OceanBuffer, p.MinDepth, p.MaxCut, p.MaxRise, slope, p.MaxSlope))
                return LakeColumn.No(LakeSkip.Protected, level);
            if (rimSpan > p.MaxRimVariation) return LakeColumn.No(LakeSkip.Protected, level);
            double fade = TerrainGuard.Fade(distToEdge, p.FadeMargin);
            int cut = TerrainGuard.LimitCut(surfaceY - (level - p.MaxCut), p.MaxCut, fade);
            int bed = surfaceY - cut;
            if (bed >= level) return LakeColumn.No(LakeSkip.Shore, level);
            if (level - bed < p.MinDepth) return LakeColumn.No(LakeSkip.Shallow, level);
            return new LakeColumn(true, level, cut, bed, LakeSkip.None);
        }

        /// <summary>
        /// May water at <paramref name="level"/> stand next to this neighbour? Unknown height: no (never pour water
        /// against missing data). Existing water: only if its top is exactly level (lake surface flush with the river).
        /// Known flooded (accepted basin): only at the same level. A candidate neighbour of a different level: no (it might
        /// be flooded to that level: a water step). Otherwise the neighbour's original ground must reach level.
        /// </summary>
        public static bool NeighbourHolds(int level, in LakeNeighbour n)
        {
            if (n.SurfaceY < 0) return false;
            if (n.WaterTop >= 0) return n.WaterTop == level;
            if (n.Flooded) return n.Level == level;
            if (n.Candidate && n.Level != level) return false;
            return n.SurfaceY >= level;
        }

        /// <summary>
        /// Bank rule (fix round 1, finding 1): a column whose ground is ABOVE level loses that ground when flooded, so it
        /// may flood only if this neighbour ends at most one block above the water: water at level, known flooded to
        /// level, or ground at most level + 1. A neighbour that might be flooded but is higher is refused too (its
        /// decision is not known here; conservative, deterministic). Use together with NeighbourHolds.
        /// </summary>
        public static bool BankNeighbourLow(int level, in LakeNeighbour n)
        {
            if (n.SurfaceY < 0) return false;
            if (n.WaterTop >= 0) return n.WaterTop == level;
            if (n.Flooded && n.Level == level) return true;
            return n.SurfaceY <= level + 1;
        }

        /// <summary>
        /// Final decision for an own column: candidate, not near a structure (RuinMargin), not within OceanBuffer of
        /// the sea, and every neighbour holds the water. Precedence of the reported reason follows ColumnExclusions
        /// (Structure, then Ocean), then Leak.
        /// </summary>
        public static LakeSkip LakeFinal(in LakeColumn c, bool nearStructure, bool nearOcean, bool allNeighboursHold)
        {
            if (!c.Candidate) return c.Reason == LakeSkip.None ? LakeSkip.Outside : c.Reason;
            if (nearStructure) return LakeSkip.Structure;
            if (nearOcean) return LakeSkip.Ocean;
            if (!allNeighboursHold) return LakeSkip.Leak;
            return LakeSkip.None;
        }

        /// <summary>Bed block codes in order of preference: sand on the shallow shore (depth &lt;= 1), gravel deeper; the other as fallback.</summary>
        public static string[] BedCodes(string rock, int depth)
        {
            if (string.IsNullOrEmpty(rock)) return Array.Empty<string>();
            return depth <= 1
                ? new[] { "sand-" + rock, "gravel-" + rock }
                : new[] { "gravel-" + rock, "sand-" + rock };
        }

        /// <summary>The rock name of a block code path "rock-&lt;name&gt;", or null.</summary>
        public static string RockOf(string codePath)
        {
            if (codePath == null || !codePath.StartsWith("rock-", StringComparison.Ordinal) || codePath.Length <= 5) return null;
            return codePath.Substring(5);
        }

        /// <summary>The first code for which exists() is true, or null (caller keeps the existing block).</summary>
        public static string FirstKnown(IEnumerable<string> codes, Func<string, bool> exists)
        {
            if (codes == null) return null;
            foreach (var c in codes)
                if (!string.IsNullOrEmpty(c) && exists(c)) return c;
            return null;
        }

        // ---------------------------------------------------------------- swamps

        /// <summary>
        /// 0 below SwampMinRain (rain must be at least the minimum), rising smoothly (smoothstep) to 1 over RainBand
        /// above it, so the swamp edge on a rain contour is a ramp, not a wall. NaN gives 0.
        /// </summary>
        public static double RainWeight(double rain, double minRain)
        {
            if (double.IsNaN(rain) || double.IsNaN(minRain) || rain < minRain) return 0.0;
            double t = (rain - minRain) / RainBand;
            if (t >= 1) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
        /// Euclidean distance (block offsets) to the nearest locked column within radius r of (lx, lz), or r + 1 when
        /// there is none. Swamps fade by TerrainGuard.Fade(LockDistance, FadeMargin); columns outside the swamp mask count
        /// as locked (fix round 1, finding 6), so the mask edge fades by exact distance, identical in every chunk.
        /// </summary>
        public static double LockDistance(Func<int, int, bool> isLocked, int lx, int lz, int r)
        {
            if (r < 0) r = 0;
            int best2 = int.MaxValue, r2 = r * r;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int d2 = dx * dx + dz * dz;
                    if (d2 > r2 || d2 >= best2) continue;
                    if (isLocked(lx + dx, lz + dz)) best2 = d2;
                }
            return best2 == int.MaxValue ? r + 1 : Math.Sqrt(best2);
        }

        /// <summary>The swamp's lock fade at (lx, lz): 0 when locked itself, else Fade(LockDistance, margin) (1 when margin is 0).</summary>
        public static double LockFade(Func<int, int, bool> isLocked, int lx, int lz, int margin)
        {
            if (isLocked(lx, lz)) return 0.0;
            if (margin <= 0) return 1.0;
            return TerrainGuard.Fade(LockDistance(isLocked, lx, lz, margin), margin);
        }

        /// <summary>
        /// Final swamp surface: flattened toward target (sea level + 1). Lowered by LimitCut(s - target, maxCut, w),
        /// raised by at most LimitFill(target - s, maxFill) scaled by w. Never moves past target.
        /// </summary>
        public static int SwampFinal(int s, int target, int maxCut, int maxFill, double w)
        {
            if (double.IsNaN(w) || w <= 0) return s;
            if (w > 1) w = 1;
            if (s > target) return s - TerrainGuard.LimitCut(s - target, maxCut, w);
            if (s < target) return s + (int)Math.Floor(TerrainGuard.LimitFill(target - s, maxFill) * w);
            return s;
        }

        /// <summary>
        /// May a swamp pocket (water at Y = finalY on ground finalY-1) stand next to this neighbour? Its height must be
        /// known; its lowest possible final surface must reach finalY (whatever the neighbour's own chunk decides);
        /// if it may itself become a pocket, its final surface must be exactly finalY (no water step); and its final surface
        /// may stand at most one block above the water (second flight test: pockets 2..3 blocks down in a hole read as pits).
        /// </summary>
        public static bool PocketNeighbourOk(int finalY, in SwampNeighbour n)
        {
            if (n.SurfaceY < 0) return false;
            if (n.MinFinal < finalY) return false;
            if (n.PocketCapable && n.ExpectedFinal != finalY) return false;
            if (n.ExpectedFinal > finalY + 1) return false;
            return true;
        }

        /// <summary>
        /// The lowest surface a swamp column may get at weight w: s - min(s - target, round(cutMax x w)); s when s &lt;= target.
        /// Monotonic in w, so a column's lowest at its rain-scaled weight is never below its lowest at the lock fade. Pure.
        /// </summary>
        public static int SwampLowest(int s, int target, int cutMax, double w)
        {
            if (s <= target || double.IsNaN(w) || w <= 0 || cutMax <= 0) return s;
            int cap = (int)Math.Round(cutMax * Math.Min(1.0, w), MidpointRounding.AwayFromZero);
            return s - Math.Min(s - target, cap);
        }

        /// <summary>One column of an ALREADY GENERATED neighbour chunk as the seam rule sees it.</summary>
        public readonly record struct SeamNeighbour(int Original, int Now, int Distance);

        /// <summary>
        /// Flight test 6 (1506,-3214): a swamp cut on one side of a chunk border and none on the other left a straight wall of
        /// the full cut. Each chunk plans the shared border from world data, but anything that differs between the two (an
        /// input one of them could not read) made them disagree. This fits an own column to neighbour columns that already
        /// exist, by their CURRENT ground (at most one block per block of distance):
        ///  - never cut more than d below a neighbour's current ground (f &gt;= now - d), but never above the own ground s;
        ///  - next to ground THIS MOD lowered (now &lt; original) where no natural step stood (|s - original| &lt;= d), lower the
        ///    own ground to at most now + d, by at most <paramref name="maxLower"/> below s.
        /// Returns the fitted surface. Pure.
        /// </summary>
        public static int SeamFit(int s, int f, int maxLower, IEnumerable<SeamNeighbour> neighbours)
        {
            if (s < 0 || neighbours == null) return f;
            int lowB = int.MinValue, highB = int.MaxValue;
            foreach (var n in neighbours)
            {
                if (n.Now < 0 || n.Distance <= 0) continue;
                lowB = Math.Max(lowB, n.Now - n.Distance);
                if (n.Original >= 0 && n.Now < n.Original && Math.Abs(s - n.Original) <= n.Distance)
                    highB = Math.Min(highB, n.Now + n.Distance);
            }
            if (f < lowB) f = Math.Min(s, lowB);
            if (f > highB) f = Math.Min(f, Math.Max(highB, s - Math.Max(0, maxLower)));
            return f;
        }

        /// <summary>
        /// Lower envelope at grid cell (gx, gz): min over cells within Manhattan radius r of grid[j] + distance (cells &lt; 0 are
        /// unknown and ignored), then raised to <paramref name="low"/>. Removes steps of more than one block per block by
        /// lowering the higher side only. Pure.
        /// </summary>
        public static int SmoothedSurface(int[] grid, int side, int gx, int gz, int r, int low)
        {
            int i = gz * side + gx;
            int env = grid[i];
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int d = Math.Abs(dx) + Math.Abs(dz);
                    if (d == 0 || d > r) continue;
                    int x = gx + dx, z = gz + dz;
                    if (x < 0 || z < 0 || x >= side || z >= side) continue;
                    int v = grid[z * side + x];
                    if (v < 0) continue;
                    if (v + d < env) env = v + d;
                }
            return Math.Max(low, env);
        }

        /// <summary>
        /// The configured plant codes that exist (exists() true), in order, without duplicates. Each unknown code is
        /// reported once through logUnknown and skipped; never throws.
        /// </summary>
        public static List<string> ResolvePlants(IEnumerable<string> codes, Func<string, bool> exists, Action<string> logUnknown)
        {
            var ok = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (codes == null) return ok;
            foreach (var raw in codes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string code = raw.Trim();
                if (!seen.Add(code)) continue;
                bool found;
                try { found = exists(code); } catch (Exception) { found = false; }
                if (found) ok.Add(code);
                else logUnknown?.Invoke(code);
            }
            return ok;
        }

        /// <summary>Deterministic hash of (seed, x, z, salt) to [0, 1). Pure, call-order independent.</summary>
        public static double Hash01(long seed, int x, int z, int salt)
        {
            unchecked
            {
                ulong h = Mix((ulong)seed ^ (0xA0761D6478BD642FUL * (ulong)(salt + 1)));
                h = Mix(h ^ (ulong)(uint)x);
                h = Mix(h ^ ((ulong)(uint)z << 32) ^ 0xE7037ED1A0B428DBUL);
                return (h >> 11) * (1.0 / 9007199254740992.0);
            }
        }

        static ulong Mix(ulong z)
        {
            unchecked
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
