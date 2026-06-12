using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints.Items.Armors;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts all BlueprintItemArmor instances from the game's blueprint cache.
 * Outputs armor.json with a curated set of build-planner-relevant fields.
 */
public static class ArmorExporter {
  private const string Source = "armor";

  // The exact set of fields the build planner needs. Everything else from the raw blueprint dump is noise.
  private static readonly HashSet<string> KeptFields = [
    "AssetGuid", "Name", "Description", "FlavorText", "Rarity",
    "Category", "DamageAbsorption", "DamageDeflection", "RaceRestriction", "ProficiencyGroup",
    "GainAbility", "IsNotable", "ProfitFactorCost",
  ];

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory,
    HashSet<string> reachableItemGuids) {
    var extractedArmor = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var armorBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintItemArmor>())
      try {
        var allFields = BlueprintFieldExtractor.ExtractSimpleFields(armorBlueprint);

        if (!allFields.TryGetValue("Name", out var nameValue) || nameValue is not string armorName ||
            !ItemFilter.IsValidName(armorName)) {
          continue;
        }

        if (!ItemFilter.IsPlayerRelevant(allFields, armorBlueprint)) {
          skippedCount++;

          continue;
        }

        var armorData = KeptFields
          .Where(allFields.ContainsKey)
          .ToDictionary(key => key, key => allFields[key]);

        ItemFilter.SetReachability(armorData, armorBlueprint.AssetGuid, reachableItemGuids);
        extractedArmor.Add(armorData);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={armorBlueprint.AssetGuid} reason={exception.Message}");
      }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedArmor);
    ExportWriter.WriteEnvelope(outputDirectory, "armor", envelope);

    logger.Result(Source, "export done", ("count", extractedArmor.Count), ("filtered", skippedCount));
  }
}