using System.Reflection;
using BlueprintExtractor.Exporters;
using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using UnityModManagerNet;

namespace BlueprintExtractor;

public static class Main {
  private static Harmony HarmonyInstance;
  private static UnityModManager.ModEntry.ModLogger Log;

  public static bool Load(UnityModManager.ModEntry modEntry) {
    Log = modEntry.Logger;
    modEntry.OnGUI = OnGUI;
    HarmonyInstance = new Harmony(modEntry.Info.Id);

    try {
      HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
    }
    catch {
      HarmonyInstance.UnpatchAll(HarmonyInstance.Id);

      throw;
    }

    return true;
  }

  private static void OnGUI(UnityModManager.ModEntry modEntry) { }

  [HarmonyPatch(typeof(BlueprintsCache))]
  public static class BlueprintsCaches_Patch {
    private static bool Initialized;

    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(BlueprintsCache.Init))]
    [HarmonyPostfix]
    public static void Init_Postfix() {
      try {
        if (Initialized) {
          Log.Log("Already initialized blueprints cache.");

          return;
        }

        Initialized = true;

        MainExporter.ExportAll();
      }
      catch (Exception e) {
        Log.Log(string.Concat("Failed to initialize.", e));
      }
    }
  }
}