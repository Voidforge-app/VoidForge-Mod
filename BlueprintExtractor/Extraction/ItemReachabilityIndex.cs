using System.Collections;
using System.Reflection;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items;
using Kingmaker.Blueprints.Loot;

namespace BlueprintExtractor.Extraction;

/**
 * Builds the set of item GUIDs reachable by the player via vendor tables and loot containers.
 * Used by item exporters to populate the "reachable" flag on each exported item.
 */
public static class ItemReachabilityIndex {
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  public static HashSet<string> Build(ModLogger logger) {
    var reachableGuids = new HashSet<string>(StringComparer.Ordinal);
    var vendorTableCount = 0;
    var lootContainerCount = 0;

    foreach (var vendorTable in BlueprintsCatalog.AllBlueprints<BlueprintSharedVendorTable>())
      try {
        CollectFromVendorTable(vendorTable, reachableGuids);
        vendorTableCount++;
      }
      catch (Exception exception) {
        logger.Warn("reachability", $"vendor guid={vendorTable.AssetGuid} reason={exception.Message}");
      }

    foreach (var lootBlueprint in BlueprintsCatalog.AllBlueprints<BlueprintLoot>())
      try {
        CollectFromLootBlueprint(lootBlueprint, reachableGuids);
        lootContainerCount++;
      }
      catch (Exception exception) {
        logger.Warn("reachability", $"loot guid={lootBlueprint.AssetGuid} reason={exception.Message}");
      }

    var companionCount = CollectFromCompanionStartingGear(reachableGuids, logger);

    logger.Result("reachability", "index built",
      ("vendorTables", vendorTableCount),
      ("lootContainers", lootContainerCount),
      ("companions", companionCount),
      ("reachableItems", reachableGuids.Count));

    return reachableGuids;
  }

  /**
   * Collects equipment GUIDs from named companion unit blueprints (their starting weapon/armor loadout).
   * Uses the same name-based filter as CompanionExporter to target only player-party companions.
   * Returns the number of companion units processed.
   */
  private static int CollectFromCompanionStartingGear(HashSet<string> reachableGuids, ModLogger logger) {
    var count = 0;

    foreach (var unit in BlueprintsCatalog.AllBlueprints<BlueprintUnit>()) {
      if (!UnitFilter.IsBaseCompanionUnit(unit)) continue;

      try {
        CollectFromUnitBody(unit, reachableGuids);
        count++;
      }
      catch (Exception exception) {
        logger.Warn("reachability", $"companion gear guid={unit.AssetGuid} reason={exception.Message}");
      }
    }

    return count;
  }

  private static void CollectFromUnitBody(BlueprintUnit unit, HashSet<string> reachableGuids) {
    var bodyField = unit.GetType().GetField("Body", AllInstanceFlags);
    var body = bodyField?.GetValue(unit);

    if (body == null) return;

    var bodyType = body.GetType();
    var handSettings = bodyType.GetField("ItemEquipmentHandSettings", AllInstanceFlags)?.GetValue(body);

    if (handSettings != null) {
      var handType = handSettings.GetType();

      AddIfResolvable(handType.GetField("m_PrimaryHand", AllInstanceFlags)?.GetValue(handSettings), reachableGuids);
      AddIfResolvable(handType.GetField("m_SecondaryHand", AllInstanceFlags)?.GetValue(handSettings), reachableGuids);
      AddIfResolvable(handType.GetField("m_PrimaryHandAlternative1", AllInstanceFlags)?.GetValue(handSettings),
        reachableGuids);
    }

    AddIfResolvable(bodyType.GetField("m_Armor", AllInstanceFlags)?.GetValue(body), reachableGuids);
    AddIfResolvable(bodyType.GetField("m_Gloves", AllInstanceFlags)?.GetValue(body), reachableGuids);
    AddIfResolvable(bodyType.GetField("m_Neck", AllInstanceFlags)?.GetValue(body), reachableGuids);
  }

  /**
   * Vendor tables store items in LootItemsPackFixed components on their ComponentsArray.
   * Each component has m_Item (a LootItem struct) with m_Type ("Item" or "Loot") and m_Item (blueprint ref).
   * We only follow m_Type == "Item" since no vendor tables reference nested loot tables.
   */
  private static void CollectFromVendorTable(BlueprintSharedVendorTable vendorTable, HashSet<string> reachableGuids) {
    var componentsProperty = vendorTable.GetType().GetProperty("ComponentsArray", AllInstanceFlags);

    if (componentsProperty?.GetValue(vendorTable) is not IEnumerable components) return;

    foreach (var component in components.Cast<object>()) {
      if (component?.GetType().Name != "LootItemsPackFixed") continue;

      var lootItemField = component.GetType().GetField("m_Item", AllInstanceFlags);
      var lootItem = lootItemField?.GetValue(component);

      if (lootItem == null) continue;

      var mTypeValue = lootItem.GetType().GetField("m_Type", AllInstanceFlags)?.GetValue(lootItem)?.ToString();

      if (mTypeValue != "Item") continue;

      var itemRef = lootItem.GetType().GetField("m_Item", AllInstanceFlags)?.GetValue(lootItem);

      AddIfResolvable(itemRef, reachableGuids);
    }
  }

  /**
   * Loot blueprints (chests, drops, exploration rewards) store items directly in the Items list.
   * Each LootEntry has an m_Item blueprint ref pointing to the actual item blueprint.
   */
  private static void CollectFromLootBlueprint(BlueprintLoot lootBlueprint, HashSet<string> reachableGuids) {
    var itemsField = lootBlueprint.GetType().GetField("Items", AllInstanceFlags);

    if (itemsField?.GetValue(lootBlueprint) is not IEnumerable items) return;

    foreach (var lootEntry in items.Cast<object>()) {
      if (lootEntry == null) continue;

      var itemRef = lootEntry.GetType().GetField("m_Item", AllInstanceFlags)?.GetValue(lootEntry);

      AddIfResolvable(itemRef, reachableGuids);
    }
  }

  private static void AddIfResolvable(object itemRef, HashSet<string> reachableGuids) {
    if (itemRef == null) return;

    try {
      var resolved = RankEntryExtractor.Dereference(itemRef);
      var guid = (resolved as SimpleBlueprint)?.AssetGuid;

      if (!string.IsNullOrEmpty(guid)) reachableGuids.Add(guid);
    }
    catch { }
  }
}