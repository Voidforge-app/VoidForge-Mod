using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Localization;

namespace StaticExtractor.Extraction;

/**
 * Shared extraction of player-facing data from any blueprint document.
 * Mirrors the runtime FeatureExtractor, producing the same field set:
 * id, name, description, featureTypes, prerequisites, talentGroup, characteristicModifiers.
 */
public static class StaticFeatureExtractor {

  /**
   * Maps Warhammer characteristic enum names to their 3-letter short-codes.
   */
  private static readonly Dictionary<string, string> StatShortCodes = new(StringComparer.Ordinal) {
    ["WarhammerWeaponSkill"] = "ws",
    ["WarhammerBallisticSkill"] = "bs",
    ["WarhammerStrength"] = "str",
    ["WarhammerToughness"] = "tgh",
    ["WarhammerAgility"] = "agi",
    ["WarhammerIntelligence"] = "int",
    ["WarhammerPerception"] = "per",
    ["WarhammerWillpower"] = "wp",
    ["WarhammerFellowship"] = "fel",
  };

  /**
   * Extracts the standard feature record from a blueprint document.
   * Resolves localized name/description, feature types, prerequisites, talent group,
   * and characteristic modifiers.
   */
  public static Dictionary<string, object?> ExtractFeature(
    BlueprintDocument document,
    LocalizedStringResolver resolver,
    BlueprintCatalog catalog) {
    var result = new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["id"] = document.AssetId,
      ["name"] = ResolveFeatureName(document, resolver),
      ["description"] = LocalizedStringResolver.Sanitize(resolver.ResolveDescription(document)),
      ["featureTypes"] = ExtractFeatureTypes(document),
      ["prerequisites"] = ExtractPrerequisites(document, catalog),
      ["talentGroup"] = ExtractTalentGroup(document),
      ["characteristicModifiers"] = ExtractCharacteristicModifiers(document),
    };

    var petInfo = ExtractPetInfo(document);
    if (petInfo != null) result["petInfo"] = petInfo;

    return result;
  }

  /**
   * Extracts the feature name, applying AddStringToFactName junction rules.
   * Mirrors the runtime StaticItemHelpers.ResolveName logic.
   */
  private static string ResolveFeatureName(BlueprintDocument document, LocalizedStringResolver resolver) {
    var baseName = LocalizedStringResolver.Sanitize(resolver.ResolveName(document));

    if (document.Data.Components == null) return baseName;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty) ||
          !(typeProperty.GetString() ?? "").Contains("AddStringToFactName", StringComparison.Ordinal)) {
        continue;
      }

      if (!component.TryGetProperty("m_AdditionalString", out var additionalElement)) continue;

      try {
        var localized = additionalElement.Deserialize<LocalizedStringField>(BlueprintDocument.SerializerOptions);
        var additional = LocalizedStringResolver.Sanitize(resolver.Resolve(localized));

        if (string.IsNullOrEmpty(additional)) continue;

        var junction = component.TryGetProperty("JunctionType", out var junctionProperty)
          ? junctionProperty.GetString() ?? ""
          : "";

        baseName = ApplyJunction(baseName, additional, junction);
      }
      catch {
        /* malformed component */
      }
    }

    return baseName;
  }

  private static string ApplyJunction(string baseName, string additional, string junctionType) {
    return junctionType switch {
      "BeforeFirstSquareBrackets" => $"[{additional}] {baseName}",
      "AfterNewString" => $"{baseName} {additional}",
      "AfterFirstRoundBrackets" => InsertAfterRoundBracket(baseName, additional, 1),
      "AfterSecondRoundBrackets" => InsertAfterRoundBracket(baseName, additional, 2),
      _ => $"{additional} {baseName}",
    };
  }

  private static string InsertAfterRoundBracket(string baseName, string additional, int bracketNumber) {
    var openingIndex = -1;

    for (var index = 0; index < bracketNumber; index++) {
      openingIndex = baseName.IndexOf('(', openingIndex + 1);
      if (openingIndex < 0) return $"{baseName} {additional}";
    }

    var closingIndex = baseName.IndexOf(')', openingIndex);
    return closingIndex < 0
      ? $"{baseName} ({additional})"
      : baseName.Insert(closingIndex + 1, $" {additional}");
  }

  /**
   * Extracts the FeatureTypes list from the blueprint's ExtraFields.
   */
  private static List<string> ExtractFeatureTypes(BlueprintDocument document) {
    var result = new List<string>();

    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue("FeatureTypes", out var element) ||
        element.ValueKind != JsonValueKind.Array) {
      return result;
    }

    foreach (var item in element.EnumerateArray()) {
      if (item.ValueKind == JsonValueKind.String) {
        var value = item.GetString();
        if (!string.IsNullOrEmpty(value)) result.Add(value);
      }
    }

    return result;
  }

  /**
   * Extracts the TalentGroup from TalentIconInfo.MainGroup in ExtraFields.
   * MainGroup is an enum that may be serialized as either a string or a number.
   */
  private static string? ExtractTalentGroup(BlueprintDocument document) {
    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue("TalentIconInfo", out var element) ||
        element.ValueKind != JsonValueKind.Object) {
      return null;
    }

    if (!element.TryGetProperty("MainGroup", out var mainGroup)) return null;

    return mainGroup.ValueKind switch {
      JsonValueKind.String => mainGroup.GetString(),
      JsonValueKind.Number => mainGroup.GetRawText(),
      _ => null,
    };
  }

  /**
   * Extracts prerequisites from both the modern Prerequisites.List field
   * and legacy Prerequisite_* components in the Components array.
   */
  private static List<Dictionary<string, object?>> ExtractPrerequisites(
    BlueprintDocument document,
    BlueprintCatalog catalog) {
    var result = new List<Dictionary<string, object?>>();

    // Modern system: Prerequisites.List in ExtraFields
    if (document.Data.ExtraFields != null &&
        document.Data.ExtraFields.TryGetValue("Prerequisites", out var prereqElement) &&
        prereqElement.ValueKind == JsonValueKind.Object) {
      if (prereqElement.TryGetProperty("List", out var listElement) &&
          listElement.ValueKind == JsonValueKind.Array) {
        result.AddRange(listElement.EnumerateArray().Select(prereq => ExtractPrerequisiteObject(prereq, catalog)).OfType<Dictionary<string, object?>>());
      }
    }

    // Legacy system: Prerequisite_* components in Components array
    if (document.Data.Components == null) return result;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      var commaIndex = typeName.IndexOf(',');
      var shortName = commaIndex >= 0 ? typeName[(commaIndex + 1)..].Trim() : typeName;

      if (!shortName.StartsWith("Prerequisite", StringComparison.Ordinal)) continue;

      var extracted = ExtractPrerequisiteComponent(component, shortName, catalog);
      if (extracted != null) result.Add(extracted);
    }

    return result;
  }

  /**
   * Extracts a prerequisite object from the modern Prerequisites.List array.
   */
  private static Dictionary<string, object?>? ExtractPrerequisiteObject(
    JsonElement element, BlueprintCatalog catalog) {
    if (element.ValueKind != JsonValueKind.Object) return null;

    if (!element.TryGetProperty("$type", out var typeProperty)) return null;

    var typeName = typeProperty.GetString() ?? "";
    var commaIndex = typeName.IndexOf(',');
    var shortName = commaIndex >= 0 ? typeName[(commaIndex + 1)..].Trim() : typeName;

    return ExtractPrerequisiteFields(element, shortName, catalog);
  }

  /**
   * Extracts a prerequisite component from the legacy Components array.
   */
  private static Dictionary<string, object?> ExtractPrerequisiteComponent(
    JsonElement component, string typeName, BlueprintCatalog catalog) {
    return ExtractPrerequisiteFields(component, typeName, catalog);
  }

  /**
   * Common field extraction for both modern and legacy prerequisites.
   */
  private static Dictionary<string, object?> ExtractPrerequisiteFields(
    JsonElement element, string typeName, BlueprintCatalog catalog) {
    var result = new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["type"] = typeName,
    };

    if (element.TryGetProperty("Group", out var groupProperty) &&
        groupProperty.ValueKind == JsonValueKind.String) {
      result["composition"] = groupProperty.GetString();
    }

    if (element.TryGetProperty("Level", out var levelProperty) &&
        levelProperty.ValueKind == JsonValueKind.Number) {
      result["requiredLevel"] = levelProperty.GetInt32();
    }

    if (element.TryGetProperty("Not", out var notProperty) &&
        notProperty.ValueKind == JsonValueKind.True) {
      result["not"] = true;
    }

    // Single class reference (PrerequisiteClassLevel)
    ExtractSingleReference(element, "m_CharacterClass", "requiredClassId", result);

    // Multi-class reference list (PrerequisiteTakenClass)
    ExtractMultiReference(element, "m_Classes", "requiredClassIds", result);

    // Feature reference (PrerequisiteFeature)
    ExtractSingleReference(element, "m_Feature", "requiredFeatureId", result);

    // Fact reference (PrerequisiteFact)
    ExtractSingleReference(element, "m_Fact", "requiredFeatureId", result);

    return result;
  }

  private static void ExtractSingleReference(
    JsonElement element, string fieldName, string outputKey,
    Dictionary<string, object?> result) {
    if (!element.TryGetProperty(fieldName, out var refProperty) ||
        refProperty.ValueKind != JsonValueKind.String) return;

    var guid = NormalizeBlueprintReference(refProperty.GetString());
    if (!string.IsNullOrEmpty(guid)) result[outputKey] = guid;
  }

  private static void ExtractMultiReference(
    JsonElement element, string fieldName, string outputKey,
    Dictionary<string, object?> result) {
    if (!element.TryGetProperty(fieldName, out var refProperty) ||
        refProperty.ValueKind != JsonValueKind.Array) return;

    var guids = (from item in refProperty.EnumerateArray()
      where item.ValueKind == JsonValueKind.String
      select NormalizeBlueprintReference(item.GetString())
      into guid
      where !string.IsNullOrEmpty(guid)
      select guid).ToList();

    if (guids.Count > 0) result[outputKey] = guids;
  }

  /**
   * Extracts characteristic modifiers from AddContextStatBonus components.
   * Returns a dictionary keyed by 3-letter short-codes (lowercase to match runtime output).
   */
  private static Dictionary<string, int> ExtractCharacteristicModifiers(
    BlueprintDocument document) {
    var result = new Dictionary<string, int>(StringComparer.Ordinal);

    if (document.Data.Components == null) return result;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("AddContextStatBonus", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("Stat", out var statProperty) ||
          statProperty.ValueKind != JsonValueKind.String) continue;

      var statName = statProperty.GetString();
      if (statName == null || !StatShortCodes.TryGetValue(statName, out var shortCode)) continue;

      if (!component.TryGetProperty("Value", out var valueProperty) ||
          valueProperty.ValueKind != JsonValueKind.Object) continue;

      if (!valueProperty.TryGetProperty("Value", out var modProperty) ||
          modProperty.ValueKind != JsonValueKind.Number) continue;

      var modifier = modProperty.GetInt32();
      result[shortCode] = result.TryGetValue(shortCode, out var existing) ? existing + modifier : modifier;
    }

    return result;
  }

  /**
   * Extracts PetKeystoneInfoComponent data from features that grant a companion pet.
   * Returns null for features without this component.
   */
  private static Dictionary<string, object?>? ExtractPetInfo(BlueprintDocument document) {
    if (document.Data.Components == null) return null;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("PetKeystoneInfoComponent", StringComparison.Ordinal)) continue;

      var petInfo = new Dictionary<string, object?>(StringComparer.Ordinal);

      if (component.TryGetProperty("PetType", out var petTypeProperty) &&
          petTypeProperty.ValueKind == JsonValueKind.String) {
        petInfo["petType"] = petTypeProperty.GetString();
      }

      if (component.TryGetProperty("KeyStats", out var keyStatsElement) &&
          keyStatsElement.ValueKind == JsonValueKind.Array) {
        var keyStats = new List<string>();
        foreach (var statEntry in keyStatsElement.EnumerateArray()) {
          if (statEntry.TryGetProperty("StatType", out var statProperty) &&
              statProperty.ValueKind == JsonValueKind.String) {
            keyStats.Add(statProperty.GetString() ?? "");
          }
        }

        if (keyStats.Count > 0) petInfo["keyStats"] = keyStats;
      }

      if (component.TryGetProperty("CoreAbilitiesReferences", out var coreElement) &&
          coreElement.ValueKind == JsonValueKind.Array) {
        var abilityIds = (from abilityRef in coreElement.EnumerateArray()
          where abilityRef.ValueKind == JsonValueKind.String
          select NormalizeBlueprintReference(abilityRef.GetString())
          into guid
          where !string.IsNullOrEmpty(guid)
          select guid).ToList();

        if (abilityIds.Count > 0) petInfo["coreAbilityIds"] = abilityIds;
      }

      if (!component.TryGetProperty("PetUnitReference", out var petUnitElement) ||
          petUnitElement.ValueKind != JsonValueKind.String) return petInfo.Count > 0 ? petInfo : null;

      {
        var guid = NormalizeBlueprintReference(petUnitElement.GetString());
        if (!string.IsNullOrEmpty(guid)) petInfo["petUnitId"] = guid;
      }

      return petInfo.Count > 0 ? petInfo : null;
    }

    return null;
  }

  /**
   * Normalizes a !bp_ reference to a bare GUID string.
   */
  public static string NormalizeBlueprintReference(string? value) {
    return string.IsNullOrWhiteSpace(value) ? "" : value.Replace("!bp_", "", StringComparison.Ordinal);
  }

  /**
   * Converts a PascalCase string to camelCase for JSON output.
   * Matches the runtime Newtonsoft.Json CamelCasePropertyNamesContractResolver behavior
   * for dictionary keys.
   */
  public static string ToCamelCase(string value) {
    if (string.IsNullOrEmpty(value)) return value;

    return char.ToLowerInvariant(value[0]) + value[1..];
  }
}