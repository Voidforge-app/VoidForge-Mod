using System.Diagnostics;
using System.Text;

namespace BlueprintExtractor.Infrastructure;

/**
 * Project-internal logger that writes to .exploration/ directory for local debugging.
 * Avoids the noise of UMM logs by keeping a separate, per-run file per caller.
 */
public class ModLogger {
  private readonly StringBuilder logBuffer = new();
  private readonly string logFilePath;
  private readonly Stopwatch sessionTimer = Stopwatch.StartNew();

  public ModLogger(string outputDirectory = null, string logFileName = "mod") {
    outputDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoidForge");
    Directory.CreateDirectory(outputDirectory);
    logFilePath = Path.Combine(outputDirectory, $"{logFileName}.log");

    Append("INF", "logger", "session started");
  }

  public void Info(string source, string message) {
    Append("INF", source, message);
  }

  public void Warn(string source, string message) {
    Append("WRN", source, message);
  }

  public void Error(string source, string message, Exception exception) {
    var flatTrace = exception.StackTrace?.Replace("\r\n", "; ").Replace("\n", "; ").TrimStart() ?? "no-stack";

    Append("ERR", source, $"{message} exception={exception.GetType().Name} msg={exception.Message} stack={flatTrace}");
  }

  public void Result(string source, string message, params (string key, object value)[] fields) {
    var fieldString = string.Join(" ", fields.Select(field => $"{field.key}={field.value}"));

    Append("INF", source, $"{message} {fieldString}".TrimEnd());
  }

  /**
   * Flushes the accumulated log buffer to disk. Gets called at the end of the export run.
   */
  public void Flush() {
    Append("INF", "logger", $"flush elapsed={sessionTimer.ElapsedMilliseconds}ms");

    try {
      File.WriteAllText(logFilePath, logBuffer.ToString());
      logBuffer.Clear();
    }
    catch {
      /* If we can't write the log, there's nothing we can do */
    }
  }

  private void Append(string level, string source, string message) {
    logBuffer.AppendLine($"{level} {source} > {message}");
  }
}