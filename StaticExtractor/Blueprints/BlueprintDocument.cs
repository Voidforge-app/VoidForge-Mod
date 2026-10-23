using System.Text.Json;
using System.Text.Json.Serialization;

namespace StaticExtractor.Blueprints;

/**
 * Parsed representation of a .jbp blueprint file.
 */
public class BlueprintDocument {
  internal static readonly JsonSerializerOptions SerializerOptions = new() {
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
  };

  [JsonPropertyName("AssetId")]
  public string AssetId { get; init; } = "";

  [JsonPropertyName("Data")]
  public BlueprintData Data { get; init; } = new();

  [JsonPropertyName("Meta")]
  public BlueprintMeta? Meta { get; init; }

  [JsonIgnore]
  public string RelativePath { get; set; } = "";

  [JsonIgnore]
  public string FullPath { get; set; } = "";

  [JsonIgnore]
  public string TypeName => ExtractTypeName(Data.Type);

  [JsonIgnore]
  public string TypeId => ExtractTypeId(Data.Type);

  [JsonIgnore]
  public bool IsShadowDeleted => Meta?.ShadowDeleted ?? false;

  [JsonIgnore]
  public string AssetName => Path.GetFileNameWithoutExtension(FullPath);

  public static BlueprintDocument FromFile(string path, string basePath) {
    var json = File.ReadAllText(path);
    var document = JsonSerializer.Deserialize<BlueprintDocument>(json, SerializerOptions)
                   ?? throw new InvalidDataException($"Failed to parse {path}");
    document.FullPath = path;
    document.RelativePath = Path.GetRelativePath(basePath, path);
    return document;
  }

  public static string ExtractTypeName(string typeString) {
    if (string.IsNullOrEmpty(typeString)) return "";
    var commaIndex = typeString.IndexOf(',');
    return commaIndex >= 0 ? typeString[(commaIndex + 1)..].Trim() : "";
  }

  public static string ExtractTypeId(string typeString) {
    if (string.IsNullOrEmpty(typeString)) return "";
    return typeString.Length > 32 ? typeString[..32] : typeString;
  }
}

public class BlueprintData {
  [JsonPropertyName("$type")]
  public string Type { get; init; } = "";

  [JsonPropertyName("PrototypeLink")]
  public string PrototypeLink { get; init; } = "";

  [JsonPropertyName("Components")]
  [JsonInclude]
  public List<JsonElement>? Components { get; init; }

  [JsonPropertyName("m_DisplayName")]
  [JsonInclude]
  public LocalizedStringField? DisplayName { get; init; }

  [JsonPropertyName("m_Description")]
  [JsonInclude]
  public LocalizedStringField? Description { get; init; }

  [JsonPropertyName("LocalizedName")]
  [JsonInclude]
  public LocalizedStringField? LocalizedName { get; init; }

  [JsonPropertyName("LocalizedDescription")]
  [JsonInclude]
  public LocalizedStringField? LocalizedDescription { get; init; }

  [JsonExtensionData]
  [JsonInclude]
  public Dictionary<string, JsonElement>? ExtraFields { get; init; }
}

public class BlueprintMeta {
  [JsonConstructor]
  public BlueprintMeta() { }

  [JsonPropertyName("ShadowDeleted")]
  [JsonInclude]
  public bool? ShadowDeleted { get; init; }
}

public class LocalizedStringField {
  [JsonPropertyName("m_Key")]
  public string? Key { get; init; }

  [JsonPropertyName("Shared")]
  public SharedStringRef? Shared { get; init; }

  /**
   * Direct assetguid - present when this field is a SharedStringAsset reference
   * (used by BlueprintUnit.LocalizedName) rather than a LocalizedString.
   */
  [JsonPropertyName("assetguid")]
  [JsonInclude]
  public string? DirectAssetGuid { get; init; }

  /**
   * Direct stringkey - present when this field is a SharedStringAsset reference
   * (used by BlueprintUnit.LocalizedName) rather than a LocalizedString.
   */
  [JsonPropertyName("stringkey")]
  [JsonInclude]
  public string? DirectStringKey { get; init; }
}

public class SharedStringRef {
  [JsonConstructor]
  public SharedStringRef() { }

  [JsonPropertyName("assetguid")]
  [JsonInclude]
  public string? AssetGuid { get; init; }

  [JsonPropertyName("stringkey")]
  [JsonInclude]
  public string? StringKey { get; init; }
}