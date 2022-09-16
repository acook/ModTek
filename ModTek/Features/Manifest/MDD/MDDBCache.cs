﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BattleTech;
using BattleTech.Data;
using ModTek.Features.CustomTags;
using ModTek.Features.Logging;
using ModTek.Features.Manifest.BTRL;
using ModTek.Misc;
using ModTek.UI;
using ModTek.Util;
using CacheDB = System.Collections.Generic.Dictionary<ModTek.Features.Manifest.CacheKey, ModTek.Features.Manifest.FileVersionTuple>;
using CacheKeyValue = System.Collections.Generic.KeyValuePair<ModTek.Features.Manifest.CacheKey, ModTek.Features.Manifest.FileVersionTuple>;

namespace ModTek.Features.Manifest.MDD
{
    internal class MDDBCache
    {
        private static string PersistentDirPath => FilePaths.MDDBCacheDirectory;
        private readonly string PersistentFilePath;

        private static string MDDBPath => FilePaths.MDDBPath;
        private static string ModMDDBPath => FilePaths.ModMDDBPath;

        private CacheDB CachedItems { get; }
        internal static bool SaveMDDB;

        private bool hasChanges;
        private void SetHasChangedAndRemoveIndex()
        {
            if (!hasChanges) // remove existing cache on first invalidation
            {
                hasChanges = true;
                File.Delete(PersistentFilePath);
            }
        }

        internal MDDBCache()
        {
            PersistentFilePath = Path.Combine(PersistentDirPath, "database_cache.json");

            if (File.Exists(PersistentFilePath) && File.Exists(ModMDDBPath))
            {
                try
                {
                    CachedItems = ModTekCacheStorage.ReadFrom<List<CacheKeyValue>>(PersistentFilePath)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    MetadataDatabase.ReloadFromDisk();
                    MTLogger.Info.Log("MDDBCache: Loaded.");
                    return;
                }
                catch (Exception e)
                {
                    MTLogger.Info.Log("MDDBCache: Loading db cache failed -- will rebuild it.", e);
                }
            }

            CachedItems = new CacheDB();
            Reset();
        }

        private void Reset()
        {
            FileUtils.CleanDirectory(PersistentDirPath);

            File.Copy(MDDBPath, ModMDDBPath);

            // create a new one if it doesn't exist or couldn't be added
            MTLogger.Info.Log("MDDBCache: Copying over DB and rebuilding cache.");
            CachedItems.Clear();
            MetadataDatabase.ReloadFromDisk();
        }

        private readonly Stopwatch saveSW = new Stopwatch();
        internal void Save()
        {
            try
            {
                saveSW.Restart();
                if (!hasChanges && !SaveMDDB)
                {
                    MTLogger.Info.Log($"MDDBCache: No changes detected, skipping save.");
                    return;
                }

                MetadataDatabase.SaveMDDToPath();
                MTLogger.Info.Log($"MDDBCache: Saved MDD.");

                if (hasChanges)
                {
                    ModTekCacheStorage.WriteTo(CachedItems.ToList(), PersistentFilePath);
                    MTLogger.Info.Log($"MDDBCache: Saved to {PersistentFilePath}.");
                    hasChanges = false;
                }
            }
            catch (Exception e)
            {
                MTLogger.Info.Log($"MDDBCache: Couldn't write mddb cache to {PersistentFilePath}", e);
            }
            finally
            {
                saveSW.Stop();
                MTLogger.Info.LogIfSlow(saveSW);
            }
        }

        private readonly Stopwatch sw = new Stopwatch();
        internal void CacheUpdate(VersionManifestEntry entry)
        {
            if (IsIgnored(entry, out var key))
            {
                return;
            }

            var json = ModsManifest.GetText(entry);
            if (json == null)
            {
                MTLogger.Info.Log($"MDDBCache: Error trying to get json for {entry.ToShortString()}");
                return;
            }

            SetHasChangedAndRemoveIndex();
            sw.Start();
            try
            {
                MTLogger.Info.Log($"MDDBCache: Indexing {entry.ToShortString()}");
                MDDBIndexer.InstantiateResourceAndUpdateMDDB(entry, json);
            }
            catch (Exception e)
            {
                MTLogger.Info.Log($"MDDBCache: Exception when indexing {entry.ToShortString()}", e);
            }
            sw.Stop();
            MTLogger.Info.LogIfSlow(sw, "InstantiateResourceAndUpdateMDDB", 10000); // every 10s log total and reset
            if (!entry.IsVanillaOrDlc())
            {
                CachedItems[key] = FileVersionTuple.From(entry);
            }
        }

        private readonly HashSet<CacheKey> IgnoredItems = new HashSet<CacheKey>();

        internal void AddToNotIndexable(ModEntry entry)
        {
            if (!BTConstants.MDDBTypes.Contains(entry.Type))
            {
                return;
            }

            var key = new CacheKey(entry);
            IgnoredItems.Add(key);
        }

        private bool IsIgnored(VersionManifestEntry entry, out CacheKey key)
        {
            key = new CacheKey(entry);
            return IgnoredItems.Contains(key);
        }

        internal IEnumerable<ProgressReport> BuildCache()
        {
            var sliderText = "Building MDDB Cache";
            yield return new ProgressReport(0, sliderText, "", true);

            bool rebuildIndex = false;
            var reindexResources = new HashSet<CacheKey>();

            // find entries missing in cache
            foreach (var type in BTConstants.MDDBTypes)
            {
                foreach (var manifestEntry in BetterBTRL.Instance.AllEntriesOfType(type))
                {
                    if (IsIgnored(manifestEntry, out var key))
                    {
                        continue;
                    }

                    // see if it is already cached
                    if (CachedItems.TryGetValue(key, out var cachedEntry))
                    {
                        cachedEntry.CacheHit = true;

                        if (!cachedEntry.Contains(manifestEntry))
                        {
                            MTLogger.Info.Log($"MDDBCache: {key} outdated in cache.");
                            CachedItems.Remove(key);
                            reindexResources.Add(key);
                        }
                    }
                    else if (!manifestEntry.IsVanillaOrDlc())
                    {
                        MTLogger.Info.Log($"MDDBCache: {key} missing in cache.");
                        reindexResources.Add(key);
                    }
                }
            }

            // find entries that shouldn't be in cache (anymore)
            foreach (var kv in CachedItems)
            {
                if (!kv.Value.CacheHit)
                {
                    MTLogger.Info.Log($"MDDBCache: {kv.Key} left over in cache.");
                    rebuildIndex = true;
                }
            }

            if (rebuildIndex)
            {
                MTLogger.Info.Log($"MDDBCache: Rebuilding.");
                Reset();
                reindexResources.Clear();
                foreach (var type in BTConstants.MDDBTypes)
                {
                    foreach (var entry in BetterBTRL.Instance.AllEntriesOfType(type))
                    {
                        if (!entry.IsVanillaOrDlc())
                        {
                            reindexResources.Add(new CacheKey(entry));
                        }
                    }
                }
            }

            CustomTagFeature.ProcessTags(); // TODO add change detection
            AddendumUtils.ProcessDataAddendums(); // TODO add change detection

            var countCurrent = 0;
            var countMax = (float)reindexResources.Count;
            foreach (var key in reindexResources)
            {
                yield return new ProgressReport(countCurrent++/countMax, sliderText, $"{key.Type}\n{key.Id}");
                var entry = BetterBTRL.Instance.EntryByIDAndType(key.Id, key.Type);
                CacheUpdate(entry);
            }

            yield return new ProgressReport(1, sliderText, $"Saving cache index", true);
            Save();
        }
    }
}
