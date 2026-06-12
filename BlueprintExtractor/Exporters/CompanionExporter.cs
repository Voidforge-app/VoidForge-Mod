using System.Collections;
using System.Reflection;
using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
using Kingmaker.Localization;
using Kingmaker.UnitLogic.Progression.Paths;

namespace BlueprintExtractor.Exporters;

/**
 * Exports all named and hidden companions - starting career, pre-selected features, unique abilities, and equipment.
 * Companions are identified by asset name suffix (Companion/_Companion) and the presence of an ApplyCareerPath
 * component in their AddFacts list.
 */
public static class CompanionExporter {
  private const string Source = "companions";

  private const BindingFlags AllInstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

  public static HashSet<string> Export(ModLogger logger, string gameVersion, string gameRevision,
    string outputDirectory) {
    var companions = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var unit in BlueprintsCatalog.AllBlueprints<BlueprintUnit>())
      try {
        if (!IsBaseCompanionUnit(unit)) continue;

        var featureListFeature = FindFeatureListFeature(unit);

        if (featureListFeature == null) {
          skippedCount++;

          continue;
        }

        companions.Add(ExtractCompanionData(unit, featureListFeature));
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={unit.AssetGuid} reason={exception.Message}");
      }

    companions = DeduplicateByName(companions);

    companions.Sort((a, b) =>
      string.Compare(a["Name"] as string ?? "", b["Name"] as string ?? "", StringComparison.Ordinal));

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, companions);
    ExportWriter.WriteEnvelope(outputDirectory, "companions", envelope);

    logger.Result(Source, "export done", ("count", companions.Count), ("filtered", skippedCount));

    return companions.Select(c => c["Id"] as string ?? "").Where(id => id.Length > 0).ToHashSet();
  }

  /**
   * Removes companions that share the same resolved name, keeping the entry with the most CareerProgressions.
   * Story-variant companions (e.g. Hope_Chorda_Companion vs Chorda_Companion) share a LocalizedName string key and
   * would otherwise export as identical-named duplicates. The richer entry (more career data) is the one players
   * actually level up.
   */
  private static List<Dictionary<string, object>> DeduplicateByName(List<Dictionary<string, object>> companions) {
    var seen = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

    foreach (var companion in companions) {
      var name = companion["Name"] as string ?? "";

      if (!seen.TryGetValue(name, out var existing)) {
        seen[name] = companion;

        continue;
      }

      var existingProgressionCount = (existing["CareerProgressions"] as List<Dictionary<string, object>>)?.Count ?? 0;
      var candidateProgressionCount = (companion["CareerProgressions"] as List<Dictionary<string, object>>)?.Count ?? 0;

      if (candidateProgressionCount > existingProgressionCount) {
        seen[name] = companion;
      }
    }

    return seen.Values.ToList();
  }

  /**
   * Matches the base companion unit blueprints by their asset name.
   * Valid names end with "Companion" (covers both "ArgentaCompanion" and "Argenta_Companion" patterns).
   * Excludes: chapter variants (e.g. Ulfar_Ch03End_Companion), dev/test units (TESTArgentaCompanion).
   */
  private static bool IsBaseCompanionUnit(BlueprintUnit unit) {
    var assetName = unit.name;

    if (!assetName.EndsWith("Companion")) return false;
    if (assetName.Contains("_Ch")) return false;

    return !assetName.StartsWith("TEST");
  }

  /**
   * Finds the *FeatureList feature in the unit's m_AddFacts - identified by having an ApplyCareerPath component.
   */
  private static object FindFeatureListFeature(BlueprintUnit unit) {
    var addFactsField = unit.GetType().GetField("m_AddFacts", AllInstanceFlags);

    if (addFactsField?.GetValue(unit) is not IEnumerable factRefs) return null;

    foreach (var factRef in factRefs) {
      if (factRef == null) continue;

      object fact = null;

      try {
        fact = RankEntryExtractor.Dereference(factRef);
      }
      catch {
        continue;
      }

      var componentsProperty = fact?.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

      if (componentsProperty?.GetValue(fact) is not IEnumerable components) continue;

      if (components.Cast<object>().Any(component => component?.GetType().Name == "ApplyCareerPath")) {
        return fact;
      }
    }

    return null;
  }

  private static Dictionary<string, object> ExtractCompanionData(BlueprintUnit unit, object featureListFeature) {
    return new Dictionary<string, object> {
      ["Id"] = unit.AssetGuid,
      ["Name"] = ExtractUnitName(unit),
      ["Gender"] = unit.GetType().GetField("Gender", AllInstanceFlags)?.GetValue(unit)?.ToString() ?? "",
      ["CareerProgressions"] = ExtractCareerProgressions(featureListFeature),
      ["UniqueFeatureIds"] = ExtractUniqueFeatureIds(featureListFeature),
      ["StartingEquipment"] = ExtractStartingEquipment(unit),
    };
  }

  /**
   * BlueprintUnit stores its name in LocalizedName (a SharedStringAsset), not m_DisplayName.
   * SharedStringAsset.String is a LocalizedString whose ToString() resolves through the localization manager.
   */
  private static string ExtractUnitName(BlueprintUnit unit) {
    var unitType = unit.GetType();

    try {
      var localizedNameField = unitType.GetField("LocalizedName", AllInstanceFlags);
      var localizedName = localizedNameField?.GetValue(unit);

      if (localizedName != null) {
        var stringField = localizedName.GetType().GetField("String", AllInstanceFlags);

        if (stringField != null) {
          var localizedString = stringField.GetValue(localizedName);

          if (localizedString != null) {
            // LocalizedString.ToString() calls a static CurrentPack that may be null at export time.
            // Use LocalizationManager.Instance.CurrentPack directly with the raw m_Key.
            var keyField = localizedString.GetType().GetField("m_Key", AllInstanceFlags);
            var key = keyField?.GetValue(localizedString) as string;

            if (!string.IsNullOrEmpty(key)) {
              var text = LocalizationManager.Instance.CurrentPack.GetText(key);

              if (!string.IsNullOrEmpty(text)) return text;
            }
          }
        }
      }
    }
    catch { }

    // Fallback: standard Name property (works for units whose m_DisplayName is populated)
    try {
      var nameProperty = unitType.GetProperty("Name", AllInstanceFlags);
      var name = nameProperty?.GetValue(unit) as string;

      if (!string.IsNullOrEmpty(name)) return name;
    }
    catch { }

    return "";
  }

  /**
   * A companion may have multiple ApplyCareerPath entries - one for their actual career and
   * optionally one for their chargen path (background homeworld/occupation selections).
   */
  private static List<Dictionary<string, object>> ExtractCareerProgressions(object featureListFeature) {
    var progressions = new List<Dictionary<string, object>>();

    var componentsProperty = featureListFeature.GetType().GetProperty("ComponentsArray", AllInstanceFlags);
    var components = componentsProperty?.GetValue(featureListFeature) as IEnumerable;

    if (components == null) return progressions;

    foreach (var component in components) {
      if (component?.GetType().Name != "ApplyCareerPath") continue;

      try {
        progressions.Add(ExtractSingleProgression(component));
      }
      catch {
        /* skip malformed */
      }
    }

    return progressions;
  }

  private static Dictionary<string, object> ExtractSingleProgression(object component) {
    var componentType = component.GetType();

    var careerPathRef = componentType.GetField("m_CareerPath", AllInstanceFlags)?.GetValue(component);
    var careerPath = careerPathRef != null ? TryDereference(careerPathRef) : null;

    var careerId = (careerPath as SimpleBlueprint)?.AssetGuid ?? "";
    var isChargenPath = careerPath is BlueprintOriginPath;
    var rank = (int)(componentType.GetField("Ranks", AllInstanceFlags)?.GetValue(component) ?? 0);

    return new Dictionary<string, object> {
      ["CareerId"] = careerId,
      ["StartingRank"] = rank,
      ["IsChargenPath"] = isChargenPath,
      ["PreSelectedFeatures"] = ExtractSelections(component, componentType),
    };
  }

  private static Dictionary<string, List<string>> ExtractSelections(object component, Type componentType) {
    var result = new Dictionary<string, List<string>>();

    var selectionsField = componentType.GetField("Selections", AllInstanceFlags);

    if (selectionsField?.GetValue(component) is not IEnumerable selections) return result;

    foreach (var selection in selections) {
      if (selection == null) continue;

      var selectionType = selection.GetType();
      var groupValue = selectionType.GetField("Group", AllInstanceFlags)?.GetValue(selection)?.ToString();
      var items = selectionType.GetField("m_Items", AllInstanceFlags)?.GetValue(selection) as IEnumerable;

      if (groupValue == null || items == null) continue;

      var itemGuids = new List<string>();

      foreach (var item in items) {
        if (item == null) continue;

        try {
          var resolved = RankEntryExtractor.Dereference(item);

          if (resolved is SimpleBlueprint simpleBp) {
            itemGuids.Add(simpleBp.AssetGuid);
          }
        }
        catch {
          /* skip unresolvable */
        }
      }

      result[groupValue] = itemGuids;
    }

    return result;
  }

  /**
   * Extracts the companion-specific feature GUIDs from the AddFacts component on the FeatureList.
   * These are the abilities unique to this companion (e.g., Argenta's Repentia features).
   */
  private static List<string> ExtractUniqueFeatureIds(object featureListFeature) {
    var result = new List<string>();

    var componentsProperty = featureListFeature.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(featureListFeature) is not IEnumerable components) return result;

    foreach (var component in components) {
      if (component?.GetType().Name != "AddFacts") continue;

      var factsField = component.GetType().GetField("m_Facts", AllInstanceFlags);

      if (factsField?.GetValue(component) is not IEnumerable facts) continue;

      foreach (var factRef in facts) {
        if (factRef == null) continue;

        try {
          var resolved = RankEntryExtractor.Dereference(factRef);

          if (resolved is SimpleBlueprint simpleBp) {
            result.Add(simpleBp.AssetGuid);
          }
        }
        catch { }
      }

      break;
    }

    return result;
  }

  private static Dictionary<string, object> ExtractStartingEquipment(BlueprintUnit unit) {
    var result = new Dictionary<string, object>();

    var bodyField = unit.GetType().GetField("Body", AllInstanceFlags);
    var body = bodyField?.GetValue(unit);

    if (body == null) return result;

    var bodyType = body.GetType();

    var handSettingsField = bodyType.GetField("ItemEquipmentHandSettings", AllInstanceFlags);
    var handSettings = handSettingsField?.GetValue(body);

    if (handSettings != null) {
      var handType = handSettings.GetType();

      var primaryId = ResolveItemId(handType, handSettings, "m_PrimaryHand");
      var secondaryId = ResolveItemId(handType, handSettings, "m_SecondaryHand");
      var altPrimaryId = ResolveItemId(handType, handSettings, "m_PrimaryHandAlternative1");

      if (primaryId != null) result["PrimaryWeaponId"] = primaryId;
      if (secondaryId != null) result["SecondaryWeaponId"] = secondaryId;
      if (altPrimaryId != null) result["PrimaryWeaponAlt1Id"] = altPrimaryId;
    }

    var armorId = ResolveItemId(bodyType, body, "m_Armor");
    if (armorId != null) result["ArmorId"] = armorId;

    return result;
  }

  private static string ResolveItemId(Type containerType, object container, string fieldName) {
    var field = containerType.GetField(fieldName, AllInstanceFlags);
    var itemRef = field?.GetValue(container);

    if (itemRef == null) return null;

    try {
      var resolved = RankEntryExtractor.Dereference(itemRef);

      return (resolved as SimpleBlueprint)?.AssetGuid;
    }
    catch {
      return null;
    }
  }

  private static object TryDereference(object reference) {
    try {
      return RankEntryExtractor.Dereference(reference);
    }
    catch {
      return null;
    }
  }
}