namespace BlueprintExtractor;

/**
 * Standard envelope wrapper for all JSON exports. Every export file uses this structure to provide version tracking,
 * timestamps, and a consistent shape for downstream consumers.
 */
public class ExportEnvelope<T> {
  public string Version { get; init; }
  public string Revision { get; init; }
  public string ExportedAt { get; init; }
  public int Count { get; init; }
  public List<T> Items { get; init; }

  public static ExportEnvelope<T> Create(string gameVersion, string gameRevision, List<T> exportedItems) {
    return new ExportEnvelope<T> {
      Version = gameVersion,
      Revision = gameRevision,
      ExportedAt = DateTime.UtcNow.ToString("o"),
      Count = exportedItems.Count,
      Items = exportedItems,
    };
  }
}