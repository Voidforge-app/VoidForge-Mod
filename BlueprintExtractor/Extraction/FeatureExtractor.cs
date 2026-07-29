using System.Collections;
using System.Reflection;
using Kingmaker.Blueprints;
using Kingmaker.UI.Models.Tooltip.Base;

namespace BlueprintExtractor.Extraction;

/**
 * Focused extraction of player-facing data from blueprint objects.
 * Produces clean dictionaries with only build-planner-relevant fields:
 * id, name, description, featureTypes, prerequisites, and petInfo for Master career pet keystones.
 */
public static class FeatureExtractor {
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  /**
   * Extracts the minimal set of fields needed by the build planner from any feature/blueprint.
   */
  public static Dictionary<string, object> ExtractFeature(object blueprint) {
    var result = new Dictionary<string, object>();
    var blueprintType = blueprint.GetType();

    if (blueprint is SimpleBlueprint simpleBlueprint) {
      result["Id"] = simpleBlueprint.AssetGuid;
    }

    if (blueprint is IUIDataProvider uiData) {
      var rawName = "";
      var rawDescription = "";

      try {
        rawName = uiData.Name;
      }
      catch {
        /* template variables require active session */
      }

      try {
        rawDescription = uiData.Description;
      }
      catch {
        /* template variables require active session */
      }

      result["Name"] = SanitizeLocalizedString(rawName);
      result["Description"] = SanitizeLocalizedString(rawDescription);
    }
    else {
      result["Name"] = "";
      result["Description"] = "";
    }

    result["FeatureTypes"] = ExtractFeatureTypes(blueprint, blueprintType);
    result["Prerequisites"] = ExtractPrerequisiteComponents(blueprint, blueprintType);
    result["TalentGroup"] = ExtractTalentGroup(blueprint, blueprintType);

    var uiParams = UiParamExtractor.ExtractUiParams(blueprint);

    if (uiParams.Count > 0) result["UiParams"] = uiParams;

    var petInfo = ExtractPetInfo(blueprint, blueprintType);

    if (petInfo != null) result["PetInfo"] = petInfo;

    var characteristicModifiers = CharacteristicExtractor.ExtractCharacteristicModifiers(blueprint);

    result["CharacteristicModifiers"] = characteristicModifiers;

    var advancementStep = CharacteristicExtractor.ExtractAdvancementStep(blueprint);

    if (advancementStep.HasValue) result["AdvancementStep"] = advancementStep.Value;

    return result;
  }

  /**
   * Extracts the chargen feature groups from a BlueprintOriginPath (e.g. ChargenHomeworld, ChargenOccupation).
   * Returns a map from group name to the list of fully-extracted feature records.
   */
  public static Dictionary<string, List<Dictionary<string, object>>> ExtractChargenGroups(object blueprint) {
    var result = new Dictionary<string, List<Dictionary<string, object>>>();

    foreach (var groupEntry in RankEntryExtractor.BuildFeatureGroupMap(blueprint)) {
      var featureList = new List<Dictionary<string, object>>();

      foreach (var feature in groupEntry.Value)
        try {
          featureList.Add(ExtractFeature(feature));
        }
        catch {
          /* skip features that fail to extract */
        }

      result[groupEntry.Key] = featureList;
    }

    return result;
  }

  /**
   * Extracts CareerPathUIMetaData from a BlueprintCareerPath's ComponentsArray.
   * Returns GUIDs for keystones, ultimates, recommended features, and recommended-by occupations.
   */
  public static Dictionary<string, object> ExtractCareerMetadata(object blueprint) {
    var result = new Dictionary<string, object> {
      ["KeystoneFeatureIds"] = new List<string>(),
      ["UltimateFeatureIds"] = new List<string>(),
      ["RecommendedFeatureIds"] = new List<string>(),
      ["RecommendedByOccupationIds"] = new List<string>(),
    };

    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return result;

    foreach (var component in components) {
      if (component?.GetType().Name != "CareerPathUIMetaData") continue;

      var componentType = component.GetType();
      result["KeystoneFeatureIds"] = ExtractReferenceGuids(component, componentType, "m_KeystoneFeatures");
      result["UltimateFeatureIds"] = ExtractReferenceGuids(component, componentType, "m_UltimateFeatures");
      result["RecommendedFeatureIds"] = ExtractReferenceGuids(component, componentType, "m_RecommendedFeatures");
      result["RecommendedByOccupationIds"] =
        ExtractReferenceGuids(component, componentType, "m_RecommendedByOccupations");

      break;
    }

    return result;
  }

  private static List<string> ExtractReferenceGuids(object component, Type componentType, string fieldName) {
    var guids = new List<string>();

    var field = componentType.GetField(fieldName, AllInstanceFlags);

    if (field == null || field.GetValue(component) is not IEnumerable references) return guids;

    foreach (var reference in references) {
      if (reference == null) continue;

      try {
        var resolved = RankEntryExtractor.Dereference(reference);

        if (resolved is SimpleBlueprint resolvedBlueprint) {
          guids.Add(resolvedBlueprint.AssetGuid);
        }
      }
      catch {
        /* skip unresolvable references */
      }
    }

    return guids;
  }

  /**
   * Extracts PetKeystoneInfoComponent data from Master career pet keystone features.
   * Returns null for all blueprints that do not carry this component.
   */
  private static Dictionary<string, object> ExtractPetInfo(object blueprint, Type blueprintType) {
    var componentsProperty = blueprintType.GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return null;

    foreach (var component in components) {
      if (component?.GetType().Name != "PetKeystoneInfoComponent") continue;

      var componentType = component.GetType();
      var petInfo = new Dictionary<string, object>();

      // PetType enum value: Mastiff, Eagle, Raven, SkullSwarm
      var petTypeField = componentType.GetField("PetType", AllInstanceFlags);

      if (petTypeField?.GetValue(component) is { } petTypeValue) {
        petInfo["PetType"] = petTypeValue.ToString();
      }

      // KeyStats: list of { StatType } structs -- each StatType is a stat enum value
      var keyStatsField = componentType.GetField("KeyStats", AllInstanceFlags);

      if (keyStatsField?.GetValue(component) is IEnumerable keyStats) {
        var statTypes = (from object keyStat in keyStats
          where keyStat != null
          let statTypeField = keyStat.GetType().GetField("StatType", AllInstanceFlags)
          select statTypeField?.GetValue(keyStat)
          into statTypeValue
          where statTypeValue != null
          select statTypeValue.ToString()).ToList();

        petInfo["KeyStats"] = statTypes;
      }

      // CoreAbilitiesReferences: list of blueprint references to the pet's signature abilities
      var coreAbilitiesField = componentType.GetField("CoreAbilitiesReferences", AllInstanceFlags);

      if (coreAbilitiesField?.GetValue(component) is IEnumerable coreRefs) {
        var abilityGuids = new List<string>();

        foreach (var abilityRef in coreRefs) {
          if (abilityRef == null) continue;

          try {
            if (RankEntryExtractor.Dereference(abilityRef) is SimpleBlueprint resolvedAbility) {
              abilityGuids.Add(resolvedAbility.AssetGuid);
            }
          }
          catch {
            /* skip unresolvable */
          }
        }

        petInfo["CoreAbilityIds"] = abilityGuids;
      }

      // PetUnitReference: the BlueprintUnit for the actual pet -- used to look up pet stats
      var petUnitRefField = componentType.GetField("PetUnitReference", AllInstanceFlags);

      if (petUnitRefField != null) {
        try {
          if (RankEntryExtractor.Dereference(petUnitRefField.GetValue(component)) is SimpleBlueprint resolvedUnit) {
            petInfo["PetUnitId"] = resolvedUnit.AssetGuid;
          }
        }
        catch {
          /* skip unresolvable */
        }
      }

      return petInfo.Count > 0 ? petInfo : null;
    }

    return null;
  }

  private static string ExtractTalentGroup(object blueprint, Type blueprintType) {
    var talentIconInfoField = blueprintType.GetField("TalentIconInfo", AllInstanceFlags);
    var talentIconInfo = talentIconInfoField?.GetValue(blueprint);

    if (talentIconInfo == null) return null;

    var mainGroupField = talentIconInfo.GetType().GetField("MainGroup", AllInstanceFlags);

    return mainGroupField?.GetValue(talentIconInfo)?.ToString();
  }

  private static List<string> ExtractFeatureTypes(object blueprint, Type blueprintType) {
    var result = new List<string>();

    var field = blueprintType.GetField("FeatureTypes", AllInstanceFlags);

    if (field == null || field.GetValue(blueprint) is not IEnumerable list) return result;

    result.AddRange(from object item in list where item != null select item.ToString());

    return result;
  }

  private static List<Dictionary<string, object>> ExtractPrerequisiteComponents(object blueprint, Type blueprintType) {
    var result = new List<Dictionary<string, object>>();

    // Modern system: prerequisites live in blueprint.Prerequisites.List
    // Prerequisites may be a field or property depending on the blueprint type — try both.
    var prerequisitesField = blueprintType.GetField("Prerequisites", AllInstanceFlags);
    var prerequisites = prerequisitesField != null
      ? prerequisitesField.GetValue(blueprint)
      : blueprintType.GetProperty("Prerequisites", AllInstanceFlags)?.GetValue(blueprint);
    var listField = prerequisites?.GetType().GetField("List", AllInstanceFlags);

    if (listField?.GetValue(prerequisites) is IEnumerable modernList) {
      foreach (var prerequisite in modernList) {
        if (prerequisite == null) continue;

        try {
          result.Add(ExtractPrerequisiteComponent(prerequisite));
        }
        catch {
          /* skip malformed */
        }
      }
    }

    // Legacy system: Prerequisite_Obsolete components on ComponentsArray
    var componentsProperty = blueprintType.GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return result;

    foreach (var component in components) {
      if (component == null) continue;
      if (!component.GetType().Name.StartsWith("Prerequisite")) continue;

      try {
        result.Add(ExtractPrerequisiteComponent(component));
      }
      catch {
        /* skip malformed */
      }
    }

    return result;
  }

  private static Dictionary<string, object> ExtractPrerequisiteComponent(object component) {
    var componentType = component.GetType();
    var prereq = new Dictionary<string, object> {
      ["Type"] = componentType.Name,
    };

    // Group = And/Any - how this prerequisite combines with siblings
    var groupField = componentType.GetField("Group", AllInstanceFlags);

    if (groupField != null) {
      prereq["Composition"] = groupField.GetValue(component)?.ToString();
    }

    // Level = minimum class level required (PrerequisiteClassLevel)
    var levelField = componentType.GetField("Level", AllInstanceFlags);

    if (levelField != null) {
      prereq["RequiredLevel"] = levelField.GetValue(component);
    }

    // Single class reference (PrerequisiteClassLevel)
    TryExtractSingleRef(component, componentType, "m_CharacterClass", "RequiredClassId", prereq);

    // Multi-class reference list (PrerequisiteTakenClass)
    TryExtractMultiRef(component, componentType, "m_Classes", "RequiredClassIds", prereq);

    // Feature reference (PrerequisiteFeature)
    TryExtractSingleRef(component, componentType, "m_Feature", "RequiredFeatureId", prereq);

    // Fact reference (PrerequisiteFact — the more common modern form)
    TryExtractSingleRef(component, componentType, "m_Fact", "RequiredFeatureId", prereq);

    // Not flag: prerequisite is inverted ("must NOT have this feature")
    var notField = componentType.GetField("Not", AllInstanceFlags);

    if (notField?.GetValue(component) is true) prereq["Not"] = true;

    return prereq;
  }

  private static void TryExtractSingleRef(
    object component, Type componentType, string fieldName, string outputKey,
    Dictionary<string, object> target) {
    var field = componentType.GetField(fieldName, AllInstanceFlags);

    if (field == null) return;

    try {
      var resolved = RankEntryExtractor.Dereference(field.GetValue(component));

      if (resolved is SimpleBlueprint resolvedBlueprint) {
        target[outputKey] = resolvedBlueprint.AssetGuid;
      }
    }
    catch {
      /* skip unresolvable reference */
    }
  }

  private static void TryExtractMultiRef(
    object component, Type componentType, string fieldName, string outputKey,
    Dictionary<string, object> target) {
    var field = componentType.GetField(fieldName, AllInstanceFlags);

    if (field == null) return;

    if (field.GetValue(component) is not IEnumerable references) return;

    var guids = new List<string>();

    foreach (var reference in references) {
      if (reference == null) continue;

      try {
        var resolved = RankEntryExtractor.Dereference(reference);

        if (resolved is SimpleBlueprint resolvedBlueprint) {
          guids.Add(resolvedBlueprint.AssetGuid);
        }
      }
      catch {
        /* skip */
      }
    }

    if (guids.Count > 0) target[outputKey] = guids;
  }

  /**
   * Returns an empty string for null/empty values and for localization sentinel strings
   * that the game returns when a key is missing ("
   * <null>" or "[unknown key: ...").
   */
  public static string SanitizeLocalizedString(string value) {
    if (string.IsNullOrWhiteSpace(value)) return "";
    if (value == "<null>") return "";

    return value.StartsWith("[unknown key:") ? "" : value;
  }
}