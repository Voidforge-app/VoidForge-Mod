using System.Text.RegularExpressions;
using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items.Equipment;
using Kingmaker.UI.Models.Tooltip.Base;
using Kingmaker.UnitLogic.Progression.Features;
using Kingmaker.UnitLogic.Progression.Paths;

namespace BlueprintExtractor.Exporters;

/**
 * Flat catalogue of all player-facing features and talents reachable from careers, chargen paths,
 * the shared base character feature pool, and companion unit blueprints.
 * Features are deduplicated by GUID; each entry carries source attribution showing which
 * career(s)/chargen path(s)/companions offer it and in what role.
 */
public static class FeaturesExporter {
  private const string Source = "features";

  /**
   * GUID of BaseCharacterFeatures -- the hidden blueprint that populates the global Talent and
   * CommonTalent pools shared by every character regardless of career. Contains universal
   * features such as weapon proficiencies, Heavy/Power Armour Proficiency, Combat Master, etc.
   */
  private const string BaseCharacterFeaturesGuid = "f7cb6da6f8424aacb7414aa35cb06824";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var featureById = new Dictionary<string, Dictionary<string, object>>();
    var sourceKeysByGuid = new Dictionary<string, HashSet<string>>();

    CollectFromCareers(logger, featureById, sourceKeysByGuid);
    CollectFromChargenPaths(featureById, sourceKeysByGuid);
    CollectFromBaseCharacterFeatures(featureById, sourceKeysByGuid);
    CollectFromCompanions(logger, featureById, sourceKeysByGuid);
    CollectFromEquipment(logger, featureById, sourceKeysByGuid);

    LogMissingFeatureLinks(logger, featureById);

    var featureList = featureById.Values
      .OrderBy(featureData => featureData.TryGetValue("Name", out var value) ? value as string ?? "" : "")
      .ToList();

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, featureList);
    ExportWriter.WriteEnvelope(outputDirectory, "features", envelope);

    logger.Result(Source, "export done", ("count", featureList.Count));
  }

  private static void CollectFromCareers(
    ModLogger logger,
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var career in BlueprintsCatalog.AllBlueprints<BlueprintCareerPath>()) {
      string careerName;

      try {
        careerName = career.Name ?? career.name ?? "(unnamed)";
      }
      catch {
        careerName = career.name ?? "(unnamed)";
      }

      if (!ItemFilter.IsValidName(careerName)
          || careerName.StartsWith("Test ", StringComparison.OrdinalIgnoreCase)
          || career.HideNotAvailibleInUI) {
        logger.Info(Source, $"career skip name={careerName} guid={career.AssetGuid} reason=no-valid-name-or-hidden");

        continue;
      }

      var careerId = career.AssetGuid;

      // Selectable features from talent/ability pools (grouped by type)
      foreach (var groupEntry in RankEntryExtractor.BuildFeatureGroupMap(career)) {
        var source = new Dictionary<string, object> {
          ["Type"] = "careerSelection",
          ["CareerId"] = careerId,
          ["CareerName"] = career.Name ?? "",
          ["Group"] = groupEntry.Key,
        };

        foreach (var feature in groupEntry.Value)
          AccumulateFeature(featureById, sourceKeysByGuid, feature, source);
      }

      // Auto-granted features at specific career ranks
      var grantedSource = new Dictionary<string, object> {
        ["Type"] = "careerGranted",
        ["CareerId"] = careerId,
        ["CareerName"] = career.Name ?? "",
      };

      foreach (var feature in RankEntryExtractor.EnumerateGrantedFeatureBlueprints(career))
        AccumulateFeature(featureById, sourceKeysByGuid, feature, grantedSource);

      // Career-level AddFacts (auto-grants on the career blueprint itself, outside rank entries).
      // These are often unnamed wrapper features that bundle abilities like Inquisitor's Decree
      // or Go For the Throat. The recursive AddFacts traversal in AccumulateFeature handles depth.
      var careerAddFacts = RankEntryExtractor.EnumerateAddFactsBlueprints(career).ToList();
      logger.Info(Source, $"career name={careerName} guid={careerId} addFactsCount={careerAddFacts.Count}");

      foreach (var fact in careerAddFacts) {
        if (fact is SimpleBlueprint factBlueprint) {
          logger.Info(Source, $"  career-addfact guid={factBlueprint.AssetGuid} name={factBlueprint.name}");
        }

        AccumulateFeature(featureById, sourceKeysByGuid, fact, grantedSource);
      }
    }
  }

  private static void CollectFromChargenPaths(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var origin in BlueprintsCatalog.AllBlueprints<BlueprintOriginPath>()) {
      var isPregenPath = origin.name.IndexOf("Pregen", StringComparison.OrdinalIgnoreCase) >= 0;
      var groupMap = RankEntryExtractor.BuildFeatureGroupMap(origin);

      if (!groupMap.ContainsKey("ChargenHomeworld") || isPregenPath) continue;

      foreach (var groupEntry in groupMap) {
        // Omit ChargenPathId: all player-facing paths share the same feature GUIDs per group,
        // so including the path ID would produce ×4 duplicate sources for the same feature.
        var source = new Dictionary<string, object> {
          ["Type"] = "chargen",
          ["Group"] = groupEntry.Key,
        };

        foreach (var feature in groupEntry.Value) {
          AccumulateFeature(featureById, sourceKeysByGuid, feature, source);

          // Occupation features carry their own sub-features: innate abilities via AddFacts
          // and occupation-specific talent pool additions via AddFeaturesToLevelUp.
          // Neither appears in the career path traversal, so we must collect them here.
          if (groupEntry.Key == "ChargenOccupation") {
            AccumulateOccupationSubFeatures(featureById, sourceKeysByGuid, feature);
          }
        }
      }
    }
  }

  private static void CollectFromBaseCharacterFeatures(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    var baseBlueprint = BlueprintsCatalog.AllBlueprints<BlueprintFeature>()
      .FirstOrDefault(blueprint => blueprint.AssetGuid == BaseCharacterFeaturesGuid);

    if (baseBlueprint == null) return;

    foreach (var groupEntry in RankEntryExtractor.BuildFeatureGroupMap(baseBlueprint)) {
      if (groupEntry.Key is "Skill" or "Attribute") continue;

      var source = new Dictionary<string, object> {
        ["Type"] = "baseCharacter",
        ["Group"] = groupEntry.Key,
      };

      foreach (var feature in groupEntry.Value)
        AccumulateFeature(featureById, sourceKeysByGuid, feature, source);
    }
  }

  private static void CollectFromCompanions(
    ModLogger logger,
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var unit in BlueprintsCatalog.AllBlueprints<BlueprintUnit>()) {
      if (!UnitFilter.IsBaseCompanionUnit(unit)) continue;

      var unitName = unit.name ?? unit.AssetGuid;

      var source = new Dictionary<string, object> {
        ["Type"] = "companionGranted",
        ["CompanionId"] = unit.AssetGuid,
      };

      // Direct unit facts (e.g. origin talents added straight to the unit blueprint).
      var directFacts = RankEntryExtractor.EnumerateUnitDirectFacts(unit).ToList();
      logger.Info(Source, $"companion unit={unitName} directUnitFacts={directFacts.Count}");

      foreach (var fact in directFacts) {
        if (fact is SimpleBlueprint directBlueprint) {
          logger.Info(Source, $"  direct-fact guid={directBlueprint.AssetGuid} name={directBlueprint.name}");
        }

        AccumulateFeature(featureById, sourceKeysByGuid, fact, source);
      }

      // Unique abilities nested in the FeatureList's AddFacts components
      // (e.g. Argenta's Repentia abilities, Cassia's Lidless Stare).
      // The FeatureList itself is unnamed and filtered out, so we traverse its AddFacts directly.
      var featureList = RankEntryExtractor.FindCompanionFeatureList(unit);

      if (featureList == null) {
        logger.Info(Source, $"companion unit={unitName} guid={unit.AssetGuid} featureList=none");

        continue;
      }

      var featureListGuid = featureList is SimpleBlueprint fl ? fl.AssetGuid : "?";
      var featureListFacts = RankEntryExtractor.EnumerateAddFactsBlueprints(featureList).ToList();
      var careerPaths = RankEntryExtractor.EnumerateFeatureListCareerPaths(featureList).ToList();

      logger.Info(Source,
        $"companion unit={unitName} guid={unit.AssetGuid} featureList={featureListGuid} directFacts={featureListFacts.Count} careerPaths={careerPaths.Count}");

      foreach (var fact in featureListFacts) {
        if (fact is SimpleBlueprint factBlueprint) {
          logger.Info(Source, $"  featureList-fact guid={factBlueprint.AssetGuid} name={factBlueprint.name}");
        }

        AccumulateFeature(featureById, sourceKeysByGuid, fact, source);
      }

      // Companion chargen occupation: companion-specific occupations (e.g. SpaceMarine for Ulfar)
      // are pre-selected in ApplyCareerPath.Selections and never appear in the player chargen paths.
      // Their AddFacts contain innate abilities unique to the companion.
      foreach (var occupation in RankEntryExtractor.EnumerateFeatureListOccupations(featureList))
        AccumulateOccupationSubFeatures(featureById, sourceKeysByGuid, occupation);

      // Auto-granted features from the companion's career path(s), including DLC-exclusive careers
      // not reached by CollectFromCareers (e.g. Kibellah's Cannoness career is not IsAvailable
      // for players but still grants her rank abilities like Death from Above).
      foreach (var careerPath in careerPaths) {
        var careerGuid = careerPath is SimpleBlueprint cp ? cp.AssetGuid : "?";
        var careerName = ResolveCareerName(careerPath);
        var careerAddFacts = RankEntryExtractor.EnumerateAddFactsBlueprints(careerPath).ToList();

        logger.Info(Source,
          $"  companion-career unit={unitName} career={careerName} guid={careerGuid} addFacts={careerAddFacts.Count}");

        foreach (var grantedFeature in RankEntryExtractor.EnumerateGrantedFeatureBlueprints(careerPath))
          AccumulateFeature(featureById, sourceKeysByGuid, grantedFeature, source);

        foreach (var fact in careerAddFacts) {
          if (fact is SimpleBlueprint factBlueprint) {
            logger.Info(Source, $"    career-addfact guid={factBlueprint.AssetGuid} name={factBlueprint.name}");
          }

          AccumulateFeature(featureById, sourceKeysByGuid, fact, source);
        }
      }
    }
  }

  private static void CollectFromEquipment(
    ModLogger logger,
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    var itemsScanned = 0;
    var factsFound = 0;

    foreach (var item in BlueprintsCatalog.AllBlueprints<BlueprintItemEquipment>()) {
      itemsScanned++;

      string itemName;

      try {
        itemName = ((IUIDataProvider)item)?.Name ?? item.name ?? item.AssetGuid;
      }
      catch {
        itemName = item.name ?? item.AssetGuid;
      }

      var source = new Dictionary<string, object> {
        ["Type"] = "itemGranted",
        ["ItemId"] = item.AssetGuid,
        ["ItemName"] = itemName,
      };

      var itemFacts = RankEntryExtractor.EnumerateEquipmentGrantedFacts(item).ToList();

      if (itemFacts.Count <= 0) continue;

      logger.Info(Source, $"equipment item={itemName} guid={item.AssetGuid} grantedFacts={itemFacts.Count}");

      foreach (var fact in itemFacts) {
        if (fact is SimpleBlueprint factBlueprint) {
          logger.Info(Source, $"  item-fact guid={factBlueprint.AssetGuid} name={factBlueprint.name}");
        }

        // Skip facts with no player-visible description -- these are internal tracking
        // markers (e.g. Augment_*_Equipped_Feature) that carry no useful build-planner data.
        if (fact is IUIDataProvider factUiData) {
          string factDescription;

          try {
            factDescription = FeatureExtractor.SanitizeLocalizedString(factUiData.Description);
          }
          catch {
            factDescription = "";
          }

          if (string.IsNullOrEmpty(factDescription)) continue;
        }

        AccumulateFeature(featureById, sourceKeysByGuid, fact, source);
        factsFound++;
      }
    }

    logger.Info(Source, $"equipment scan done itemsScanned={itemsScanned} totalFactsFound={factsFound}");
  }

  private static void AccumulateOccupationSubFeatures(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid,
    object occupationBlueprint) {
    if (occupationBlueprint is not SimpleBlueprint occupationFeature) return;

    var occupationId = occupationFeature.AssetGuid;
    var occupationName = (occupationBlueprint as IUIDataProvider)?.Name ?? "";

    // Innate abilities auto-granted by the occupation (e.g. "You. Serve Me." for Noble).
    var innateSource = new Dictionary<string, object> {
      ["Type"] = "occupationGranted",
      ["OccupationId"] = occupationId,
      ["OccupationName"] = occupationName,
    };

    foreach (var innateFeature in RankEntryExtractor.EnumerateAddFactsBlueprints(occupationBlueprint))
      AccumulateFeature(featureById, sourceKeysByGuid, innateFeature, innateSource);

    // Occupation-specific talent pool additions (Talent/CommonTalent/FirstCareerTalent/etc.).
    // Skill and Attribute groups are structural advancement trackers, not player-selectable.
    foreach (var groupEntry in RankEntryExtractor.BuildFeatureGroupMap(occupationBlueprint)) {
      if (groupEntry.Key is "Skill" or "Attribute") continue;

      var poolSource = new Dictionary<string, object> {
        ["Type"] = "occupationSelection",
        ["OccupationId"] = occupationId,
        ["OccupationName"] = occupationName,
        ["Group"] = groupEntry.Key,
      };

      foreach (var poolFeature in groupEntry.Value)
        AccumulateFeature(featureById, sourceKeysByGuid, poolFeature, poolSource);
    }
  }

  /**
   * Scans all collected feature descriptions for cross-feature links (f:GUID) and logs any
   * referenced GUIDs that are not in the feature map. For each missing GUID it probes the
   * live blueprint cache to report its type and resolved name so we can diagnose filtering.
   */
  private static void LogMissingFeatureLinks(
    ModLogger logger,
    Dictionary<string, Dictionary<string, object>> featureById) {
    var linkedGuids = new HashSet<string>();
    var linkPattern = new Regex(@"f:([0-9a-f]{32})");

    foreach (var featureData in featureById.Values) {
      if (!featureData.TryGetValue("Description", out var rawDescription)) continue;

      foreach (Match match in linkPattern.Matches(rawDescription?.ToString() ?? ""))
        linkedGuids.Add(match.Groups[1].Value);
    }

    var missingGuids = linkedGuids.Where(guid => !featureById.ContainsKey(guid)).ToList();

    if (missingGuids.Count == 0) {
      logger.Info(Source, "feature-link check: all linked GUIDs resolved");

      return;
    }

    logger.Info(Source, $"feature-link check: {missingGuids.Count} unresolved f:GUID links");

    // Single forward pass: build reverse map of grantedGuid -> granterName for all missing GUIDs.
    var missingSet = new HashSet<string>(missingGuids);
    var grantersByGuid = new Dictionary<string, List<string>>();

    foreach (var candidate in BlueprintsCatalog.AllBlueprints<SimpleBlueprint>())
    foreach (var granted in RankEntryExtractor.EnumerateAddFactsBlueprints(candidate).OfType<SimpleBlueprint>()) {
      if (!missingSet.Contains(granted.AssetGuid)) continue;

      if (!grantersByGuid.TryGetValue(granted.AssetGuid, out var granterList)) {
        granterList = [];
        grantersByGuid[granted.AssetGuid] = granterList;
      }

      granterList.Add(candidate.name ?? candidate.AssetGuid);
    }

    foreach (var guid in missingGuids) {
      var blueprint = BlueprintsCatalog.AllBlueprints<SimpleBlueprint>().FirstOrDefault(bp => bp.AssetGuid == guid);

      if (blueprint == null) {
        logger.Info(Source, $"  missing guid={guid} blueprint=NOT_IN_CACHE");

        continue;
      }

      string resolvedName;

      try {
        resolvedName = (blueprint as IUIDataProvider)?.Name ?? "(no IUIDataProvider)";
      }
      catch (Exception exception) {
        resolvedName = $"(exception: {exception.Message})";
      }

      logger.Info(Source,
        $"  missing guid={guid} type={blueprint.GetType().Name} name={resolvedName} internalName={blueprint.name}");

      var granters = grantersByGuid.TryGetValue(guid, out var granterList) ? granterList : [];
      logger.Info(Source,
        granters.Count > 0
          ? $"    granted-by: {string.Join(", ", granters)}"
          : "    granted-by: NONE FOUND via AddFacts");
    }
  }

  /**
   * Accumulates a blueprint as a player-facing feature, attributing it to the given source.
   * Unnamed blueprints are traversed via AddFacts to capture named abilities nested inside
   * structural wrappers (e.g. Inquisitor's Decree inside a nameless career-level wrapper).
   * Named blueprints are NOT traversed -- their AddFacts children are implementation details,
   * not separately selectable features (preventing duplicates like "Go for the Throat").
   * Cycle detection is provided by ancestorGuids, which tracks GUIDs on the current call stack.
   */
  private static void AccumulateFeature(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid,
    object blueprint,
    Dictionary<string, object> source,
    HashSet<string> ancestorGuids = null) {
    if (blueprint is not SimpleBlueprint simpleBlueprint) return;

    var guid = simpleBlueprint.AssetGuid;

    if (string.IsNullOrEmpty(guid)) return;

    // Soul marks are story-alignment trackers, not build choices.
    // BlueprintShipPostExpertise covers all SSExpertise_ starship features (starship is v2).
    var blueprintTypeName = simpleBlueprint.GetType().Name;

    if (blueprintTypeName is "BlueprintSoulMark" or "BlueprintShipPostExpertise") return;

    if (!featureById.TryGetValue(guid, out var featureData)) {
      featureData = FeatureExtractor.ExtractFeature(blueprint);

      var featureName = featureData.ContainsKey("Name") ? featureData["Name"] as string ?? "" : "";
      var isNamed = ItemFilter.IsValidName(featureName);

      if (!isNamed) {
        // Unnamed blueprint: traverse its AddFacts to find named abilities inside structural
        // wrappers. Named blueprints are the selectable features; they are not traversed.
        var path = ancestorGuids ?? new HashSet<string>();

        if (!path.Add(guid)) return;

        foreach (var nestedFact in RankEntryExtractor.EnumerateAddFactsBlueprints(blueprint))
          AccumulateFeature(featureById, sourceKeysByGuid, nestedFact, source, path);
        path.Remove(guid);

        return;
      }

      featureData["Sources"] = new List<Dictionary<string, object>>();
      featureById[guid] = featureData;
      sourceKeysByGuid[guid] = [];
    }

    // Deduplicate sources: a feature may appear in the same career pool multiple times
    // across different AddFeaturesToLevelUp components that share the same group.
    var sourceKey = string.Join(":",
      source.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value?.ToString() ?? ""}"));

    if (sourceKeysByGuid[guid].Add(sourceKey)) {
      ((List<Dictionary<string, object>>)featureData["Sources"]).Add(source);
    }
  }

  private static string ResolveCareerName(object blueprint) {
    if (blueprint is not BlueprintCareerPath career) return "?";

    try {
      return career.Name ?? career.name ?? "?";
    }
    catch {
      return career.name ?? "?";
    }
  }
}