using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Exports player-facing character-slot equipment while excluding weapons, armor, and consumables.
 */
public static class StaticEquipmentExporter {
  private static readonly Dictionary<string, string> SlotByType = new(StringComparer.Ordinal) {
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

  private static readonly string[] OutputFields = [
    "assetGuid", "name", "description", "flavorText", "rarity", "gainAbility", "isNotable", "profitFactorCost", "slot",
  ];

  public static IReadOnlyCollection<string> SupportedTypes => SlotByType.Keys;

  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    HashSet<string> reachableGuids,
    string gameVersion,
    string gameRevision,
    string outputDirectory,
    StaticLogger logger) {
    var items = new List<Dictionary<string, object?>>();
    var filtered = 0;

    foreach (var (typeName, slot) in SlotByType) {
      foreach (var document in catalog.ByType(typeName, true)) {
        try {
          var fields = StaticItemHelpers.ExtractCommonFields(document, resolver);
          PopulateEquipmentFields(fields, document, catalog);
          var name = StaticItemHelpers.GetString(fields, "name");

          if (!StaticItemFilter.IsValidName(name)) continue;
          if (!StaticItemFilter.IsPlayerRelevant(fields, typeName)) {
            filtered++;
            continue;
          }

          fields["slot"] = slot;
          var output = StaticItemHelpers.SelectFields(fields, OutputFields);
          StaticItemFilter.SetReachability(output, document.AssetId, reachableGuids);
          items.Add(output);
        }
        catch (Exception exception) {
          logger.Warn("equipment", $"skipped guid={document.AssetId} reason={exception.Message}");
        }
      }
    }

    ExportWriter.WriteEnvelope(outputDirectory, "equipment",
      ExportEnvelope<Dictionary<string, object?>>.Create(gameVersion, gameRevision, items));
    logger.Result("equipment", "export done", ("count", items.Count), ("filtered", filtered));
  }

  private static void PopulateEquipmentFields(
    Dictionary<string, object?> fields,
    BlueprintDocument document,
    BlueprintCatalog catalog) {
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_Rarity");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsNonRemovable");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsUnlootable");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsNatural");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsNotable");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_ProfitFactorCost");
    fields["gainAbility"] = StaticItemHelpers.HasEquipmentAbility(document, catalog);
  }
}