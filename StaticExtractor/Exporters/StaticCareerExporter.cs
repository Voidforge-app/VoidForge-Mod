using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Extracts BlueprintCareerPath instances representing character careers and their rank trees.
 * Mirrors the runtime CareerExporter, reading from .jbp files.
 */
public static class StaticCareerExporter {

  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    string gameVersion,
    string gameRevision,
    string outputDirectory,
    StaticLogger logger) {
    var extractedCareers = new List<Dictionary<string, object?>>();
    var skippedCount = 0;

    foreach (var career in catalog.ByType("BlueprintCareerPath")) {
      try {
        var name = LocalizedStringResolver.Sanitize(resolver.ResolveName(career));

        if (!IsValidName(name) || IsHiddenInUI(career)) {
          skippedCount++;
          continue;
        }

        var careerData = new Dictionary<string, object?>(StringComparer.Ordinal) {
          ["id"] = career.AssetId,
          ["name"] = name,
          ["description"] = LocalizedStringResolver.Sanitize(resolver.ResolveDescription(career)),
          ["tier"] = GetExtraFieldString(career, "Tier") ?? "",
          ["ranks"] = GetExtraFieldInt(career, "Ranks"),
          ["isHunter"] = GetExtraFieldBool(career, "IsHunter"),
          ["prerequisites"] = StaticFeatureExtractor.ExtractFeature(career, resolver, catalog)["prerequisites"],
          ["metadata"] = ExtractCareerMetadata(career),
          ["rankEntries"] = StaticRankEntryExtractor.ExtractRankEntries(career, catalog, resolver),
        };

        extractedCareers.Add(careerData);
      }
      catch (Exception exception) {
        logger.Warn("careers", $"skipped guid={career.AssetId} reason={exception.Message}");
      }
    }

    ExportWriter.WriteEnvelope(outputDirectory, "careers",
      ExportEnvelope<Dictionary<string, object?>>.Create(gameVersion, gameRevision, extractedCareers));

    logger.Result("careers", "static export done", ("count", extractedCareers.Count), ("filtered", skippedCount));
  }

  /**
   * Extracts CareerPathUIMetaData from the career's Components array.
   * Returns GUIDs for keystones, ultimates, recommended features, and recommended-by occupations.
   */
  private static Dictionary<string, object?> ExtractCareerMetadata(
    BlueprintDocument document) {
    var result = new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["keystoneFeatureIds"] = new List<string>(),
      ["ultimateFeatureIds"] = new List<string>(),
      ["recommendedFeatureIds"] = new List<string>(),
      ["recommendedByOccupationIds"] = new List<string>(),
    };

    if (document.Data.Components == null) return result;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("CareerPathUIMetaData", StringComparison.Ordinal)) continue;

      result["keystoneFeatureIds"] = ExtractReferenceGuids(component, "m_KeystoneFeatures");
      result["ultimateFeatureIds"] = ExtractReferenceGuids(component, "m_UltimateFeatures");
      result["recommendedFeatureIds"] = ExtractReferenceGuids(component, "m_RecommendedFeatures");
      result["recommendedByOccupationIds"] = ExtractReferenceGuids(component, "m_RecommendedByOccupations");
      break;
    }

    return result;
  }

  private static List<string> ExtractReferenceGuids(JsonElement component, string fieldName) {
    var guids = new List<string>();

    if (!component.TryGetProperty(fieldName, out var refsElement) ||
        refsElement.ValueKind != JsonValueKind.Array) {
      return guids;
    }

    guids.AddRange(from refItem in refsElement.EnumerateArray()
      where refItem.ValueKind == JsonValueKind.String
      select StaticFeatureExtractor.NormalizeBlueprintReference(refItem.GetString())
      into guid
      where !string.IsNullOrEmpty(guid)
      select guid);

    return guids;
  }

  private static bool IsValidName(string name) {
    if (string.IsNullOrWhiteSpace(name)) return false;
    if (name.StartsWith("Test ", StringComparison.OrdinalIgnoreCase)) return false;
    return true;
  }

  private static bool IsHiddenInUI(BlueprintDocument document) {
    return GetExtraFieldBool(document, "HideNotAvailibleInUI");
  }

  private static string? GetExtraFieldString(BlueprintDocument document, string fieldName) {
    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue(fieldName, out var element) ||
        element.ValueKind != JsonValueKind.String) {
      return null;
    }

    return element.GetString();
  }

  private static int GetExtraFieldInt(BlueprintDocument document, string fieldName) {
    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue(fieldName, out var element) ||
        element.ValueKind != JsonValueKind.Number) {
      return 0;
    }

    return element.TryGetInt32(out var value) ? value : 0;
  }

  private static bool GetExtraFieldBool(BlueprintDocument document, string fieldName) {
    return document.Data.ExtraFields != null &&
           document.Data.ExtraFields.TryGetValue(fieldName, out var element) &&
           element.ValueKind == JsonValueKind.True;
  }
}