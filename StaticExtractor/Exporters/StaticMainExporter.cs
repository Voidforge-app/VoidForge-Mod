using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Main orchestrator for the static export pipeline.
 *
 * Mirrors the runtime MainExporter but reads from disk instead of the game's blueprint cache.
 *
 * Loads the catalog and reference graph once, then delegates to per-type exporters.
 */
public static class StaticMainExporter {

  public static void ExportAll(
    string blueprintsPath,
    string stringsPath,
    string gameVersion,
    string gameRevision,
    string outputDirectory,
    StaticLogger logger,
    HashSet<string>? typeFilter = null,
    bool noReachability = false) {

    Directory.CreateDirectory(outputDirectory);

    logger.Info("export", $"version={gameVersion} revision={gameRevision}");
    logger.Info("export", $"blueprintsPath={blueprintsPath}");
    logger.Info("export", $"stringsPath={stringsPath}");
    logger.Info("export", $"outputDir={outputDirectory}");

    var effectiveTypeFilter = typeFilter ?? BuildDefaultTypeFilter();

    logger.Info("catalog", $"loading selected blueprint types ({effectiveTypeFilter.Count})...");
    var catalog = new BlueprintCatalog(blueprintsPath, effectiveTypeFilter, !noReachability);
    logger.Result("catalog", "blueprints loaded",
      ("total", catalog.TotalCount), ("active", catalog.ActiveCount),
      ("types", catalog.ByTypeName.Count));

    var graph = catalog.Graph;
    HashSet<string> reachableGuids;

    if (noReachability || graph == null) {
      logger.Info("reachability", "skipped (--no-reachability or --types filter)");

      // Mark all loaded items as reachable so the field is present and truthful.
      reachableGuids = catalog.ById.Keys.ToHashSet(StringComparer.Ordinal);
    }
    else {
      logger.Info("reachability", "building item reachability index...");
      reachableGuids = StaticReachabilityIndex.Build(catalog, graph);
      logger.Result("reachability", "index built", ("reachableItems", reachableGuids.Count));
    }

    // Collect localization keys for filtered string loading.
    // When running the full export (no --types filter), load ALL strings because
    // on-demand blueprint loading during extraction resolves references whose
    // string keys are not known ahead of time.
    var neededKeys = typeFilter != null ? CollectNeededKeys(catalog) : null;

    logger.Info("localization", "loading strings...");
    var stringCatalog = new StringCatalog(stringsPath, neededKeys);
    var resolver = new LocalizedStringResolver(stringCatalog);
    logger.Result("localization", "strings loaded", ("count", stringCatalog.Count));

    logger.Info("export", "running exporters...");

    StaticWeaponExporter.Export(catalog, resolver, reachableGuids,
      gameVersion, gameRevision, outputDirectory, logger);

    StaticArmorExporter.Export(catalog, resolver, reachableGuids,
      gameVersion, gameRevision, outputDirectory, logger);

    StaticEquipmentExporter.Export(catalog, resolver, reachableGuids,
      gameVersion, gameRevision, outputDirectory, logger);

    StaticCareerExporter.Export(catalog, resolver,
      gameVersion, gameRevision, outputDirectory, logger);

    StaticOriginExporter.Export(catalog, resolver,
      gameVersion, gameRevision, outputDirectory, logger);

    StaticFeaturesExporter.Export(catalog, resolver,
      gameVersion, gameRevision, outputDirectory, logger);

    StaticCompanionExporter.Export(catalog, resolver, gameVersion, gameRevision, outputDirectory, logger);
    StaticEncyclopediaExporter.Export(catalog, resolver, gameVersion, gameRevision, outputDirectory, logger);
    StaticBaselineWriter.Write(outputDirectory, gameVersion, gameRevision, logger);
    StaticAssetManifestWriter.Write(catalog, outputDirectory, gameVersion, gameRevision, logger);

    logger.Info("export", "all exports complete");
  }

  private static HashSet<string> BuildDefaultTypeFilter() {
    var types = new HashSet<string>(StringComparer.Ordinal) {
      // Items
      "BlueprintItemWeapon",
      "BlueprintItemArmor",
      "BlueprintSharedVendorTable",
      "BlueprintLoot",
      // Units (companions)
      "BlueprintUnit",
      // Encyclopedia
      "BlueprintEncyclopediaGlossaryEntry",
      // Careers and chargen paths
      "BlueprintCareerPath",
      "BlueprintOriginPath",
      // Features - the flat catalogue is built from careers, chargen, companions, and equipment.
      // BlueprintFeature is the base type for talents, abilities, and all selectable features.
      "BlueprintFeature",
      "BlueprintFeatureSelection",
      "BlueprintSelectionFeature",
      // FeatureList wrappers used by companions
      "BlueprintFeatureList",
      // BlueprintAbility is referenced by equipment-granted facts and occupation innates
      "BlueprintAbility",
      // BlueprintBuff is referenced by some features via AddFacts
      "BlueprintBuff",
      // Consumables (grenades, drugs, medkits) and mechadendrites grant features via AddFactToEquipmentWielder
      "BlueprintItemEquipmentUsable",
      "BlueprintItemMechadendrite",
      // Starship equipment grants features too (plating, weapons).
      "BlueprintItemArmorPlating",
      "BlueprintStarshipWeapon",
    };

    foreach (var equipmentType in StaticEquipmentExporter.SupportedTypes) types.Add(equipmentType);

    return types;
  }

  /**
   * Collects all localization keys and owner GUIDs referenced by loaded blueprints.
   *
   * Used to filter string file loading when only a subset of blueprints is loaded.
   *
   * Scans both direct fields (DisplayName, Description) and component localization fields (AddStringToFactName,
   * AdditionalDescriptionComponent, etc.).
   */
  private static HashSet<string> CollectNeededKeys(BlueprintCatalog catalog) {
    var keys = new HashSet<string>(StringComparer.Ordinal);

    foreach (var document in catalog.ById.Values) {
      CollectFieldKeys(document.Data.DisplayName, keys);
      CollectFieldKeys(document.Data.Description, keys);
      CollectFieldKeys(document.Data.LocalizedName, keys);
      CollectFieldKeys(document.Data.LocalizedDescription, keys);
      CollectComponentKeys(document, keys);
    }

    return keys;
  }

  private static void CollectFieldKeys(LocalizedStringField? field, HashSet<string> keys) {
    if (field == null) return;

    if (!string.IsNullOrEmpty(field.Key)) keys.Add(field.Key);

    if (field.Shared?.StringKey != null) keys.Add(field.Shared.StringKey);
    if (field.Shared?.AssetGuid != null) keys.Add(field.Shared.AssetGuid);

    // Direct stringkey/assetguid (SharedStringAsset pattern used by BlueprintUnit.LocalizedName)
    if (!string.IsNullOrEmpty(field.DirectStringKey)) keys.Add(field.DirectStringKey);
    if (!string.IsNullOrEmpty(field.DirectAssetGuid)) keys.Add(field.DirectAssetGuid);
  }

  /**
   * Scans components for localized string fields (AddStringToFactName, etc.).
   */
  private static void CollectComponentKeys(BlueprintDocument document, HashSet<string> keys) {
    if (document.Data.Components != null) {
      foreach (var component in document.Data.Components) {
        // AddStringToFactName and AddStringToFactDescription have m_AdditionalString.
        if (component.TryGetProperty("m_AdditionalString", out var additionalString)) {
          CollectJsonFieldKeys(additionalString, keys);
        }

        // AdditionalDescriptionComponent has AdditionalDescription.
        if (component.TryGetProperty("AdditionalDescription", out var additionalDescription)) {
          CollectJsonFieldKeys(additionalDescription, keys);
        }
      }
    }

    // Also scan ExtraFields for localized string fields like m_FlavorText.
    if (document.Data.ExtraFields == null) return;

    foreach (var (_, element) in document.Data.ExtraFields) {
      if (element.ValueKind == JsonValueKind.Object) {
        CollectJsonFieldKeys(element, keys);
      }
    }
  }

  /**
   * Extracts localization keys from a JsonElement that represents a LocalizedStringField.
   */
  private static void CollectJsonFieldKeys(JsonElement element, HashSet<string> keys) {
    if (element.ValueKind != JsonValueKind.Object) return;

    if (element.TryGetProperty("m_Key", out var keyProp)) {
      var key = keyProp.GetString();

      if (!string.IsNullOrEmpty(key)) keys.Add(key);
    }

    if (!element.TryGetProperty("Shared", out var sharedProp) ||
        sharedProp.ValueKind != JsonValueKind.Object) return;

    if (sharedProp.TryGetProperty("assetguid", out var assetGuidProp)) {
      var assetGuid = assetGuidProp.GetString();

      if (!string.IsNullOrEmpty(assetGuid)) keys.Add(assetGuid);
    }

    if (!sharedProp.TryGetProperty("stringkey", out var stringKeyProp)) return;

    var stringKey = stringKeyProp.GetString();

    if (!string.IsNullOrEmpty(stringKey)) keys.Add(stringKey);
  }
}