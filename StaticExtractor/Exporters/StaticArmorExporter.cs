using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Exports player-facing BlueprintItemArmor records using the runtime armor envelope.
 */
public static class StaticArmorExporter {
  private static readonly string[] OutputFields = [
    "assetGuid",
    "name",
    "description",
    "flavorText",
    "rarity",
    "category",
    "damageAbsorption",
    "damageDeflection",
    "raceRestriction",
    "proficiencyGroup",
    "gainAbility",
    "isNotable",
    "profitFactorCost",
  ];

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

    foreach (var document in catalog.ByType("BlueprintItemArmor", includeShadowDeleted: true)) {
      try {
        var fields = StaticItemHelpers.ExtractCommonFields(document, resolver);
        PopulateArmorFields(fields, document, catalog);
        var name = StaticItemHelpers.GetString(fields, "name");

        if (!StaticItemFilter.IsValidName(name)) continue;
        if (!StaticItemFilter.IsPlayerRelevant(fields, "BlueprintItemArmor")) {
          filtered++;
          continue;
        }

        var output = StaticItemHelpers.SelectFields(fields, OutputFields);
        StaticItemFilter.SetReachability(output, document.AssetId, reachableGuids);
        items.Add(output);
      }
      catch (Exception exception) {
        logger.Warn("armor", $"skipped guid={document.AssetId} reason={exception.Message}");
      }
    }

    ExportWriter.WriteEnvelope(outputDirectory, "armor",
      ExportEnvelope<Dictionary<string, object?>>.Create(gameVersion, gameRevision, items));
    logger.Result("armor", "export done", ("count", items.Count), ("filtered", filtered));
  }

  private static void PopulateArmorFields(
    Dictionary<string, object?> fields,
    BlueprintDocument document,
    BlueprintCatalog catalog) {
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_Rarity");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsNonRemovable");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsUnlootable");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsNatural");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_DamageAbsorption");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_DamageDeflection");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_IsNotable");
    StaticItemHelpers.ResolveField(fields, document, catalog, "m_ProfitFactorCost");

    fields["gainAbility"] = false;
    var armorType = ResolveArmorTypeFields(fields, catalog);
    fields["category"] = armorType.GetValueOrDefault("category");
    fields["raceRestriction"] = armorType.GetValueOrDefault("raceRestriction");
    fields["proficiencyGroup"] = armorType.GetValueOrDefault("proficiencyGroup");

    if (!StaticItemHelpers.GetBool(fields, "overrideDamageDeflection")) {
      fields["damageDeflection"] = armorType.GetValueOrDefault("damageDeflection");
    }
  }

  private static Dictionary<string, object?> ResolveArmorTypeFields(
    Dictionary<string, object?> fields,
    BlueprintCatalog catalog) {
    if (!fields.TryGetValue("type", out var typeValue) || typeValue is not string reference) {
      return new Dictionary<string, object?>();
    }

    var assetId = reference.Replace("!bp_", "", StringComparison.Ordinal);
    var typeDocument = catalog.GetById(assetId);
    var source = typeDocument?.Data.ExtraFields;

    if (source == null) return new Dictionary<string, object?>();

    return new Dictionary<string, object?> {
      ["category"] = ReadString(source, "m_Category"),
      ["raceRestriction"] = ReadString(source, "m_RaceRestriction"),
      ["proficiencyGroup"] = ReadString(source, "m_ProficiencyGroup"),
      ["damageDeflection"] = ReadNumber(source, "m_DamageDeflection"),
    };
  }

  private static string? ReadString(Dictionary<string, JsonElement> fields, string name) {
    return fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
      ? value.GetString()
      : null;
  }

  private static object? ReadNumber(Dictionary<string, JsonElement> fields, string name) {
    if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;

    return value.TryGetInt32(out var integer) ? integer : value.GetDouble();
  }
}