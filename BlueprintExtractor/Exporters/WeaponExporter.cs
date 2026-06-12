using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints.Items.Weapons;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts all BlueprintItemWeapon instances from the game's blueprint cache.
 * 
 * Outputs weapons.json (data) and weapons_schema.json (type API surface for development).
 */
public static class WeaponExporter {
  private const string Source = "weapons";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var extractedWeapons = new List<Dictionary<string, object>>();

    var skippedCount = 0;

    foreach (var weaponBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintItemWeapon>())
      try {
        var weaponFields = BlueprintFieldExtractor.ExtractSimpleFields(weaponBlueprint);

        if (!weaponFields.TryGetValue("Name", out var nameValue) || nameValue is not string weaponName ||
            !ItemFilter.IsValidName(weaponName)) {
          continue;
        }

        if (!ItemFilter.IsPlayerRelevant(weaponFields, weaponBlueprint)) {
          skippedCount++;

          continue;
        }

        ItemFilter.AddReachabilityPlaceholder(weaponFields);
        extractedWeapons.Add(weaponFields);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={weaponBlueprint.AssetGuid} reason={exception.Message}");
      }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedWeapons);
    ExportWriter.WriteEnvelope(outputDirectory, "weapons", envelope);

    var weaponSchema = BlueprintFieldExtractor.BuildTypeSchema(typeof(BlueprintItemWeapon));
    ExportWriter.WriteSchema(outputDirectory, "weapons_schema", weaponSchema);

    logger.Result(Source, "export done", ("count", extractedWeapons.Count), ("filtered", skippedCount));
  }
}