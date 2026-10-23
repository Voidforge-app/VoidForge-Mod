using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StaticExtractor.Blueprints;

/**
 * Scans the Blueprints/ folder and loads .jbp files into BlueprintDocument objects.
 * Builds a type-name index for fast lookup by blueprint type (e.g. "BlueprintItemWeapon").
 * Mirrors the runtime BlueprintsCatalog but reads from disk instead of the game's cache.
 *
 * Supports optional type filtering (only load matching types) and parallel loading.
 * When buildGraph is true, the reference graph is built in the same pass as the catalog,
 * avoiding a second full scan of all files.
 */
public partial class BlueprintCatalog {

  /**
   * Lightweight GUID-to-file index used to load prototype parents on demand.
   */
  private readonly ConcurrentDictionary<string, string> _fileByAssetId = new(StringComparer.Ordinal);

  /**
   * Loads all .jbp files from the given folder.
   * If typeFilter is provided, only blueprints of those types are loaded (fast preview check
   * before full deserialization). The asset index, catalog, and optional reference graph are built
   * in one file-reading pass.
   */
  public BlueprintCatalog(string blueprintsPath, HashSet<string>? typeFilter = null, bool buildGraph = true) {
    BasePath = blueprintsPath;

    if (!Directory.Exists(blueprintsPath)) {
      throw new DirectoryNotFoundException($"Blueprints folder not found: {blueprintsPath}");
    }

    LoadAll(typeFilter, buildGraph);
  }

  /**
   * All loaded blueprints keyed by AssetId.
   */
  public Dictionary<string, BlueprintDocument> ById { get; } = new(StringComparer.Ordinal);

  /**
   * Blueprints grouped by type name for fast type-filtered enumeration.
   */
  public Dictionary<string, List<BlueprintDocument>> ByTypeName { get; } = new(StringComparer.Ordinal);

  /**
   * The root path of the Blueprints/ folder.
   */
  private string BasePath { get; }

  /**
   * The reference graph, built during loading if requested. Null otherwise.
   */
  public ReferenceGraph? Graph { get; private set; }

  /**
   * Total count of all loaded blueprints (including shadow-deleted).
   */
  public int TotalCount => ById.Count;

  /**
   * Count of non-shadow-deleted blueprints.
   */
  public int ActiveCount => ById.Values.Count(bp => !bp.IsShadowDeleted);

  [GeneratedRegex(
    """
    "AssetId"\s*:\s*"([0-9a-fA-F]{32})"
    """,
    RegexOptions.CultureInvariant)]
  private static partial Regex AssetIdRegex();

  [GeneratedRegex(
    """
    "\$type"\s*:\s*"[0-9a-fA-F]{32},\s*(\w+)"
    """,
    RegexOptions.CultureInvariant)]
  private static partial Regex TypePreviewRegex();

  /**
   * Enumerates all blueprints of the given type name (e.g. "BlueprintItemWeapon").
   * Excludes shadow-deleted blueprints unless includeShadowDeleted is true.
   */
  public IEnumerable<BlueprintDocument> ByType(string typeName, bool includeShadowDeleted = false) {
    if (!ByTypeName.TryGetValue(typeName, out var list)) return [];

    return includeShadowDeleted ? list : list.Where(bp => !bp.IsShadowDeleted);
  }

  /**
   * Returns a blueprint by its AssetId, or null if not found.
   */
  public BlueprintDocument? GetById(string id) {
    return ById.TryGetValue(id, out var document) ? document : LoadOnDemand(id);
  }

  /**
   * Resolves a raw field value by walking up the prototype chain.
   * Returns the first non-null/non-empty value found for the given field name
   * in the ExtraFields dictionary, starting from the given blueprint and
   * following PrototypeLink GUIDs up to the root.
   */
  public JsonElement? ResolvePrototypeField(BlueprintDocument document, string fieldName) {
    var visited = new HashSet<string>(StringComparer.Ordinal);
    var current = document;

    while (current != null && visited.Add(current.AssetId)) {
      if (current.Data.ExtraFields != null &&
          current.Data.ExtraFields.TryGetValue(fieldName, out var value) &&
          value.ValueKind != JsonValueKind.Null &&
          (value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(value.GetString()))) {
        return value;
      }

      var parentId = current.Data.PrototypeLink;

      if (string.IsNullOrEmpty(parentId)) break;

      current = GetById(parentId) ?? LoadOnDemand(parentId);
    }

    return null;
  }

  private void LoadAll(HashSet<string>? typeFilter, bool buildGraph) {
    var files = Directory.EnumerateFiles(BasePath, "*.jbp", SearchOption.AllDirectories).ToList();
    var graph = buildGraph ? new ReferenceGraph() : null;

    var localLock = new object();
    var parallelOptions = new ParallelOptions {
      MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
    };

    Parallel.ForEach(files, parallelOptions,
      () => new ThreadState(),
      (file, _, local) => {
        try {
          var json = File.ReadAllText(file);
          var assetIdMatch = AssetIdRegex().Match(json);

          if (assetIdMatch.Success) _fileByAssetId[assetIdMatch.Groups[1].Value] = file;

          // Quick type preview: extract the first $type match without full deserialization.
          var typeName = ExtractTypePreview(json);

          if (typeName == null || typeFilter != null && !typeFilter.Contains(typeName)) return local;

          // Full parse for matching types (or when no filter).
          var document = JsonSerializer.Deserialize<BlueprintDocument>(
            json, BlueprintDocument.SerializerOptions);

          if (document == null || string.IsNullOrEmpty(document.AssetId)) return local;

          document.FullPath = file;
          document.RelativePath = Path.GetRelativePath(BasePath, file);
          local.Documents.Add(document);

          if (graph != null) {
            var referencedIds = ExtractReferences(json);
            local.References.Add((document.AssetId, referencedIds));
          }
        }
        catch {
          /* skip malformed files */
        }

        return local;
      },
      local => {
        lock (localLock) {
          foreach (var document in local.Documents) {
            ById[document.AssetId] = document;
            IndexByType(document);
          }

          if (graph == null) return;
          foreach (var (sourceId, referencedIds) in local.References) {
            graph.ReferencesFrom[sourceId] = referencedIds;

            foreach (var referencedId in referencedIds) {
              graph.ReferencedBy.GetOrAdd(
                  referencedId, _ => new HashSet<string>(StringComparer.Ordinal))
                .Add(sourceId);
            }
          }
        }
      });

    Graph = graph;
  }

  private BlueprintDocument? LoadOnDemand(string assetId) {
    if (!_fileByAssetId.TryGetValue(assetId, out var file)) return null;

    try {
      var document = BlueprintDocument.FromFile(file, BasePath);

      if (string.IsNullOrEmpty(document.AssetId)) return null;

      ById[document.AssetId] = document;
      IndexByType(document);

      return document;
    }
    catch {
      return null;
    }
  }

  /**
   * Quick regex-based type extraction from raw JSON, avoiding full deserialization.
   */
  private static string? ExtractTypePreview(string json) {
    var match = TypePreviewRegex().Match(json);

    return match.Success ? match.Groups[1].Value : null;
  }

  /**
   * Extracts !bp_ references from raw JSON content.
   */
  private static HashSet<string> ExtractReferences(string content) {
    var referencedIds = new HashSet<string>(StringComparer.Ordinal);

    foreach (Match match in ReferenceGraph.BlueprintReferenceRegex.Matches(content)) {
      referencedIds.Add(match.Groups[1].Value);
    }

    return referencedIds;
  }

  private void IndexByType(BlueprintDocument document) {
    var typeName = document.TypeName;

    if (string.IsNullOrEmpty(typeName)) return;

    if (!ByTypeName.TryGetValue(typeName, out var list)) {
      list = [];
      ByTypeName[typeName] = list;
    }

    list.Add(document);
  }

  /**
   * Thread-local accumulation state for parallel loading.
   */
  private class ThreadState {
    public List<BlueprintDocument> Documents { get; } = new();
    public List<(string SourceId, HashSet<string> ReferencedIds)> References { get; } = new();
  }
}

/**
 * Builds a bidirectional reference graph by scanning .jbp files for !bp_ patterns.
 * This is the same approach Owlcat's BlueprintIndexingServer uses internally (see IFileReader.cs).
 * The graph maps: blueprint GUID -> set of blueprint GUIDs it references, and the reverse.
 */
public partial class ReferenceGraph {

  /**
 * Matches !bp_{32 hex chars} patterns inside .jbp file content.
 */
  public static Regex BlueprintReferenceRegex => BlueprintReferencePattern();

  /**
 * Maps a blueprint GUID to every GUID it references (outgoing edges).
 */
  public Dictionary<string, HashSet<string>> ReferencesFrom { get; } = new(StringComparer.Ordinal);

  /**
 * Maps a blueprint GUID to every GUID that references it (incoming edges).
 */
  public ConcurrentDictionary<string, HashSet<string>> ReferencedBy { get; } = new(StringComparer.Ordinal);

  [GeneratedRegex(@"!bp_([0-9a-f]{32})", RegexOptions.CultureInvariant)]
  private static partial Regex BlueprintReferencePattern();

  /**
 * Returns all GUIDs that the given blueprint references.
 */
  public IEnumerable<string> GetReferencesFrom(string id) {
    return ReferencesFrom.TryGetValue(id, out var refs) ? refs : Enumerable.Empty<string>();
  }
}