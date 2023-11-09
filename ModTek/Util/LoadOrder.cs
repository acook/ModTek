using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ModTek.Util;

internal static class LoadOrder
{
    public static List<string> CreateLoadOrder(Dictionary<string, ModDefEx> registeredMods, out List<ModDefEx> notLoaded, List<string> cachedOrder)
    {
        var candidates = new Dictionary<string, ModDefEx>(registeredMods);
        var loadOrder = new List<string>();

        // remove all mods that have a conflict
        var tryToLoad = candidates.Keys.ToList();
        var hasConflicts = new List<ModDefEx>();
        foreach (var modDef in registeredMods.Values)
        {
            var conflicts = modDef.CalcConflicts(tryToLoad);
            if (conflicts.Count == 0)
            {
                continue;
            }
            candidates.Remove(modDef.Name);
            hasConflicts.Add(modDef);
        }

        FillInOptionalDependencies(candidates);

        // load the order specified in the file
        foreach (var modName in cachedOrder)
        {
            if (!candidates.ContainsKey(modName) || candidates[modName].CalcMissingDependsOn(loadOrder).Count > 0)
            {
                continue;
            }

            candidates.Remove(modName);
            loadOrder.Add(modName);
        }

        // everything that is left in the candidates list hasn't been loaded before
        notLoaded = new List<ModDefEx>();
        notLoaded.AddRange(candidates.Values.OrderByDescending(x => x.Name).ToList());

        // there is nothing left to load
        if (candidates.Count == 0)
        {
            notLoaded.AddRange(hasConflicts);
            return loadOrder;
        }

        // this is the remainder that haven't been loaded before
        int removedThisPass;
        do
        {
            removedThisPass = 0;

            for (var i = notLoaded.Count - 1; i >= 0; i--)
            {
                var modDef = notLoaded[i];

                if (modDef.CalcMissingDependsOn(loadOrder).Count > 0)
                {
                    continue;
                }

                notLoaded.RemoveAt(i);
                loadOrder.Add(modDef.Name);
                removedThisPass++;
            }
        }
        while (removedThisPass > 0 && notLoaded.Count > 0);

        notLoaded.AddRange(hasConflicts);
        return loadOrder;
    }

    public static void ToFile(List<string> order, string path)
    {
        if (order == null)
        {
            return;
        }

        File.WriteAllText(path, JsonConvert.SerializeObject(order, Formatting.Indented));
    }

    public static List<string> FromFile(string path)
    {
        List<string> order;

        if (File.Exists(path))
        {
            try
            {
                order = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                Log.Main.Info?.Log("Loaded cached load order.");
                return order;
            }
            catch (Exception e)
            {
                Log.Main.Info?.Log("Loading cached load order failed, rebuilding it.", e);
            }
        }

        // create a new one if it doesn't exist or couldn't be added
        Log.Main.Info?.Log("Building new load order!");
        order = new List<string>();
        return order;
    }

    private static void FillInOptionalDependencies(Dictionary<string, ModDefEx> modDefs)
    {
        // add optional dependencies if they are present
        foreach (var modDef in modDefs.Values)
        {
            if (modDef.OptionallyDependsOn.Count == 0)
            {
                continue;
            }

            foreach (var optDep in modDef.OptionallyDependsOn)
            {
                if (modDefs.ContainsKey(optDep))
                {
                    modDef.DependsOn.Add(optDep);
                }
            }
        }
    }
}