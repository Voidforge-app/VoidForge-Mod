using System.Collections;
using System.Reflection;

namespace BlueprintExtractor.Extraction;

// Extracts UIPropertiesComponent parameter formulas from feature blueprints for {uip|...} token resolution.
public static class UiParamExtractor {
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  /**
   * Returns a map from UIParam key (e.g. "MovementPoints") to its formula structure.
   * Returns an empty dict if the blueprint has no UIPropertiesComponent.
   */
  public static Dictionary<string, object> ExtractUiParams(object blueprint) {
    var result = new Dictionary<string, object>();

    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return result;

    foreach (var component in components) {
      if (component?.GetType().Name != "UIPropertiesComponent") continue;

      var propertiesField = component.GetType().GetField("Properties", AllInstanceFlags);

      if (propertiesField?.GetValue(component) is not IEnumerable properties) break;

      foreach (var propertyEntry in properties) {
        if (propertyEntry == null) continue;

        try {
          var entryType = propertyEntry.GetType();
          var linkKeyField = entryType.GetField("m_LinkKey", AllInstanceFlags);
          var linkKey = linkKeyField?.GetValue(propertyEntry) as string;

          if (string.IsNullOrEmpty(linkKey)) continue;

          var formula = ExtractFormula(propertyEntry, entryType, blueprint);

          if (formula != null) result[linkKey] = formula;
        }
        catch {
          /* skip malformed UIProperties entries */
        }
      }

      break; // at most one UIPropertiesComponent per blueprint
    }

    return result;
  }

  private static Dictionary<string, object> ExtractFormula(
    object propertyEntry, Type entryType, object defaultBlueprint) {
    var propertyNameField = entryType.GetField("m_PropertyName", AllInstanceFlags);
    var propertyName = propertyNameField?.GetValue(propertyEntry) as string;

    if (string.IsNullOrEmpty(propertyName)) return null;

    var sourceBlueprintForLookup = defaultBlueprint;
    var propertySourceField = entryType.GetField("m_PropertySource", AllInstanceFlags);

    if (propertySourceField != null) {
      try {
        var resolved = RankEntryExtractor.Dereference(propertySourceField.GetValue(propertyEntry));

        if (resolved != null) sourceBlueprintForLookup = resolved;
      }
      catch {
        /* fall back to defaultBlueprint */
      }
    }

    var calculatorComponent = FindPropertyCalculator(sourceBlueprintForLookup, propertyName);

    return calculatorComponent == null ? null : ParsePropertyCalculator(calculatorComponent);
  }

  private static object FindPropertyCalculator(object blueprint, string propertyName) {
    var componentsProperty = blueprint.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(blueprint) is not IEnumerable components) return null;

    foreach (var component in components) {
      if (component?.GetType().Name != "PropertyCalculatorComponent") continue;

      var nameField = component.GetType().GetField("Name", AllInstanceFlags);

      if (nameField?.GetValue(component) as string == propertyName) return component;
    }

    return null;
  }

  private static Dictionary<string, object> ParsePropertyCalculator(object calculatorComponent) {
    var valueField = calculatorComponent.GetType().GetField("Value", AllInstanceFlags);
    var formulaValue = valueField?.GetValue(calculatorComponent);

    if (formulaValue == null) return null;

    var formulaType = formulaValue.GetType();
    var result = new Dictionary<string, object>();

    var operationField = formulaType.GetField("Operation", AllInstanceFlags);
    result["operation"] = operationField?.GetValue(formulaValue)?.ToString() ?? "Sum";

    var gettersField = formulaType.GetField("Getters", AllInstanceFlags);
    var parts = new List<Dictionary<string, object>>();

    if (gettersField?.GetValue(formulaValue) is IEnumerable getters) {
      foreach (var getter in getters) {
        if (getter == null) continue;

        try {
          var part = ParseGetter(getter);

          if (part != null) parts.Add(part);
        }
        catch {
          /* skip malformed getters */
        }
      }
    }

    result["parts"] = parts;

    return result;
  }

  private static Dictionary<string, object> ParseGetter(object getter) {
    var getterType = getter.GetType();

    return getterType.Name switch {
      "SimplePropertyGetter" => ParseSimplePropertyGetter(getter, getterType),
      "ContextValueGetter" => ParseContextValueGetter(getter, getterType),
      _ => ParseUnknownGetter(getter, getterType),
    };
  }

  private static Dictionary<string, object> ParseSimplePropertyGetter(object getter, Type getterType) {
    var part = new Dictionary<string, object> { ["type"] = "stat" };

    var propertyField = getterType.GetField("Property", AllInstanceFlags);
    part["stat"] = propertyField?.GetValue(getter)?.ToString() ?? "Unknown";

    var settingsField = getterType.GetField("Settings", AllInstanceFlags);
    var settings = settingsField?.GetValue(getter);

    if (settings != null) {
      var settingsType = settings.GetType();

      var progressionField = settingsType.GetField("Progression", AllInstanceFlags);
      part["progression"] = progressionField?.GetValue(settings)?.ToString() ?? "AsIs";

      var stepLevelField = settingsType.GetField("m_StepLevel", AllInstanceFlags);

      if (stepLevelField?.GetValue(settings) is int stepLevel && stepLevel != 0) {
        part["step"] = stepLevel;
      }

      var negateField = settingsType.GetField("Negate", AllInstanceFlags);

      if (negateField?.GetValue(settings) is true) {
        part["negate"] = true;
      }
    }

    return part;
  }

  private static Dictionary<string, object> ParseContextValueGetter(object getter, Type getterType) {
    var valueField = getterType.GetField("Value", AllInstanceFlags);
    var contextValue = valueField?.GetValue(getter);

    if (contextValue == null) return ParseUnknownGetter(getter, getterType);

    var contextValueType = contextValue.GetType();
    var valueTypeField = contextValueType.GetField("ValueType", AllInstanceFlags);
    var valueTypeStr = valueTypeField?.GetValue(contextValue)?.ToString();

    if (valueTypeStr == "Simple") {
      var part = new Dictionary<string, object> { ["type"] = "constant" };

      var numericValueField = contextValueType.GetField("Value", AllInstanceFlags);
      part["value"] = numericValueField?.GetValue(contextValue) ?? 0;

      // A static value can still have a progression applied (e.g. Negate, Div2)
      var settingsField = getterType.GetField("Settings", AllInstanceFlags);
      var settings = settingsField?.GetValue(getter);

      if (settings != null) {
        var settingsType = settings.GetType();

        var progressionField = settingsType.GetField("Progression", AllInstanceFlags);
        var progression = progressionField?.GetValue(settings)?.ToString();

        if (!string.IsNullOrEmpty(progression) && progression != "AsIs") {
          part["progression"] = progression;
        }

        var negateField = settingsType.GetField("Negate", AllInstanceFlags);

        if (negateField?.GetValue(settings) is true) {
          part["negate"] = true;
        }
      }

      return part;
    }

    // Non-simple ContextValueGetter (rank-based, named property, etc.) - export raw fields
    var rawPart = new Dictionary<string, object> {
      ["type"] = "contextValue",
      ["valueType"] = valueTypeStr,
    };

    foreach (var field in contextValueType.GetFields(AllInstanceFlags)) {
      if (!IsSimpleType(field.FieldType)) continue;

      try {
        var fieldValue = field.GetValue(contextValue);

        if (fieldValue != null) rawPart[field.Name] = fieldValue.ToString();
      }
      catch {
        /* skip */
      }
    }

    return rawPart;
  }

  private static Dictionary<string, object> ParseUnknownGetter(object getter, Type getterType) {
    var part = new Dictionary<string, object> {
      ["type"] = "unknown",
      ["typeName"] = getterType.Name,
    };

    foreach (var field in getterType.GetFields(AllInstanceFlags)) {
      if (!IsSimpleType(field.FieldType)) continue;

      try {
        var fieldValue = field.GetValue(getter);

        if (fieldValue != null) part[field.Name] = fieldValue.ToString();
      }
      catch {
        /* skip */
      }
    }

    return part;
  }

  private static bool IsSimpleType(Type type) {
    return type == typeof(string) || type == typeof(int) || type == typeof(float) ||
           type == typeof(double) || type == typeof(bool) || type.IsEnum;
  }
}