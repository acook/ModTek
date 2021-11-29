using System;
using System.IO;
using ModTek.Features.Logging;
using Newtonsoft.Json;
using static ModTek.Features.Logging.MTLogger;

namespace ModTek.Misc
{
    internal class Configuration
    {
        [JsonProperty]
        internal bool ShowLoadingScreenErrors = true;
        [JsonProperty]
        internal const string ShowLoadingScreenErrors_Description = "TODO";

        [JsonProperty]
        internal bool ShowErrorPopup = true;
        [JsonProperty]
        internal const string ShowErrorPopup_Description = "TODO";

        [JsonProperty]
        internal bool UseErrorWhiteList = true;
        [JsonProperty]
        internal const string UseErrorWhiteList_Description = "TODO";

        [JsonProperty]
        internal string[] ErrorWhitelist = { "Data.DataManager [ERROR] ManifestEntry is null" };

        [JsonProperty]
        internal bool UseFileCompression;
        [JsonProperty]
        internal const string UseFileCompression_Description = "Manifest, database and cache files are compressed using gzip.";

        [JsonProperty]
        internal bool SearchModsInSubDirectories = true;
        [JsonProperty]
        internal const string SearchModsInSubDirectories_Description = "Searches recursively all directories for mod.json instead only for directories directly found under Mods. Set to false for pre v2.0 behavior.";

        [JsonProperty]
        internal bool ImplicitManifestShouldMergeJSON = true;
        [JsonProperty]
        internal const string ImplicitManifestShouldMergeJSON_Description = "How JSONs in a mods implicit manifest (StreamingAssets) are being treated.";

        [JsonProperty]
        internal bool ImplicitManifestShouldAppendText;
        [JsonProperty]
        internal const string ImplicitManifestShouldAppendText_Description = "How CSVs in a mods implicit manifest (StreamingAssets) are being treated.";

        [JsonProperty]
        internal bool PreloadResourcesForCache;
        [JsonProperty]
        internal const string PreloadResourcesForCache_Description = "Instead of waiting for the game to request resources naturally and then merge when loading" +
            ", pre-request all mergeable and indexable resources during the game startup. Not all mods are compatible with pre-loading, therefore disabled by default.";

        [JsonProperty]
        internal string[] BlockedMods = { "FYLS" };
        [JsonProperty]
        internal const string BlockedMods_Description = "Mods that should not be allowed to load. Useful in cases where those mods would (newly) interfere with ModTek.";

        [JsonProperty]
        internal string[] IgnoreMissingMods = { "FYLS" };
        [JsonProperty]
        internal const string IgnoreMissingMods_Description = "Ignore the dependency requirement of mods that depend on the listed mods. Useful if e.g. ModTek provides the same functionality as the ignored mods.";

        [JsonProperty]
        internal string[] AssembliesToPreload = { };
        [JsonProperty]
        internal const string AssembliesToPreload_Description = "A list of assemblies to pre-load before ModTek starts harmony patching." +
            " Useful for mods that modify the assembly directly and introduce dependencies not found in the default assembly search path of the game." +
            " Path is relative to the Mods/ directory";

        [JsonProperty]
        internal LoggingSettings Logging = new();

        private static string ConfigPath => Path.Combine(FilePaths.ModTekDirectory, "config.json");
        private static string ConfigDefaultsPath => Path.Combine(FilePaths.ModTekDirectory, "config.defaults.json");
        private static string ConfigLastPath => Path.Combine(FilePaths.ModTekDirectory, "config.last.json");

        internal static Configuration FromDefaultFile()
        {
            var path = ConfigPath;
            var config = new Configuration();
            config.WriteConfig(ConfigDefaultsPath);

            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    JsonConvert.PopulateObject(
                        text,
                        config,
                        new JsonSerializerSettings
                        {
                            ObjectCreationHandling = ObjectCreationHandling.Replace,
                            DefaultValueHandling = DefaultValueHandling.Ignore,
                            NullValueHandling = NullValueHandling.Ignore
                        }
                    );
                    Log($"Loaded config from path: {path}");
                }
                catch (Exception e)
                {
                    Log("Reading configuration failed, using defaults", e);
                }
            }
            else
            {
                File.WriteAllText(path, "{}");
            }

            config.WriteConfig(ConfigLastPath);

            return config;
        }

        private void WriteConfig(string path)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(this,
                Formatting.Indented
            ));
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }
    }
}
