using System.Reflection;
using Kingmaker.Blueprints;

namespace BlueprintExtractor.Infrastructure;

/**
 * Provides enumeration over the game's loaded blueprint cache.
 * Uses publicized internals (via BepInEx.AssemblyPublicizer) to access blueprints.
 * m_LoadedBlueprints contains all registered GUIDs but entries are lazily deserialized,
 * so we force-load each via ResourcesLibrary.TryGetBlueprint.
 */
public static class BlueprintsCatalog {
  /// <summary>
  ///   Enumerates all blueprints of the specified type by force-loading every registered GUID.
  ///   m_LoadedBlueprints has all GUIDs registered at Init time but .Blueprint is null until accessed.
  /// </summary>
  public static IEnumerable<T> AllBlueprints<T>() where T : SimpleBlueprint {
    var cache = ResourcesLibrary.BlueprintsCache;

    if (cache == null) yield break;

    // Snapshot the keys to avoid modification during iteration
    var allGuids = cache.m_LoadedBlueprints.Keys.ToList();

    foreach (var blueprintGuid in allGuids) {
      SimpleBlueprint loadedBlueprint = null;

      try {
        loadedBlueprint = cache.Load(blueprintGuid);
      }
      catch {
        // Some entries fail to deserialize - skip them
      }

      if (loadedBlueprint is T matchingBlueprint) {
        yield return matchingBlueprint;
      }
    }
  }

  /// <summary>
  ///   Returns the total number of registered blueprint GUIDs in the cache.
  /// </summary>
  public static int TotalRegisteredCount() {
    var cache = ResourcesLibrary.BlueprintsCache;

    return cache?.m_LoadedBlueprints?.Count ?? 0;
  }

  /// <summary>
  ///   Returns diagnostic info about the BlueprintsCache internals for schema discovery.
  /// </summary>
  public static object DumpCacheSchema() {
    var cache = ResourcesLibrary.BlueprintsCache;

    if (cache == null) return new { Error = "cache is null" };

    var cacheType = cache.GetType();
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    return new {
      TypeName = cacheType.FullName,
      LoadedCount = cache.m_LoadedBlueprints?.Count ?? -1,
      Fields = cacheType.GetFields(flags)
        .Select(field => new { field.Name, Type = field.FieldType.Name })
        .ToList(),
      Properties = cacheType.GetProperties(flags)
        .Select(property => new { property.Name, Type = property.PropertyType.Name })
        .ToList(),
    };
  }
}