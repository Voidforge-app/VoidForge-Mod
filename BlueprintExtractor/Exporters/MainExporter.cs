using BlueprintExtractor.Infrastructure;

namespace BlueprintExtractor.Exporters;

/**
 * Main, top-level orchestrator for all exports (items, careers, abilities).
 * Resolves the output directory, creates the mod logger, then delegates to individual per-type exporters.
 */
public static class MainExporter {
  public static void ExportAll() {
    var gameVersion = GameVersionHelper.GetVersion();
    var gameRevision = GameVersionHelper.GetRevision();
    var outputDirectory = ExportWriter.ResolveOutputDirectory(gameVersion);

    Directory.CreateDirectory(outputDirectory);

    var logger = new ModLogger(outputDirectory);

    logger.Info("export", $"version={gameVersion} revision={gameRevision}");
    logger.Info("export", $"outputDir={outputDirectory}");
    logger.Info("export", $"registeredBlueprints={BlueprintsCatalog.TotalRegisteredCount()}");

    // Dump cache internals for development reference
    var cacheSchema = BlueprintsCatalog.DumpCacheSchema();
    ExportWriter.WriteSchema(outputDirectory, "cache_schema", cacheSchema);

    try {
      WeaponExporter.Export(logger, gameVersion, gameRevision, outputDirectory);
    }
    catch (Exception exception) {
      logger.Error("export", "fatal error during item export", exception);
    }
    finally {
      logger.Flush();
    }
  }
}