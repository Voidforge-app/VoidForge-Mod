using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StaticExtractor.Localization;

/**
 * Loads the Strings/Mechanics/ localization files shipped with the modding template.
 * Each JSON file contains a key, ownerGuid, and a languages array with per-locale text.
 * We index by both the string key and the ownerGuid for flexible resolution.
 *
 * Supports parallel loading and optional filtering by a set of owner GUIDs or string keys,
 * so when only a subset of blueprints is loaded, we don't need to parse all 77K string files.
 */
public class StringCatalog {

  public StringCatalog(string stringsPath, HashSet<string>? neededKeys = null) {
    if (!Directory.Exists(stringsPath)) {
      throw new DirectoryNotFoundException($"Strings folder not found: {stringsPath}");
    }

    LoadAll(stringsPath, neededKeys);
  }

  /**
   * Maps string key -> English text.
   */
  private Dictionary<string, string> ByKey { get; } = new(StringComparer.Ordinal);

  /**
   * Maps owner GUID -> English text.
   */
  private static Dictionary<string, string> ByOwnerGuid => new(StringComparer.Ordinal);

  /**
   * Maps owner GUID -> the string key.
   */
  private static Dictionary<string, string> KeyByOwnerGuid => new(StringComparer.Ordinal);

  /**
   * Total number of loaded string entries.
   */
  public int Count => ByKey.Count;

  /**
   * Resolves a string by its key (the GUID-style localization key).
   * Returns empty string if the key is not found or has no English text.
   */
  public string ResolveByKey(string? key) {
    return string.IsNullOrEmpty(key) ? "" : ByKey.GetValueOrDefault(key, "");

  }

  private void LoadAll(string stringsPath, HashSet<string>? neededKeys) {
    var files = Directory.EnumerateFiles(stringsPath, "*.json", SearchOption.AllDirectories).ToList();

    var localLock = new object();
    var parallelOptions = new ParallelOptions {
      MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
    };

    Parallel.ForEach(files, parallelOptions,
      () => new ConcurrentDictionary<string, (string text, string key)>(StringComparer.Ordinal),
      (file, _, local) => {
        try {
          var entry = StringEntry.FromFile(file);

          if (entry == null) return local;

          // When filtering by needed keys, skip entries we don't need.
          if (neededKeys != null) {
            var needsKey = !string.IsNullOrEmpty(entry.Key) && neededKeys.Contains(entry.Key);
            var needsOwner = !string.IsNullOrEmpty(entry.OwnerGuid) && neededKeys.Contains(entry.OwnerGuid);

            if (!needsKey && !needsOwner) return local;
          }

          var englishText = entry.GetEnglishText();

          if (!string.IsNullOrEmpty(entry.Key)) {
            local[entry.Key] = (englishText, entry.Key);
          }

          if (!string.IsNullOrEmpty(entry.OwnerGuid)) {
            local[entry.OwnerGuid] = (englishText, entry.Key);
          }
        }
        catch {
          /* skip malformed files */
        }

        return local;
      },
      local => {
        lock (localLock) {
          foreach (var (key, (text, stringKey)) in local) {
            ByKey[key] = text;

            // Heuristic: 32-char hex keys are owner GUIDs, not string keys.
            if (key.Length != 32 || !IsHexKey(key)) continue;

            ByOwnerGuid[key] = text;

            if (!string.IsNullOrEmpty(stringKey)) {
              KeyByOwnerGuid[key] = stringKey;
            }
          }
        }
      });
  }

  private static bool IsHexKey(string key) {
    return key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
  }
}

/**
 * Represents a single Strings/Mechanics/*.json file.
 * Each file has a source key, an owner GUID, and a languages array with per-locale text.
 */
public class StringEntry {

  private static readonly JsonSerializerOptions SerializerOptions = new() {
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
  };

  [JsonPropertyName("key")]
  public string Key { get; init; } = "";

  [JsonPropertyName("ownerGuid")]
  public string OwnerGuid { get; init; } = "";

  [JsonPropertyName("languages")]
  public List<LanguageEntry>? Languages { get; init; }

  /**
   * Parses a Strings/Mechanics JSON file into a StringEntry.
   */
  public static StringEntry? FromFile(string path) {
    var json = File.ReadAllText(path);
    var entry = JsonSerializer.Deserialize<StringEntry>(json, SerializerOptions);

    return entry;
  }

  /**
   * Extracts the English text from the languages array.
   * Falls back to the "dev" locale if enGB is not present.
   */
  public string GetEnglishText() {
    if (Languages == null) return "";

    var english = Languages.FirstOrDefault(language =>
      language.Locale is "enGB" or "enUS");

    if (english != null) return english.Text ?? "";

    var dev = Languages.FirstOrDefault(language => language.Locale == "dev");

    return dev?.Text ?? "";
  }
}

public class LanguageEntry {
  [JsonPropertyName("locale")]
  public string Locale { get; init; } = "";

  [JsonPropertyName("text")]
  public string? Text { get; init; }
}