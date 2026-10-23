using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Shared field, localization, prototype, and item-name logic for static item exporters.
 */
public static class StaticItemHelpers {
  public static Dictionary<string, object?> ExtractCommonFields(
    BlueprintDocument document,
    LocalizedStringResolver resolver) {
    var fields = new Dictionary<string, object?>(StringComparer.Ordinal) {
      ["assetGuid"] = document.AssetId,
      ["name"] = ResolveName(document, resolver),
      ["description"] = LocalizedStringResolver.Sanitize(resolver.ResolveDescription(document)),
      ["flavorText"] = ResolveFlavorText(document, resolver),
    };

    if (document.Data.ExtraFields == null) return fields;

    foreach (var (fieldName, element) in document.Data.ExtraFields) {
      if (!IsSimple(element.ValueKind)) continue;

      fields[ToCamelCase(fieldName)] = ExtractSimple(element);
    }

    return fields;
  }

  public static void ResolveField(Dictionary<string, object?> fields,
    BlueprintDocument document,
    BlueprintCatalog catalog,
    string jsonName) {
    var outputName = ToCamelCase(jsonName);

    if (fields.TryGetValue(outputName, out var value)) return;

    var inherited = catalog.ResolvePrototypeField(document, jsonName);

    if (inherited == null || !IsSimple(inherited.Value.ValueKind)) return;

    value = ExtractSimple(inherited.Value);
    fields[outputName] = value;

  }

  public static bool GetBool(Dictionary<string, object?> fields, string key) {
    return fields.TryGetValue(key, out var value) && value is true;
  }

  public static bool HasEquipmentAbility(BlueprintDocument document, BlueprintCatalog catalog) {
    var visited = new HashSet<string>(StringComparer.Ordinal);
    var current = document;

    while (visited.Add(current.AssetId)) {
      if (current.Data.Components != null) {
        foreach (var component in current.Data.Components) {
          if (!component.TryGetProperty("$type", out var typeProperty) ||
              !(typeProperty.GetString() ?? "").Contains("AddFactToEquipmentWielder", StringComparison.Ordinal) ||
              !component.TryGetProperty("m_Fact", out var factProperty)) {
            continue;
          }

          var factReference = factProperty.GetString() ?? "";
          var fact = catalog.GetById(factReference.Replace("!bp_", "", StringComparison.Ordinal));

          if (fact?.TypeName.Contains("BlueprintAbility", StringComparison.Ordinal) == true) return true;
        }
      }

      var parentId = current.Data.PrototypeLink;
      if (string.IsNullOrEmpty(parentId)) break;

      current = catalog.GetById(parentId);
      if (current == null) break;
    }

    return false;
  }

  public static string? GetString(Dictionary<string, object?> fields, string key) {
    return fields.TryGetValue(key, out var value) ? value as string : null;
  }

  public static Dictionary<string, object?> SelectFields(
    Dictionary<string, object?> fields,
    IEnumerable<string> names) {
    var selected = new Dictionary<string, object?>(StringComparer.Ordinal);

    foreach (var name in names) {
      selected[name] = fields.GetValueOrDefault(name);
    }

    return selected;
  }

  private static string ResolveName(BlueprintDocument document, LocalizedStringResolver resolver) {
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
        /* malformed component is ignored */
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

  private static string ResolveFlavorText(BlueprintDocument document, LocalizedStringResolver resolver) {
    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue("m_FlavorText", out var element)) {
      return "";
    }

    try {
      var localized = element.Deserialize<LocalizedStringField>(BlueprintDocument.SerializerOptions);
      return LocalizedStringResolver.Sanitize(resolver.Resolve(localized));
    }
    catch {
      return "";
    }
  }

  private static bool IsSimple(JsonValueKind kind) {
    return kind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
  }

  private static object? ExtractSimple(JsonElement element) {
    return element.ValueKind switch {
      JsonValueKind.String => element.GetString(),
      JsonValueKind.Number => element.GetRawText().Contains('.')
        ? element.GetDouble()
        : element.TryGetInt32(out var integer) ? integer : element.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => null,
    };
  }

  private static string ToCamelCase(string name) {
    if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
    if (name.Length == 0) return name;

    return char.ToLowerInvariant(name[0]) + name[1..];
  }
}