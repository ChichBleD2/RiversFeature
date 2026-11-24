using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace RiverFeatures.Rivers
{
    /// <summary>
    /// Builds <see cref="TerrainModel"/> from the live game: the landform map generator (GenMaps.landformsGen, the same layer the
    /// game uses for region landform maps, in map cells of RegionSize / noiseSizeLandform blocks, averaged over 3x3 cells like the
    /// game's smoothing), each landform's terrainYKey thresholds, Rivers' riverlandform, Rivers' valley noise and its valley
    /// config. Everything is read, nothing is changed. If any piece is missing, <see cref="Ok"/> is false and the terrain checks
    /// are skipped (one log line).
    /// </summary>
    public sealed class GameTerrain
    {
        public bool Ok { get; private set; }
        public string Missing { get; private set; }

        MapLayerBase landformsGen;
        double cellBlocks;
        int mapSizeY, seaLevel;
        float[][] byIndex;
        float[] river;
        object valleyNoise;
        MethodInfo getNoise;
        double noiseExpansion = 1.5, vMin = 0.4, vMax = 1.0, maxValley = 150;
        readonly ConcurrentDictionary<long, float[]> cellCache = new ConcurrentDictionary<long, float[]>();
        readonly object genLock = new object();

        public static GameTerrain Bind(ICoreServerAPI api, Assembly rivers, object riverConfig)
        {
            var g = new GameTerrain();
            try
            {
                var maps = api.ModLoader.GetModSystem<GenMaps>();
                g.landformsGen = maps?.landformsGen;
                if (g.landformsGen == null || maps.noiseSizeLandform <= 0) { g.Missing = "GenMaps.landformsGen"; return g; }
                g.cellBlocks = api.WorldManager.RegionSize / (double)maps.noiseSizeLandform;
                g.oceanGen = maps.oceanGen;
                g.climateGen = maps.climateGen;
                if (g.climateGen != null && maps.noiseSizeClimate > 0) g.climateCellBlocks = api.WorldManager.RegionSize / (double)maps.noiseSizeClimate;
                if (g.oceanGen != null && maps.noiseSizeOcean > 0) g.oceanCellBlocks = api.WorldManager.RegionSize / (double)maps.noiseSizeOcean;
                g.mapSizeY = api.WorldManager.MapSizeY;
                g.seaLevel = TerraGenConfig.seaLevel;
                var lf = NoiseLandforms.landforms;
                if (lf?.LandFormsByIndex == null) { g.Missing = "NoiseLandforms.landforms"; return g; }
                g.byIndex = lf.LandFormsByIndex.Select(v => v == null ? null : TerrainModel.KeyThresholds(v.TerrainYKeyPositions, v.TerrainYKeyThresholds, g.mapSizeY)).ToArray();
                var rv = lf.LandFormsByIndex.FirstOrDefault(v => v?.Code?.Path == "riverlandform");
                g.river = rv == null ? null : TerrainModel.KeyThresholds(rv.TerrainYKeyPositions, rv.TerrainYKeyThresholds, g.mapSizeY);

                var api2 = rivers.GetType("Rivers.RiversApi");
                var vf = api2?.GetField("valleyNoise", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                g.valleyNoise = vf?.GetValue(null);
                g.getNoise = g.valleyNoise?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "GetNoise" && m.GetParameters().Length == 2).OrderBy(m => m.GetParameters()[0].ParameterType == typeof(double) ? 0 : 1).FirstOrDefault();
                if (riverConfig != null)
                {
                    var t = riverConfig.GetType();
                    object F(string n) => t.GetField(n)?.GetValue(riverConfig);
                    if (F("noiseExpansion") is float ne) g.noiseExpansion = ne;
                    if (F("valleyStrengthMin") is float a) g.vMin = a;
                    if (F("valleyStrengthMax") is float b) g.vMax = b;
                    if (F("maxValleyWidth") is double mv) g.maxValley = mv;
                }
                g.Ok = true;
            }
            catch (Exception e) { g.Missing = e.GetType().Name + ": " + e.Message; }
            return g;
        }

        /// <summary>Drops cached landform cells (called after each Rivers region; the map is deterministic, so this only bounds memory).</summary>
        public void ClearCache() => cellCache.Clear();

        public TerrainModel Model(double noiseShare) => new TerrainModel
        {
            SeaLevel = seaLevel, River = river, MaxValleyWidth = maxValley, NoiseShare = noiseShare,
            Land = LandAt, Strength = StrengthAt,
        };

        MapLayerBase climateGen;
        double climateCellBlocks;
        readonly object climateLock = new object();

        /// <summary>
        /// Rain (green channel / 255) and temperature (red channel, the game's own conversion at sea level,
        /// Climate.GetScaledAdjustedTemperatureFloat) at a world column from the climate map layer; Unknown when unavailable.
        /// </summary>
        public ClimateSample ClimateAt(double x, double z)
        {
            if (climateGen == null || !(climateCellBlocks > 0)) return ClimateSample.Unknown;
            int cx = (int)Math.Floor(x / climateCellBlocks), cz = (int)Math.Floor(z / climateCellBlocks);
            int[] r;
            lock (climateLock) r = climateGen.GenLayer(cx, cz, 1, 1);
            if (r == null || r.Length == 0) return ClimateSample.Unknown;
            double rain = ((r[0] >> 8) & 0xFF) / 255.0;
            double temp = double.NaN;
            try { temp = Vintagestory.API.Common.Climate.GetScaledAdjustedTemperatureFloat((r[0] >> 16) & 0xFF, 0); } catch (Exception) { }
            return new ClimateSample(rain, temp);
        }

        /// <summary>This terrain's climate map as a climate source (the default when no mod registered one).</summary>
        public IClimateSource AsClimateSource() => new MapClimate(this);

        sealed class MapClimate : IClimateSource
        {
            readonly GameTerrain t;
            public MapClimate(GameTerrain t) { this.t = t; }
            public string Name => "game climate map";
            public ClimateSample At(double x, double z) => t.ClimateAt(x, z);
        }

        MapLayerBase oceanGen;
        double oceanCellBlocks;
        readonly ConcurrentDictionary<long, int> oceanCache = new ConcurrentDictionary<long, int>();
        readonly object oceanLock = new object();

        /// <summary>Whether the ocean map is available (GenMaps.oceanGen, which Continental Worlds replaces through GetOceanMapGen).</summary>
        public bool HasOcean => oceanGen != null && oceanCellBlocks > 0;

        /// <summary>
        /// Oceanicity 0..255 at a world column (the game's ocean map layer, bilinear between map cells, as the game lerps it
        /// per chunk); NaN when unavailable. Rivers counts a zone as ocean above its oceanThreshold (30).
        /// </summary>
        public double OceanAt(double x, double z)
        {
            if (!HasOcean) return double.NaN;
            double fx = x / oceanCellBlocks - 0.5, fz = z / oceanCellBlocks - 0.5;
            int cx = (int)Math.Floor(fx), cz = (int)Math.Floor(fz);
            double tx = fx - cx, tz = fz - cz;
            double a = OceanCell(cx, cz), b = OceanCell(cx + 1, cz), c = OceanCell(cx, cz + 1), d = OceanCell(cx + 1, cz + 1);
            return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
        }

        int OceanCell(int cx, int cz)
        {
            long key = ((long)cx << 32) ^ (uint)cz;
            if (oceanCache.TryGetValue(key, out int v)) return v;
            int[] r;
            lock (oceanLock) r = oceanGen.GenLayer(cx, cz, 1, 1);
            v = r != null && r.Length > 0 ? r[0] & 0xFF : 0;
            if (oceanCache.Count > 200000) oceanCache.Clear();
            oceanCache[key] = v;
            return v;
        }

        /// <summary>Rivers' valley strength at a world column (RiversApi.ModifyLandformWeights, read from IL).</summary>
        double StrengthAt(double x, double z)
        {
            double n = 0;
            if (getNoise != null)
            {
                var pt = getNoise.GetParameters()[0].ParameterType;
                object X = Convert.ChangeType(pt == typeof(int) ? Math.Floor(x) : x, pt), Z = Convert.ChangeType(pt == typeof(int) ? Math.Floor(z) : z, pt);
                n = Convert.ToDouble(getNoise.Invoke(valleyNoise, new[] { X, Z }));
            }
            n = Math.Clamp(n * noiseExpansion, -1, 1);
            double t = (n + 1) / 2;
            double s = (1 - vMin) + t * ((1 - vMax) - (1 - vMin));
            return s < 0.02 ? 0.02 : s;
        }

        /// <summary>Average per-Y solid probability over the 3x3 landform map cells around a world column.</summary>
        float[] LandAt(double x, double z)
        {
            int cx = (int)Math.Floor(x / cellBlocks), cz = (int)Math.Floor(z / cellBlocks);
            long key = ((long)cx << 32) ^ (uint)cz;
            return cellCache.GetOrAdd(key, _ =>
            {
                int[] idx;
                lock (genLock) idx = landformsGen.GenLayer(cx - 1, cz - 1, 3, 3);
                var sum = new float[mapSizeY];
                int used = 0;
                foreach (int i in idx)
                {
                    if (i < 0 || i >= byIndex.Length || byIndex[i] == null) continue;
                    var t = byIndex[i];
                    for (int y = 0; y < mapSizeY; y++) sum[y] += t[y];
                    used++;
                }
                if (used == 0) return null;
                for (int y = 0; y < mapSizeY; y++) sum[y] /= used;
                return sum;
            });
        }
    }
}
