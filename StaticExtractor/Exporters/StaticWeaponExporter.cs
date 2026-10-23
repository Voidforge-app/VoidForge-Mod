using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Static replacement for the runtime WeaponExporter.
 * Reads BlueprintItemWeapon .jbp files directly and extracts the same fields
 * the runtime extractor pulls via reflection. Uses the reference graph for reachability.
 *
 * Key differences from the runtime extractor:
 * - Field names in .jbp use m_ prefixes (m_Rarity, m_HoldingType, etc.) which we strip.
 * - Some fields are derived: isRanged/isMelee/attackType come from m_Range.
 * - flavorText and gainAbility require component inspection and localization.
 * - All output keys are camelCase to match the runtime export format.
 */
public static class StaticWeaponExporter {

  /**
 * Maps .jbp field names (with m_ prefix) to the runtime export field names.
 */
  private static readonly Dictionary<string, string> FieldNameMap = new(StringComparer.Ordinal) {
    ["m_Rarity"] = "rarity",
    ["m_HoldingType"] = "holdingType",
    ["m_Range"] = "attackType",
    ["m_Heaviness"] = "heaviness",
    ["m_IsNotable"] = "isNotable",
    ["m_ProfitFactorCost"] = "profitFactorCost",
    ["m_FlavorText"] = "flavorText",
    ["Category"] = "category",
    ["Family"] = "family",
    ["Classification"] = "classification",
    ["IsTwoHanded"] = "isTwoHanded",
    ["WarhammerDamage"] = "warhammerDamage",
    ["WarhammerMaxDamage"] = "warhammerMaxDamage",
    ["WarhammerPenetration"] = "warhammerPenetration",
    ["DodgePenetration"] = "dodgePenetration",
    ["WarhammerRecoil"] = "warhammerRecoil",
    ["WarhammerMaxDistance"] = "warhammerMaxDistance",
    ["WarhammerMaxAmmo"] = "warhammerMaxAmmo",
    ["RateOfFire"] = "rateOfFire",
    ["AdditionalHitChance"] = "additionalHitChance",
    ["SpendCharges"] = "spendCharges",
    ["Charges"] = "charges",
    ["RestoreChargesAfterCombat"] = "restoreChargesAfterCombat",
    ["CanBeUsedInGame"] = "canBeUsedInGame",
    ["IsNonRemovable"] = "isNonRemovable",
    ["IsUnlootable"] = "isUnlootable",
    ["m_IsNatural"] = "isNatural",
  };

  /**
 * Fields that may be null in the output even when present.
 */
  private static readonly HashSet<string> NullableFields = new(StringComparer.Ordinal) {
    "overrideOverpenetrationFactorPercents",
  };

  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    HashSet<string> reachableItemGuids,
    string gameVersion,
    string gameRevision,
    string outputDirectory,
    StaticLogger logger) {

    var extractedWeapons = new List<Dictionary<string, object?>>();
    var skippedCount = 0;

    // Include shadow-deleted: the runtime does not filter by shadow-deleted status.
    // Shadow-deleted is a modding flag; the blueprints still exist in the game.
    foreach (var weaponBlueprint in catalog.ByType("BlueprintItemWeapon", true)) {
      try {
        var allFields = ExtractFields(weaponBlueprint, resolver, catalog);

        if (!allFields.TryGetValue("name", out var nameValue) ||
            nameValue is not string weaponName ||
            !StaticItemFilter.IsValidName(weaponName)) {
          continue;
        }

        if (!StaticItemFilter.IsPlayerRelevant(allFields, "BlueprintItemWeapon")) {
          skippedCount++;

          continue;
        }

        var weaponData = BuildExportRecord(allFields);
        StaticItemFilter.SetReachability(weaponData, weaponBlueprint.AssetId, reachableItemGuids);
        extractedWeapons.Add(weaponData);
      }
      catch (Exception exception) {
        logger.Warn("weapons", $"skipped guid={weaponBlueprint.AssetId} reason={exception.Message}");
      }
    }

    var envelope = ExportEnvelope<Dictionary<string, object?>>.Create(gameVersion, gameRevision, extractedWeapons);
    ExportWriter.WriteEnvelope(outputDirectory, "weapons", envelope);

    logger.Result("weapons", "export done",
      ("count", extractedWeapons.Count), ("filtered", skippedCount));
  }

  /**
   * Extracts all fields from a BlueprintItemWeapon .jbp file, with proper name mapping
   * and localization resolution. Returns a dictionary with camelCase keys.
   * Uses the catalog to resolve inherited fields from the prototype chain.
   */
  private static Dictionary<string, object?> ExtractFields(
    BlueprintDocument document, LocalizedStringResolver resolver, BlueprintCatalog catalog) {

    var result = new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["assetGuid"] = document.AssetId,
      ["name"] = ResolveWeaponName(document, resolver),
      ["description"] = LocalizedStringResolver.Sanitize(resolver.ResolveDescription(document)),
      ["flavorText"] = ResolveFlavorText(document, resolver),
    };

    // Extract simple-typed fields from the Data object's extra fields.
    if (document.Data.ExtraFields != null) {
      foreach (var (fieldName, jsonElement) in document.Data.ExtraFields) {
        if (!IsSimpleType(jsonElement.ValueKind)) continue;

        // Map the .jbp field name to the export name, or skip if not in our map.
        if (!FieldNameMap.TryGetValue(fieldName, out var exportName)) continue;

        result[exportName] = ExtractSimpleValue(jsonElement);
      }
    }

    // For any mapped fields missing from the child, resolve from prototype chain.
    foreach (var (jbpName, exportName) in FieldNameMap) {
      if (result.ContainsKey(exportName)) continue;

      var inherited = catalog.ResolvePrototypeField(document, jbpName);

      if (inherited != null && inherited.Value.ValueKind != JsonValueKind.Null) {
        result[exportName] = ExtractSimpleValue(inherited.Value);
      }
    }

    // Derive attackType from m_Origin when m_Range is "None" or missing.
    // The runtime game does this derivation internally for weapons like Melta Ultima
    // where m_Range is explicitly "None" but the weapon is clearly ranged.
    var attackType = result.GetValueOrDefault("attackType") as string;

    if (string.IsNullOrEmpty(attackType) || attackType == "None") {
      var origin = catalog.ResolvePrototypeField(document, "m_Origin");

      if (origin is { ValueKind: JsonValueKind.String }) {
        var originStr = origin.Value.GetString() ?? "";

        if (originStr.Contains("Ranged", StringComparison.OrdinalIgnoreCase)) {
          attackType = "Ranged";
        }
        else if (originStr.Contains("Melee", StringComparison.OrdinalIgnoreCase)) {
          attackType = "Melee";
        }
      }

      result["attackType"] = attackType ?? "None";
    }

    result["isRanged"] = string.Equals(attackType, "Ranged", StringComparison.OrdinalIgnoreCase);
    result["isMelee"] = string.Equals(attackType, "Melee", StringComparison.OrdinalIgnoreCase);

    // gainAbility is always true for weapons - all weapons grant their attack abilities.
    result["gainAbility"] = true;

    // Ensure nullable fields are present even if not in the .jbp.
    foreach (var nullableField in NullableFields.Where(nullableField => !result.ContainsKey(nullableField))) {
      result[nullableField] = null;
    }

    return result;
  }

  /**
   * Builds the final export record with only the fields the frontend expects,
   * in the correct order and format.
   */
  private static Dictionary<string, object?> BuildExportRecord(
    Dictionary<string, object?> allFields) {

    var exportFields = new (string key, object? defaultValue)[] {
      ("assetGuid", ""),
      ("name", ""),
      ("description", ""),
      ("flavorText", ""),
      ("rarity", ""),
      ("category", ""),
      ("family", ""),
      ("classification", ""),
      ("holdingType", ""),
      ("isRanged", false),
      ("isMelee", false),
      ("isTwoHanded", false),
      ("attackType", ""),
      ("heaviness", ""),
      ("warhammerDamage", 0),
      ("warhammerMaxDamage", 0),
      ("warhammerPenetration", 0),
      ("dodgePenetration", 0),
      ("warhammerRecoil", 0),
      ("warhammerMaxDistance", 0),
      ("warhammerMaxAmmo", 0),
      ("rateOfFire", 0),
      ("additionalHitChance", 0),
      ("overrideOverpenetrationFactorPercents", null),
      ("spendCharges", false),
      ("charges", 0),
      ("restoreChargesAfterCombat", false),
      ("gainAbility", false),
      ("isNotable", false),
      ("profitFactorCost", 0.0),
    };

    var record = new Dictionary<string, object?>(StringComparer.Ordinal);

    foreach (var (key, defaultValue) in exportFields) {
      record[key] = allFields.GetValueOrDefault(key, defaultValue);
    }

    return record;
  }

  /**
   * Resolves the weapon name, applying AddStringToFactName components (pattern prefixes).
   */
  private static string ResolveWeaponName(BlueprintDocument document, LocalizedStringResolver resolver) {
    var baseName = LocalizedStringResolver.Sanitize(resolver.ResolveName(document));

    if (document.Data.Components == null) return baseName;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProp)) continue;

      var typeStr = typeProp.GetString() ?? "";

      if (!typeStr.Contains("AddStringToFactName", StringComparison.Ordinal)) continue;

      try {
        var additionalString = component.GetProperty("m_AdditionalString");

        var localized = additionalString.Deserialize<LocalizedStringField>(
          BlueprintDocument.SerializerOptions);

        var prefix = LocalizedStringResolver.Sanitize(resolver.Resolve(localized));

        if (string.IsNullOrEmpty(prefix)) continue;

        var junctionType = component.TryGetProperty("JunctionType", out var junctionProp)
          ? junctionProp.GetString() ?? "BeforeFirstSquareBrackets"
          : "BeforeFirstSquareBrackets";

        baseName = ApplyJunction(baseName, prefix, junctionType);
      }
      catch {
        /* skip malformed component */
      }
    }

    return baseName;
  }

  /**
   * Applies a string junction to combine a prefix with the base name.
   * Mirrors the game's AddStringToFactName.JunctionType behavior.
   */
  private static string ApplyJunction(string baseName, string additional, string junctionType) {
    switch (junctionType) {
      case "BeforeFirstSquareBrackets": {
        var bracketed = $"[{additional}]";
        var bracketIndex = baseName.IndexOf('[');

        return bracketIndex > 0 ? baseName.Insert(bracketIndex, bracketed) : $"{bracketed} {baseName}";

      }
      case "AfterNewString":
        return $"{baseName} {additional}";
      case "AfterFirstRoundBrackets": {
        var parenIndex = baseName.IndexOf('(');

        if (parenIndex < 0) return $"{baseName} {additional}";
        var closeIndex = baseName.IndexOf(')', parenIndex);

        return closeIndex >= 0 ? baseName.Insert(closeIndex + 1, $" {additional}") : $"{baseName} {additional}";

      }

      case "AfterSecondRoundBrackets": {
        var firstParen = baseName.IndexOf('(');

        if (firstParen < 0) return $"{baseName} {additional}";
        var secondParen = baseName.IndexOf('(', firstParen + 1);

        if (secondParen < 0) return $"{baseName} {additional}";
        var closeIndex = baseName.IndexOf(')', secondParen);

        return closeIndex >= 0 ? baseName.Insert(closeIndex + 1, $" {additional}") : $"{baseName} {additional}";
      }
      default:
        return $"{additional} {baseName}";
    }

  }

  /**
   * Resolves the flavor text through the SharedStringAsset indirection.
   */
  private static string ResolveFlavorText(BlueprintDocument document, LocalizedStringResolver resolver) {
    if (document.Data.ExtraFields == null) return "";

    if (!document.Data.ExtraFields.TryGetValue("m_FlavorText", out var flavorField)) return "";

    // m_FlavorText is a LocalizedStringField serialized as a JsonElement.
    try {
      var localized = flavorField.Deserialize<LocalizedStringField>(
        BlueprintDocument.SerializerOptions);

      return LocalizedStringResolver.Sanitize(resolver.Resolve(localized));
    }
    catch {
      return "";
    }
  }

  private static bool IsSimpleType(JsonValueKind kind) {
    return kind is JsonValueKind.String or JsonValueKind.Number
      or JsonValueKind.True or JsonValueKind.False;
  }

  private static object? ExtractSimpleValue(JsonElement element) {
    return element.ValueKind switch {
      JsonValueKind.String => element.GetString(),
      // Preserve float formatting: if the raw JSON contains a decimal point, return a double
      // so the output shows 0.0 instead of 0 (matching the runtime extractor).
      JsonValueKind.Number => element.GetRawText().Contains('.')
        ? element.GetDouble()
        : element.TryGetInt32(out var intValue) ? intValue : element.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => null,
    };
  }
}