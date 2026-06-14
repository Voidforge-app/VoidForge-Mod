using System.Collections;
using System.Reflection;

namespace BlueprintExtractor.Extraction;

/**
 * Shared extraction logic for BlueprintPath subclasses (careers, origins).
 * 
 * Both BlueprintCareerPath and BlueprintOriginPath use the same RankEntry structure.
 * 
 * How selection features are found:
 * 
 * - The path blueprint carries AddFeaturesToLevelUp components, one per talent pool.
 * - Each component has: group (FeatureGroup enum) + m_Features (BlueprintFeatureReference[]).
 * - At each rank, selections reference a BlueprintSelectionFeature whose Group enum value
 * - matches a component's group, giving the available talent list for that selection.
 */
public static class RankEntryExtractor {
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  public static List<Dictionary<string, object>> ExtractRankEntries(object blueprint) {
    var entries = new List<Dictionary<string, object>>();

    // Build FeatureGroup -> features map from AddFeaturesToLevelUp components on the path blueprint.
    var featureGroupMap = BuildFeatureGroupMap(blueprint);

    var rankEntriesField = blueprint.GetType().GetField("RankEntries", AllInstanceFlags);

    if (rankEntriesField == null || rankEntriesField.GetValue(blueprint) is not IEnumerable rankEntries) return entries;

    foreach (var rankEntry in rankEntries) {
      if (rankEntry == null) continue;

      var entryType = rankEntry.GetType();
      var entryData = new Dictionary<string, object>();

      var rankField = entryType.GetField("Rank", AllInstanceFlags);
      if (rankField != null) entryData["Rank"] = rankField.GetValue(rankEntry);

      var tierField = entryType.GetField("Tier", AllInstanceFlags);
      if (tierField != null) entryData["Tier"] = tierField.GetValue(rankEntry)?.ToString();

      var grantedFeatures = ExtractFeaturesFromRankEntry(rankEntry, entryType);
      entryData["GrantedFeatures"] = grantedFeatures;

      var selections = ExtractSelectionsFromRankEntry(rankEntry, entryType, featureGroupMap);
      entryData["Selections"] = selections;

      entries.Add(entryData);
    }

    return entries;
  }

  /**
   * Reads all AddFeaturesToLevelUp components from a career/origin path blueprint and builds a map from FeatureGroup
   * enum string to the list of resolved feature blueprints.
   * 
   * Public so FeatureExtractor can reuse it for chargen group extraction.
   */
  public static Dictionary<string, List<object>> BuildFeatureGroupMap(object blueprint) {
    var groupMap = new Dictionary<string, List<object>>();

    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return groupMap;

    foreach (var component in components) {
      if (component == null) continue;
      if (component.GetType().Name != "AddFeaturesToLevelUp") continue;

      var groupField = component.GetType().GetField("Group", AllInstanceFlags);
      var featuresField = component.GetType().GetField("m_Features", AllInstanceFlags);

      if (groupField == null || featuresField == null) continue;

      var groupValue = groupField.GetValue(component)?.ToString();

      if (groupValue == null) continue;

      var featureRefs = featuresField.GetValue(component) as IEnumerable;

      if (featureRefs == null) continue;

      if (!groupMap.TryGetValue(groupValue, out var featureList)) {
        featureList = [];
        groupMap[groupValue] = featureList;
      }

      foreach (var featureRef in featureRefs) {
        if (featureRef == null) continue;

        try {
          var feature = Dereference(featureRef);
          if (feature != null) featureList.Add(feature);
        }
        catch {
          /* skip unresolvable references */
        }
      }
    }

    return groupMap;
  }

  private static List<Dictionary<string, object>> ExtractFeaturesFromRankEntry(object rankEntry, Type entryType) {
    var features = new List<Dictionary<string, object>>();

    var featuresField = entryType.GetField("m_Features", AllInstanceFlags);

    if (featuresField == null || featuresField.GetValue(rankEntry) is not IEnumerable featureRefs) return features;

    foreach (var featureRef in featureRefs) {
      if (featureRef == null) continue;

      try {
        var feature = Dereference(featureRef);

        if (feature == null) continue;
        features.Add(FeatureExtractor.ExtractFeature(feature));
      }
      catch {
        /* skip features that fail to extract */
      }
    }

    return features;
  }

  private static List<Dictionary<string, object>> ExtractSelectionsFromRankEntry(
    object rankEntry, Type entryType, Dictionary<string, List<object>> featureGroupMap) {
    var selections = new List<Dictionary<string, object>>();

    var selectionsField = entryType.GetField("m_Selections", AllInstanceFlags);

    if (selectionsField == null) return selections;

    var selectionRefs = selectionsField.GetValue(rankEntry) as IEnumerable;

    if (selectionRefs == null) return selections;

    foreach (var selectionRef in selectionRefs) {
      if (selectionRef == null) continue;

      try {
        var selection = Dereference(selectionRef);

        if (selection == null) continue;

        var selectionType = selection.GetType();

        // Match this selection's Group field against the career's AddFeaturesToLevelUp map
        var groupField = selectionType.GetField("Group", AllInstanceFlags);
        var groupValue = groupField?.GetValue(selection)?.ToString();

        var availableFeatures = new List<Dictionary<string, object>>();

        if (groupValue != null && featureGroupMap.TryGetValue(groupValue, out var featuresForGroup)) {
          foreach (var feature in featuresForGroup)
            try {
              availableFeatures.Add(FeatureExtractor.ExtractFeature(feature));
            }
            catch {
              /* skip */
            }
        }

        var maxRankField = selectionType.GetField("MaxRank", AllInstanceFlags)
                           ?? selectionType.GetField("m_MaxRank", AllInstanceFlags);

        // Use FeatureExtractor for id/name/featureTypes/prerequisites - it has the proven
        // SimpleBlueprint cast that correctly resolves AssetGuid.
        var selectionData = FeatureExtractor.ExtractFeature(selection);
        selectionData["Group"] = groupValue ?? "";
        selectionData["MaxRank"] = maxRankField?.GetValue(selection) ?? 1;
        selectionData["AvailableFeatures"] = availableFeatures;

        selections.Add(selectionData);
      }
      catch {
        /* skip selections that fail to extract */
      }
    }

    return selections;
  }

  /**
   * Yields the raw feature blueprint objects auto-granted across all rank entries of a career/origin path.
   * 
   * Used by FeaturesExporter to build the flat feature catalogue without redundant extraction.
   */
  public static IEnumerable<object> EnumerateGrantedFeatureBlueprints(object blueprint) {
    var rankEntriesField = blueprint.GetType().GetField("RankEntries", AllInstanceFlags);

    if (rankEntriesField == null) yield break;

    var rankEntries = rankEntriesField.GetValue(blueprint) as IEnumerable;

    if (rankEntries == null) yield break;

    foreach (var rankEntry in rankEntries) {
      if (rankEntry == null) continue;

      var featuresField = rankEntry.GetType().GetField("m_Features", AllInstanceFlags);

      if (featuresField == null) continue;

      if (featuresField.GetValue(rankEntry) is not IEnumerable featureRefs) continue;

      foreach (var featureRef in featureRefs) {
        if (featureRef == null) continue;
        object feature = null;

        try {
          feature = Dereference(featureRef);
        }
        catch {
          /* skip unresolvable */
        }

        if (feature != null) yield return feature;
      }
    }
  }

  /**
   * Yields the raw blueprint objects granted via AddFacts components on the given blueprint.
   * 
   * Used to traverse occupation innate abilities that are not listed in AddFeaturesToLevelUp.
   */
  public static IEnumerable<object> EnumerateAddFactsBlueprints(object blueprint) {
    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) yield break;

    foreach (var component in components) {
      if (component == null) continue;
      if (component.GetType().Name != "AddFacts") continue;

      var factsField = component.GetType().GetField("m_Facts", AllInstanceFlags);

      if (factsField?.GetValue(component) is not IEnumerable factRefs) continue;

      foreach (var factRef in factRefs) {
        if (factRef == null) continue;

        object fact = null;

        try {
          fact = Dereference(factRef);
        }
        catch {
          /* skip unresolvable */
        }

        if (fact != null) yield return fact;
      }
    }
  }

  /**
   * Yields facts granted to the wielder by an equipment item via AddFactToEquipmentWielder components.
   * Covers item-granted abilities like "Inquisitor's Decree" from the Inquisitor Ring.
   */
  public static IEnumerable<object> EnumerateEquipmentGrantedFacts(object item) {
    var componentsProperty = item.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(item) is not IEnumerable components) yield break;

    foreach (var component in components) {
      if (component == null) continue;
      if (component.GetType().Name != "AddFactToEquipmentWielder") continue;

      var factField = component.GetType().GetField("m_Fact", AllInstanceFlags);

      if (factField == null) continue;

      var factRef = factField.GetValue(component);

      if (factRef == null) continue;

      object fact = null;

      try {
        fact = Dereference(factRef);
      }
      catch {
        /* skip unresolvable */
      }

      if (fact != null) yield return fact;
    }
  }

  /**
   * Yields career path blueprints referenced by ApplyCareerPath components on a companion FeatureList.
   * Used to collect auto-granted features from DLC-exclusive companion careers that are skipped by
   * the IsAvailable gate in the main career traversal (e.g. Kibellah's Cannoness career).
   */
  public static IEnumerable<object> EnumerateFeatureListCareerPaths(object featureList) {
    var componentsProperty = featureList.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(featureList) is not IEnumerable components) yield break;

    foreach (var component in components) {
      if (component == null) continue;
      if (component.GetType().Name != "ApplyCareerPath") continue;

      var careerPathField = component.GetType().GetField("m_CareerPath", AllInstanceFlags);

      if (careerPathField == null) continue;

      var careerPathRef = careerPathField.GetValue(component);

      if (careerPathRef == null) continue;

      object careerPath = null;

      try {
        careerPath = Dereference(careerPathRef);
      }
      catch {
        /* skip unresolvable */
      }

      if (careerPath != null) yield return careerPath;
    }
  }

  /**
   * Yields occupation blueprints from the ChargenOccupation selections recorded in a companion FeatureList.
   * Each ApplyCareerPath component on the FeatureList records what the companion "chose" at chargen --
   * for companion-specific occupations (e.g. SpaceMarine for Ulfar) these are never in the main
   * player chargen path traversal, so we must read them here to find their innate AddFacts abilities.
   */
  public static IEnumerable<object> EnumerateFeatureListOccupations(object featureList) {
    var componentsProperty = featureList.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(featureList) is not IEnumerable components) yield break;

    foreach (var component in components) {
      if (component == null) continue;
      if (component.GetType().Name != "ApplyCareerPath") continue;

      var selectionsField = component.GetType().GetField("Selections", AllInstanceFlags);

      if (selectionsField?.GetValue(component) is not IEnumerable selectionGroups) continue;

      foreach (var selectionGroup in selectionGroups) {
        if (selectionGroup == null) continue;

        var groupField = selectionGroup.GetType().GetField("Group", AllInstanceFlags);
        var groupValue = groupField?.GetValue(selectionGroup)?.ToString();

        if (groupValue != "ChargenOccupation") continue;

        var itemsField = selectionGroup.GetType().GetField("m_Items", AllInstanceFlags);

        if (itemsField?.GetValue(selectionGroup) is not IEnumerable itemRefs) continue;

        foreach (var itemRef in itemRefs) {
          if (itemRef == null) continue;

          object occupation = null;

          try {
            occupation = Dereference(itemRef);
          }
          catch {
            /* skip unresolvable */
          }

          if (occupation != null) yield return occupation;
        }
      }
    }
  }

  /**
   * Returns the companion FeatureList blueprint from a BlueprintUnit's m_AddFacts field.
   * The FeatureList is identified by containing at least one ApplyCareerPath component.
   * Returns null if none is found.
   */
  public static object FindCompanionFeatureList(object unit) {
    var addFactsField = unit.GetType().GetField("m_AddFacts", AllInstanceFlags);

    if (addFactsField?.GetValue(unit) is not IEnumerable factRefs) return null;

    foreach (var factRef in factRefs) {
      if (factRef == null) continue;

      object fact = null;

      try {
        fact = Dereference(factRef);
      }
      catch {
        continue;
      }

      if (fact == null) continue;

      var componentsProperty = fact.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

      if (componentsProperty?.GetValue(fact) is IEnumerable components &&
          components.Cast<object>().Any(component => component?.GetType().Name == "ApplyCareerPath")) {
        return fact;
      }
    }

    return null;
  }

  /**
   * Yields raw blueprint objects from a BlueprintUnit's m_AddFacts field, skipping the companion
   * FeatureList (identified by containing an ApplyCareerPath component) which is handled separately.
   * Used by FeaturesExporter to collect companion-specific features not reachable from any career
   * or chargen path.
   */
  public static IEnumerable<object> EnumerateUnitDirectFacts(object unit) {
    var addFactsField = unit.GetType().GetField("m_AddFacts", AllInstanceFlags);

    if (addFactsField?.GetValue(unit) is not IEnumerable factRefs) yield break;

    foreach (var factRef in factRefs) {
      if (factRef == null) continue;

      object fact = null;

      try {
        fact = Dereference(factRef);
      }
      catch {
        /* skip unresolvable */
      }

      if (fact == null) continue;

      // Skip the FeatureList — it has ApplyCareerPath components and is handled by CompanionExporter.
      var componentsProperty = fact.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

      if (componentsProperty?.GetValue(fact) is IEnumerable components &&
          components.Cast<object>().Any(component => component?.GetType().Name == "ApplyCareerPath")) {
        continue;
      }

      yield return fact;
    }
  }

  /**
   * Resolves an Owlcat blueprint reference object to the actual blueprint it points at.
   * 
   * Tries the three reference patterns used across the engine.
   */
  public static object Dereference(object reference) {
    var referenceType = reference.GetType();

    var getMethod = referenceType.GetMethod("Get", Type.EmptyTypes);

    if (getMethod != null) return getMethod.Invoke(reference, null);

    var blueprintProperty = referenceType.GetProperty("Blueprint", AllInstanceFlags);

    if (blueprintProperty != null) return blueprintProperty.GetValue(reference);

    var getBlueprintMethod = referenceType.GetMethod("GetBlueprint", Type.EmptyTypes);

    return getBlueprintMethod != null ? getBlueprintMethod.Invoke(reference, null) : null;
  }
}