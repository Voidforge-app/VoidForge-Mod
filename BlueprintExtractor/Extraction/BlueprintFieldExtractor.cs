using System.Reflection;
using Kingmaker.Blueprints;

namespace BlueprintExtractor.Extraction;

/**
 * Reflection-based field extraction for blueprint objects.
 * Reads all public simple-typed fields and properties from a blueprint instance
 * into a flat dictionary, suitable for JSON serialization during discovery phase.
 */
public static class BlueprintFieldExtractor {
  private const BindingFlags InstancePublicFlags = BindingFlags.Public | BindingFlags.Instance;

  /**
   * Extracts all public instance fields and properties from a blueprint into a flat dictionary.
   * Only includes primitive, enum, and string types to avoid serialization explosions
   * from complex Unity objects or circular references.
   */
  public static Dictionary<string, object> ExtractSimpleFields(object blueprintObject) {
    var extractedFields = new Dictionary<string, object>();
    var blueprintType = blueprintObject.GetType();

    if (blueprintObject is SimpleBlueprint simpleBlueprint) {
      extractedFields["AssetGuid"] = simpleBlueprint.AssetGuid;
      extractedFields["BlueprintType"] = blueprintType.Name;
    }

    // Track camelCase names already added so fields don't collide with properties.
    // Example: "Name" property and Unity's "name" field both serialize to "name" - property wins.
    var usedCamelCaseNames = new HashSet<string>(extractedFields.Keys.Select(ToCamelCase));

    foreach (var property in blueprintType.GetProperties(InstancePublicFlags)) {
      if (!IsSerializablePrimitive(property.PropertyType)) continue;

      try {
        var propertyValue = property.GetValue(blueprintObject);
        extractedFields[property.Name] = SimplifyValue(propertyValue);
        usedCamelCaseNames.Add(ToCamelCase(property.Name));
      }
      catch {
        /* skip inaccessible properties */
      }
    }

    foreach (var field in blueprintType.GetFields(InstancePublicFlags)) {
      if (!IsSerializablePrimitive(field.FieldType)) continue;
      if (extractedFields.ContainsKey(field.Name)) continue;
      if (usedCamelCaseNames.Contains(ToCamelCase(field.Name))) continue;

      try {
        var fieldValue = field.GetValue(blueprintObject);
        extractedFields[field.Name] = SimplifyValue(fieldValue);
        usedCamelCaseNames.Add(ToCamelCase(field.Name));
      }
      catch {
        /* skip inaccessible fields */
      }
    }

    return extractedFields;
  }

  /**
   * Dumps the full public API surface (properties + fields) of a blueprint type
   * for developer inspection. Used during discovery phase to identify correct field names.
   */
  public static object BuildTypeSchema(Type blueprintType) {
    return new {
      TypeName = blueprintType.FullName,
      Properties = blueprintType.GetProperties(InstancePublicFlags)
        .Select(property => new { property.Name, Type = property.PropertyType.Name, property.CanRead })
        .ToList(),
      Fields = blueprintType.GetFields(InstancePublicFlags)
        .Select(field => new { field.Name, Type = field.FieldType.Name })
        .ToList(),
    };
  }

  private static string ToCamelCase(string name) {
    if (string.IsNullOrEmpty(name)) return name;

    return char.ToLowerInvariant(name[0]) + name.Substring(1);
  }

  private static bool IsSerializablePrimitive(Type fieldType) {
    if (fieldType == null) return false;
    fieldType = Nullable.GetUnderlyingType(fieldType) ?? fieldType;

    return fieldType.IsPrimitive || fieldType.IsEnum || fieldType == typeof(string) || fieldType == typeof(decimal);
  }

  private static object SimplifyValue(object value) {
    return value switch {
      null => null,
      Enum enumValue => enumValue.ToString(),
      _ => value,
    };
  }
}