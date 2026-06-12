using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints.Items.Weapons;

namespace BlueprintExtractor.Exporters;

/**
 * Extracts all BlueprintItemWeapon instances from the game's blueprint cache.
 * Outputs weapons.json with a curated set of build-planner-relevant fields.
 */
public static class WeaponExporter {
  private const string Source = "weapons";

  // The exact set of fields the build planner needs. Everything else from the raw blueprint dump is noise.
  private static readonly HashSet<string> KeptFields = [
    "AssetGuid", "Name", "Description", "FlavorText", "Rarity",
    "Category", "Family", "HoldingType", "IsRanged", "IsMelee", "IsTwoHanded", "AttackType", "Heaviness",
    "WarhammerDamage", "WarhammerMaxDamage", "WarhammerPenetration", "DodgePenetration",
    "WarhammerRecoil", "WarhammerMaxDistance", "WarhammerMaxAmmo", "RateOfFire",
    "AdditionalHitChance", "OverrideOverpenetrationFactorPercents",
    "SpendCharges", "Charges", "RestoreChargesAfterCombat",
    "GainAbility", "IsNotable", "ProfitFactorCost",
  ];

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory,
    HashSet<string> reachableItemGuids) {
    var extractedWeapons = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var weaponBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintItemWeapon>())
      try {
        var allFields = BlueprintFieldExtractor.ExtractSimpleFields(weaponBlueprint);

        if (!allFields.TryGetValue("Name", out var nameValue) || nameValue is not string weaponName ||
            !ItemFilter.IsValidName(weaponName)) {
          continue;
        }

        if (!ItemFilter.IsPlayerRelevant(allFields, weaponBlueprint)) {
          skippedCount++;

          continue;
        }

        var weaponData = KeptFields
          .Where(allFields.ContainsKey)
          .ToDictionary(key => key, key => allFields[key]);

        ItemFilter.SetReachability(weaponData, weaponBlueprint.AssetGuid, reachableItemGuids);
        extractedWeapons.Add(weaponData);
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped guid={weaponBlueprint.AssetGuid} reason={exception.Message}");
      }

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, extractedWeapons);
    ExportWriter.WriteEnvelope(outputDirectory, "weapons", envelope);

    logger.Result(Source, "export done", ("count", extractedWeapons.Count), ("filtered", skippedCount));
  }
}