// StaticExtractor reads .jbp blueprint files directly from the modding template's
// Blueprints/ folder and exports JSON in the same format as the runtime BlueprintExtractor.
//
// Usage:
//   StaticExtractor <gameRoot> [--output <path>] [--version <ver>]
//                  [--types Type1,Type2,...] [--no-reachability] [--merge-runtime <path>]
//
// The gameRoot should point at the WhRtModificationTemplate folder (the one containing
// Blueprints/ and Strings/). The game version is auto-detected from
// WH40KRT_Data/StreamingAssets/Version.info. Defaults to the Steam install path.
//
// --types filters loading to only the specified blueprint types (fast test mode).
// --no-reachability skips the reference graph build (implied by --types).

using System.Text.RegularExpressions;
using StaticExtractor.Exporters;
using StaticExtractor.Infrastructure;

var gameRoot = args.Length > 0 && !args[0].StartsWith("--")
  ? args[0]
  : @"Z:\Steam\steamapps\common\Warhammer 40,000 Rogue Trader\Modding\WhRtModificationTemplate\WhRtModificationTemplate";

string? outputOverride = null;
string? gameVersion = null;
HashSet<string>? typeFilter = null;
string? runtimeMergeDirectory = null;
var noReachability = false;

for (var i = 0; i < args.Length; i++)
  switch (args[i]) {
    case "--output" when i + 1 < args.Length:
      outputOverride = args[++i];
      break;

    case "--version" when i + 1 < args.Length:
      gameVersion = args[++i];
      break;

    case "--types" when i + 1 < args.Length:
      typeFilter = args[++i]
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);
      break;

    case "--no-reachability":
      noReachability = true;
      break;

    case "--merge-runtime" when i + 1 < args.Length:
      runtimeMergeDirectory = args[++i];
      break;

    case "--help":
      PrintUsage();
      return;
  }

// --types implies --no-reachability since the full graph is needed for reachability
if (typeFilter != null) noReachability = true;

var blueprintsPath = Path.Combine(gameRoot, "Blueprints");
var stringsPath = Path.Combine(gameRoot, "Strings", "Mechanics");

// Auto-detect game version from WH40KRT_Data\StreamingAssets\Version.info
gameVersion ??= DetectGameVersion(gameRoot);

var outputDirectory = ExportWriter.ResolveOutputDirectory(gameVersion, outputOverride);
Directory.CreateDirectory(outputDirectory);

var logPath = Path.Combine(outputDirectory, "static-extract.log");
var logger = new StaticLogger(logPath);

try {
  Console.WriteLine("VoidForge Static Extractor");
  Console.WriteLine($"  Blueprints: {blueprintsPath}");
  Console.WriteLine($"  Strings:    {stringsPath}");
  Console.WriteLine($"  Version:    {gameVersion}");
  Console.WriteLine($"  Output:     {outputDirectory}");
  if (typeFilter != null)
    Console.WriteLine($"  TypeFilter: {string.Join(", ", typeFilter)}");
  if (noReachability)
    Console.WriteLine("  Reachability: skipped");
  Console.WriteLine();

  // When --merge-runtime points to the same directory as --output, the runtime export
  // is already there. We need to preserve it before static extraction overwrites it.
  string? runtimeMergeSource = null;
  if (!string.IsNullOrWhiteSpace(runtimeMergeDirectory)) {
    var runtimePath = Path.GetFullPath(runtimeMergeDirectory);
    var staticPath = Path.GetFullPath(outputDirectory);

    if (string.Equals(runtimePath.TrimEnd(Path.DirectorySeparatorChar),
          staticPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) {
      var tempDir = Path.Combine(Path.GetTempPath(), $"voidforge-runtime-{Guid.NewGuid():N}");
      Console.WriteLine($"  Preserving runtime export to {tempDir}...");
      CopyDirectory(runtimePath, tempDir);
      runtimeMergeSource = tempDir;
    }
    else {
      runtimeMergeSource = runtimePath;
    }
  }

  StaticMainExporter.ExportAll(blueprintsPath, stringsPath, gameVersion, gameVersion,
    outputDirectory, logger, typeFilter, noReachability);

  if (runtimeMergeSource != null) {
    ExportMerge.MergeDirectory(runtimeMergeSource, outputDirectory, logger);
  }
}
catch (Exception exception) {
  logger.Warn("fatal", exception.Message);
  logger.Info("fatal", exception.StackTrace ?? "");
  Environment.ExitCode = 1;
}
finally {
  logger.Flush();
  logger.Dispose();
}

return;

// Reads the game version from WH40KRT_Data\StreamingAssets\Version.info.
// The file contains a line like "2026-Jul-17 11:57 <hash> <version> <branch>".
static string DetectGameVersion(string gameRoot) {
  // Walk up from the modding template to find the game root (the one with WH40KRT_Data)
  var versionInfo = FindVersionFile(gameRoot);

  if (versionInfo == null) return "static-export";

  var line = File.ReadLines(versionInfo).FirstOrDefault();
  if (line == null) return "static-export";

  // Format: "2026-Jul-17 11:57 <hash> <version> <branch>"
  var match = Regex.Match(line, @"\d+\.\d+\.\d+\.\d+");
  return match.Success ? match.Value : "static-export";
}

// Walks up from the given directory to find WH40KRT_Data\StreamingAssets\Version.info.
static string? FindVersionFile(string startDir) {
  var dir = new DirectoryInfo(startDir);

  while (dir != null) {
    var versionFile = Path.Combine(dir.FullName, "WH40KRT_Data", "StreamingAssets", "Version.info");
    if (File.Exists(versionFile)) return versionFile;
    dir = dir.Parent;
  }

  return null;
}

static void CopyDirectory(string source, string target) {
  Directory.CreateDirectory(target);
  foreach (var file in Directory.EnumerateFiles(source))
    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
  foreach (var dir in Directory.EnumerateDirectories(source))
    CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
}

void PrintUsage() {
  Console.WriteLine("Usage: StaticExtractor <gameRoot> [options]");
  Console.WriteLine();
  Console.WriteLine("Arguments:");
  Console.WriteLine("  gameRoot    Path to the WhRtModificationTemplate folder");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --output <path>          Override the output directory");
  Console.WriteLine("  --version <ver>          Override the game version (auto-detected by default)");
  Console.WriteLine("  --types Type1,Type2,...  Only load these blueprint types (fast test mode)");
  Console.WriteLine("  --no-reachability        Skip reference graph build");
  Console.WriteLine("  --merge-runtime <path>   Merge a runtime export directory into this output");
  Console.WriteLine("  --help                   Show this help");
}