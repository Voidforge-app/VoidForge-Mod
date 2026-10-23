using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StaticExtractor.Infrastructure;

/**
 * Merges runtime and static exports into a single output, picking the better value for each field.
 *
 * Runtime wins for:
 * - Identity fields (id, assetGuid, key) - the runtime is the authoritative source
 * - Resolved text (name, description, flavorText) - the runtime resolves localization to HTML
 *
 * Static wins for:
 * - Items the runtime misses (unresolved names, shadow-deleted blueprints)
 * - Fields the runtime doesn't extract
 *
 * Lists (sources, prerequisites, featureTypes) are unioned and deduplicated.
 * Dictionaries (characteristicModifiers, preSelectedFeatures, chargenGroups) are merged key-by-key.
 *
 * The output always uses the runtime envelope format. Static-only items are appended to categories
 * where the runtime may have gaps.
 */
public static class ExportMerge {
  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  /**
   * Fields where the runtime value is always preferred when it has content.
   * These are fields where the runtime produces higher-quality output (resolved localization,
   * HTML markup, authoritative IDs).
   */
  private static readonly HashSet<string> RuntimePreferredFields = new(StringComparer.Ordinal) {
    "assetGuid",
    "id",
    "key",
    "name",
    "description",
    "flavorText",
    "title",
  };

  /**
   * Fields where static is always preferred. These are fields that the runtime either
   * doesn't extract or where the static .jbp data is more complete.
   */
  private static readonly HashSet<string> StaticAuthoritativeFields = new(StringComparer.Ordinal) {
    "assetGuid",
    "rarity",
    "category",
    "family",
    "classification",
    "holdingType",
    "isRanged",
    "isMelee",
    "isTwoHanded",
    "attackType",
    "heaviness",
    "warhammerDamage",
    "warhammerMaxDamage",
    "warhammerPenetration",
    "dodgePenetration",
    "warhammerRecoil",
    "warhammerMaxDistance",
    "warhammerMaxAmmo",
    "rateOfFire",
    "additionalHitChance",
    "spendCharges",
    "charges",
    "restoreChargesAfterCombat",
    "gainAbility",
    "isNotable",
    "profitFactorCost",
    "damageAbsorption",
    "damageDeflection",
    "raceRestriction",
    "proficiencyGroup",
    "slot",
    "reachable",
    "description",
    "flavorText",
  };

  /**
   * Fields that are JSON arrays of objects (or primitives) where items from both
   * sources should be unioned and deduplicated rather than choosing one side.
   */
  private static readonly HashSet<string> MergeableArrayFields = new(StringComparer.Ordinal) {
    "sources",
    "prerequisites",
    "featureTypes",
    "characteristicModifiers",
    "keystoneFeatureIds",
    "ultimateFeatureIds",
    "recommendedFeatureIds",
    "recommendedByOccupationIds",
    "uniqueFeatureIds",
    "careerProgressions",
    "preSelectedFeatures",
    "chargenGroups",
    "rankEntries",
    "quickSlotIds",
  };

  /**
   * Fields that are JSON objects where keys should be merged (not replaced).
   */
  private static readonly HashSet<string> MergeableObjectFields = new(StringComparer.Ordinal) {
    "characteristicModifiers",
    "baseCharacteristics",
    "preSelectedFeatures",
    "chargenGroups",
    "startingEquipment",
    "metadata",
    "petInfo",
  };

  private static readonly string[] SourceArray = [
    "assetGuid", "id", "guid", "key",
  ];

  /**
   * Merges a runtime export directory into a static export directory.
   * Runtime items win for identity; static items fill gaps and add missing entries.
   * For each item present in both, fields are merged intelligently.
   */
  public static void MergeDirectory(string runtimeDirectory, string staticDirectory, StaticLogger logger) {
    if (!Directory.Exists(runtimeDirectory)) {
      throw new DirectoryNotFoundException($"Runtime export directory not found: {runtimeDirectory}");
    }

    Directory.CreateDirectory(staticDirectory);

    foreach (var runtimeJsonPath in Directory.EnumerateFiles(runtimeDirectory, "*.json", SearchOption.TopDirectoryOnly)) {
      var fileName = Path.GetFileName(runtimeJsonPath);
      var staticJsonPath = Path.Combine(staticDirectory, fileName);

      try {
        var merged = File.Exists(staticJsonPath)
          ? MergeJson(File.ReadAllText(runtimeJsonPath), File.ReadAllText(staticJsonPath),
            Path.GetFileNameWithoutExtension(fileName), logger)
          : JsonNode.Parse(File.ReadAllText(runtimeJsonPath));

        if (merged != null) {
          File.WriteAllText(staticJsonPath, merged.ToJsonString(JsonOptions));
        }
      }
      catch (Exception exception) {
        logger.Warn("merge", $"json file={fileName} reason={exception.Message}");
      }
    }

    foreach (var runtimeDirectoryPath in Directory.EnumerateDirectories(runtimeDirectory, "*", SearchOption.TopDirectoryOnly)) {
      var targetDirectory = Path.Combine(staticDirectory, Path.GetFileName(runtimeDirectoryPath));
      CopyDirectory(runtimeDirectoryPath, targetDirectory);
    }

    logger.Info("merge", $"runtime exports merged from {runtimeDirectory}");
  }

  private static JsonNode? MergeJson(
    string runtimeJson, string staticJson, string category, StaticLogger logger) {
    var runtimeRoot = JsonNode.Parse(runtimeJson);
    var staticRoot = JsonNode.Parse(staticJson);

    if (runtimeRoot is not JsonObject runtimeObject || staticRoot is not JsonObject staticObject ||
        runtimeObject["items"] is not JsonArray runtimeItems ||
        staticObject["items"] is not JsonArray staticItems) {
      return runtimeRoot;
    }

    // Index static items by identity for fast lookup.
    var staticById = staticItems
      .OfType<JsonObject>()
      .Select(item => (Id: GetIdentity(item), Item: item))
      .Where(entry => entry.Id != null)
      .ToDictionary(entry => entry.Id!, entry => entry.Item, StringComparer.Ordinal);

    var mergedCount = 0;
    var appendedCount = 0;
    var identities = new HashSet<string>(StringComparer.Ordinal);

    // Merge runtime items with their static counterparts.
    foreach (var runtimeItem in runtimeItems.OfType<JsonObject>()) {
      var identity = GetIdentity(runtimeItem);
      if (identity == null) continue;

      identities.Add(identity);

      if (!staticById.TryGetValue(identity, out var staticItem)) continue;
      IntelligentMergeItem(runtimeItem, staticItem);
      mergedCount++;
    }

    // Append static-only items that the runtime doesn't have.
    foreach (var staticItem in staticItems.OfType<JsonObject>()) {
      var identity = GetIdentity(staticItem);

      if (identity == null || identities.Contains(identity)) continue;

      runtimeItems.Add(staticItem.DeepClone());
      appendedCount++;
    }

    runtimeObject["count"] = runtimeItems.Count;

    if (mergedCount > 0 || appendedCount > 0) {
      logger.Info("merge", $"category={category} merged={mergedCount} appended={appendedCount}");
    }

    return runtimeObject;
  }

  /**
   * Intelligently merges fields from a static item into a runtime item.
   * Picks the better value for each field based on content quality.
   */
  private static void IntelligentMergeItem(JsonObject runtimeItem, JsonObject staticItem) {
    foreach (var property in staticItem) {
      var fieldName = property.Key;
      var staticValue = property.Value;

      if (staticValue == null) continue;

      if (!runtimeItem.ContainsKey(fieldName) || runtimeItem[fieldName] == null) {
        // Runtime doesn't have this field - always take from static.
        runtimeItem[fieldName] = staticValue.DeepClone();
        continue;
      }

      var runtimeValue = runtimeItem[fieldName];

      // Field is present in both - pick the better value.
      var better = PickBetterValue(fieldName, runtimeValue, staticValue);

      if (better != null) {
        runtimeItem[fieldName] = better;
      }
    }
  }

  /**
   * Chooses the better value between runtime and static for a given field.
   * Returns the winning value, or null if runtime should keep its current value.
   */
  private static JsonNode? PickBetterValue(
    string fieldName, JsonNode? runtimeValue, JsonNode staticValue) {

    // Identity fields always prefer runtime (authoritative).
    if (RuntimePreferredFields.Contains(fieldName)) {
      return PreferText(runtimeValue, staticValue);
    }

    // Static-authoritative fields always prefer static (when runtime is empty/default).
    if (StaticAuthoritativeFields.Contains(fieldName)) {
      return PreferStaticIfRuntimeIsEmpty(runtimeValue, staticValue);
    }

    // Mergeable array fields: union both, deduplicate.
    if (MergeableArrayFields.Contains(fieldName) &&
        runtimeValue is JsonArray runtimeArray && staticValue is JsonArray staticArray) {
      return MergeArrays(runtimeArray, staticArray);
    }

    // Mergeable object fields: merge keys recursively.
    if (MergeableObjectFields.Contains(fieldName) &&
        runtimeValue is JsonObject runtimeObject && staticValue is JsonObject staticObject) {
      return MergeObjects(runtimeObject, staticObject);
    }

    // Default: prefer runtime (it's the authoritative source).
    return null;
  }

  /**
   * For text fields, prefer the value with resolved content.
   * Runtime text has resolved HTML markup; static has raw {g|key} markup.
   * We prefer resolved > raw > empty.
   */
  private static JsonNode? PreferText(JsonNode? runtimeValue, JsonNode staticValue) {
    var runtimeText = GetText(runtimeValue);
    var staticText = GetText(staticValue);

    // If runtime is empty but static has content, use static.
    if (string.IsNullOrWhiteSpace(runtimeText) && !string.IsNullOrWhiteSpace(staticText)) {
      return staticValue.DeepClone();
    }

    // If both have content, prefer runtime (resolved HTML > raw markup).
    // Runtime wins because it resolves {g|key}text{/g} to proper HTML links.
    if (!string.IsNullOrWhiteSpace(runtimeText)) {
      return null;
    }

    // Runtime is empty - use static.
    return string.IsNullOrWhiteSpace(staticText) ? null : staticValue.DeepClone();
  }

  /**
   * For static-authoritative fields, prefer static when runtime is empty/default.
   */
  private static JsonNode? PreferStaticIfRuntimeIsEmpty(JsonNode? runtimeValue, JsonNode staticValue) {
    if (runtimeValue == null) return staticValue.DeepClone();

    var runtimeKind = runtimeValue.GetValueKind();
    var staticKind = staticValue.GetValueKind();

    // For primitives, prefer non-default static value when runtime is default.
    if (runtimeKind == JsonValueKind.Number && staticKind == JsonValueKind.Number) {
      var runtimeNum = runtimeValue.GetValue<double>();
      var staticNum = staticValue.GetValue<double>();

      // Prefer the non-zero value.
      if (runtimeNum == 0 && staticNum != 0) return staticValue.DeepClone();
    }

    if (runtimeKind != JsonValueKind.String || staticKind != JsonValueKind.String) return null;

    var runtimeStr = runtimeValue.GetValue<string>();
    var staticStr = staticValue.GetValue<string>();

    // Prefer non-empty.
    if (string.IsNullOrWhiteSpace(runtimeStr) && !string.IsNullOrWhiteSpace(staticStr)) {
      return staticValue.DeepClone();
    }

    // For most cases, keep runtime (it's already populated).
    return null;
  }

  /**
   * Merges two JSON arrays by unioning unique elements. Preserves runtime order,
   * appends static-only elements at the end.
   */
  private static JsonArray? MergeArrays(JsonArray runtimeArray, JsonArray staticArray) {
    var runtimeSet = new HashSet<string>(StringComparer.Ordinal);
    var result = new JsonArray();

    // Add all runtime items first (preserving order and identity).
    foreach (var item in runtimeArray) {
      if (item == null) {
        result.Add(null);
        continue;
      }

      var key = GetElementKey(item);
      result.Add(item.DeepClone());
      runtimeSet.Add(key);
    }

    // Append static items not already present.
    var appended = 0;
    foreach (var item in staticArray) {
      if (item == null) continue;

      var key = GetElementKey(item);
      if (runtimeSet.Contains(key)) continue;

      result.Add(item.DeepClone());
      runtimeSet.Add(key);
      appended++;
    }

    return appended > 0 ? result : null;
  }

  /**
   * Merges two JSON objects by taking the union of keys. For overlapping keys,
   * recursively picks the better value.
   */
  private static JsonObject? MergeObjects(JsonObject runtimeObject, JsonObject staticObject) {
    var changed = false;
    var result = new JsonObject();

    // Copy all runtime properties first.
    foreach (var (key, value) in runtimeObject) {
      result[key] = value?.DeepClone();
    }

    // Merge static properties - add missing, recursively merge objects.
    foreach (var (key, staticValue) in staticObject) {
      if (!runtimeObject.ContainsKey(key) || runtimeObject[key] == null) {
        result[key] = staticValue?.DeepClone();
        changed = true;
      }
      else {
        switch (runtimeObject[key]) {
          case JsonObject runtimeChild when staticValue is JsonObject staticChild: {
            var merged = MergeObjects(runtimeChild, staticChild);
            if (merged != null) {
              result[key] = merged;
              changed = true;
            }

            break;
          }
          case JsonArray runtimeChildArray when staticValue is JsonArray staticChildArray: {
            var merged = MergeArrays(runtimeChildArray, staticChildArray);
            if (merged == null) continue;

            result[key] = merged;
            changed = true;
            break;
          }
        }
      }
    }

    return changed ? result : null;
  }

  /**
   * Generates a deduplication key for a JSON element.
   * For objects, uses their identity fields; for primitives, uses the value itself.
   */
  private static string GetElementKey(JsonNode element) {
    switch (element) {
      case JsonObject obj: {
        var identity = GetIdentity(obj);
        return identity ??
               // No identity fields - use the raw JSON for deduplication.
               obj.ToJsonString();

      }
      case JsonValue value:
        return value.ToJsonString();
      default:
        return element.ToJsonString();
    }

  }

  /**
   * Extracts the text content of a JSON node for comparison purposes.
   */
  private static string? GetText(JsonNode? node) {
    if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;

    return null;
  }

  /**
   * Finds the primary identity key of an item for deduplication.
   * Checks common identity fields in priority order.
   */
  private static string? GetIdentity(JsonObject item) {
    return (from key in SourceArray
      let value = item[key]?.GetValue<string>()
      where !string.IsNullOrWhiteSpace(value)
      select $"{key}:{value}").FirstOrDefault();
  }

  private static void CopyDirectory(string sourceDirectory, string targetDirectory) {
    Directory.CreateDirectory(targetDirectory);

    foreach (var file in Directory.EnumerateFiles(sourceDirectory)) {
      File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), true);
    }

    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory)) {
      CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
    }
  }
}