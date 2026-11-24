using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    /// <summary>
    /// /riverfeatures show | set &lt;key&gt; &lt;value&gt; | reload, for players with the controlserver privilege.
    /// All parsing and validation is ConfigEdit / TerrainWatersConfig (reflection over the config type), so the command stays in sync
    /// with the settings by construction. Bad input gets a usage or error message, never an exception.
    /// </summary>
    public static class ConfigCommands
    {
        const string NewChunksOnly = "Changes affect newly generated chunks only; chunks that are already generated are unchanged.";
        const string SetRewrites = "Note: set rewrites riverfeatures-lakes.json from the loaded settings, so hand edits made since the last start or reload are overwritten; run reload first if you edited the file by hand.";
        const string Usage = "Usage: /riverfeatures show | /riverfeatures set <key> <value> | /riverfeatures reload. " + SetRewrites;

        public static void Register(ICoreServerAPI api, ConfigStore store)
        {
            var p = api.ChatCommands.Parsers;
            api.ChatCommands
                .Create("riverfeatures")
                .WithDescription("Show or change the River Features terrain settings (riverfeatures-lakes.json: swamps, plain lakes).")
                .WithAdditionalInformation(Usage + "\n" + NewChunksOnly)
                .WithExamples("/riverfeatures show", "/riverfeatures set LakeChance 0.1", "/riverfeatures reload")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("action"), p.OptionalWord("key"), p.OptionalAll("value"))
                .HandleWith(args => Handle(store, args.Parsers.Count > 0 ? args[0] as string : null,
                    args.Parsers.Count > 1 ? args[1] as string : null, args.Parsers.Count > 2 ? args[2] as string : null));
        }

        /// <summary>The command logic without game types except the result; never throws.</summary>
        static TextCommandResult Handle(ConfigStore store, string action, string key, string value)
        {
            try
            {
                switch ((action ?? "").Trim().ToLowerInvariant())
                {
                    case "show":
                        return TextCommandResult.Success(
                            "River Features terrain settings (riverfeatures-lakes.json: swamps, plain lakes; distances and heights are for a 256-high world and are scaled to your world). Every key of both config files, with default, range and meaning: ModConfig/riverfeatures-settings.txt.\n"
                            + string.Join("\n", ConfigEdit.Describe(store.Current)) + "\n" + NewChunksOnly);

                    case "set":
                        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                            return TextCommandResult.Error("Usage: /riverfeatures set <key> <value>. Keys: " + string.Join(", ", ConfigEdit.KeyNames()) + ". " + SetRewrites);
                        if (!store.TrySet(key, value, out var msg)) return TextCommandResult.Error(msg);
                        var set = new System.Text.StringBuilder(msg).Append(' ').Append(NewChunksOnly).Append(' ').Append(SetRewrites);
                        return TextCommandResult.Success(Finish(store, set.ToString()));

                    case "reload":
                        if (!store.Reload(out var rmsg)) return TextCommandResult.Error(rmsg);
                        return TextCommandResult.Success(Finish(store,
                            rmsg + " Reload covers every setting for newly generated chunks, including Enabled, PlainLakes and the swamp chance. "
                            + NewChunksOnly));

                    default:
                        return TextCommandResult.Error(Usage);
                }
            }
            catch (Exception e)
            {
                return TextCommandResult.Error("riverfeatures command failed: " + e.Message + ". " + Usage);
            }
        }

        /// <summary>Applies the stored config to worldgen and appends what the system says could not be applied live.</summary>
        static string Finish(ConfigStore store, string reply)
        {
            string note;
            try { note = store.ApplyLive?.Invoke(); }
            catch (Exception e) { note = "could not apply to worldgen (" + e.Message + "); the previous settings stay active for new chunks."; }
            return string.IsNullOrEmpty(note) ? reply : reply + " " + note;
        }
    }
}
