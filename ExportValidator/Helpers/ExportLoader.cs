// Discovers the .exploration output directory and loads export envelopes as JArrays.

using Newtonsoft.Json.Linq;

namespace ExportValidator.Helpers;

public static class ExportLoader {
  private static string ExplorationRoot { get; } = FindExplorationRoot();
  private static string LatestVersionFolder { get; } = FindLatestVersionFolder();
  public static string LatestVersion { get; } = Path.GetFileName(LatestVersionFolder);

  private static string FindExplorationRoot() {
    // First: walk up from the test binary looking for a .exploration dir with version subfolders
    var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

    while (directory != null) {
      var candidate = Path.Combine(directory.FullName, ".exploration");

      if (Directory.Exists(candidate) && Directory.GetDirectories(candidate).Length > 0) {
        return candidate;
      }

      directory = directory.Parent;
    }

    // Fallback: production game exports land in Documents\VoidForge\<version>\
    var documentsCandidate = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoidForge");

    if (Directory.Exists(documentsCandidate) && Directory.GetDirectories(documentsCandidate).Length > 0) {
      return documentsCandidate;
    }

    throw new DirectoryNotFoundException(
      "Could not locate export output. Run the game at least once to generate exports " +
      @"(expected at Documents\VoidForge\<version>\ or .exploration\<version>\).");
  }

  private static string FindLatestVersionFolder() {
    return Directory.GetDirectories(ExplorationRoot)
      .OrderByDescending(path => {
        var name = Path.GetFileName(path);

        return Version.TryParse(name, out var version) ? version : new Version(0, 0);
      })
      .First();
  }

  /**
   * Loads the items array from a top-level export envelope (e.g. "features", "encyclopedia").
   */
  public static JArray LoadItems(string fileName) {
    var filePath = Path.Combine(LatestVersionFolder, $"{fileName}.json");
    var envelope = JObject.Parse(File.ReadAllText(filePath));

    return (JArray)(envelope["items"] ?? throw new InvalidDataException(
      $"{fileName}.json has no 'items' array"));
  }

  /**
   * Loads the full envelope object (for accessing version, count, etc.).
   */
  public static JObject LoadEnvelope(string fileName) {
    var filePath = Path.Combine(LatestVersionFolder, $"{fileName}.json");

    return JObject.Parse(File.ReadAllText(filePath));
  }
}