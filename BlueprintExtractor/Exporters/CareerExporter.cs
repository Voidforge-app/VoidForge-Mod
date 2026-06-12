using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.UnitLogic.Progression.Paths;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts BlueprintCareerPath instances representing character careers and their rank trees.
 * Each career has ranks with selectable talents, abilities, and stat bonuses.
 */
public static class CareerExporter {
  private const string Source = "careers";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var extractedCareers = new List<Dictionary<string, object>>();

    foreach (var career in BlueprintsCatalog.AllBlueprints<BlueprintCareerPath>()) 
      try {
        if (!career.IsAvailable) continue;

        var careerData = new Dictionary<string, object> {
          ["Id"] = career.AssetGuid,
          ["Name"] = career.Name ?? "",
          ["Description"] = career.Description ?? "",
          ["Tier"] = career.Tier.ToString(),
          ["Ranks"] = career.Ranks,
          ["IsHunter"] = career.IsHunter,
          ["Prerequisites"] = FeatureExtractor.ExtractFeature(career)["Prerequisites"],
          ["Metadata"] = FeatureExtractor.ExtractCareerMetadata(career),
          ["RankEntries"] = RankEntryExtractor.ExtractRankEntries(career),
        };

        extractedCareers.Add(careerData);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={career.AssetGuid} reason={exception.Message}");
      }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedCareers);
    ExportWriter.WriteEnvelope(outputDirectory, "careers", envelope);

    logger.Result(Source, "export done", ("count", extractedCareers.Count));
  }
}