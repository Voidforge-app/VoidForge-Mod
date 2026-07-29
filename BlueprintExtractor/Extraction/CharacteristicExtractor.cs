using System.Collections;
using System.Reflection;

namespace BlueprintExtractor.Extraction;

/**
 * Extracts Warhammer 40K characteristic values from game blueprints.
 * 
 * Handles base stats on BlueprintUnit (OldWarhammer* fields) and stat modifiers on feature blueprints
 */
public static class CharacteristicExtractor {
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  /**
   * Maps the 9 Warhammer characteristics to their 3-letter short-codes.
   * The game uses "Warhammer" + full name as the field/enum suffix.
   */
  private static readonly (string GameName, string ShortCode)[] CharacteristicFields = [
    ("WarhammerWeaponSkill", "WS"),
    ("WarhammerBallisticSkill", "BS"),
    ("WarhammerStrength", "STR"),
    ("WarhammerToughness", "TGH"),
    ("WarhammerAgility", "AGI"),
    ("WarhammerIntelligence", "INT"),
    ("WarhammerPerception", "PER"),
    ("WarhammerWillpower", "WP"),
    ("WarhammerFellowship", "FEL"),
  ];

  /**
   * Reads the OldWarhammer* base stat fields from a BlueprintUnit.
   * Returns a dictionary keyed by 3-letter short-codes (WS, BS, STR, etc.).
   */
  public static Dictionary<string, int> ExtractBaseCharacteristics(object unit) {
    var result = new Dictionary<string, int>();
    var unitType = unit.GetType();

    foreach (var (gameName, shortCode) in CharacteristicFields) {
      var fieldName = "Old" + gameName;
      var field = unitType.GetField(fieldName, AllInstanceFlags);

      if (field?.GetValue(unit) is int value) {
        result[shortCode] = value;
      }
      else {
        result[shortCode] = 0;
      }
    }

    return result;
  }

  /**
   * Scans a feature blueprint's ComponentsArray for AddContextStatBonus components that modify Warhammer
   * characteristics. Returns a dictionary keyed by 3-letter short-codes, containing only the stats that are actually
   * modified. If no characteristic modifiers are found, returns an empty dictionary.
   */
  public static Dictionary<string, int> ExtractCharacteristicModifiers(object blueprint) {
    var result = new Dictionary<string, int>();

    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return result;

    foreach (var component in components) {
      if (component == null) continue;

      var componentType = component.GetType();

      if (componentType.Name != "AddContextStatBonus") continue;

      var statField = componentType.GetField("Stat", AllInstanceFlags);
      var statValue = statField?.GetValue(component)?.ToString();

      if (statValue == null) continue;

      var shortCode = MapStatNameToShortCode(statValue);

      if (shortCode == null) continue;

      var valueField = componentType.GetField("Value", AllInstanceFlags);
      var valueObj = valueField?.GetValue(component);

      if (valueObj == null) continue;

      var valueType = valueObj.GetType();
      var valueProperty = valueType.GetField("Value", AllInstanceFlags);

      if (valueProperty?.GetValue(valueObj) is not int modifier) continue;

      if (result.TryGetValue(shortCode, out var existing)) {
        result[shortCode] = existing + modifier;
      }
      else {
        result[shortCode] = modifier;
      }
    }

    return result;
  }

  /**
   * Attempts to extract the advancement step (increment per rank) from a BlueprintAttributeAdvancement. The
   * StatAdvancement component may carry a Step field; if not found, falls back to the game's default of 5.
   */
  public static int? ExtractAdvancementStep(object blueprint) {
    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return null;

    foreach (var component in components) {
      if (component == null) continue;

      var componentType = component.GetType();

      if (componentType.Name != "StatAdvancement") continue;

      var stepField = componentType.GetField("Step", AllInstanceFlags);

      if (stepField?.GetValue(component) is int step) return step;

      var valueField = componentType.GetField("Value", AllInstanceFlags);

      if (valueField?.GetValue(component) is int value) return value;

      var rankValueField = componentType.GetField("RankValue", AllInstanceFlags);

      if (rankValueField?.GetValue(component) is int rankValue) return rankValue;
    }

    return null;
  }

  private static string MapStatNameToShortCode(string statName) {
    foreach (var (gameName, shortCode) in CharacteristicFields)
      if (statName == gameName) {
        return shortCode;
      }

    return null;
  }
}