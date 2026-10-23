using System.Text.Json;
using System.Text.Json.Serialization;

namespace StaticExtractor.Infrastructure;

/**
 * Standard envelope wrapper for all JSON exports. Matches the runtime extractor's format
 * so downstream consumers (VoidForge website) work with both outputs.
 */
public class ExportEnvelope<T> {
  [JsonPropertyName("version")]
  [JsonInclude]
  public string Version { get; init; } = "";

  [JsonPropertyName("revision")]
  [JsonInclude]
  public string Revision { get; init; } = "";

  [JsonPropertyName("exportedAt")]
  [JsonInclude]
  public string ExportedAt { get; init; } = "";

  [JsonPropertyName("count")]
  [JsonInclude]
  public int Count { get; init; }

  [JsonPropertyName("items")]
  [JsonInclude]
  public List<T> Items { get; init; } = [];

  public static ExportEnvelope<T> Create(string gameVersion, string gameRevision, List<T> items) {
    return new ExportEnvelope<T> {
      Version = gameVersion,
      Revision = gameRevision,
      ExportedAt = DateTime.UtcNow.ToString("o"),
      Count = items.Count,
      Items = items,
    };
  }
}

/**
 * Handles JSON serialization and file writing for export envelopes.
 * Uses camelCase serialization to match the runtime extractor's output.
 */
public static class ExportWriter {
  private static readonly JsonSerializerOptions SerializerOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  public static void WriteEnvelope<T>(string outputDirectory, string fileName, ExportEnvelope<T> envelope) {
    var json = JsonSerializer.Serialize(envelope, SerializerOptions);
    var outputPath = Path.Combine(outputDirectory, $"{fileName}.json");

    File.WriteAllText(outputPath, json);
  }

  /**
   * Resolves the output directory for a given game version.
   *
   * Defaults to Documents/VoidForge/{version} to match the runtime extractor.
   */
  public static string ResolveOutputDirectory(string gameVersion, string? baseOverride = null) {
    if (!string.IsNullOrWhiteSpace(baseOverride)) {
      return Path.Combine(baseOverride, gameVersion);
    }

    var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    return Path.Combine(documentsPath, "VoidForge", gameVersion);
  }
}

/**
 * Simple structured logger for the static extractor.
 *
 * Writes to console and optionally to a log file in the output directory.
 */
public class StaticLogger {
  private readonly StreamWriter? _logWriter;

  public StaticLogger(string? logPath = null) {

    if (logPath != null) {
      _logWriter = new StreamWriter(logPath, true);
    }
  }

  public void Info(string category, string message) {
    var line = $"[{category}] {message}";
    Console.WriteLine(line);
    _logWriter?.WriteLine(line);
  }

  public void Warn(string category, string message) {
    var line = $"[WARN:{category}] {message}";
    Console.Error.WriteLine(line);
    _logWriter?.WriteLine(line);
  }

  public void Result(string category, string message, params (string, object)[] stats) {
    var statsText = string.Join(", ", stats.Select(s => $"{s.Item1}={s.Item2}"));
    var line = $"[{category}] {message} ({statsText})";
    Console.WriteLine(line);
    _logWriter?.WriteLine(line);
  }

  public void Flush() {
    _logWriter?.Flush();
  }

  public void Dispose() {
    _logWriter?.Dispose();
  }
}