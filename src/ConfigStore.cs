using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace RiverFeatures
{
    /// <summary>
    /// Loads and saves ModConfig/riverfeatures-lakes.json. Problems are logged, never thrown.
    /// Unknown keys (misspelled, wrong case, another mod's) are named in one log line and KEPT: every rewrite writes the
    /// settings followed by the unknown keys exactly as read (TerrainWatersConfig.ToFileObject), so a typo is never silently
    /// erased. A file that is not a JSON object (invalid JSON, empty, `[1]`, `"x"`, `null`) is malformed: it is copied to
    /// riverfeatures-lakes.json.bak before defaults are written, and left untouched if the copy fails.
    /// Every dynamic message goes to the logger as an argument ("{0}"), never as the format string, because 1.22.7's
    /// Logger.Warning(string) forwards the text as a composite format and braces in exception text or JSON could throw.
    /// </summary>
    public class ConfigStore
    {
        const string FileName = "riverfeatures-lakes.json";
        readonly ICoreAPI api;

        TerrainWatersConfig current = new TerrainWatersConfig();
        /// <summary>Unknown keys of the last file read (kept on every Save); null when none.</summary>
        JObject keep;
        /// <summary>The active config. Replaced as a whole (never edited in place), so readers on other threads see a consistent object.</summary>
        public TerrainWatersConfig Current { get { return current; } private set { current = value; } }

        /// <summary>
        /// Set by TerrainWatersSystem: pushes the current config into the worldgen handler for chunks generated from now on.
        /// Returns a short note for the player (what could not be applied live, or an error), or null when all is applied.
        /// </summary>
        public Func<string> ApplyLive;

        public ConfigStore(ICoreAPI api) { this.api = api; }

        string PathOf()
        {
            string dir = api.GetOrCreateDataPath("ModConfig"), p = Path.Combine(dir, FileName);
            return p;
        }

        void Warn(string msg)
        {
            try { api.Logger.Warning("[RiverFeatures] {0}", msg); } catch (Exception) { /* logging must never abort loading */ }
        }

        /// <summary>
        /// Reads the file WITHOUT logging, writing or backing up anything (an early peek for systems that start before TerrainWatersSystem, so the repairs and
        /// their log lines happen once, in TerrainWatersSystem's Load). Any problem gives the defaults. Never throws.
        /// </summary>
        public TerrainWatersConfig Peek()
        {
            try
            {
                string p = PathOf();
                return File.Exists(p) ? TerrainWatersConfig.Parse(File.ReadAllText(p), null) : new TerrainWatersConfig();
            }
            catch (Exception) { return new TerrainWatersConfig(); }
        }

        /// <summary>Back to the defaults (used by the caller when Load itself failed unexpectedly).</summary>
        public void ResetToDefaults() { Current = new TerrainWatersConfig(); keep = null; }

        /// <summary>Loads the file; never throws (an unexpected failure gives the defaults and one log line).</summary>
        public void Load()
        {
            try { LoadInner(); }
            catch (Exception e)
            {
                ResetToDefaults();
                Warn("could not load config (" + e.Message + "); using defaults for this session");
            }
        }

        void LoadInner()
        {
            string text = null;
            bool readFailed = false;
            string p = null;
            try
            {
                p = PathOf();
                if (File.Exists(p)) text = File.ReadAllText(p);
            }
            catch (Exception e)
            {
                readFailed = true;
                Warn("could not read config (" + e.Message + "); using defaults for this session and leaving the file untouched");
            }

            if (readFailed) { Current = new TerrainWatersConfig(); return; }

            Current = text == null ? new TerrainWatersConfig() : TerrainWatersConfig.Parse(text, Warn);
            keep = null;

            // Rewrite when missing, repaired, or missing newer keys so the file always shows every setting. Unknown keys
            // are kept (ToFileObject), so a file whose only difference is an unknown key is not rewritten at all.
            bool rewrite = text == null;
            if (!rewrite)
            {
                JObject parsed = null;
                try { parsed = JToken.Parse(text) as JObject; } catch (Exception) { parsed = null; }
                if (parsed == null)
                {
                    // Invalid JSON or not an object ([1], "x", null): malformed. Back it up first; never overwrite hand edits
                    // we could not preserve.
                    if (!BackUp(p)) return;
                    rewrite = true;
                }
                else
                {
                    keep = parsed;
                    rewrite = !JToken.DeepEquals(parsed, TerrainWatersConfig.ToFileObject(Current, keep));
                }
            }
            if (rewrite) Save();
        }

        bool BackUp(string p)
        {
            try
            {
                File.Copy(p, p + ".bak", true);
                Warn("config was not a valid JSON object; copied it to " + FileName + ".bak and wrote defaults");
                return true;
            }
            catch (Exception e)
            {
                Warn("config was not a valid JSON object and could not be backed up (" + e.Message + "); using defaults for this session and leaving the file untouched");
                return false;
            }
        }

        public bool Save()
        {
            try
            {
                string path = PathOf();
                string tmp = path + ".tmp";
                try
                {
                    File.WriteAllText(tmp, TerrainWatersConfig.ToFileObject(Current, keep).ToString(Formatting.Indented));
                    File.Move(tmp, path, true); // same directory: replaces the target in one step
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                }
                return true;
            }
            catch (Exception e)
            {
                Warn("could not write config (" + e.Message + ")");
                return false;
            }
        }

        /// <summary>
        /// Re-reads the file for /riverfeatures reload WITHOUT touching Current or the file unless the file parses.
        /// A missing, unreadable, empty, malformed or non-object file keeps the old settings (returns false, message says why).
        /// A parsed file replaces Current (clamping notes are reported) and is written back only if clamping or missing keys changed it.
        /// </summary>
        public bool Reload(out string message)
        {
            string text;
            try
            {
                string p = PathOf();
                if (!File.Exists(p)) { message = "riverfeatures-lakes.json does not exist; the old settings were kept."; return false; }
                text = File.ReadAllText(p);
            }
            catch (Exception e)
            {
                message = "Could not read riverfeatures-lakes.json (" + e.Message + "); the old settings were kept and the file was not touched.";
                return false;
            }
            if (!ConfigEdit.TryParseCandidate(text, out var cfg, out var notes, out var error))
            {
                message = "riverfeatures-lakes.json was not applied: " + error + ". The old settings were kept and the file was not touched.";
                return false;
            }
            Current = cfg;
            bool rewrite;
            try
            {
                keep = JToken.Parse(text) as JObject; // an object: TryParseCandidate checked it
                rewrite = !JToken.DeepEquals(keep, TerrainWatersConfig.ToFileObject(cfg, keep));
            }
            catch (Exception) { rewrite = true; }
            bool saved = !rewrite || Save();
            message = "Reloaded riverfeatures-lakes.json"
                + (notes.Count > 0 ? " (notes: " + string.Join("; ", notes) + ")" : "")
                + (saved ? "." : ". The repaired values could not be written back (see the server log).");
            return true;
        }

        /// <summary>
        /// Sets one setting by name (case-insensitive) using the same parse and clamp rules as loading the file
        /// (<see cref="ConfigEdit.TrySet"/>). Saves only on success; the message names the value actually stored.
        /// The caller applies the change to worldgen (<see cref="ApplyLive"/>).
        /// </summary>
        public bool TrySet(string key, string value, out string message)
        {
            if (!ConfigEdit.TrySet(Current, key, value, out var updated, out message)) return false;
            Current = updated;
            if (!Save()) message += " The file could not be written, so the change lasts only until the server restarts (see the server log).";
            return true;
        }
    }
}
