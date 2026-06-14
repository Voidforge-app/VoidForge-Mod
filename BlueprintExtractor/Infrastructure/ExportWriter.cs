using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BlueprintExtractor.Infrastructure;

/**
 * Handles serialization and file writing for export envelopes.
 * Centralizes JSON settings and output path resolution so individual exporters stay focused on data extraction.
 * Writes all output to the .exploration/ folder in the project root (path injected at build time).
 */
public static class ExportWriter {
  private static readonly JsonSerializerSettings SerializerSettings = new() {
    Formatting = Formatting.Indented,
    ContractResolver = new CamelCasePropertyNamesContractResolver(),
  };

  public static void WriteEnvelope<T>(string outputDirectory, string fileName, ExportEnvelope<T> envelope) {
    var serializedJson = JsonConvert.SerializeObject(envelope, SerializerSettings);
    var outputPath = Path.Combine(outputDirectory, $"{fileName}.json");

    File.WriteAllText(outputPath, serializedJson);
  }

  public static string ResolveOutputDirectory(string gameVersion, string baseDirectoryOverride = null) {
    if (!string.IsNullOrWhiteSpace(baseDirectoryOverride)) {
      return Path.Combine(baseDirectoryOverride, gameVersion);
    }

    var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    return Path.Combine(documentsPath, "VoidForge", gameVersion);
  }
}