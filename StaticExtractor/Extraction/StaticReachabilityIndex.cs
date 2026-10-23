using StaticExtractor.Blueprints;

namespace StaticExtractor.Extraction;

/**
 * Builds the set of item GUIDs reachable by the player, using the static reference graph. Replaces the runtime
 * ItemReachabilityIndex which walks vendor tables and loot containers via reflection. Here we use the same logic but
 * traverse the pre-built reference graph.
 *
 * We consider an item "reachable" if it appears in any vendor table or loot container blueprint. Companion starting gear
 * is also included by following references from companion units.
 */
public static class StaticReachabilityIndex {
  /**
   * Builds the reachable item GUID set from the reference graph.
   * Vendor tables and loot containers reference items directly; we collect all
   * GUIDs referenced by any blueprint of those types.
   */
  public static HashSet<string> Build(BlueprintCatalog catalog, ReferenceGraph graph) {
    var reachableGuids = new HashSet<string>(StringComparer.Ordinal);

    CollectFromVendorTables(catalog, graph, reachableGuids);
    CollectFromLootContainers(catalog, graph, reachableGuids);
    CollectFromCompanionStartingGear(catalog, graph, reachableGuids);

    return reachableGuids;
  }

  /**
   * Vendor tables (BlueprintSharedVendorTable) reference items in their components.
   * Referenced blueprints are type-checked before being marked as reachable.
   */
  private static void CollectFromVendorTables(
    BlueprintCatalog catalog, ReferenceGraph graph, HashSet<string> reachableGuids) {

    foreach (var vendorTable in catalog.ByType("BlueprintSharedVendorTable"))
      CollectItemReferences(catalog, graph.GetReferencesFrom(vendorTable.AssetId), reachableGuids);
  }

  /**
   * Loot blueprints (BlueprintLoot) reference items in their Items list.
   * Referenced blueprints are type-checked before being marked as reachable.
   */
  private static void CollectFromLootContainers(
    BlueprintCatalog catalog, ReferenceGraph graph, HashSet<string> reachableGuids) {

    foreach (var lootBlueprint in catalog.ByType("BlueprintLoot"))
      CollectItemReferences(catalog, graph.GetReferencesFrom(lootBlueprint.AssetId), reachableGuids);
  }

  /**
   * Companion unit blueprints reference their starting equipment via m_AddFacts and Body fields.
   * We follow all references from companion units to collect their starting gear.
   */
  private static void CollectFromCompanionStartingGear(
    BlueprintCatalog catalog, ReferenceGraph graph, HashSet<string> reachableGuids) {

    foreach (var unit in catalog.ByType("BlueprintUnit")) {
      if (!IsCompanionUnit(unit)) continue;

      CollectItemReferences(catalog, graph.GetReferencesFrom(unit.AssetId), reachableGuids);
    }
  }

  private static void CollectItemReferences(
    BlueprintCatalog catalog, IEnumerable<string> referencedIds, HashSet<string> reachableGuids) {
    foreach (var referencedId in referencedIds) {
      var referencedBlueprint = catalog.GetById(referencedId);

      if (referencedBlueprint?.TypeName.StartsWith("BlueprintItem", StringComparison.Ordinal) == true)
        reachableGuids.Add(referencedId);
    }
  }

  /**
   * Heuristic for identifying companion unit blueprints by asset name.
   * Mirrors the runtime UnitFilter.IsBaseCompanionUnit check.
   */
  private static bool IsCompanionUnit(BlueprintDocument unit) {
    var name = unit.AssetName;

    return name.EndsWith("Companion", StringComparison.OrdinalIgnoreCase) &&
           !name.Contains("_Ch", StringComparison.OrdinalIgnoreCase) &&
           !name.StartsWith("TEST", StringComparison.OrdinalIgnoreCase);
  }
}

/**
 * Heuristic filters for identifying player-equippable items.
 * Mirrors the runtime ItemFilter but operates on parsed JSON fields instead of reflection.
 */
public static class StaticItemFilter {

  /**
   * Returns true if the extracted item fields indicate a player-relevant item.
   * Filters out: non-removable NPC gear, unlootable items, natural weapons, and dev items.
   */
  public static bool IsPlayerRelevant(Dictionary<string, object?> fields, string typeName) {
    if (GetBool(fields, "isNonRemovable")) return false;
    if (GetBool(fields, "isUnlootable")) return false;
    if (GetBool(fields, "isNatural")) return false;

    // Weapons need CanBeUsedInGame; other item types don't have this field.
    return typeName != "BlueprintItemWeapon" || GetBool(fields, "canBeUsedInGame");
  }

  /**
   * Sets the "reachable" field based on whether the GUID is in the reachability index.
   */
  public static void SetReachability(
    Dictionary<string, object?> fields, string guid, HashSet<string> reachableGuids) {
    fields["reachable"] = reachableGuids.Contains(guid);
  }

  /**
   * Returns true if a name is a real player-visible string.
   * Filters out null/whitespace and localization sentinels.
   */
  public static bool IsValidName(string? name) {
    if (string.IsNullOrWhiteSpace(name)) return false;
    if (name == "<null>") return false;

    return !name.StartsWith("[unknown key:");
  }

  private static bool GetBool(Dictionary<string, object?> fields, string key) {
    if (!fields.TryGetValue(key, out var value)) return false;

    return value is true;
  }
}