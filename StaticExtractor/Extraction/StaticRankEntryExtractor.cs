using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Localization;

namespace StaticExtractor.Extraction;

/**
 * Shared extraction logic for BlueprintPath subclasses (careers, origins).
 * Mirrors the runtime RankEntryExtractor, reading from .jbp JSON instead of live game objects.
 *
 * Career/origin paths carry AddFeaturesToLevelUp components (one per talent pool) and a
 * RankEntries array. Each rank entry has m_Features (auto-granted) and m_Selections
 * (references to BlueprintSelectionFeature objects). Each selection's Group field matches
 * an AddFeaturesToLevelUp component's Group, giving the available feature list for that selection.
 */
public static class StaticRankEntryExtractor {

  /**
   * Builds a map from FeatureGroup name to the list of feature blueprints available in that group.
   * Reads AddFeaturesToLevelUp components from the blueprint's Components array.
   */
  public static Dictionary<string, List<BlueprintDocument>> BuildFeatureGroupMap(
    BlueprintDocument document, BlueprintCatalog catalog) {
    var groupMap = new Dictionary<string, List<BlueprintDocument>>(StringComparer.Ordinal);

    if (document.Data.Components == null) return groupMap;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("AddFeaturesToLevelUp", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("Group", out var groupProperty) ||
          groupProperty.ValueKind != JsonValueKind.String) continue;

      var groupValue = groupProperty.GetString();
      if (string.IsNullOrEmpty(groupValue)) continue;

      if (!component.TryGetProperty("m_Features", out var featuresProperty) ||
          featuresProperty.ValueKind != JsonValueKind.Array) continue;

      if (!groupMap.TryGetValue(groupValue, out var featureList)) {
        featureList = [];
        groupMap[groupValue] = featureList;
      }

      foreach (var featureRef in featuresProperty.EnumerateArray()) {
        if (featureRef.ValueKind != JsonValueKind.String) continue;

        var guid = StaticFeatureExtractor.NormalizeBlueprintReference(featureRef.GetString());
        if (string.IsNullOrEmpty(guid)) continue;

        var feature = catalog.GetById(guid);
        if (feature != null) featureList.Add(feature);
      }
    }

    return groupMap;
  }

  /**
   * Extracts rank entries from a career/origin path blueprint.
   * Each entry contains granted features and selections with their available feature pools.
   */
  public static List<Dictionary<string, object?>> ExtractRankEntries(
    BlueprintDocument document,
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver) {
    var entries = new List<Dictionary<string, object?>>();

    var featureGroupMap = BuildFeatureGroupMap(document, catalog);

    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue("RankEntries", out var rankEntriesElement) ||
        rankEntriesElement.ValueKind != JsonValueKind.Array) {
      return entries;
    }

    entries.AddRange(from rankEntry in rankEntriesElement.EnumerateArray()
      where rankEntry.ValueKind == JsonValueKind.Object
      select new Dictionary<string, object?>(StringComparer.Ordinal) {
        ["grantedFeatures"] = ExtractGrantedFeatures(rankEntry, catalog, resolver),
        ["selections"] = ExtractSelections(rankEntry, catalog, resolver, featureGroupMap),
      });

    return entries;
  }

  /**
   * Extracts the m_Features list from a rank entry as fully-extracted feature records.
   */
  private static List<Dictionary<string, object?>> ExtractGrantedFeatures(
    JsonElement rankEntry,
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver) {
    var features = new List<Dictionary<string, object?>>();

    if (!rankEntry.TryGetProperty("m_Features", out var featuresElement) ||
        featuresElement.ValueKind != JsonValueKind.Array) {
      return features;
    }

    foreach (var feature in (from featureRef in featuresElement.EnumerateArray()
             where featureRef.ValueKind == JsonValueKind.String
             select StaticFeatureExtractor.NormalizeBlueprintReference(featureRef.GetString())
             into guid
             where !string.IsNullOrEmpty(guid)
             select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
      try {
        features.Add(StaticFeatureExtractor.ExtractFeature(feature, resolver, catalog));
      }
      catch {
        /* skip features that fail to extract */
      }
    }

    return features;
  }

  /**
   * Extracts the m_Selections list from a rank entry, matching each selection to its
   * available feature pool via the feature group map.
   */
  private static List<Dictionary<string, object?>> ExtractSelections(
    JsonElement rankEntry,
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    Dictionary<string, List<BlueprintDocument>> featureGroupMap) {
    var selections = new List<Dictionary<string, object?>>();

    if (!rankEntry.TryGetProperty("m_Selections", out var selectionsElement) ||
        selectionsElement.ValueKind != JsonValueKind.Array) {
      return selections;
    }

    foreach (var selection in (from selectionRef in selectionsElement.EnumerateArray()
             where selectionRef.ValueKind == JsonValueKind.String
             select StaticFeatureExtractor.NormalizeBlueprintReference(selectionRef.GetString())
             into guid
             where !string.IsNullOrEmpty(guid)
             select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
      try {
        var selectionData = StaticFeatureExtractor.ExtractFeature(selection, resolver, catalog);

        // Get the selection's Group field
        var groupValue = "";
        if (selection.Data.ExtraFields != null &&
            selection.Data.ExtraFields.TryGetValue("Group", out var groupElement) &&
            groupElement.ValueKind == JsonValueKind.String) {
          groupValue = groupElement.GetString() ?? "";
        }

        // Get MaxRank
        var maxRank = 1;
        if (selection.Data.ExtraFields != null &&
            selection.Data.ExtraFields.TryGetValue("MaxRank", out var maxRankElement) &&
            maxRankElement.ValueKind == JsonValueKind.Number) {
          maxRank = maxRankElement.GetInt32();
        }

        // Match against feature group map to get available features
        var availableFeatures = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(groupValue) &&
            featureGroupMap.TryGetValue(groupValue, out var featuresForGroup)) {
          foreach (var feature in featuresForGroup) {
            try {
              availableFeatures.Add(StaticFeatureExtractor.ExtractFeature(feature, resolver, catalog));
            }
            catch {
              /* skip */
            }
          }
        }

        selectionData["group"] = groupValue;
        selectionData["maxRank"] = maxRank;
        selectionData["availableFeatures"] = availableFeatures;

        selections.Add(selectionData);
      }
      catch {
        /* skip selections that fail to extract */
      }
    }

    return selections;
  }

  /**
   * Yields the raw feature blueprint documents auto-granted across all rank entries.
   */
  public static IEnumerable<BlueprintDocument> EnumerateGrantedFeatureBlueprints(
    BlueprintDocument document, BlueprintCatalog catalog) {
    if (document.Data.ExtraFields == null ||
        !document.Data.ExtraFields.TryGetValue("RankEntries", out var rankEntriesElement) ||
        rankEntriesElement.ValueKind != JsonValueKind.Array) {
      yield break;
    }

    foreach (var rankEntry in rankEntriesElement.EnumerateArray()) {
      if (!rankEntry.TryGetProperty("m_Features", out var featuresElement) ||
          featuresElement.ValueKind != JsonValueKind.Array) {
        continue;
      }

      foreach (var feature in (from featureRef in featuresElement.EnumerateArray()
               where featureRef.ValueKind == JsonValueKind.String
               select StaticFeatureExtractor.NormalizeBlueprintReference(featureRef.GetString())
               into guid
               where !string.IsNullOrEmpty(guid)
               select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
        yield return feature;
      }
    }
  }

  /**
   * Yields raw blueprint documents granted via AddFacts components on the given blueprint.
   * Used to traverse occupation innate abilities and career-level auto-grants.
   */
  public static IEnumerable<BlueprintDocument> EnumerateAddFactsBlueprints(
    BlueprintDocument document, BlueprintCatalog catalog) {
    if (document.Data.Components == null) yield break;

    foreach (var component in document.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("AddFacts", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("m_Facts", out var factsElement) ||
          factsElement.ValueKind != JsonValueKind.Array) {
        continue;
      }

      foreach (var fact in (from factRef in factsElement.EnumerateArray()
               where factRef.ValueKind == JsonValueKind.String
               select StaticFeatureExtractor.NormalizeBlueprintReference(factRef.GetString())
               into guid
               where !string.IsNullOrEmpty(guid)
               select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
        yield return fact;
      }
    }
  }

  /**
   * Yields career path blueprints referenced by ApplyCareerPath components on a companion FeatureList.
   */
  public static IEnumerable<BlueprintDocument> EnumerateFeatureListCareerPaths(
    BlueprintDocument featureList, BlueprintCatalog catalog) {
    if (featureList.Data.Components == null) yield break;

    foreach (var component in featureList.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("ApplyCareerPath", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("m_CareerPath", out var careerRef) ||
          careerRef.ValueKind != JsonValueKind.String) continue;

      var guid = StaticFeatureExtractor.NormalizeBlueprintReference(careerRef.GetString());
      if (string.IsNullOrEmpty(guid)) continue;

      var career = catalog.GetById(guid);
      if (career != null) yield return career;
    }
  }

  /**
   * Yields occupation blueprints from ChargenOccupation selections in ApplyCareerPath components.
   * Used for companion-specific occupations not in player chargen paths.
   */
  public static IEnumerable<BlueprintDocument> EnumerateFeatureListOccupations(
    BlueprintDocument featureList, BlueprintCatalog catalog) {
    if (featureList.Data.Components == null) yield break;

    foreach (var component in featureList.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("ApplyCareerPath", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("Selections", out var selectionsElement) ||
          selectionsElement.ValueKind != JsonValueKind.Array) {
        continue;
      }

      foreach (var selectionGroup in selectionsElement.EnumerateArray()) {
        if (!selectionGroup.TryGetProperty("Group", out var groupProperty) ||
            groupProperty.GetString() != "ChargenOccupation") continue;

        if (!selectionGroup.TryGetProperty("m_Items", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array) continue;

        foreach (var occupation in (from itemRef in itemsElement.EnumerateArray()
                 where itemRef.ValueKind == JsonValueKind.String
                 select StaticFeatureExtractor.NormalizeBlueprintReference(itemRef.GetString())
                 into guid
                 where !string.IsNullOrEmpty(guid)
                 select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
          yield return occupation;
        }
      }
    }
  }

  /**
   * Returns the companion FeatureList blueprint from a BlueprintUnit's m_AddFacts field.
   * The FeatureList is identified by containing at least one ApplyCareerPath component.
   */
  public static BlueprintDocument? FindCompanionFeatureList(
    BlueprintDocument unit, BlueprintCatalog catalog) {
    if (unit.Data.ExtraFields == null ||
        !unit.Data.ExtraFields.TryGetValue("m_AddFacts", out var addFactsElement) ||
        addFactsElement.ValueKind != JsonValueKind.Array) {
      return null;
    }

    foreach (var fact in (from factRef in addFactsElement.EnumerateArray()
             where factRef.ValueKind == JsonValueKind.String
             select StaticFeatureExtractor.NormalizeBlueprintReference(factRef.GetString())
             into guid
             where !string.IsNullOrEmpty(guid)
             select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
      if (fact.Data.Components == null) continue;

      var hasApplyCareerPath = false;

      foreach (var component in fact.Data.Components) {
        if (!component.TryGetProperty("$type", out var typeProperty)) continue;
        if (!(typeProperty.GetString() ?? "").Contains("ApplyCareerPath", StringComparison.Ordinal)) continue;

        hasApplyCareerPath = true;
        break;
      }

      if (hasApplyCareerPath) return fact;
    }

    return null;
  }

  /**
   * Yields raw blueprint documents from a BlueprintUnit's m_AddFacts, skipping the FeatureList
   * (identified by containing an ApplyCareerPath component).
   */
  public static IEnumerable<BlueprintDocument> EnumerateUnitDirectFacts(
    BlueprintDocument unit, BlueprintCatalog catalog) {
    if (unit.Data.ExtraFields == null ||
        !unit.Data.ExtraFields.TryGetValue("m_AddFacts", out var addFactsElement) ||
        addFactsElement.ValueKind != JsonValueKind.Array) {
      yield break;
    }

    foreach (var fact in (from factRef in addFactsElement.EnumerateArray()
             where factRef.ValueKind == JsonValueKind.String
             select StaticFeatureExtractor.NormalizeBlueprintReference(factRef.GetString())
             into guid
             where !string.IsNullOrEmpty(guid)
             select catalog.GetById(guid)).OfType<BlueprintDocument>()) {
      // Skip the FeatureList - it has ApplyCareerPath components
      if (fact.Data.Components != null) {
        var hasApplyCareerPath = false;
        foreach (var component in fact.Data.Components) {
          if (!component.TryGetProperty("$type", out var typeProperty)) continue;
          if (!(typeProperty.GetString() ?? "").Contains("ApplyCareerPath", StringComparison.Ordinal)) continue;

          hasApplyCareerPath = true;
          break;
        }

        if (hasApplyCareerPath) continue;
      }

      yield return fact;
    }
  }

  /**
   * Extracts pre-selected features from a single ApplyCareerPath component's Selections field.
   * Returns a dictionary from group name to list of feature GUIDs.
   * Used by the companion exporter to read what a companion "chose" at chargen.
   */
  public static Dictionary<string, List<string>> ExtractSelectionsFromComponent(
    JsonElement applyCareerPathComponent) {
    var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    if (!applyCareerPathComponent.TryGetProperty("Selections", out var selectionsElement) ||
        selectionsElement.ValueKind != JsonValueKind.Array) {
      return result;
    }

    foreach (var selectionGroup in selectionsElement.EnumerateArray()) {
      if (!selectionGroup.TryGetProperty("Group", out var groupProperty) ||
          groupProperty.ValueKind != JsonValueKind.String) continue;

      var groupValue = groupProperty.GetString() ?? "";
      if (string.IsNullOrEmpty(groupValue)) continue;

      // CamelCase to match runtime JSON output (Newtonsoft camelCases dictionary keys).
      groupValue = StaticFeatureExtractor.ToCamelCase(groupValue);

      if (!selectionGroup.TryGetProperty("m_Items", out var itemsElement) ||
          itemsElement.ValueKind != JsonValueKind.Array) continue;

      var itemGuids = (from itemRef in itemsElement.EnumerateArray()
        where itemRef.ValueKind == JsonValueKind.String
        select StaticFeatureExtractor.NormalizeBlueprintReference(itemRef.GetString())
        into guid
        where !string.IsNullOrEmpty(guid)
        select guid).ToList();

      if (itemGuids.Count <= 0) continue;

      if (!result.TryGetValue(groupValue, out var existing)) {
        result[groupValue] = itemGuids;
      }
      else {
        existing.AddRange(itemGuids);
      }
    }

    return result;
  }

  /**
   * Yields facts granted to the wielder by an equipment item via AddFactToEquipmentWielder components.
   */
  public static IEnumerable<BlueprintDocument> EnumerateEquipmentGrantedFacts(
    BlueprintDocument item, BlueprintCatalog catalog) {
    if (item.Data.Components == null) yield break;

    foreach (var component in item.Data.Components) {
      if (!component.TryGetProperty("$type", out var typeProperty)) continue;

      var typeName = typeProperty.GetString() ?? "";
      if (!typeName.Contains("AddFactToEquipmentWielder", StringComparison.Ordinal)) continue;

      if (!component.TryGetProperty("m_Fact", out var factRef) ||
          factRef.ValueKind != JsonValueKind.String) continue;

      var guid = StaticFeatureExtractor.NormalizeBlueprintReference(factRef.GetString());
      if (string.IsNullOrEmpty(guid)) continue;

      var fact = catalog.GetById(guid);
      if (fact != null) yield return fact;
    }
  }
}