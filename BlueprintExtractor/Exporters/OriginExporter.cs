using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
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

  /**
   * GUID of the CustomCompanion BlueprintUnit - the base unit template used for player character creation. Its
   * OldWarhammer* fields define the base characteristics that every fresh player starts with before any chargen choices
   * are applied.
   */
  private const string CustomCompanionGuid = "baaff53a675a84f4983f1e2113b24552";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var extractedOrigins = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    var baseCharacteristics = ResolveBaseCharacteristics(logger);

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
          ["BaseCharacteristics"] = baseCharacteristics,
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

  /**
   * Reads the 9 base characteristic values from the CustomCompanion BlueprintUnit.
   * All player characters share the same base stats before homeworld/occupation modifiers.
   */
  private static Dictionary<string, int> ResolveBaseCharacteristics(ModLogger logger) {
    var baseUnit = BlueprintsCatalog.AllBlueprints<BlueprintUnit>()
      .FirstOrDefault(unit => unit.AssetGuid == CustomCompanionGuid);

    if (baseUnit != null) {
      return CharacteristicExtractor.ExtractBaseCharacteristics(baseUnit);
    }

    logger.Warn(Source, $"base unit not found guid={CustomCompanionGuid}, using default 30 for all stats");

    return new Dictionary<string, int> {
      ["WS"] = 30, ["BS"] = 30, ["STR"] = 30, ["TGH"] = 30, ["AGI"] = 30,
      ["INT"] = 30, ["PER"] = 30, ["WP"] = 30, ["FEL"] = 30,
    };
  }
}