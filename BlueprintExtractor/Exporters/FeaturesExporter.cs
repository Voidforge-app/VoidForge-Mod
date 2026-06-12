using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Progression.Paths;

namespace BlueprintExtractor.Exporters;

/**
 * Flat catalogue of all player-facing features and talents reachable from careers and chargen paths.
 * Features are deduplicated by GUID; each entry carries source attribution showing which
 * career(s)/chargen path(s) offer it and in what role (selectable talent, auto-granted, chargen option).
 */
public static class FeaturesExporter {
  private const string Source = "features";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var featureById = new Dictionary<string, Dictionary<string, object>>();
    var sourceKeysByGuid = new Dictionary<string, HashSet<string>>();

    CollectFromCareers(featureById, sourceKeysByGuid);
    CollectFromChargenPaths(featureById, sourceKeysByGuid);

    var featureList = featureById.Values
      .OrderBy(featureData => featureData.TryGetValue("Name", out var value) ? value as string ?? "" : "")
      .ToList();

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, featureList);
    ExportWriter.WriteEnvelope(outputDirectory, "features", envelope);

    logger.Result(Source, "export done", ("count", featureList.Count));
  }

  private static void CollectFromCareers(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var career in BlueprintsCatalog.AllBlueprints<BlueprintCareerPath>()) {
      if (!career.IsAvailable) continue;

      var careerId = career.AssetGuid;

      // Selectable features from talent/ability pools (grouped by type)
      foreach (var groupEntry in RankEntryExtractor.BuildFeatureGroupMap(career)) {
        var source = new Dictionary<string, object> {
          ["Type"] = "careerSelection",
          ["CareerId"] = careerId,
          ["Group"] = groupEntry.Key,
        };

        foreach (var feature in groupEntry.Value)
          AccumulateFeature(featureById, sourceKeysByGuid, feature, source);
      }

      // Auto-granted features at specific career ranks
      var grantedSource = new Dictionary<string, object> {
        ["Type"] = "careerGranted",
        ["CareerId"] = careerId,
      };

      foreach (var feature in RankEntryExtractor.EnumerateGrantedFeatureBlueprints(career))
        AccumulateFeature(featureById, sourceKeysByGuid, feature, grantedSource);
    }
  }

  private static void CollectFromChargenPaths(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid) {
    foreach (var origin in BlueprintsCatalog.AllBlueprints<BlueprintOriginPath>()) {
      var isPregenPath = origin.name.IndexOf("Pregen", StringComparison.OrdinalIgnoreCase) >= 0;
      var groupMap = RankEntryExtractor.BuildFeatureGroupMap(origin);

      if (!groupMap.ContainsKey("ChargenHomeworld") || isPregenPath) continue;

      var chargenPathId = origin.AssetGuid;

      foreach (var groupEntry in groupMap) {
        var source = new Dictionary<string, object> {
          ["Type"] = "chargen",
          ["ChargenPathId"] = chargenPathId,
          ["Group"] = groupEntry.Key,
        };

        foreach (var feature in groupEntry.Value)
          AccumulateFeature(featureById, sourceKeysByGuid, feature, source);
      }
    }
  }

  private static void AccumulateFeature(
    Dictionary<string, Dictionary<string, object>> featureById,
    Dictionary<string, HashSet<string>> sourceKeysByGuid,
    object blueprint,
    Dictionary<string, object> source) {
    if (blueprint is not SimpleBlueprint simpleBlueprint) return;

    var guid = simpleBlueprint.AssetGuid;

    if (string.IsNullOrEmpty(guid)) return;

    if (!featureById.TryGetValue(guid, out var featureData)) {
      featureData = FeatureExtractor.ExtractFeature(blueprint);

      // Skip unnamed features - they are structural selector containers (attribute allocation,
      // skill picks, class-level trackers) with no player-visible identity.
      var featureName = featureData.ContainsKey("Name") ? featureData["Name"] as string ?? "" : "";

      if (!ItemFilter.IsValidName(featureName)) return;

      featureData["Sources"] = new List<Dictionary<string, object>>();
      featureById[guid] = featureData;
      sourceKeysByGuid[guid] = [];
    }

    // Deduplicate sources: a feature may appear in the same career pool multiple times
    // across different AddFeaturesToLevelUp components that share the same group.
    var sourceKey = string.Join(":", source.Values.Select(value => value?.ToString() ?? ""));

    if (sourceKeysByGuid[guid].Add(sourceKey)) {
      ((List<Dictionary<string, object>>)featureData["Sources"]).Add(source);
    }
  }
}