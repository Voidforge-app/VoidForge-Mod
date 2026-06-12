using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.UnitLogic.Progression.Paths;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts the 4 player-facing BlueprintOriginPath chargen flows.
 * Filtered to paths that include ChargenHomeworld - excludes appearance/pregen/deleted flows.
 * Output groups features by chargen category (homeworld, occupation, darkest hour, etc.)
 * rather than by rank entry, since chargen options are flat pools, not sequential progression.
 */
public static class OriginExporter {
  private const string Source = "origins";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var extractedOrigins = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var origin in BlueprintsCatalog.AllBlueprints<BlueprintOriginPath>())
      try {
        var chargenGroups = FeatureExtractor.ExtractChargenGroups(origin);

        // Filter 1: must define homeworld selection (excludes appearance-only flows).
        // Filter 2: asset name must not contain "Pregen" (excludes premade/pregen loading paths).
        var isPregenPath = origin.name.IndexOf("Pregen", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!chargenGroups.ContainsKey("ChargenHomeworld") || isPregenPath) {
          skippedCount++;

          continue;
        }

        var originData = new Dictionary<string, object> {
          ["Id"] = origin.AssetGuid,
          ["ChargenGroups"] = chargenGroups,
        };

        extractedOrigins.Add(originData);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={origin.AssetGuid} reason={exception.Message}");
      }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedOrigins);
    ExportWriter.WriteEnvelope(outputDirectory, "origins", envelope);

    logger.Result(Source, "export done", ("count", extractedOrigins.Count), ("filtered", skippedCount));
  }
}