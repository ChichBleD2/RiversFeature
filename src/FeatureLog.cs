using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    /// <summary>
    /// Where River Features placed things this session, for /rffind (flight test 10: "I can't find deltas or swamps").
    /// Deltas and oxbows are logged when Rivers builds a region's network (positions known before the chunks exist);
    /// swamps when a chunk column with at least 64 swamp columns is generated. Bounded (oldest dropped past 50000).
    /// </summary>
    public static class FeatureLog
    {
        public readonly record struct Entry(int X, int Z, string Kind);
        static readonly ConcurrentQueue<Entry> entries = new ConcurrentQueue<Entry>();

        public static void Add(double x, double z, string kind)
        {
            entries.Enqueue(new Entry((int)Math.Round(x), (int)Math.Round(z), kind));
            while (entries.Count > 50000 && entries.TryDequeue(out _)) { }
        }

        static readonly ConcurrentDictionary<(string, int, int), byte> cells = new ConcurrentDictionary<(string, int, int), byte>();

        /// <summary>Adds an entry unless this kind already has one in the same cell (one entry per lake that spans chunks).</summary>
        public static void AddOncePerCell(double x, double z, string kind, int cell)
        {
            var key = (kind, (int)Math.Floor(x / cell), (int)Math.Floor(z / cell));
            if (cells.TryAdd(key, 0)) Add(x, z, kind);
        }

        public static void Clear() { while (entries.TryDequeue(out _)) { } cells.Clear(); }

        /// <summary>
        /// Whether an entry kind answers a query (pure): the kind starts with the query, or every dash-separated word of the
        /// query is one of the kind's words. Deltas are logged as "delta-&lt;shape&gt;-&lt;look&gt;", so "delta", "delta-shallow",
        /// "delta-marshy" and "delta-shallow-marshy" all find a shallow marshy delta.
        /// </summary>
        public static bool Matches(string kind, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (kind.StartsWith(query, StringComparison.Ordinal)) return true;
            var words = kind.Split('-');
            foreach (var q in query.Split('-', StringSplitOptions.RemoveEmptyEntries))
                if (Array.IndexOf(words, q) < 0) return false;
            return true;
        }

        /// <summary>Query aliases (pure): plurals and other spellings people type.</summary>
        public static string Normalize(string kind)
        {
            kind = (kind ?? "delta").Trim().ToLowerInvariant();
            switch (kind)
            {
                case "river-dry": case "dryriver": case "wadi": return "dry-river";
                case "lakes": return "lake";
                case "source-lake": case "sourcelake": return "lake-source";
                case "deltas": return "delta";
                case "traces": case "past": return "trace";
                case "oxbows": return "oxbow";
                case "swamps": case "marsh": return "swamp";
                case "marsh-delta": case "swampy": return "delta-marshy";
                case "dry-delta": return "delta-dry";
                default: return kind;
            }
        }

        /// <summary>Every kind logged this session with its count, for /rffind kinds.</summary>
        public static string KindsSummary()
        {
            var groups = entries.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count()).ToList();
            return groups.Count == 0 ? "nothing logged yet" : string.Join(", ", groups);
        }

        const string KindsHelp = "river [all] [minBlocks] [maxBlocks] (by main-stem length, rivers within 24000 blocks of you; no numbers = the longest; all = the whole session), lake (source lakes; plain lakes are deferred in 1.0.0), lake-source (-oval, -finger, -winding, -lobed, -teardrop), delta (shape: small, carving, shallow; look: sandy, marshy, overgrown, dry, intermittent, rocky; e.g. delta-marshy, delta-shallow-dry), oxbow, oxbow-dry, dry-river, trace (-channel, -backwater, -pocket), swamp; '/rffind kinds' lists what was logged";

        public static void Register(ICoreServerAPI api)
        {
            api.ChatCommands.Create("rffind")
                .WithDescription("River Features: the nearest source lakes, deltas (by shape or look), oxbows, dry rivers, traces of the past or swamps placed this session; '/rffind river [min] [max]' finds rivers by main-stem length. '/rffind kinds' lists the kinds logged.")
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("kind"), api.ChatCommands.Parsers.OptionalWord("min"), api.ChatCommands.Parsers.OptionalWord("max"), api.ChatCommands.Parsers.OptionalWord("extra"))
                .HandleWith(args =>
                {
                    try
                    {
                        string kind = Normalize(args[0] as string);
                        if (kind == "kinds" || kind == "list") return TextCommandResult.Success("Logged this session: " + KindsSummary() + "; rivers " + RiverLog.Count + ".\nQueries: " + KindsHelp + ".");
                        var at = args.Caller.Player.Entity.Pos.AsBlockPos;
                        if (kind == "river" || kind == "rivers")
                        {
                            // /rffind river [min] [max]: rivers by main-stem length (blocks), longest first or nearest in range.
                            // Rivers near the player by default; '/rffind river all [min] [max]' covers the whole session.
                            string a1 = args[1] as string, a2 = args[2] as string;
                            bool everywhere = string.Equals(a1?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
                            if (everywhere) { a1 = a2; a2 = args[3] as string; }
                            if (!RiverLog.TryParseRange(a1, a2, out int? min, out int? max, out string err))
                                return TextCommandResult.Error("Usage: /rffind river [all] [minBlocks] [maxBlocks], e.g. /rffind river 8000 or /rffind river 5000 9000: " + err + ".");
                            return TextCommandResult.Success(RiverLog.Reply(RiverLog.All(), min, max, at.X, at.Z,
                                api.WorldManager.MapSizeX / 2, api.WorldManager.MapSizeZ / 2, everywhere));
                        }
                        var hits = entries.Where(e => Matches(e.Kind, kind))
                            .Select(e => (e, d: Math.Sqrt((double)(e.X - at.X) * (e.X - at.X) + (double)(e.Z - at.Z) * (e.Z - at.Z))))
                            .OrderBy(t => t.d).Take(8).ToList();
                        int total = entries.Count(e => Matches(e.Kind, kind));
                        if (hits.Count == 0) return TextCommandResult.Success("No '" + kind + "' logged this session (kinds: " + KindsHelp + "). Explore or restart to log new regions.");
                        int ox = api.WorldManager.MapSizeX / 2, oz = api.WorldManager.MapSizeZ / 2; // the HUD shows positions relative to the map centre
                        var sb = new StringBuilder().Append(total).Append(" '").Append(kind).Append("' this session; nearest (absolute X,Z, then map X,Z as the HUD shows):\n");
                        foreach (var (e, d) in hits)
                            sb.Append(e.Kind).Append(" at ").Append(e.X).Append(',').Append(e.Z)
                              .Append(" (map ").Append(e.X - ox).Append(", ").Append(e.Z - oz).Append("), ").Append((int)d).Append(" blocks away\n");
                        return TextCommandResult.Success(sb.ToString());
                    }
                    catch (Exception ex) { return TextCommandResult.Error("rffind failed: " + ex.Message); }
                });
        }
    }
}
