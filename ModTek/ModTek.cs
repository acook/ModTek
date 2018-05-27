using BattleTech;
using BattleTech.Data;
using BattleTechModLoader;
using Harmony;
using HBS.Util;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ModTek
{
    using static Logger;

    public static class ModTek
    {
        private const string MODS_DIRECTORY_NAME = "Mods";
        private const string MOD_JSON_NAME = "mod.json";
        private const string MODTEK_DIRECTORY_NAME = ".modtek";
        private const string CACHE_DIRECTORY_NAME = "Cache";
        private const string MERGE_CACHE_FILE_NAME = "merge_cache.json";
        private const string TYPE_CACHE_FILE_NAME = "type_cache.json";
        private const string LOG_NAME = "ModTek.log";
        private const string LOAD_ORDER_FILE_NAME = "load_order.json";
        private const string DATABASE_DIRECTORY_NAME = "Database";
        private const string MOD_MDD_FILE_NAME = "MetadataDatabase.db";
        private const string DB_CACHE_FILE_NAME = "database_cache.json";

        private static bool hasLoadedMods; //defaults to false

        private static List<string> modLoadOrder;
        private static MergeCache jsonMergeCache;
        private static Dictionary<string, List<string>> typeCache;
        private static Dictionary<string, DateTime> dbCache;

        private static List<ModDef.ManifestEntry> modEntries;
        private static Dictionary<string, List<ModDef.ManifestEntry>> modManifest = new Dictionary<string, List<ModDef.ManifestEntry>>();

        public static string GameDirectory { get; private set; }
        public static string ModDirectory { get; private set; }
        public static string StreamingAssetsDirectory { get; private set; }

        internal static string ModTekDirectory { get; private set; }
        internal static string CacheDirectory { get; private set; }
        internal static string DatabaseDirectory { get; private set; }
        internal static string MergeCachePath { get; private set; }
        internal static string TypeCachePath { get; private set; }
        internal static string ModDBPath { get; private set; }
        internal static string DBCachePath { get; private set; }
        internal static string LoadOrderPath { get; private set; }

        internal static Dictionary<string, string> ModAssetBundlePaths { get; } = new Dictionary<string, string>();


        // INITIALIZATION (called by BTML)
        [UsedImplicitly]
        public static void Init()
        {
            // if the manifest directory is null, there is something seriously wrong
            var manifestDirectory = Path.GetDirectoryName(VersionManifestUtilities.MANIFEST_FILEPATH);
            if (manifestDirectory == null)
                return;

            // setup directories
            ModDirectory = Path.GetFullPath(
                Path.Combine(manifestDirectory,
                    Path.Combine(Path.Combine(Path.Combine(
                        "..", ".."), ".."), MODS_DIRECTORY_NAME)));

            StreamingAssetsDirectory = Path.GetFullPath(Path.Combine(manifestDirectory, ".."));
            GameDirectory = Path.GetFullPath(Path.Combine(Path.Combine(StreamingAssetsDirectory, ".."), ".."));

            ModTekDirectory = Path.Combine(ModDirectory, MODTEK_DIRECTORY_NAME);
            CacheDirectory = Path.Combine(ModTekDirectory, CACHE_DIRECTORY_NAME);
            DatabaseDirectory = Path.Combine(ModTekDirectory, DATABASE_DIRECTORY_NAME);

            LogPath = Path.Combine(ModTekDirectory, LOG_NAME);
            LoadOrderPath = Path.Combine(ModTekDirectory, LOAD_ORDER_FILE_NAME);
            MergeCachePath = Path.Combine(CacheDirectory, MERGE_CACHE_FILE_NAME);
            TypeCachePath = Path.Combine(CacheDirectory, TYPE_CACHE_FILE_NAME);
            ModDBPath = Path.Combine(DatabaseDirectory, MOD_MDD_FILE_NAME);
            DBCachePath = Path.Combine(DatabaseDirectory, DB_CACHE_FILE_NAME);

            // creates the directories above it as well
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(DatabaseDirectory);

            // create log file, overwritting if it's already there
            using (var logWriter = File.CreateText(LogPath))
            {
                logWriter.WriteLine($"ModTek v{Assembly.GetExecutingAssembly().GetName().Version} -- {DateTime.Now}");
            }

            // copy database over if needed
            if (!File.Exists(ModDBPath))
            {
                var dbPath = Path.Combine(Path.Combine(StreamingAssetsDirectory, "MDD"), "MetadataDatabase.db");
                File.Copy(dbPath, ModDBPath);
            }

            // create all of the caches
            dbCache = LoadOrCreateDBCache(DBCachePath);
            jsonMergeCache = LoadOrCreateMergeCache(MergeCachePath);
            typeCache = LoadOrCreateTypeCache(TypeCachePath);

            // init harmony and patch the stuff that comes with ModTek (contained in Patches.cs)
            var harmony = HarmonyInstance.Create("io.github.mpstark.ModTek");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log("");
        }


        // LOAD ORDER
        private static void PropagateConflictsForward(Dictionary<string, ModDef> modDefs)
        {
            // conflicts are a unidirectional edge, so make them one in ModDefs
            foreach (var modDefKvp in modDefs)
            {
                var modDef = modDefKvp.Value;
                if (modDef.ConflictsWith.Count == 0) continue;

                foreach (var conflict in modDef.ConflictsWith) modDefs[conflict].ConflictsWith.Add(modDef.Name);
            }
        }

        private static List<string> LoadLoadOrder(string path)
        {
            List<string> order;

            if (File.Exists(path))
                try
                {
                    order = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                    Log("Loaded cached load order.");
                    return order;
                }
                catch (Exception e)
                {
                    Log("Loading cached load order failed, rebuilding it.");
                    Log($"\t{e.Message}");
                }

            // create a new one if it doesn't exist or couldn't be added
            Log("Building new load order!");
            order = new List<string>();
            return order;
        }

        private static bool AreDependanciesResolved(ModDef modDef, HashSet<string> loaded)
        {
            return !(modDef.DependsOn.Count != 0 && modDef.DependsOn.Intersect(loaded).Count() != modDef.DependsOn.Count
                || modDef.ConflictsWith.Count != 0 && modDef.ConflictsWith.Intersect(loaded).Any());
        }

        private static List<string> GetLoadOrder(Dictionary<string, ModDef> modDefs, out List<string> unloaded)
        {
            var modDefsCopy = new Dictionary<string, ModDef>(modDefs);
            var cachedOrder = LoadLoadOrder(LoadOrderPath);
            var loadOrder = new List<string>();
            var loaded = new HashSet<string>();

            // load the order specified in the file
            foreach (var modName in cachedOrder)
            {
                if (!modDefs.ContainsKey(modName) || !AreDependanciesResolved(modDefs[modName], loaded)) continue;

                modDefsCopy.Remove(modName);
                loadOrder.Add(modName);
                loaded.Add(modName);
            }

            // everything that is left in the copy hasn't been loaded before
            unloaded = modDefsCopy.Keys.OrderByDescending(x => x).ToList();

            // there is nothing left to load
            if (unloaded.Count == 0)
                return loadOrder;

            // this is the remainder that haven't been loaded before
            int removedThisPass;
            do
            {
                removedThisPass = 0;

                for (var i = unloaded.Count - 1; i >= 0; i--)
                {
                    var modDef = modDefs[unloaded[i]];

                    if (!AreDependanciesResolved(modDef, loaded)) continue;

                    unloaded.RemoveAt(i);
                    loadOrder.Add(modDef.Name);
                    loaded.Add(modDef.Name);
                    removedThisPass++;
                }
            } while (removedThisPass > 0 && unloaded.Count > 0);

            return loadOrder;
        }
        

        // LOADING MODS
        private static void LoadMod(ModDef modDef)
        {
            var potentialAdditions = new List<ModDef.ManifestEntry>();

            LogWithDate($"Loading {modDef.Name}");

            // load out of the manifest
            if (modDef.LoadImplicitManifest && modDef.Manifest.All(x => Path.GetFullPath(Path.Combine(modDef.Directory, x.Path)) != Path.GetFullPath(Path.Combine(modDef.Directory, "StreamingAssets"))))
                modDef.Manifest.Add(new ModDef.ManifestEntry("StreamingAssets", true));

            // note: if a JSON has errors, this mod will not load, since InferIDFromFile will throw from parsing the JSON
            foreach (var entry in modDef.Manifest)
            {
                // handle prefabs; they have potential internal path to assetbundle
                if (entry.Type == "Prefab" && !string.IsNullOrEmpty(entry.AssetBundleName))
                {
                    if (!potentialAdditions.Any(x => x.Type == "AssetBundle" && x.Id == entry.AssetBundleName))
                    {
                        Log($"\t{modDef.Name} has a Prefab that's referencing an AssetBundle that hasn't been loaded. Put the assetbundle first in the manifest!");
                        return;
                    }

                    entry.Id = Path.GetFileNameWithoutExtension(entry.Path);
                    potentialAdditions.Add(entry);
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Path) && string.IsNullOrEmpty(entry.Type) && entry.Path != "StreamingAssets")
                {
                    Log($"\t{modDef.Name} has a manifest entry that is missing its path or type! Aborting load.");
                    return;
                }

                var entryPath = Path.Combine(modDef.Directory, entry.Path);
                if (Directory.Exists(entryPath))
                {
                    // path is a directory, add all the files there
                    var files = Directory.GetFiles(entryPath, "*", SearchOption.AllDirectories);
                    foreach (var filePath in files)
                    {
                        var childModDef = new ModDef.ManifestEntry(entry, filePath, InferIDFromFile(filePath));
                        potentialAdditions.Add(childModDef);
                    }
                }
                else if (File.Exists(entryPath))
                {
                    // path is a file, add the single entry
                    entry.Id = entry.Id ?? InferIDFromFile(entryPath);
                    entry.Path = entryPath;
                    potentialAdditions.Add(entry);
                }
                else if (entry.Path != "StreamingAssets")
                {
                    // path is not streamingassets and it's missing
                    Log($"\tMissing Entry: Manifest specifies file/directory of {entry.Type} at path {entry.Path}, but it's not there. Continuing to load.");
                }
            }

            // load mod dll
            if (modDef.DLL != null)
            {
                var dllPath = Path.Combine(modDef.Directory, modDef.DLL);
                string typeName = null;
                var methodName = "Init";

                if (!File.Exists(dllPath))
                {
                    Log($"\t{modDef.Name} has a DLL specified ({dllPath}), but it's missing! Aborting load.");
                    return;
                }

                if (modDef.DLLEntryPoint != null)
                {
                    var pos = modDef.DLLEntryPoint.LastIndexOf('.');
                    if (pos == -1)
                    {
                        methodName = modDef.DLLEntryPoint;
                    }
                    else
                    {
                        typeName = modDef.DLLEntryPoint.Substring(0, pos);
                        methodName = modDef.DLLEntryPoint.Substring(pos + 1);
                    }
                }

                Log($"\tUsing BTML to load dll {Path.GetFileName(dllPath)} with entry path {typeName ?? "NoNameSpecified"}.{methodName}");

                BTModLoader.LoadDLL(dllPath, methodName, typeName,
                    new object[] { modDef.Directory, modDef.Settings.ToString(Formatting.None) });
            }

            // actually add the additions, since we successfully got through loading the other stuff
            if (potentialAdditions.Count > 0)
            {
                foreach (var addition in potentialAdditions) Log($"\tNew Entry: {addition.Path.Replace(ModDirectory, "")}");

                modManifest[modDef.Name] = potentialAdditions;
            }
        }

        internal static void LoadMods()
        {
            if (hasLoadedMods)
                return;

            // find all sub-directories that have a mod.json file
            var modDirectories = Directory.GetDirectories(ModDirectory)
                .Where(x => File.Exists(Path.Combine(x, MOD_JSON_NAME))).ToArray();

            if (modDirectories.Length == 0)
            {
                hasLoadedMods = true;
                Log("No ModTek-compatable mods found.");
                return;
            }

            // create ModDef objects for each mod.json file
            var modDefs = new Dictionary<string, ModDef>();
            foreach (var modDirectory in modDirectories)
            {
                ModDef modDef;
                var modDefPath = Path.Combine(modDirectory, MOD_JSON_NAME);
                
                try
                {
                    modDef = ModDef.CreateFromPath(modDefPath);
                }
                catch (Exception e)
                {
                    Log($"Caught exception while parsing {MOD_JSON_NAME} at path {modDefPath}");
                    Log($"\t{e.Message}");
                    continue;
                }

                if (!modDef.Enabled)
                {
                    LogWithDate($"Will not load {modDef.Name} because it's disabled.");
                    continue;
                }

                if (modDefs.ContainsKey(modDef.Name))
                {
                    LogWithDate($"Already loaded a mod named {modDef.Name}. Skipping load from {modDef.Directory}.");
                    continue;
                }

                modDefs.Add(modDef.Name, modDef);
            }

            // TODO: be able to read load order from a JSON
            PropagateConflictsForward(modDefs);
            modLoadOrder = GetLoadOrder(modDefs, out var willNotLoad);

            // lists guarentee order
            foreach (var modName in modLoadOrder)
            {
                var modDef = modDefs[modName];

                try
                {
                    LoadMod(modDef);
                }
                catch (Exception e)
                {
                    LogWithDate($"Tried to load mod: {modDef.Name}, but something went wrong. Make sure all of your JSON is correct!");
                    Log($"{e.Message}");
                }
            }

            foreach (var modDef in willNotLoad) LogWithDate($"Will not load {modDef}. It's lacking a dependancy or a conflict loaded before it.");

            Log("");
            Log("----------");
            Log("");

            // write out load order
            File.WriteAllText(LoadOrderPath, JsonConvert.SerializeObject(modLoadOrder, Formatting.Indented));

            hasLoadedMods = true;
        }

        private static string InferIDFromFile(string path)
        {
            // if not json, return the file name without the extension, as this is what HBS uses
            var ext = Path.GetExtension(path);
            if (ext == null || ext.ToLower() != ".json" || !File.Exists(path))
                return Path.GetFileNameWithoutExtension(path);

            // read the json and get ID out of it if able to
            return InferIDFromJObject(ParseGameJSON(File.ReadAllText(path))) ?? Path.GetFileNameWithoutExtension(path);
        }


        // JSON HANDLING
        /// <summary>
        ///     Create JObject from string, removing comments and adding commas first.
        /// </summary>
        /// <param name="jsonText">JSON contained in a string</param>
        /// <returns>JObject parsed from jsonText, null if invalid</returns>
        internal static JObject ParseGameJSON(string jsonText)
        {
            // because StripHBSCommentsFromJSON is private, use Harmony to call the method
            var commentsStripped = Traverse.Create(typeof(JSONSerializationUtility)).Method("StripHBSCommentsFromJSON", jsonText).GetValue() as string;
            
            if (commentsStripped == null)
                throw new Exception("StripHBSCommentsFromJSON returned null.");
            
            // add missing commas, this only fixes if there is a newline
            var rgx = new Regex(@"(\]|\}|""|[A-Za-z0-9])\s*\n\s*(\[|\{|"")", RegexOptions.Singleline);
            var commasAdded = rgx.Replace(commentsStripped, "$1,\n$2");

            return JObject.Parse(commasAdded);
        }

        private static string InferIDFromJObject(JObject jObj)
        {
            if (jObj == null)
                return null;

            // go through the different kinds of id storage in JSONS
            string[] jPaths = { "Description.Id", "id", "Id", "ID", "identifier", "Identifier" };
            foreach (var jPath in jPaths)
            {
                var id = (string)jObj.SelectToken(jPath);
                if (id != null)
                    return id;
            }

            return null;
        }


        // CACHES
        internal static MergeCache LoadOrCreateMergeCache(string path)
        {
            MergeCache mergeCache;

            if (File.Exists(path))
                try
                {
                    mergeCache = JsonConvert.DeserializeObject<MergeCache>(File.ReadAllText(path));
                    Log("Loaded merge cache.");
                    return mergeCache;
                }
                catch (Exception e)
                {
                    Log("Loading merge cache failed -- will rebuild it.");
                    Log($"\t{e.Message}");
                }

            // create a new one if it doesn't exist or couldn't be added'
            Log("Building new Merge Cache.");
            mergeCache = new MergeCache();
            return mergeCache;
        }

        internal static Dictionary<string, List<string>> LoadOrCreateTypeCache(string path)
        {
            Dictionary<string, List<string>> cache;

            if (File.Exists(path))
                try
                {
                    cache = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(path));
                    Log("Loaded type cache.");
                    return cache;
                }
                catch (Exception e)
                {
                    Log("Loading type cache failed -- will rebuild it.");
                    Log($"\t{e.Message}");
                }

            // create a new one if it doesn't exist or couldn't be added
            Log("Building new Type Cache.");
            cache = new Dictionary<string, List<string>>();
            return cache;
        }

        internal static Dictionary<string, DateTime> LoadOrCreateDBCache(string path)
        {
            Dictionary<string, DateTime> cache;

            if (File.Exists(path))
                try
                {
                    cache = JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(File.ReadAllText(path));
                    Log("Loaded db cache.");
                    return cache;
                }
                catch (Exception e)
                {
                    Log("Loading db cache failed -- will rebuild it.");
                    Log($"\t{e.Message}");
                }

            // create a new one if it doesn't exist or couldn't be added
            Log("Building new DB Cache.");
            cache = new Dictionary<string, DateTime>();
            return cache;
        }

        internal static void WriteJsonFile(string path, object obj)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
        }


        // ADDING TO VERSION MANIFEST
        private static bool AddModEntry(VersionManifest manifest, ModDef.ManifestEntry modEntry, bool addToDB = false)
        {
            if (modEntry.Path == null)
                return false;

            VersionManifestAddendum addendum = null;
            if (!string.IsNullOrEmpty(modEntry.AddToAddendum))
            {
                addendum = manifest.GetAddendumByName(modEntry.AddToAddendum);

                // create the addendum if it doesn't exist
                if (addendum == null)
                {
                    Log($"\t\tCreated addendum: {modEntry.AddToAddendum}");
                    addendum = new VersionManifestAddendum(modEntry.AddToAddendum);
                    manifest.ApplyAddendum(addendum);
                }
            }

            // add to DB
            if (addToDB && Path.GetExtension(modEntry.Path).ToLower() == ".json")
            {
                var type = (BattleTechResourceType) Enum.Parse(typeof(BattleTechResourceType), modEntry.Type);
                switch (type) // switch is to avoid poisoning the output_log.txt with known types that don't use MDD
                {
                    case BattleTechResourceType.TurretDef:
                    case BattleTechResourceType.UpgradeDef:
                    case BattleTechResourceType.VehicleDef:
                    case BattleTechResourceType.ContractOverride:
                    case BattleTechResourceType.SimGameEventDef:
                    case BattleTechResourceType.LanceDef:
                    case BattleTechResourceType.MechDef:
                    case BattleTechResourceType.PilotDef:
                    case BattleTechResourceType.WeaponDef:
                        if (!dbCache.ContainsKey(modEntry.Path) || dbCache[modEntry.Path] != File.GetLastWriteTimeUtc(modEntry.Path))
                            using (var metadataDatabase = new MetadataDatabase())
                            {
                                Log($"\t\t\tAdd/Update DB: {Path.GetFileNameWithoutExtension(modEntry.Path)} ({modEntry.Type})");
                                VersionManifestHotReload.InstantiateResourceAndUpdateMDDB(type, modEntry.Path, metadataDatabase);
                                dbCache[modEntry.Path] = File.GetLastWriteTimeUtc(modEntry.Path);
                            }

                        break;
                }
            }

            // add assetbundle path so it can be changed when the assetbundle path is requested
            if (modEntry.Type == "AssetBundle")
                ModAssetBundlePaths[modEntry.Id] = modEntry.Path;

            // add to addendum instead of adding to manifest
            if (addendum != null)
            {
                Log($"\t\tAddOrUpdate => {modEntry.Id} ({modEntry.Type}) to addendum {addendum.Name}");
                addendum.AddOrUpdate(modEntry.Id, modEntry.Path, modEntry.Type, DateTime.Now, modEntry.AssetBundleName, modEntry.AssetBundlePersistent);
                return true;
            }

            // not added to addendum, not added to jsonmerges
            Log($"\t\tAddOrUpdate => {modEntry.Id} ({modEntry.Type})");
            manifest.AddOrUpdate(modEntry.Id, modEntry.Path, modEntry.Type, DateTime.Now, modEntry.AssetBundleName, modEntry.AssetBundlePersistent);
            return true;
        }

        internal static void AddModEntries(VersionManifest manifest)
        {
            if (!hasLoadedMods)
                LoadMods();

            // there are no mods loaded, just return
            if (modLoadOrder == null || modLoadOrder.Count == 0)
                return;

            if (modEntries != null)
            {
                LogWithDate("Loading another manifest with already setup mod manifests.");
                foreach (var modEntry in modEntries) AddModEntry(manifest, modEntry);
                LogWithDate("Done.");
                return;
            }

            modEntries = new List<ModDef.ManifestEntry>();

            LogWithDate("Setting up mod manifests...");

            var breakMyGame = File.Exists(Path.Combine(ModDirectory, "break.my.game"));
            if (breakMyGame)
            {
                var mddPath = Path.Combine(Path.Combine(StreamingAssetsDirectory, "MDD"), "MetadataDatabase.db");
                var mddBackupPath = mddPath + ".orig";

                Log("\tBreak my game mode enabled! All new modded content (doesn't currently support merges) will be added to the DB.");

                if (!File.Exists(mddBackupPath))
                {
                    Log($"\t\tBacking up metadata database to {Path.GetFileName(mddBackupPath)}");
                    File.Copy(mddPath, mddBackupPath);
                }
            }

            var jsonMerges = new Dictionary<string, List<string>>();

            foreach (var modName in modLoadOrder)
            {
                if (!modManifest.ContainsKey(modName))
                    continue;

                Log($"\t{modName}:");
                foreach (var modEntry in modManifest[modName])
                {
                    // type being null means we have to figure out the type from the path (StreamingAssets)
                    if (modEntry.Type == null)
                    {
                        // TODO: + 16 is a little bizzare looking, it's the length of the substring + 1 because we want to get rid of it and the \
                        var relPath = modEntry.Path.Substring(modEntry.Path.LastIndexOf("StreamingAssets", StringComparison.Ordinal) + 16);
                        var fakeStreamingAssetsPath = Path.GetFullPath(Path.Combine(StreamingAssetsDirectory, relPath));

                        List<string> types;

                        if (typeCache.ContainsKey(fakeStreamingAssetsPath))
                        {
                            types = typeCache[fakeStreamingAssetsPath];
                        }
                        else
                        {
                            // get the type from the manifest
                            var matchingEntries = manifest.FindAll(x => Path.GetFullPath(x.FilePath) == fakeStreamingAssetsPath);
                            if (matchingEntries == null || matchingEntries.Count == 0)
                            {
                                Log($"\t\tCould not find an existing VersionManifest entry for {modEntry.Id}. Is this supposed to be a new entry? Don't put new entries in StreamingAssets!");
                                continue;
                            }

                            types = new List<string>();

                            foreach (var existingEntry in matchingEntries) types.Add(existingEntry.Type);

                            typeCache[fakeStreamingAssetsPath] = types;
                        }

                        if (Path.GetExtension(modEntry.Path).ToLower() == ".json" && modEntry.ShouldMergeJSON)
                        {
                            if (!typeCache.ContainsKey(fakeStreamingAssetsPath))
                            {
                                Log($"\t\tUnable to determine type of {modEntry.Id}. Is there someone screwy with your this mod.json?");
                                continue;
                            }

                            if (!jsonMerges.ContainsKey(fakeStreamingAssetsPath))
                                jsonMerges[fakeStreamingAssetsPath] = new List<string>();

                            if (jsonMerges[fakeStreamingAssetsPath].Contains(modEntry.Path))
                                continue;

                            // this assumes that .json can only have a single type
                            modEntry.Type = typeCache[fakeStreamingAssetsPath][0];

                            Log($"\t\tMerge => {modEntry.Id} ({modEntry.Type})");

                            jsonMerges[fakeStreamingAssetsPath].Add(modEntry.Path);
                            continue;
                        }

                        foreach (var type in types)
                        {
                            var subModEntry = new ModDef.ManifestEntry(modEntry, modEntry.Path, modEntry.Id);
                            subModEntry.Type = type;

                            if (AddModEntry(manifest, subModEntry, breakMyGame))
                                modEntries.Add(modEntry);
                        }

                        continue;
                    }

                    // non-streamingassets json merges
                    if (Path.GetExtension(modEntry.Path)?.ToLower() == ".json" && modEntry.ShouldMergeJSON)
                    {
                        // have to find the original path for the manifest entry that we're merging onto
                        var matchingEntry = manifest.Find(x => x.Id == modEntry.Id);

                        if (matchingEntry == null)
                        {
                            Log($"\t\tCould not find an existing VersionManifest entry for {modEntry.Id}!");
                            continue;
                        }

                        if (!jsonMerges.ContainsKey(matchingEntry.FilePath))
                            jsonMerges[matchingEntry.FilePath] = new List<string>();

                        if (jsonMerges[matchingEntry.FilePath].Contains(modEntry.Path))
                            continue;

                        // this assumes that .json can only have a single type
                        modEntry.Type = matchingEntry.Type;

                        Log($"\t\tMerge => {modEntry.Id} ({modEntry.Type})");

                        jsonMerges[matchingEntry.FilePath].Add(modEntry.Path);
                        continue;
                    }

                    if (AddModEntry(manifest, modEntry, breakMyGame))
                        modEntries.Add(modEntry);
                }
            }

            LogWithDate("Doing merges...");
            foreach (var jsonMerge in jsonMerges)
            {
                var cachePath = jsonMergeCache.GetOrCreateCachedEntry(jsonMerge.Key, jsonMerge.Value);

                // something went wrong (the parent json prob had errors)
                if (cachePath == null)
                    continue;

                var cacheEntry = new ModDef.ManifestEntry(cachePath);

                cacheEntry.ShouldMergeJSON = false;
                cacheEntry.Type = typeCache[jsonMerge.Key][0];
                cacheEntry.Id = InferIDFromFile(cachePath);

                if (AddModEntry(manifest, cacheEntry, breakMyGame))
                    modEntries.Add(cacheEntry);
            }

            // write merge cache to disk
            jsonMergeCache.WriteCacheToDisk(Path.Combine(CacheDirectory, MERGE_CACHE_FILE_NAME));

            // write db/type cache to disk
            WriteJsonFile(DBCachePath, dbCache);
            WriteJsonFile(TypeCachePath, typeCache);

            LogWithDate("Done.");
            Log("");
        }
    }
}
