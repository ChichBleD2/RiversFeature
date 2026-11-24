using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RiverFeatures
{
    /// <summary>
    /// Every river whose network River Features finished this session, with its main-stem length, for /rffind river
    /// (checking the River length setting in the world). Recorded when Rivers sizes a river (after headwater trims and
    /// branch spacing, before deltas and oxbows are added), once per river: the first build of a region only (region-rebuild
    /// deduplication) and keyed by the mouth position as a second guard. Bounded (20000 rivers).
    /// </summary>
    public static class RiverLog
    {
        /// <summary>World block coordinates; Length along the main stem's nodes in blocks.</summary>
        public readonly record struct River(int MouthX, int MouthZ, int SourceX, int SourceZ, int Length, int StemNodes, int TotalNodes);

        static readonly ConcurrentDictionary<(int, int), River> rivers = new ConcurrentDictionary<(int, int), River>();
        public const int MaxRivers = 20000;

        /// <summary>Records a river unless one with the same mouth is already recorded. Returns whether it was added.</summary>
        public static bool Add(River r)
        {
            if (rivers.Count >= MaxRivers) return false;
            return rivers.TryAdd((r.MouthX, r.MouthZ), r);
        }

        public static void Clear() => rivers.Clear();
        public static int Count => rivers.Count;
        public static List<River> All() => rivers.Values.ToList();

        /// <summary>Rivers with min &lt;= Length &lt;= max (either bound may be null). Pure.</summary>
        public static List<River> Filter(IEnumerable<River> all, int? min, int? max) =>
            all.Where(r => (min == null || r.Length >= min) && (max == null || r.Length <= max)).ToList();

        /// <summary>"N rivers, main stem min / avg / max X / Y / Z blocks" (or "no rivers"). Pure.</summary>
        public static string Summary(IReadOnlyCollection<River> list)
        {
            if (list.Count == 0) return "no rivers";
            return list.Count + (list.Count == 1 ? " river" : " rivers") + ", main stem min / avg / max "
                + list.Min(r => r.Length) + " / " + (int)Math.Round(list.Average(r => r.Length)) + " / " + list.Max(r => r.Length) + " blocks";
        }

        /// <summary>
        /// Parses the optional bounds of /rffind river: none, a minimum, or a range (in either order). Accepts "8000",
        /// "8k", "8.5k". Returns false with an error message for anything else. Pure.
        /// </summary>
        public static bool TryParseRange(string a, string b, out int? min, out int? max, out string error)
        {
            min = max = null; error = null;
            if (string.IsNullOrWhiteSpace(a)) return true;
            if (!TryBlocks(a, out int x)) { error = "'" + a + "' is not a length in blocks (e.g. 8000 or 8k)"; return false; }
            if (string.IsNullOrWhiteSpace(b)) { min = x; return true; }
            if (!TryBlocks(b, out int y)) { error = "'" + b + "' is not a length in blocks (e.g. 9000 or 9k)"; return false; }
            min = Math.Min(x, y); max = Math.Max(x, y);
            return true;
        }

        static bool TryBlocks(string s, out int v)
        {
            v = 0; s = s.Trim().ToLowerInvariant();
            double k = 1;
            if (s.EndsWith("k")) { k = 1000; s = s.Substring(0, s.Length - 1); }
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < 0 || d * k > 1e7) return false;
            v = (int)Math.Round(d * k);
            return true;
        }

        /// <summary>
        /// Rivers whose mouth or source lies within this many blocks of the player count as "near you" for /rffind river.
        /// Rivers builds a whole region's network (32768 blocks wide) when its first chunks generate, so after a long
        /// teleport the rivers around the new spot appear as soon as chunks load there.
        /// </summary>
        public const int NearRadius = 24000;

        /// <summary>Distance (blocks) from (px, pz) to the river's mouth or source, whichever is nearer. Pure.</summary>
        public static double Distance(River r, int px, int pz)
        {
            double dm = Math.Sqrt((double)(r.MouthX - px) * (r.MouthX - px) + (double)(r.MouthZ - pz) * (r.MouthZ - pz));
            double ds = Math.Sqrt((double)(r.SourceX - px) * (r.SourceX - px) + (double)(r.SourceZ - pz) * (r.SourceZ - pz));
            return Math.Min(dm, ds);
        }

        /// <summary>
        /// The /rffind river reply (pure). Everything is relative to the player at (px, pz): only rivers within
        /// <see cref="NearRadius"/> of the player (mouth or source) are listed, unless <paramref name="everywhere"/>
        /// ('/rffind river all'). No bounds: the longest of those; bounds: the matching ones nearest the player. At most
        /// <paramref name="take"/> lines. Positions as the other /rffind lines: absolute X,Z and map X,Z (relative to the map
        /// centre, as the HUD shows); distances from the player. When nothing is recorded near the player yet (a far teleport
        /// into ungenerated land), the reply says so instead of listing far-off rivers.
        /// </summary>
        public static string Reply(IReadOnlyCollection<River> all, int? min, int? max, int px, int pz, int mapCentreX, int mapCentreZ,
            bool everywhere = false, int take = 8)
        {
            var sb = new StringBuilder();
            double Dist(River r) => Distance(r, px, pz);
            if (all.Count == 0)
                return "No rivers recorded this session yet. Rivers are recorded when Rivers builds a region (when its first chunks generate): explore, then ask again.";
            var scope = everywhere ? all.ToList() : all.Where(r => Dist(r) <= NearRadius).ToList();
            string where = everywhere ? "this session (everywhere)" : "within " + NearRadius + " blocks of you (" + px + "," + pz + ", map " + (px - mapCentreX) + ", " + (pz - mapCentreZ) + ")";
            sb.Append("Rivers ").Append(where).Append(": ").Append(Summary(scope)).Append('.');
            if (!everywhere) sb.Append(" Whole session: ").Append(Summary(all)).Append('.');
            if (scope.Count == 0)
            {
                var nearest = all.OrderBy(Dist).First();
                return sb.Append("\nNo river has been generated near you yet: Rivers builds a region's rivers when its first chunks generate, so look around here (let chunks load) and ask again.")
                    .Append(" The nearest recorded river is ").Append((int)Dist(nearest)).Append(" blocks away; '/rffind river all' lists the whole session.").ToString();
            }
            bool ranged = min != null || max != null;
            var hits = ranged ? Filter(scope, min, max) : scope;
            if (ranged)
            {
                sb.Append("\nMain stem ").Append(min != null ? min.ToString() : "0").Append(max != null ? ".." + max : "+").Append(" blocks")
                  .Append(everywhere ? "" : " near you").Append(": ").Append(Summary(hits)).Append('.');
                if (hits.Count == 0) return sb.ToString();
                hits = hits.OrderBy(Dist).Take(take).ToList();
                sb.Append(" Nearest:");
            }
            else
            {
                hits = hits.OrderByDescending(r => r.Length).ThenBy(Dist).Take(take).ToList();
                sb.Append(" Longest:");
            }
            foreach (var r in hits)
            {
                double dm = Math.Sqrt((double)(r.MouthX - px) * (r.MouthX - px) + (double)(r.MouthZ - pz) * (r.MouthZ - pz));
                double ds = Math.Sqrt((double)(r.SourceX - px) * (r.SourceX - px) + (double)(r.SourceZ - pz) * (r.SourceZ - pz));
                sb.Append('\n').Append(r.Length).Append(" blocks (").Append(r.StemNodes).Append(" stem nodes, ").Append(r.TotalNodes).Append(" total)")
                  .Append(" mouth ").Append(r.MouthX).Append(',').Append(r.MouthZ)
                  .Append(" (map ").Append(r.MouthX - mapCentreX).Append(", ").Append(r.MouthZ - mapCentreZ).Append("), ").Append((int)dm).Append(" blocks away")
                  .Append("; source ").Append(r.SourceX).Append(',').Append(r.SourceZ)
                  .Append(" (map ").Append(r.SourceX - mapCentreX).Append(", ").Append(r.SourceZ - mapCentreZ).Append("), ").Append((int)ds).Append(" blocks away");
            }
            return sb.ToString();
        }
    }
}
