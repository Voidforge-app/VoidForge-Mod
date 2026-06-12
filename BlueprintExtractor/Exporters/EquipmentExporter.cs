using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints.Items.Armors;
using Kingmaker.Blueprints.Items.Equipment;
using Kingmaker.Blueprints.Items.Weapons;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts character-slot equipment (rings, necks, heads, gloves, augments, shields, etc.).
 * Excludes weapons, armor (handled by their own exporters), consumables, and starship items.
 * Outputs equipment.json with a curated set of build-planner-relevant fields.
 */
public static class EquipmentExporter {
  private const string Source = "equipment";

  // Maps the blueprint type name to a clean slot label for FE use.
  // Types not in this map (consumables, starship items) are silently excluded.
  private static readonly Dictionary<string, string> SlotByBlueprintType = new() {
    ["BlueprintItemEquipmentFeet"] = "Feet",
    ["BlueprintItemEquipmentGloves"] = "Gloves",
    ["BlueprintItemEquipmentHead"] = "Head",
    ["BlueprintItemEquipmentNeck"] = "Neck",
    ["BlueprintItemEquipmentRing"] = "Ring",
    ["BlueprintItemEquipmentShoulders"] = "Shoulders",
    ["BlueprintItemAugment"] = "Augment",
    ["BlueprintItemShield"] = "Shield",
    ["BlueprintItemEquipmentPetProtocol"] = "PetProtocol",
  };

  // The exact set of fields the build planner needs. Everything else from the raw blueprint dump is noise.
  private static readonly HashSet<string> KeptFields = [
    "AssetGuid", "Name", "Description", "FlavorText", "Rarity",
    "GainAbility", "IsNotable", "ProfitFactorCost",
  ];

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory,
    HashSet<string> reachableItemGuids) {
    var extractedEquipment = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var equipmentBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintItemEquipment>()) {
      // Weapons and armor are exported by their own dedicated exporters
      if (equipmentBlueprint is BlueprintItemWeapon or BlueprintItemArmor) continue;

      // Only include character-slot items; consumables and starship items are excluded
      if (!SlotByBlueprintType.TryGetValue(equipmentBlueprint.GetType().Name, out var slot)) continue;

      try {
        var allFields = BlueprintFieldExtractor.ExtractSimpleFields(equipmentBlueprint);

        if (!allFields.TryGetValue("Name", out var nameValue) || nameValue is not string equipmentName ||
            !ItemFilter.IsValidName(equipmentName)) {
          continue;
        }

        if (!ItemFilter.IsPlayerRelevant(allFields, equipmentBlueprint)) {
          skippedCount++;

          continue;
        }

        var equipmentData = KeptFields
          .Where(allFields.ContainsKey)
          .ToDictionary(key => key, key => allFields[key]);

        equipmentData["Slot"] = slot;
        ItemFilter.SetReachability(equipmentData, equipmentBlueprint.AssetGuid, reachableItemGuids);
        extractedEquipment.Add(equipmentData);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={equipmentBlueprint.AssetGuid} reason={exception.Message}");
      }
    }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedEquipment);
    ExportWriter.WriteEnvelope(outputDirectory, "equipment", envelope);

    logger.Result(Source, "export done", ("count", extractedEquipment.Count), ("filtered", skippedCount));
  }
}