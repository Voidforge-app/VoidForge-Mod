using System.Reflection;
using UnityEngine;

namespace BlueprintExtractor.Infrastructure;

/**
 * Resolves the game's version and revision strings at runtime via reflection.
 * Uses dynamic type lookup because Owlcat moves/renames the GameVersion class between patches.
 */
public static class GameVersionHelper {
  public static string GetVersion() {
    try {
      var gameVersionType = FindGameVersionType();

      if (gameVersionType == null) return Application.version ?? "unknown";
      var getVersionMethod = gameVersionType.GetMethod("GetVersion", BindingFlags.Public | BindingFlags.Static);

      if (getVersionMethod == null) return Application.version ?? "unknown";
      var versionResult = getVersionMethod.Invoke(
        null, getVersionMethod.GetParameters().Length > 0 ? [0] : null
      );

      if (versionResult is string versionString && !string.IsNullOrEmpty(versionString)) return versionString;

      return Application.version ?? "unknown";
    }
    catch {
      return "unknown";
    }
  }

  public static string GetRevision() {
    try {
      var gameVersionType = FindGameVersionType();

      if (gameVersionType == null) return "unknown";

      var revisionProperty = gameVersionType.GetProperty("Revision", BindingFlags.Public | BindingFlags.Static);

      if (revisionProperty != null) return revisionProperty.GetValue(null)?.ToString() ?? "unknown";

      return "unknown";
    }
    catch {
      return "unknown";
    }
  }

  /**
   * Locates the Kingmaker GameVersion type dynamically.
   * Name and namespace can shift between patches, so we search all loaded assemblies.
   */
  private static Type FindGameVersionType() {
    return AppDomain.CurrentDomain.GetAssemblies()
      .SelectMany(assembly => {
        try {
          return assembly.GetTypes();
        }
        catch {
          return [];
        }
      })
      .FirstOrDefault(type => type.Name == "GameVersion" && type.Namespace?.Contains("Kingmaker") == true);
  }
}