using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints.Items.Armors;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts all BlueprintItemArmor instances from the game's blueprint cache.
 * Outputs armor.json (data) and armor_schema.json (type API surface for development).
 */
public static class ArmorExporter {
  private const string Source = "armor";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var extractedArmor = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var armorBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintItemArmor>())
      try {
        var armorFields = BlueprintFieldExtractor.ExtractSimpleFields(armorBlueprint);

        if (!armorFields.TryGetValue("Name", out var nameValue) || nameValue is not string armorName ||
            !ItemFilter.IsValidName(armorName)) {
          continue;
        }

        if (!ItemFilter.IsPlayerRelevant(armorFields, armorBlueprint)) {
          skippedCount++;

          continue;
        }

        ItemFilter.AddReachabilityPlaceholder(armorFields);
        extractedArmor.Add(armorFields);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={armorBlueprint.AssetGuid} reason={exception.Message}");
      }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedArmor);
    ExportWriter.WriteEnvelope(outputDirectory, "armor", envelope);

    var armorSchema = BlueprintFieldExtractor.BuildTypeSchema(typeof(BlueprintItemArmor));
    ExportWriter.WriteSchema(outputDirectory, "armor_schema", armorSchema);

    logger.Result(Source, "export done", ("count", extractedArmor.Count), ("filtered", skippedCount));
  }
}