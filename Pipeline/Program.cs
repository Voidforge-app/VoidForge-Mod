/**
 * VoidForge interactive export pipeline.
 *
 * Orchestrates the full export workflow:
 *   1. Auto-detects game root and version
 *   2. Runs static extraction
 *   3. Prompts user to run the runtime mod export
 *   4. Merges runtime + static into the final output
 *   5. Optionally uploads via the Uploader project
 */

using System.Diagnostics;
using System.Text.RegularExpressions;

var gameRoot = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
string? outputBase = null;
string? versionOverride = null;
var skipUpload = false;

for (var i = 0; i < args.Length; i++)
  switch (args[i]) {
    case "--output" when i + 1 < args.Length:
      outputBase = args[++i];
      break;

    case "--version" when i + 1 < args.Length:
      versionOverride = args[++i];
      break;

    case "--no-upload":
      skipUpload = true;
      break;

    case "--help":
      PrintUsage();
      return 0;
  }

PrintBanner();

// Step 1: Find the game root
gameRoot ??= DetectGameRoot() ?? PromptGameRoot();
var version = versionOverride ?? DetectVersion(gameRoot);
var outputDirectory = ResolveOutputDirectory(version, outputBase);

Console.WriteLine($"  Game root:  {gameRoot}");
Console.WriteLine($"  Version:    {version}");
Console.WriteLine($"  Output:     {outputDirectory}");
Console.WriteLine();

// Preserve existing runtime export before static extraction overwrites it.
// Only preserve if it actually looks like a runtime export (has resolved HTML descriptions).
var runtimeDir = GetRuntimeOutputDir(version);
string? preservedRuntimeDir = null;
if (Directory.Exists(runtimeDir) && Directory.GetFiles(runtimeDir, "*.json").Length > 0) {
  if (HasRuntimeData(runtimeDir)) {
    preservedRuntimeDir = Path.Combine(Path.GetTempPath(), $"voidforge-runtime-{Guid.NewGuid():N}");
    CopyDirectory(runtimeDir, preservedRuntimeDir);
    Console.WriteLine($"  Preserved existing runtime export ({Directory.GetFiles(preservedRuntimeDir, "*.json").Length} files)");
  }
  else {
    Console.WriteLine("  Existing data looks like static output (not runtime). Will overwrite.");
  }
}

// Step 2: Static extraction
var result = await Step("Static extraction", () => RunDotnet("run", "--project", FindProject("StaticExtractor"),
  "--", gameRoot,
  "--output", Path.GetDirectoryName(outputDirectory)!,
  "--version", version));
if (result != 0) return 1;
var staticExtractTime = DateTime.Now;

// Step 3: Runtime extraction prompt
Console.WriteLine();
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("  Launch the game and run the extractor:");
Console.WriteLine();
Console.WriteLine("    1. Launch Warhammer 40,000: Rogue Trader");
Console.WriteLine("    2. Open UMM overlay (Ctrl+F10)");
Console.WriteLine("    3. Click \"Export All\"");
Console.WriteLine("    4. Click \"Export Icons + Portraits\"");
Console.WriteLine();
Console.WriteLine($"  Expected runtime output: {GetRuntimeOutputDir(version)}");
Console.WriteLine();
Console.Write("  Press Enter when the runtime export is complete... ");
Console.ReadLine();

// Verify the runtime export exists
if (!Directory.Exists(runtimeDir) || Directory.GetFiles(runtimeDir, "*.json").Length == 0) {
  Console.WriteLine($"  WARNING: No runtime export found at {runtimeDir}");
  Console.Write("  Continue anyway? [y/N] ");
  var confirm = Console.ReadLine();
  if (confirm?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true) return 0;
}

// Step 4: Merge - use runtimeDir if the user ran the game, otherwise the preserved copy
var mergeSource = runtimeDir;
if (preservedRuntimeDir != null) {
  // Check if runtimeDir has NEW runtime data (files written after static extraction finished)
  var hasNewRuntime = Directory.GetFiles(runtimeDir, "*.json")
    .Any(file => File.GetLastWriteTimeUtc(file) > staticExtractTime);

  if (!hasNewRuntime) mergeSource = preservedRuntimeDir;
}

result = await Step("Merging runtime + static", () => RunDotnet("run", "--project", FindProject("StaticExtractor"),
  "--", gameRoot,
  "--output", Path.GetDirectoryName(outputDirectory)!,
  "--version", version,
  "--merge-runtime", mergeSource));
if (result != 0) return 1;

// Step 5: Upload
if (!skipUpload) {
  Console.WriteLine();
  Console.Write("Upload to R2? [y/N] ");
  var upload = Console.ReadLine();

  if (upload?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true) {
    result = await Step("Uploading", () => RunDotnet("run", "--project", FindProject("Uploader"),
      "--", "--dev", "--version", version, "--yes"));
    if (result != 0) return 1;
  }
}

Console.WriteLine();
Console.WriteLine("Done! Output is in:");
Console.WriteLine($"  {outputDirectory}");
Console.WriteLine();
return 0;

static void PrintBanner() {
  Console.WriteLine("""
                    VoidForge Pipeline
                    ==================
                    """);
}

static string? DetectGameRoot() {
  const string defaultPath = @"Z:\Steam\steamapps\common\Warhammer 40,000 Rogue Trader\Modding\WhRtModificationTemplate\WhRtModificationTemplate";

  if (Directory.Exists(defaultPath) && Directory.Exists(Path.Combine(defaultPath, "Blueprints")))
    return defaultPath;

  // Try to find the game install via Player.log
  var localLow = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "..", "LocalLow", "Owlcat Games", "Warhammer 40000 Rogue Trader");

  var playerLog = Path.Combine(localLow, "Player.log");

  if (!File.Exists(playerLog)) return null;

  var installDir = File.ReadLines(playerLog)
    .Select(line => Regex.Match(line, @"([A-Z]:\\.*Warhammer 40,000 Rogue Trader)"))
    .FirstOrDefault(m => m.Success)
    ?.Groups[1].Value;

  if (installDir == null) return null;
  var templatePath = Path.Combine(installDir, "Modding", "WhRtModificationTemplate", "WhRtModificationTemplate");

  return Directory.Exists(templatePath) ? templatePath : null;
}

static string PromptGameRoot() {
  while (true) {
    Console.Write("  Game root (WhRtModificationTemplate path): ");
    var path = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) {
      Console.WriteLine("  Directory not found.");
      continue;
    }

    if (Directory.Exists(Path.Combine(path, "Blueprints"))) return path;

    Console.WriteLine("  No Blueprints/ folder found. Expected WhRtModificationTemplate.");
  }
}

static string DetectVersion(string gameRoot) {
  var searchDir = new DirectoryInfo(gameRoot);

  while (searchDir != null) {
    var versionFile = Path.Combine(searchDir.FullName, "WH40KRT_Data", "StreamingAssets", "Version.info");
    if (File.Exists(versionFile)) {
      var line = File.ReadLines(versionFile).FirstOrDefault();
      if (line != null) {
        var match = Regex.Match(line, @"\d+\.\d+\.\d+\.\d+");
        if (match.Success) return match.Value;
      }
    }

    searchDir = searchDir.Parent;
  }

  Console.Write("  Could not auto-detect version. Enter version: ");
  return Console.ReadLine()?.Trim() ?? "unknown";
}

static string ResolveOutputDirectory(string version, string? baseOverride) {
  var baseDir = baseOverride ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoidForge");

  return Path.Combine(baseDir, version);
}

// Checks if a directory contains runtime export data (resolved HTML descriptions, not {g|key} markup).
static bool HasRuntimeData(string directory) {
  var featuresFile = Path.Combine(directory, "features.json");
  if (!File.Exists(featuresFile)) return false;

  var json = File.ReadAllText(featuresFile);
  // Runtime descriptions have resolved HTML like <b><color>; static has {g|key}
  return json.Contains("<b><color") || json.Contains("</color></b>");
}

static string GetRuntimeOutputDir(string version) {
  var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
  return Path.Combine(documents, "VoidForge", version);
}

static string FindProject(string projectName) {
  var dir = new DirectoryInfo(AppContext.BaseDirectory);

  while (dir != null) {
    var candidate = Path.Combine(dir.FullName, projectName, $"{projectName}.csproj");
    if (File.Exists(candidate)) return candidate;

    dir = dir.Parent;
  }

  return projectName;
}

static async Task<int> Step(string name, Func<Task<int>> action) {
  Console.Write($"  {name}... ");
  var result = await action();
  Console.WriteLine(result == 0 ? "done" : $"FAILED (exit {result})");
  return result;
}

static Task<int> RunDotnet(params string[] args) {
  var arguments = string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));

  var process = Process.Start(new ProcessStartInfo {
    FileName = "dotnet",
    Arguments = arguments,
    RedirectStandardOutput = false,
    RedirectStandardError = false,
    UseShellExecute = false,
  });

  if (process == null) return Task.FromResult(1);

  process.WaitForExit();
  return Task.FromResult(process.ExitCode);
}

static void CopyDirectory(string source, string target) {
  Directory.CreateDirectory(target);
  foreach (var file in Directory.EnumerateFiles(source))
    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
  foreach (var dir in Directory.EnumerateDirectories(source))
    CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
}

static void PrintUsage() {
  Console.WriteLine("Usage: VoidForge [gameRoot] [options]");
  Console.WriteLine();
  Console.WriteLine("Arguments:");
  Console.WriteLine("  gameRoot    Path to the WhRtModificationTemplate folder (auto-detected by default)");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --output <path>     Override output base directory (default: Documents\\VoidForge)");
  Console.WriteLine("  --version <ver>     Override game version (auto-detected from Version.info)");
  Console.WriteLine("  --no-upload         Skip the upload prompt at the end");
  Console.WriteLine("  --help              Show this help");
}