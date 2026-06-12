using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints.Items.Armors;
using Kingmaker.Blueprints.Items.Equipment;
using Kingmaker.Blueprints.Items.Weapons;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts non-weapon, non-armor BlueprintItemEquipment instances (rings, necks, heads, gloves, etc.)
 * from the game's blueprint cache. Outputs equipment.json and equipment_schema.json.
 */
public static class EquipmentExporter {
  private const string Source = "equipment";

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory,
    HashSet<string> reachableItemGuids) {
    var extractedEquipment = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var equipmentBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintItemEquipment>()) {
      // Weapons and armor are exported by their own dedicated exporters
      if (equipmentBlueprint is BlueprintItemWeapon or BlueprintItemArmor) continue;

      try {
        var equipmentFields = BlueprintFieldExtractor.ExtractSimpleFields(equipmentBlueprint);

        if (!equipmentFields.TryGetValue("Name", out var nameValue) || nameValue is not string equipmentName ||
            !ItemFilter.IsValidName(equipmentName)) {
          continue;
        }

        if (!ItemFilter.IsPlayerRelevant(equipmentFields, equipmentBlueprint)) {
          skippedCount++;

          continue;
        }

        ItemFilter.SetReachability(equipmentFields, equipmentBlueprint.AssetGuid, reachableItemGuids);
        extractedEquipment.Add(equipmentFields);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={equipmentBlueprint.AssetGuid} reason={exception.Message}");
      }
    }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedEquipment);
    ExportWriter.WriteEnvelope(outputDirectory, "equipment", envelope);

    var equipmentSchema = BlueprintFieldExtractor.BuildTypeSchema(typeof(BlueprintItemEquipment));
    ExportWriter.WriteSchema(outputDirectory, "equipment_schema", equipmentSchema);

    logger.Result(Source, "export done", ("count", extractedEquipment.Count), ("filtered", skippedCount));
  }
}