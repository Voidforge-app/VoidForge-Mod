using System.Text.Json;
using System.Text.Json.Serialization;
using StaticExtractor.Blueprints;

namespace StaticExtractor.Infrastructure;

/**
 * Static references for image assets that require runtime or Unity decoding.
 */
public sealed class StaticAssetManifest {
  [JsonPropertyName("version")]
  [JsonInclude]
  public string Version { get; init; } = "";

  [JsonPropertyName("revision")]
  [JsonInclude]
  public string Revision { get; init; } = "";

  [JsonPropertyName("items")]
  [JsonInclude]
  public List<StaticAssetReference> Items { get; init; } = [];

  [JsonPropertyName("portraits")]
  [JsonInclude]
  public List<StaticAssetReference> Portraits { get; init; } = [];
}

public sealed class StaticAssetReference {
  [JsonPropertyName("assetGuid")]
  [JsonInclude]
  public string AssetGuid { get; init; } = "";

  [JsonPropertyName("assetReference")]
  [JsonInclude]
  public string AssetReference { get; init; } = "";

  [JsonPropertyName("blueprintType")]
  [JsonInclude]
  public string BlueprintType { get; init; } = "";
}

/**
 * Writes icon and portrait references without attempting to decode Unity assets statically.
 */
public static class StaticAssetManifestWriter {
  private static readonly JsonSerializerOptions Options = new() {
    WriteIndented = true,
  };

  public static void Write(
    BlueprintCatalog catalog,
    string outputDirectory,
    string version,
    string revision,
    StaticLogger logger) {
    var manifest = new StaticAssetManifest {
      Version = version,
      Revision = revision,
    };

    foreach (var document in catalog.ById.Values) {
      if (document.Data.ExtraFields == null) continue;

      if (TryGetGuidReference(document.Data.ExtraFields, "m_Icon", out var iconGuid)) {
        manifest.Items.Add(new StaticAssetReference {
          AssetGuid = document.AssetId,
          AssetReference = iconGuid,
          BlueprintType = document.TypeName,
        });
      }

      if (TryGetGuidReference(document.Data.ExtraFields, "m_Portrait", out var portraitGuid)) {
        manifest.Portraits.Add(new StaticAssetReference {
          AssetGuid = document.AssetId,
          AssetReference = portraitGuid,
          BlueprintType = document.TypeName,
        });
      }
    }

    var path = Path.Combine(outputDirectory, "static-asset-manifest.json");
    File.WriteAllText(path, JsonSerializer.Serialize(manifest, Options));
    logger.Result("assets", "manifest written",
      ("icons", manifest.Items.Count), ("portraits", manifest.Portraits.Count));
  }

  private static bool TryGetGuidReference(
    Dictionary<string, JsonElement> fields,
    string fieldName,
    out string assetGuid) {
    assetGuid = "";

    if (!fields.TryGetValue(fieldName, out var element) || element.ValueKind != JsonValueKind.Object) return false;
    if (!element.TryGetProperty("guid", out var guidProperty)) return false;

    assetGuid = guidProperty.GetString() ?? "";
    return assetGuid.Length > 0;
  }
}