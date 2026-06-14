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
  private static Settings settings;
  private static bool blueprintsReady;
  private static string cachedGameVersion;
  private static string lastOutputDirectory;
  private static string exportStatus = "";
  private static string assetExportStatus = "";

  public static bool Load(UnityModManager.ModEntry modEntry) {
    Log = modEntry.Logger;
    settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
    modEntry.OnGUI = OnGUI;
    modEntry.OnSaveGUI = OnSaveGUI;
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

  private static void OnSaveGUI(UnityModManager.ModEntry modEntry) {
    settings.Save(modEntry);
  }

  private static void OnGUI(UnityModManager.ModEntry modEntry) {
    GUILayout.Label("VoidForge Blueprint Extractor");
    GUILayout.Space(4);

    GUILayout.BeginHorizontal();
    GUILayout.Label("Base output directory:", GUILayout.Width(160));
    settings.BaseOutputDirectory = GUILayout.TextField(settings.BaseOutputDirectory, GUILayout.Width(400));
    GUILayout.EndHorizontal();

    var versionLabel = blueprintsReady ? cachedGameVersion : "<version>";
    var resolvedPath = ExportWriter.ResolveOutputDirectory(versionLabel, settings.BaseOutputDirectory);
    GUILayout.Label($"Resolved path: {resolvedPath}");
    GUILayout.Label("(leave blank to use default: Documents/VoidForge/<version>)");

    GUILayout.Space(8);

    GUI.enabled = blueprintsReady;

    if (GUILayout.Button("Export All", GUILayout.ExpandWidth(true))) {
      try {
        lastOutputDirectory = MainExporter.ExportAll(settings.BaseOutputDirectory);
        exportStatus = $"Done. Output: {lastOutputDirectory}";
      }
      catch (Exception exception) {
        exportStatus = $"Failed: {exception.Message}";
      }
    }

    GUI.enabled = true;

    if (!blueprintsReady) GUILayout.Label("Waiting for blueprints to load...");
    if (!string.IsNullOrEmpty(exportStatus)) GUILayout.Label(exportStatus);

    GUILayout.Space(8);

    if (string.IsNullOrEmpty(lastOutputDirectory)) return;

    if (GUILayout.Button("Export Icons + Portraits", GUILayout.ExpandWidth(true))) {
      try {
        var logger = new ModLogger(lastOutputDirectory, "icons");

        IconExporter.ExportIcons(logger, lastOutputDirectory);
        IconExporter.ExportPortraits(logger, lastOutputDirectory, MainExporter.ExportedCompanionGuids);
        logger.Flush();

        assetExportStatus = "Icon & Portrait export done";
      }
      catch (Exception exception) {
        assetExportStatus = $"Failed: {exception.Message}";
      }
    }

    if (!string.IsNullOrEmpty(assetExportStatus)) GUILayout.Label(assetExportStatus);
  }

  [HarmonyPatch(typeof(BlueprintsCache))]
  public static class BlueprintsCaches_Patch {
    private static bool Initialized;

    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(BlueprintsCache.Init))]
    [HarmonyPostfix]
    public static void Init_Postfix() {
      if (Initialized) return;

      Initialized = true;
      blueprintsReady = true;
      cachedGameVersion = GameVersionHelper.GetVersion();

      Log.Log("Blueprints cache ready. Use the UMM panel to export.");
    }
  }
}