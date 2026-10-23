using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Extracts player-facing BlueprintOriginPath chargen flows.
 * Mirrors the runtime OriginExporter, reading from .jbp files.
 * Filters to paths that include ChargenHomeworld - excludes appearance/pregen/deleted flows.
 */
public static class StaticOriginExporter {

  /**
   * GUID of the CustomCompanion BlueprintUnit - the base unit template for player character creation.
   * Its OldWarhammer* fields define the base characteristics every fresh player starts with.
   */
  private const string CustomCompanionGuid = "baaff53a675a84f4983f1e2113b24552";

  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    string gameVersion,
    string gameRevision,
    string outputDirectory,
    StaticLogger logger) {
    var extractedOrigins = new List<Dictionary<string, object?>>();
    var skippedCount = 0;

    var baseCharacteristics = ResolveBaseCharacteristics(catalog, logger);

    // Include shadow-deleted: the runtime does not filter by shadow-deleted status,
    // it filters by content (ChargenHomeworld + not Pregen). Some valid chargen paths
    // are shadow-deleted in the .jbp data (e.g. NewCompanion_Navigator_CustomCharacterChargenPath).
    foreach (var origin in catalog.ByType("BlueprintOriginPath", includeShadowDeleted: true)) {
      try {
        var chargenGroups = ExtractChargenGroups(origin, catalog, resolver);

        // Filter 1: must define homeworld selection (excludes appearance-only flows).
        // Filter 2: asset name must not contain "Pregen" (excludes premade/pregen loading paths).
        var isPregenPath = origin.AssetName.Contains("Pregen", StringComparison.OrdinalIgnoreCase);

        if (!chargenGroups.ContainsKey("chargenHomeworld") || isPregenPath) {
          skippedCount++;
          continue;
        }

        var originData = new Dictionary<string, object?>(StringComparer.Ordinal) {
          ["id"] = origin.AssetId,
          ["baseCharacteristics"] = baseCharacteristics,
          ["chargenGroups"] = chargenGroups,
        };

        extractedOrigins.Add(originData);
      }
      catch (Exception exception) {
        logger.Warn("origins", $"skipped guid={origin.AssetId} reason={exception.Message}");
      }
    }

    ExportWriter.WriteEnvelope(outputDirectory, "origins",
      ExportEnvelope<Dictionary<string, object?>>.Create(gameVersion, gameRevision, extractedOrigins));

    logger.Result("origins", "static export done", ("count", extractedOrigins.Count), ("filtered", skippedCount));
  }

  /**
   * Extracts chargen feature groups from a BlueprintOriginPath.
   * Returns a map from group name to the list of fully-extracted feature records.
   */
  private static Dictionary<string, List<Dictionary<string, object?>>> ExtractChargenGroups(
    BlueprintDocument document,
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver) {
    var result = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);

    var groupMap = StaticRankEntryExtractor.BuildFeatureGroupMap(document, catalog);

    foreach (var groupEntry in groupMap) {
      var featureList = new List<Dictionary<string, object?>>();

      foreach (var feature in groupEntry.Value) {
        try {
          featureList.Add(StaticFeatureExtractor.ExtractFeature(feature, resolver, catalog));
        }
        catch {
          /* skip features that fail to extract */
        }
      }

      result[StaticFeatureExtractor.ToCamelCase(groupEntry.Key)] = featureList;
    }

    return result;
  }

  /**
   * Reads the 9 base characteristic values from the CustomCompanion BlueprintUnit.
   * All player characters share the same base stats before homeworld/occupation modifiers.
   */
  private static Dictionary<string, int> ResolveBaseCharacteristics(
    BlueprintCatalog catalog, StaticLogger logger) {
    var baseUnit = catalog.GetById(CustomCompanionGuid);

    if (baseUnit != null) {
      return ExtractBaseCharacteristics(baseUnit);
    }

    logger.Warn("origins", $"base unit not found guid={CustomCompanionGuid}, using default 30 for all stats");

    return new Dictionary<string, int>(StringComparer.Ordinal) {
      ["ws"] = 30, ["bs"] = 30, ["str"] = 30, ["tgh"] = 30, ["agi"] = 30,
      ["int"] = 30, ["per"] = 30, ["wp"] = 30, ["fel"] = 30,
    };
  }

  /**
   * Reads the OldWarhammer* base stat fields from a BlueprintUnit's ExtraFields.
   */
  private static Dictionary<string, int> ExtractBaseCharacteristics(BlueprintDocument unit) {
    var result = new Dictionary<string, int>(StringComparer.Ordinal);

    var characteristicFields = new (string GameName, string ShortCode)[] {
      ("OldWarhammerWeaponSkill", "ws"),
      ("OldWarhammerBallisticSkill", "bs"),
      ("OldWarhammerStrength", "str"),
      ("OldWarhammerToughness", "tgh"),
      ("OldWarhammerAgility", "agi"),
      ("OldWarhammerIntelligence", "int"),
      ("OldWarhammerPerception", "per"),
      ("OldWarhammerWillpower", "wp"),
      ("OldWarhammerFellowship", "fel"),
    };

    foreach (var (gameName, shortCode) in characteristicFields) {
      if (unit.Data.ExtraFields != null &&
          unit.Data.ExtraFields.TryGetValue(gameName, out var element) &&
          element.ValueKind == JsonValueKind.Number &&
          element.TryGetInt32(out var value)) {
        result[shortCode] = value;
      }
      else {
        result[shortCode] = 0;
      }
    }

    return result;
  }
}
