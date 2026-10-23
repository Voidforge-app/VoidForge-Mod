using System.Text.Json;
using System.Text.Json.Serialization;

namespace StaticExtractor.Infrastructure;

/**
 * Versioned allowlist and schema metadata generated from static item exports.
 */
public sealed class StaticBaseline {
  [JsonPropertyName("version")]
  public string Version { get; init; } = "";

  [JsonPropertyName("revision")]
  public string Revision { get; init; } = "";

  [JsonPropertyName("complete")]
  public bool Complete { get; init; }

  [JsonPropertyName("generatedAt")]
  public string GeneratedAt { get; init; } = "";

  [JsonPropertyName("categories")]
  public Dictionary<string, StaticBaselineCategory> Categories { get; init; } = new(StringComparer.Ordinal);

  public static StaticBaseline Create(string version, string revision) {
    return new StaticBaseline {
      Version = version,
      Revision = revision,
      GeneratedAt = DateTime.UtcNow.ToString("O"),
    };
  }
}

/**
 * Static baseline metadata for one frontend export category.
 */
public sealed class StaticBaselineCategory {
  [JsonPropertyName("ids")]
  public List<string> Ids { get; } = [];

  [JsonPropertyName("fields")]
  public List<string> Fields { get; } = [];

  [JsonPropertyName("reachableIds")]
  public List<string> ReachableIds { get; } = [];
}

/**
 * Writes the static baseline from the item envelopes generated in the same run.
 */
public static class StaticBaselineWriter {
  private static readonly JsonSerializerOptions Options = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  };

  public static void Write(string outputDirectory, string version, string revision, StaticLogger logger) {
    var baseline = StaticBaseline.Create(version, revision);
    var complete = true;

    foreach (var category in new[] {
               "weapons", "armor", "equipment",
               "careers", "origins", "features", "companions",
             }) {
      var path = Path.Combine(outputDirectory, $"{category}.json");

      if (!File.Exists(path)) {
        complete = false;
        logger.Warn("baseline", $"category={category} export is missing");
        continue;
      }

      try {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var items = document.RootElement.GetProperty("items");
        var metadata = new StaticBaselineCategory();
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items.EnumerateArray()) {
          // Item exports use assetGuid; career/origin/feature/companion exports use id.
          if (!item.TryGetProperty("assetGuid", out var idProperty) &&
              !item.TryGetProperty("id", out idProperty)) continue;
          var id = idProperty.GetString();
          if (string.IsNullOrWhiteSpace(id)) continue;

          metadata.Ids.Add(id);
          if (item.TryGetProperty("reachable", out var reachable) && reachable.GetBoolean()) {
            metadata.ReachableIds.Add(id);
          }

          foreach (var property in item.EnumerateObject()) fields.Add(property.Name);
        }

        metadata.Fields.AddRange(fields.OrderBy(field => field, StringComparer.Ordinal));
        baseline.Categories[category] = metadata;
      }
      catch (Exception exception) {
        complete = false;

        logger.Warn("baseline", $"category={category} reason={exception.Message}");
      }
    }

    baseline = new StaticBaseline {
      Version = baseline.Version,
      Revision = baseline.Revision,
      GeneratedAt = baseline.GeneratedAt,
      Complete = complete,
      Categories = baseline.Categories,
    };

    var outputPath = Path.Combine(outputDirectory, "static-baseline.json");
    File.WriteAllText(outputPath, JsonSerializer.Serialize(baseline, Options));

    var itemCount = baseline.Categories.Values.Sum(category => category.Ids.Count);
    var fieldCount = baseline.Categories.Values.Sum(category => category.Fields.Count);
    var reachableItemCount = baseline.Categories.Values.Sum(category => category.ReachableIds.Count);

    logger.Result("baseline", "written",
      ("categories", baseline.Categories.Count),
      ("complete", baseline.Complete),
      ("items", itemCount),
      ("fields", fieldCount),
      ("reachableItems", reachableItemCount));
  }
}