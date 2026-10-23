using Newtonsoft.Json;

namespace BlueprintExtractor.Infrastructure;

/**
 * Optional static-export baseline used to constrain runtime item enumeration and validate coverage.
 */
public sealed class StaticBaseline {
  [JsonProperty("version")]
  public string Version { get; set; } = "";

  [JsonProperty("revision")]
  public string Revision { get; set; } = "";

  [JsonProperty("complete")]
  public bool Complete { get; set; }

  [JsonProperty("categories")]
  public Dictionary<string, StaticBaselineCategory> Categories { get; set; } = new();
}

public sealed class StaticBaselineCategory {
  [JsonProperty("ids")]
  public List<string> Ids { get; set; } = [];
}

/**
 * Loads a complete version/revision-matched static baseline and records runtime/static discrepancies.
 */
public static class RuntimeBaseline {
  private static readonly Dictionary<string, HashSet<string>> IdsByCategory = new(StringComparer.Ordinal);

  private static bool IsLoaded => Current != null;
  private static StaticBaseline Current { get; set; }

  public static void Load(string gameVersion, string gameRevision, string outputDirectory, ModLogger logger) {
    Current = null;
    IdsByCategory.Clear();

    var path = Path.Combine(outputDirectory, "static-baseline.json");

    if (!File.Exists(path)) {
      logger.Info("baseline", $"not found path={path}; runtime fallback remains enabled");

      return;
    }

    try {
      var loaded = JsonConvert.DeserializeObject<StaticBaseline>(File.ReadAllText(path));

      if (loaded is not { Complete: true } ||
          !string.Equals(loaded.Version, gameVersion, StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(loaded.Revision, gameRevision, StringComparison.OrdinalIgnoreCase)) {
        logger.Warn("baseline", $"ignored mismatch expected={gameVersion}/{gameRevision} " +
                                $"actual={loaded?.Version}/{loaded?.Revision} complete={loaded?.Complete}");

        return;
      }

      Current = loaded;
      IdsByCategory.Clear();

      foreach (var entry in loaded.Categories) {
        IdsByCategory[entry.Key] = new HashSet<string>(
          entry.Value.Ids ?? [], StringComparer.Ordinal);
      }

      logger.Result("baseline", "loaded", ("categories", IdsByCategory.Count));
    }
    catch (Exception exception) {
      logger.Warn("baseline", $"failed path={path} reason={exception.Message}");
    }
  }

  public static bool Allows(string category, string assetGuid) {
    return !IsLoaded || !IdsByCategory.TryGetValue(category, out var ids) || ids.Contains(assetGuid);
  }

  public static void ValidateRuntimeId(string category, string assetGuid, ModLogger logger) {
    if (IsLoaded && !Allows(category, assetGuid)) {
      logger.Warn("baseline", $"runtime-only category={category} guid={assetGuid}");
    }
  }
}