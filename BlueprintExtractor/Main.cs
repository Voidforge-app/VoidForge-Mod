using System.Reflection;
using BlueprintExtractor.Exporters;
using BlueprintExtractor.Infrastructure;
using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using UnityEngine;
using UnityModManagerNet;

namespace BlueprintExtractor;

public static class Main {
  private static Harmony HarmonyInstance;
  private static UnityModManager.ModEntry.ModLogger Log;
  private static string outputDirectory;
  private static string assetExportStatus = "";

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

  private static void OnGUI(UnityModManager.ModEntry modEntry) {
    GUILayout.Label("VoidForge Blueprint Extractor");

    if (!string.IsNullOrEmpty(outputDirectory)) {
      if (GUILayout.Button("Export Icons + Portraits")) {
        try {
          var logger = new ModLogger(outputDirectory);

          IconExporter.ExportIcons(logger, outputDirectory);
          IconExporter.ExportPortraits(logger, outputDirectory, MainExporter.ExportedCompanionGuids);
          logger.Flush();

          assetExportStatus = "Done - check icons/ and portraits/ in the output directory.";
        }
        catch (Exception exception) {
          assetExportStatus = $"Failed: {exception.Message}";
        }
      }

      if (!string.IsNullOrEmpty(assetExportStatus)) GUILayout.Label(assetExportStatus);
    }
    else {
      GUILayout.Label("Run the game to completion of blueprint load first.");
    }
  }

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

        outputDirectory = MainExporter.ExportAll();
      }
      catch (Exception e) {
        Log.Log(string.Concat("Failed to initialize.", e));
      }
    }
  }
}