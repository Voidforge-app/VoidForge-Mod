using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Flat catalogue of all player-facing features reachable from careers, chargen paths,
 * the shared base character feature pool, companion unit blueprints, and equipment.
 * Mirrors the runtime FeaturesExporter, reading from .jbp files.
 * Features are deduplicated by GUID; each entry carries source attribution.
 */
public static class StaticFeaturesExporter {

  /**
   * GUID of BaseCharacterFeatures - the hidden blueprint that populates the global Talent and
   * CommonTalent pools shared by every character regardless of career.
   */
  private const string BaseCharacterFeaturesGuid = "f7cb6da6f8424aacb7414aa35cb06824";

  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    string gameVersion,
    string gameRevision,
    string outputDirectory,
    StaticLogger logger) {
    var featureById = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
    var sourceKeysByGuid = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    CollectFromCareers(catalog, resolver, featureById, sourceKeysByGuid);
    CollectFromChargenPaths(catalog, resolver, featureById, sourceKeysByGuid);
    CollectFromBaseCharacterFeatures(catalog, resolver, featureById, sourceKeysByGuid);
    CollectFromCompanions(catalog, resolver, featureById, sourceKeysByGuid);
    CollectFromEquipment(catalog, resolver, featureById, sourceKeysByGuid);

    var featureList = featureById.Values
      .OrderBy(feature => feature.TryGetValue("name", out var value) ? value as string ?? "" : "")
      .ToList();

    ExportWriter.WriteEnvelope(outputDirectory, "features",
      ExportEnvelope<Dictionary<string, object?>>.Create(gameVersion, gameRevision, featureList));

    logger.Result("features", "static export done", ("count", featureList.Count));
  }

  private static void CollectFromCareers(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var career in catalog.ByType("BlueprintCareerPath")) {
      var careerName = LocalizedStringResolver.Sanitize(resolver.ResolveName(career));

      if (!IsValidName(careerName) ||
          careerName.StartsWith("Test ", StringComparison.OrdinalIgnoreCase) ||
          GetExtraFieldBool(career, "HideNotAvailibleInUI")) {
        continue;
      }

      var careerId = career.AssetId;

      // Selectable features from talent/ability pools
      foreach (var groupEntry in StaticRankEntryExtractor.BuildFeatureGroupMap(career, catalog)) {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal) {
          ["type"] = "careerSelection",
          ["careerId"] = careerId,
          ["careerName"] = careerName,
          ["group"] = groupEntry.Key,
        };

        foreach (var feature in groupEntry.Value) {
          AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, feature, source);
        }
      }

      // Auto-granted features at specific career ranks
      var grantedSource = new Dictionary<string, object?>(StringComparer.Ordinal) {
        ["type"] = "careerGranted",
        ["careerId"] = careerId,
        ["careerName"] = careerName,
      };

      foreach (var feature in StaticRankEntryExtractor.EnumerateGrantedFeatureBlueprints(career, catalog)) {
        AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, feature, grantedSource);
      }

      // Career-level AddFacts
      foreach (var fact in StaticRankEntryExtractor.EnumerateAddFactsBlueprints(career, catalog)) {
        AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, fact, grantedSource);
      }
    }
  }

  private static void CollectFromChargenPaths(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    // Include shadow-deleted: the runtime does not filter by shadow-deleted status,
    // it filters by content (ChargenHomeworld + not Pregen).
    foreach (var origin in catalog.ByType("BlueprintOriginPath", true)) {
      var isPregenPath = origin.AssetName.Contains("Pregen", StringComparison.OrdinalIgnoreCase);
      var groupMap = StaticRankEntryExtractor.BuildFeatureGroupMap(origin, catalog);

      if (!groupMap.ContainsKey("ChargenHomeworld") || isPregenPath) continue;

      foreach (var groupEntry in groupMap) {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal) {
          ["type"] = "chargen",
          ["group"] = groupEntry.Key,
        };

        foreach (var feature in groupEntry.Value) {
          AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, feature, source);

          // Occupation features carry sub-features via AddFacts and AddFeaturesToLevelUp
          if (groupEntry.Key == "ChargenOccupation") {
            AccumulateOccupationSubFeatures(catalog, resolver, featureById, sourceKeysByGuid, feature);
          }
        }
      }
    }
  }

  private static void CollectFromBaseCharacterFeatures(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    var baseBlueprint = catalog.GetById(BaseCharacterFeaturesGuid);
    if (baseBlueprint == null) return;

    foreach (var groupEntry in StaticRankEntryExtractor.BuildFeatureGroupMap(baseBlueprint, catalog)) {
      if (groupEntry.Key is "Skill" or "Attribute") continue;

      var source = new Dictionary<string, object?>(StringComparer.Ordinal) {
        ["type"] = "baseCharacter",
        ["group"] = groupEntry.Key,
      };

      foreach (var feature in groupEntry.Value) {
        AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, feature, source);
      }
    }
  }

  private static void CollectFromCompanions(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var unit in catalog.ByType("BlueprintUnit")) {
      if (!IsCanonicalCompanion(unit)) continue;

      var source = new Dictionary<string, object?>(StringComparer.Ordinal) {
        ["type"] = "companionGranted",
        ["companionId"] = unit.AssetId,
      };

      // Direct unit facts
      foreach (var fact in StaticRankEntryExtractor.EnumerateUnitDirectFacts(unit, catalog)) {
        AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, fact, source);
      }

      // Unique abilities nested in the FeatureList's AddFacts components
      var featureList = StaticRankEntryExtractor.FindCompanionFeatureList(unit, catalog);
      if (featureList == null) continue;

      foreach (var fact in StaticRankEntryExtractor.EnumerateAddFactsBlueprints(featureList, catalog)) {
        AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, fact, source);
      }

      // Companion chargen occupation sub-features
      foreach (var occupation in StaticRankEntryExtractor.EnumerateFeatureListOccupations(featureList, catalog)) {
        AccumulateOccupationSubFeatures(catalog, resolver, featureById, sourceKeysByGuid, occupation);
      }

      // Auto-granted features from companion career paths
      foreach (var careerPath in StaticRankEntryExtractor.EnumerateFeatureListCareerPaths(featureList, catalog)) {
        foreach (var grantedFeature in StaticRankEntryExtractor.EnumerateGrantedFeatureBlueprints(careerPath, catalog)) {
          AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, grantedFeature, source);
        }

        foreach (var fact in StaticRankEntryExtractor.EnumerateAddFactsBlueprints(careerPath, catalog)) {
          AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, fact, source);
        }
      }
    }
  }

  private static void CollectFromEquipment(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    var equipmentTypes = new[] {
      "BlueprintItemEquipmentFeet",
      "BlueprintItemEquipmentGloves",
      "BlueprintItemEquipmentHead",
      "BlueprintItemEquipmentNeck",
      "BlueprintItemEquipmentRing",
      "BlueprintItemEquipmentShoulders",
      "BlueprintItemAugment",
      "BlueprintItemShield",
      "BlueprintItemEquipmentPetProtocol",
      "BlueprintItemWeapon",
      "BlueprintItemArmor",
      // Consumables: grenades, drugs, medkits. These grant abilities via AddFactToEquipmentWielder.
      "BlueprintItemEquipmentUsable",
      // Mechadendrites are equipment-granted abilities.
      "BlueprintItemMechadendrite",
      // Starship equipment grants features too (plating, weapons).
      "BlueprintItemArmorPlating",
      "BlueprintStarshipWeapon",
    };

    foreach (var typeName in equipmentTypes) {
      foreach (var item in catalog.ByType(typeName)) {
        var itemName = LocalizedStringResolver.Sanitize(resolver.ResolveName(item));

        var source = new Dictionary<string, object?>(StringComparer.Ordinal) {
          ["type"] = "itemGranted",
          ["itemId"] = item.AssetId,
          ["itemName"] = itemName,
        };

        foreach (var fact in StaticRankEntryExtractor.EnumerateEquipmentGrantedFacts(item, catalog)) {
          // Skip facts with no player-visible description
          var description = LocalizedStringResolver.Sanitize(resolver.ResolveDescription(fact));
          if (string.IsNullOrEmpty(description)) continue;

          AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, fact, source);
        }
      }
    }
  }

  private static void AccumulateOccupationSubFeatures(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid,
    BlueprintDocument occupation) {
    var occupationId = occupation.AssetId;
    var occupationName = LocalizedStringResolver.Sanitize(resolver.ResolveName(occupation));

    // Innate abilities auto-granted by the occupation
    var innateSource = new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["type"] = "occupationGranted",
      ["occupationId"] = occupationId,
      ["occupationName"] = occupationName,
    };

    foreach (var innateFeature in StaticRankEntryExtractor.EnumerateAddFactsBlueprints(occupation, catalog)) {
      AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, innateFeature, innateSource);
    }

    // Occupation-specific talent pool additions
    foreach (var groupEntry in StaticRankEntryExtractor.BuildFeatureGroupMap(occupation, catalog)) {
      if (groupEntry.Key is "Skill" or "Attribute") continue;

      var poolSource = new Dictionary<string, object?>(StringComparer.Ordinal) {
        ["type"] = "occupationSelection",
        ["occupationId"] = occupationId,
        ["occupationName"] = occupationName,
        ["group"] = groupEntry.Key,
      };

      foreach (var poolFeature in groupEntry.Value) {
        AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, poolFeature, poolSource);
      }
    }
  }

  /**
   * Accumulates a blueprint as a player-facing feature, attributing it to the given source.
   * Unnamed blueprints are traversed via AddFacts to capture named abilities nested inside
   * structural wrappers. Named blueprints are NOT traversed.
   */
  private static void AccumulateFeature(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, Dictionary<string, object?>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid,
    BlueprintDocument blueprint,
    Dictionary<string, object?> source,
    HashSet<string>? ancestorGuids = null) {
    var guid = blueprint.AssetId;
    if (string.IsNullOrEmpty(guid)) return;

    // Soul marks and starship expertise are not build choices
    var typeName = blueprint.TypeName;
    if (typeName is "BlueprintSoulMark" or "BlueprintShipPostExpertise") return;

    if (!featureById.TryGetValue(guid, out var featureData)) {
      featureData = StaticFeatureExtractor.ExtractFeature(blueprint, resolver, catalog);

      var featureName = featureData.TryGetValue("name", out var nameValue) ? nameValue as string ?? "" : "";
      var isNamed = IsValidName(featureName);

      if (!isNamed) {
        // Unnamed blueprint: traverse its AddFacts to find named abilities inside
        var path = ancestorGuids ?? new HashSet<string>(StringComparer.Ordinal);

        if (!path.Add(guid)) return;

        foreach (var nestedFact in StaticRankEntryExtractor.EnumerateAddFactsBlueprints(blueprint, catalog)) {
          AccumulateFeature(catalog, resolver, featureById, sourceKeysByGuid, nestedFact, source, path);
        }

        path.Remove(guid);
        return;
      }

      featureData["sources"] = new List<Dictionary<string, object?>>();
      featureById[guid] = featureData;
      sourceKeysByGuid[guid] = new HashSet<string>(StringComparer.Ordinal);
    }

    // Deduplicate sources
    var sourceKey = string.Join(":",
      source.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value?.ToString() ?? ""}"));

    if (sourceKeysByGuid[guid].Add(sourceKey)) {
      ((List<Dictionary<string, object?>>)featureData["sources"]!).Add(source);
    }
  }

  private static bool IsCanonicalCompanion(BlueprintDocument document) {
    var name = document.AssetName;
    return name.EndsWith("Companion", StringComparison.Ordinal) &&
           !name.Contains("_Ch", StringComparison.OrdinalIgnoreCase) &&
           !name.StartsWith("TEST", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsValidName(string name) {
    if (string.IsNullOrWhiteSpace(name)) return false;
    if (name == "<null>") return false;

    return !name.StartsWith("[unknown key:");
  }

  private static bool GetExtraFieldBool(BlueprintDocument document, string fieldName) {
    return document.Data.ExtraFields != null &&
           document.Data.ExtraFields.TryGetValue(fieldName, out var element) &&
           element.ValueKind == JsonValueKind.True;
  }
}