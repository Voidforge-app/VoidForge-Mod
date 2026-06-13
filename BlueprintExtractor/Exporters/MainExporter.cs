using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;

namespace BlueprintExtractor.Exporters;

/**
 * Main, top-level orchestrator for all exports (items, careers, abilities).
 * Resolves the output directory, creates the mod logger, then delegates to individual per-type exporters.
 */
public static class MainExporter {
  public static HashSet<string> ExportedCompanionGuids { get; private set; } = new();

  public static string ExportAll() {
    var gameVersion = GameVersionHelper.GetVersion();
    var gameRevision = GameVersionHelper.GetRevision();
    var outputDirectory = ExportWriter.ResolveOutputDirectory(gameVersion);

    Directory.CreateDirectory(outputDirectory);

    var logger = new ModLogger(outputDirectory);

    logger.Info("export", $"version={gameVersion} revision={gameRevision}");
    logger.Info("export", $"outputDir={outputDirectory}");
    logger.Info("export", $"registeredBlueprints={BlueprintsCatalog.TotalRegisteredCount()}");

    try {
      var reachableItemGuids = ItemReachabilityIndex.Build(logger);

      WeaponExporter.Export(logger, gameVersion, gameRevision, outputDirectory, reachableItemGuids);
      ArmorExporter.Export(logger, gameVersion, gameRevision, outputDirectory, reachableItemGuids);
      EquipmentExporter.Export(logger, gameVersion, gameRevision, outputDirectory, reachableItemGuids);
      CareerExporter.Export(logger, gameVersion, gameRevision, outputDirectory);
      OriginExporter.Export(logger, gameVersion, gameRevision, outputDirectory);
      FeaturesExporter.Export(logger, gameVersion, gameRevision, outputDirectory);
      EncyclopediaExporter.Export(logger, gameVersion, gameRevision, outputDirectory);
      ExportedCompanionGuids = CompanionExporter.Export(logger, gameVersion, gameRevision, outputDirectory);

      logger.Info("export", "all exports complete");
    }
    catch (Exception exception) {
      logger.Error("export", "fatal error during export", exception);
    }
    finally {
      logger.Flush();
    }

    return outputDirectory;
  }
}