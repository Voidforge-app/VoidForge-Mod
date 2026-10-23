using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Exports all named companions - starting career, pre-selected features, unique abilities, and equipment.
 * Mirrors the runtime CompanionExporter, reading from .jbp files.
 * Companions are identified by asset name suffix (Companion) and the presence of an ApplyCareerPath
 * component in their m_AddFacts list (the FeatureList).
 */
public static class StaticCompanionExporter {

  private static readonly (string GameName, string ShortCode)[] CharacteristicFields = [
    ("OldWarhammerWeaponSkill", "ws"),
    ("OldWarhammerBallisticSkill", "bs"),
    ("OldWarhammerStrength", "str"),
    ("OldWarhammerToughness", "tgh"),
    ("OldWarhammerAgility", "agi"),
    ("OldWarhammerIntelligence", "int"),
    ("OldWarhammerPerception", "per"),
    ("OldWarhammerWillpower", "wp"),
    ("OldWarhammerFellowship", "fel"),
  ];

  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    string version,
    string revision,
    string outputDirectory,
    StaticLogger logger) {
    var companions = new List<Dictionary<string, object?>>();
    var skippedCount = 0;

    foreach (var unit in catalog.ByType("BlueprintUnit")) {
      if (!IsCanonicalCompanion(unit)) continue;

      var featureList = StaticRankEntryExtractor.FindCompanionFeatureList(unit, catalog);

      if (featureList == null) {
        skippedCount++;
        continue;
      }

      try {
        companions.Add(ExtractCompanionData(unit, featureList, catalog, resolver));
      }
      catch (Exception exception) {
        logger.Warn("companions", $"skipped guid={unit.AssetId} reason={exception.Message}");
      }
    }

    companions = DeduplicateByName(companions);

    companions.Sort((left, right) =>
      string.Compare(left["name"] as string, right["name"] as string, StringComparison.Ordinal));

    ExportWriter.WriteEnvelope(outputDirectory, "companions",
      ExportEnvelope<Dictionary<string, object?>>.Create(version, revision, companions));

    logger.Result("companions", "static export done", ("count", companions.Count), ("filtered", skippedCount));
  }

  /**
   * Removes companions that share the same resolved name, keeping the entry with the most
   * CareerProgressions. Story-variant companions share a LocalizedName and would otherwise
   * export as identical-named duplicates. The richer entry is the one players level up.
   */
  private static List<Dictionary<string, object?>> DeduplicateByName(
    List<Dictionary<string, object?>> companions) {
    var seen = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

    foreach (var companion in companions) {
      var name = companion["name"] as string ?? "";

      if (!seen.TryGetValue(name, out var existing)) {
        seen[name] = companion;
        continue;
      }

      var existingCount = (existing["careerProgressions"] as List<Dictionary<string, object?>>)?.Count ?? 0;
      var candidateCount = (companion["careerProgressions"] as List<Dictionary<string, object?>>)?.Count ?? 0;

      if (candidateCount > existingCount) {
        seen[name] = companion;
      }
    }

    return seen.Values.ToList();
  }

  private static Dictionary<string, object?> ExtractCompanionData(
    BlueprintDocument unit,
    BlueprintDocument featureList,
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver) {
    return new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["id"] = unit.AssetId,
      ["name"] = ExtractUnitName(unit, resolver),
      ["gender"] = GetExtraFieldString(unit, "Gender") ?? "",
      ["baseCharacteristics"] = ExtractBaseCharacteristics(unit),
      ["careerProgressions"] = ExtractCareerProgressions(featureList, catalog),
      ["uniqueFeatureIds"] = ExtractUniqueFeatureIds(featureList),
      ["startingEquipment"] = ExtractStartingEquipment(unit),
    };
  }

  /**
   * Resolves the companion's name from LocalizedName, falling back to m_DisplayName,
   * then to a cleaned-up asset name.
   */
  private static string ExtractUnitName(BlueprintDocument unit, LocalizedStringResolver resolver) {
    var name = LocalizedStringResolver.Sanitize(resolver.Resolve(unit.Data.LocalizedName));

    if (string.IsNullOrEmpty(name)) {
      name = LocalizedStringResolver.Sanitize(resolver.ResolveName(unit));
    }

    if (string.IsNullOrEmpty(name)) {
      name = unit.AssetName.Replace("_", " ", StringComparison.Ordinal)
        .Replace("Companion", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    return name;
  }

  /**
   * Reads the OldWarhammer* base stat fields from the unit's ExtraFields.
   * Keys are lowercase 3-letter short-codes to match the runtime output.
   */
  private static Dictionary<string, int> ExtractBaseCharacteristics(BlueprintDocument unit) {
    var result = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var (gameName, shortCode) in CharacteristicFields) {
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

  /**
   * Extracts career progressions from ApplyCareerPath components on the FeatureList.
   * Each progression includes the career GUID, starting rank, whether it's a chargen path,
   * and any pre-selected features from the Selections field.
   */
  private static List<Dictionary<string, object?>> ExtractCareerProgressions(
    BlueprintDocument featureList, BlueprintCatalog catalog) {
    var progressions = new List<Dictionary<string, object?>>();

    if (featureList.Data.Components == null) return progressions;

    foreach (var component in featureList.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("ApplyCareerPath", StringComparison.Ordinal)) continue;

      try {
        progressions.Add(ExtractSingleProgression(component, catalog));
      }
      catch {
        /* skip malformed */
      }
    }

    return progressions;
  }

  private static Dictionary<string, object?> ExtractSingleProgression(
    JsonElement component, BlueprintCatalog catalog) {
    var careerId = "";
    var isChargenPath = false;

    if (component.TryGetProperty("m_CareerPath", out var careerRef) &&
        careerRef.ValueKind == JsonValueKind.String) {
      var guid = StaticFeatureExtractor.NormalizeBlueprintReference(careerRef.GetString());
      if (!string.IsNullOrEmpty(guid)) {
        var career = catalog.GetById(guid);
        careerId = career?.AssetId ?? guid;
        isChargenPath = career?.TypeName == "BlueprintOriginPath";
      }
    }

    var rank = 0;
    if (component.TryGetProperty("Ranks", out var rankProperty) &&
        rankProperty.ValueKind == JsonValueKind.Number &&
        rankProperty.TryGetInt32(out var rankValue)) {
      rank = rankValue;
    }

    return new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["careerId"] = careerId,
      ["startingRank"] = rank,
      ["isChargenPath"] = isChargenPath,
      ["preSelectedFeatures"] = StaticRankEntryExtractor.ExtractSelectionsFromComponent(component),
    };
  }

  /**
   * Extracts companion-specific feature GUIDs from AddFacts components on the FeatureList.
   * These are the abilities unique to this companion (e.g. Argenta's Repentia features).
   */
  private static List<string> ExtractUniqueFeatureIds(BlueprintDocument featureList) {
    var result = new List<string>();

    if (featureList.Data.Components == null) return result;

    foreach (var component in featureList.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("AddFacts", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("m_Facts", out var factsElement) ||
          factsElement.ValueKind != JsonValueKind.Array) continue;

      result.AddRange(from factRef in factsElement.EnumerateArray()
        where factRef.ValueKind == JsonValueKind.String
        select StaticFeatureExtractor.NormalizeBlueprintReference(factRef.GetString())
        into guid
        where !string.IsNullOrEmpty(guid)
        select guid);
    }

    return result;
  }

  /**
   * Extracts starting equipment from the unit's Body field in ExtraFields.
   * Includes weapon hands, armor, accessory slots, and quick slots.
   */
  private static Dictionary<string, object?> ExtractStartingEquipment(BlueprintDocument unit) {
    var result = new Dictionary<string, object?>(StringComparer.Ordinal);

    if (unit.Data.ExtraFields == null ||
        !unit.Data.ExtraFields.TryGetValue("Body", out var bodyElement) ||
        bodyElement.ValueKind != JsonValueKind.Object) {
      return result;
    }

    // Hand settings (weapons)
    if (bodyElement.TryGetProperty("ItemEquipmentHandSettings", out var handSettings) &&
        handSettings.ValueKind == JsonValueKind.Object) {
      TryAddReference(handSettings, "m_PrimaryHand", "primaryWeaponId", result);
      TryAddReference(handSettings, "m_SecondaryHand", "secondaryWeaponId", result);
      TryAddReference(handSettings, "m_PrimaryHandAlternative1", "primaryWeaponAlt1Id", result);
    }

    // Armor and accessory slots
    TryAddReference(bodyElement, "m_Armor", "armorId", result);
    TryAddReference(bodyElement, "m_Head", "headId", result);
    TryAddReference(bodyElement, "m_Neck", "neckId", result);
    TryAddReference(bodyElement, "m_Gloves", "glovesId", result);
    TryAddReference(bodyElement, "m_Belt", "beltId", result);
    TryAddReference(bodyElement, "m_Feet", "feetId", result);
    TryAddReference(bodyElement, "m_Ring1", "ring1Id", result);
    TryAddReference(bodyElement, "m_Ring2", "ring2Id", result);
    TryAddReference(bodyElement, "m_Wrist", "wristId", result);
    TryAddReference(bodyElement, "m_Shoulders", "shouldersId", result);

    // Quick slots - array of !bp_ refs or nulls
    result["quickSlotIds"] = ExtractQuickSlots(bodyElement);

    return result;
  }

  /**
   * Extracts the m_QuickSlots array from the Body, preserving null entries.
   * The runtime exports nulls for empty quick slots to maintain array index alignment.
   */
  private static List<string?> ExtractQuickSlots(JsonElement bodyElement) {
    var quickSlots = new List<string?>();

    if (!bodyElement.TryGetProperty("m_QuickSlots", out var slotsElement) ||
        slotsElement.ValueKind != JsonValueKind.Array) {
      return quickSlots;
    }

    foreach (var slot in slotsElement.EnumerateArray()) {
      switch (slot.ValueKind) {
        case JsonValueKind.Null:
          quickSlots.Add(null);
          continue;
        case JsonValueKind.String: {
          var guid = StaticFeatureExtractor.NormalizeBlueprintReference(slot.GetString());
          quickSlots.Add(string.IsNullOrEmpty(guid) ? null : guid);
          continue;
        }
        case JsonValueKind.Undefined:
        case JsonValueKind.Object:
        case JsonValueKind.Array:
        case JsonValueKind.Number:
        case JsonValueKind.True:
        case JsonValueKind.False:
        default:
          quickSlots.Add(null);
          break;
      }

    }

    return quickSlots;
  }

  private static void TryAddReference(
    JsonElement container, string fieldName, string outputKey,
    Dictionary<string, object?> result) {
    if (!container.TryGetProperty(fieldName, out var value)) return;

    if (value.ValueKind == JsonValueKind.Null) return;

    if (value.ValueKind != JsonValueKind.String) return;

    var guid = StaticFeatureExtractor.NormalizeBlueprintReference(value.GetString());
    if (!string.IsNullOrEmpty(guid)) result[outputKey] = guid;
  }

  private static bool IsCanonicalCompanion(BlueprintDocument document) {
    var name = document.AssetName;
    return name.EndsWith("Companion", StringComparison.Ordinal) &&
           !name.Contains("_Ch", StringComparison.OrdinalIgnoreCase) &&
           !name.StartsWith("TEST", StringComparison.OrdinalIgnoreCase);
  }

  private static string? GetExtraFieldString(BlueprintDocument document, string fieldName) {
    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue(fieldName, out var element) ||
        element.ValueKind != JsonValueKind.String) {
      return null;
    }

    return element.GetString();
  }
}