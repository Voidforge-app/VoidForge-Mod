using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Uploader;

/**
 * VoidForge export uploader.
 * <br />
 * Usage:
 * <code>VoidForgeUploader [--dev] [--version {ver}] [--bucket {name}] [--source {path}] [--remote {name}] [--yes] [--help]</code>
 */
public static partial class Program {
  private const string DefaultRemote = "VoidforgeR2";
  private const string DevBucket = "voidforge-dev";

  private static readonly JsonSerializerOptions CompactJsonSerializerOptions = new() {
    WriteIndented = false,
  };

  public static int Main(string[] args) {
    var options = ParseArgs(args);

    if (options.ShowHelp) {
      PrintUsage();
      return 0;
    }

    var remote = options.Remote ?? DefaultRemote;

    var sourceRoot = options.SourceOverride ?? GetDefaultSourceRoot();

    if (!Directory.Exists(sourceRoot)) {
      Console.WriteLine($"Source root not found: {sourceRoot}");
      return 1;
    }

    // Step 1: find and select a version directory
    var versionDirs = FindVersionDirectories(sourceRoot);

    if (versionDirs.Count == 0) {
      Console.WriteLine($"No version directories found in {sourceRoot}");
      return 1;
    }

    var selectedVersion = options.Version != null
      ? versionDirs.FirstOrDefault(directory => directory.Name == options.Version)
      : SelectVersion(versionDirs);

    if (selectedVersion == null) {
      Console.WriteLine($"Version '{options.Version}' not found. Available:");
      foreach (var directory in versionDirs) Console.WriteLine($"  {directory.Name}");
      return 1;
    }

    var sourcePath = selectedVersion.FullName;
    var isNewest = selectedVersion.Name == versionDirs[0].Name;

    // Step 2: determine bucket
    string? bucket;

    if (options.IsDev) {
      bucket = DevBucket;
    }
    else if (options.Bucket != null) {
      bucket = options.Bucket;
    }
    else {
      bucket = SelectBucket(remote);
      if (bucket == null) return 1;
    }

    var destination = $"{remote}:{bucket}/{selectedVersion.Name}";

    // Step 3: confirm
    Console.WriteLine();
    Console.WriteLine("Upload summary:");
    Console.WriteLine($"  Source      : {sourcePath}");

    if (options.IsDev) Console.WriteLine("               (excluding *.log)");
    Console.WriteLine($"  Destination : {destination}");

    if (options.IsDev)
      Console.WriteLine($"  Update latest.json : {(isNewest ? "yes" : "no (not newest)")}");

    if (!options.SkipConfirm) {
      Console.WriteLine();
      Console.Write("Proceed? [Y/n] ");

      var confirm = Console.ReadLine();
      if (!string.IsNullOrWhiteSpace(confirm) && !confirm.Equals("y", StringComparison.OrdinalIgnoreCase)) {
        Console.WriteLine("Cancelled.");
        return 0;
      }
    }

    // Step 4: run rclone sync
    Console.WriteLine();
    Console.WriteLine("Uploading...");

    var syncArgs = BuildSyncArgs(sourcePath, destination, options.IsDev);
    var exitCode = RunRclone(syncArgs);

    if (exitCode != 0) {
      Console.WriteLine($"rclone exited with code {exitCode}");
      return exitCode;
    }

    Console.WriteLine();
    Console.WriteLine($"Upload complete. -> {destination}");

    switch (options.IsDev) {
      // Step 5: dev mode - update latest.json if this is the newest version
      case true when isNewest:
        Console.WriteLine();
        Console.WriteLine("Updating latest.json...");
        UpdateLatestJson(remote, bucket, selectedVersion.Name);

        break;
      case true when !isNewest:
        Console.WriteLine();
        Console.WriteLine($"Skipping latest.json - {selectedVersion.Name} is not the newest ({versionDirs[0].Name}).");

        break;
    }

    return 0;
  }

  // Finds subdirectories whose names match a version pattern (e.g. "1.6.1.514"), sorted newest first
  private static List<DirectoryInfo> FindVersionDirectories(string sourceRoot) {
    return [
      .. Directory.GetDirectories(sourceRoot)
        .Select(path => new DirectoryInfo(path))
        .Where(directory => MyRegex().IsMatch(directory.Name))
        .OrderByDescending(directory => TryParseVersion(directory.Name))
        .ThenByDescending(directory => directory.Name),
    ];
  }

  // Parses a version string into a comparable Version, falling back to 0.0.0.0 on failure
  private static Version TryParseVersion(string name) {
    try {
      return new Version(name);
    }
    catch {
      return new Version(0, 0, 0, 0);
    }
  }

  // Interactively prompts the user to pick a version, defaulting to the newest (index 1)
  private static DirectoryInfo? SelectVersion(List<DirectoryInfo> versionDirs) {
    Console.WriteLine();
    Console.WriteLine("Available versions:");

    for (var index = 0; index < versionDirs.Count; index++) {
      var marker = index == 0 ? "  <-- default" : "";
      Console.WriteLine($"  [{index + 1}] {versionDirs[index].Name}{marker}");
    }

    Console.WriteLine();
    Console.Write("Select version [1]: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) input = "1";

    if (int.TryParse(input, out var selectedIndex) && selectedIndex >= 1 && selectedIndex <= versionDirs.Count) return versionDirs[selectedIndex - 1];
    Console.WriteLine("Invalid selection.");

    return null;
  }

  //Lists buckets from the rclone remote and prompts the user to pick one
  private static string? SelectBucket(string remote) {
    Console.WriteLine();
    Console.WriteLine("Fetching buckets...");

    var buckets = ListBuckets(remote);

    if (buckets.Count == 0) {
      Console.WriteLine("No buckets found - is rclone configured?");
      return null;
    }

    Console.WriteLine();
    Console.WriteLine("Available buckets:");
    for (var index = 0; index < buckets.Count; index++)
      Console.WriteLine($"  [{index + 1}] {buckets[index]}");

    Console.WriteLine();
    Console.Write("Select bucket: ");
    var input = Console.ReadLine();

    if (int.TryParse(input, out var selectedIndex) && selectedIndex >= 1 && selectedIndex <= buckets.Count) return buckets[selectedIndex - 1];
    Console.WriteLine("Invalid selection.");

    return null;
  }

  private static List<string> ListBuckets(string remote) {
    // Request path-only output so bucket names are not affected by rclone's human-readable size columns.
    var output = RunRcloneCapture(["lsd", "--format", "p", $"{remote}:"]);

    if (string.IsNullOrWhiteSpace(output)) return [];

    return [
      .. output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)),
    ];
  }

  private static string[] BuildSyncArgs(string source, string destination, bool isDev) {
    var args = new List<string> {
      "sync",
      source,
      destination,
      "--progress",
      "--transfers",
      "32",
      "--checkers",
      "16",
      "--buffer-size",
      "16M",
    };

    if (isDev) args.Add("--exclude");
    if (isDev) args.Add("*.log");

    return [.. args];
  }

  /**
   * Runs rclone with the given args, streaming output directly to the console.
   */
  private static int RunRclone(string[] args) {
    var startInfo = new ProcessStartInfo {
      FileName = "rclone",
      RedirectStandardOutput = false,
      RedirectStandardError = false,
      UseShellExecute = false,
    };

    foreach (var arg in args) startInfo.ArgumentList.Add(arg);

    var process = Process.Start(startInfo);

    if (process == null) {
      Console.WriteLine("Failed to start rclone. Is it installed and on PATH?");
      return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
  }

  /**
   * Runs rclone and captures stdout (used for `lsd` bucket listing).
   */
  private static string RunRcloneCapture(string[] args) {
    var startInfo = new ProcessStartInfo {
      FileName = "rclone",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };

    foreach (var arg in args) startInfo.ArgumentList.Add(arg);

    try {
      var process = Process.Start(startInfo);
      if (process == null) return "";

      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return output;
    }
    catch (Exception) {
      return "";
    }
  }

  /**
   * Writes latest.json with the selected version and uploads it to the bucket root.
   */
  private static void UpdateLatestJson(string remote, string bucket, string version) {
    var json = JsonSerializer.Serialize(new {
      version,
    }, CompactJsonSerializerOptions);

    var tempFile = Path.Combine(Path.GetTempPath(), "voidforge-latest.json");
    File.WriteAllText(tempFile, json);

    var destination = $"{remote}:{bucket}/latest.json";
    var exitCode = RunRclone(["copyto", tempFile, destination, "--progress"]);

    File.Delete(tempFile);

    Console.WriteLine(exitCode == 0 ? $"latest.json updated to {version}" : $"Failed to update latest.json (exit {exitCode})");
  }

  /**
   * Returns the default source root depending on mode.
   */
  private static string GetDefaultSourceRoot() {
    var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    return Path.Combine(documents, "VoidForge");
  }

  private static UploadOptions ParseArgs(string[] args) {
    var options = new UploadOptions();

    for (var index = 0; index < args.Length; index++) {
      switch (args[index]) {
        case "--dev":
          options.IsDev = true;
          break;
        case "--version" when index + 1 < args.Length:
          options.Version = args[++index];
          break;
        case "--bucket" when index + 1 < args.Length:
          options.Bucket = args[++index];
          break;
        case "--source" when index + 1 < args.Length:
          options.SourceOverride = args[++index];
          break;
        case "--remote" when index + 1 < args.Length:
          options.Remote = args[++index];
          break;
        case "--yes":
        case "-y":
          options.SkipConfirm = true;
          break;
        case "--help":
        case "-h":
          options.ShowHelp = true;
          break;
      }
    }

    return options;
  }

  private static void PrintUsage() {
    Console.WriteLine("Usage: VoidForgeUploader [options]");
    Console.WriteLine();
    Console.WriteLine("Modes:");
    Console.WriteLine("  (default)    Production upload. Source: Documents\\VoidForge, pick bucket from rclone.");
    Console.WriteLine("  --dev        Dev upload. Source: Documents\\VoidForge, bucket=voidforge-dev,");
    Console.WriteLine("               excludes *.log, updates latest.json if newest version.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --dev                 Use dev mode (voidforge-dev bucket, exclude logs, latest.json)");
    Console.WriteLine("  --version <ver>       Skip version prompt, use this version directory");
    Console.WriteLine("  --bucket <name>       Skip bucket prompt, use this bucket (production mode only)");
    Console.WriteLine("  --source <path>       Override the source root directory");
    Console.WriteLine("  --remote <name>       Override rclone remote name (default: VoidforgeR2)");
    Console.WriteLine("  --yes, -y             Skip confirmation prompt");
    Console.WriteLine("  --help, -h            Show this help");
  }

  [GeneratedRegex(@"^\d+\.\d+")]
  private static partial Regex MyRegex();

  private class UploadOptions {
    public string? Bucket;
    public bool IsDev;
    public string? Remote;
    public bool ShowHelp;
    public bool SkipConfirm;
    public string? SourceOverride;
    public string? Version;
  }
}