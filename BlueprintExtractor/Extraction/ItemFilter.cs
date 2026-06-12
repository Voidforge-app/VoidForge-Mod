using Kingmaker.Blueprints.Items.Weapons;

namespace BlueprintExtractor.Extraction;

/**
 * Heuristic filters for identifying player-equippable items.
 * Excludes NPC-only gear, dev/test items, natural weapons, and non-game items.
 * A "reachable" bool is set by cross-referencing the ItemReachabilityIndex.
 */
public static class ItemFilter {
  /// <summary>
  ///   Returns true if the extracted item fields indicate a player-relevant item.
  ///   Filters out: non-removable NPC gear, unlootable items, natural weapons, and dev items.
  /// </summary>
  public static bool IsPlayerRelevant(Dictionary<string, object> itemFields, object blueprintObject) {
    // Exclude items hardcoded onto NPCs
    if (GetBool(itemFields, "IsNonRemovable")) return false;

    // Exclude items that can never be picked up
    if (GetBool(itemFields, "IsUnlootable")) return false;

    // Exclude natural weapons (claws, bites, body parts)
    if (GetBool(itemFields, "IsNatural")) return false;

    // Exclude dev/test items not meant for gameplay (weapons only)
    return blueprintObject is not BlueprintItemWeapon || GetBool(itemFields, "CanBeUsedInGame");
  }

  /// <summary>
  ///   Sets the "reachable" field to true if the given GUID appears in the reachability index,
  ///   false otherwise. An item is reachable if it appears in any vendor table or loot container.
  /// </summary>
  public static void SetReachability(Dictionary<string, object> itemFields, string guid, HashSet<string> reachableGuids) {
    itemFields["reachable"] = reachableGuids.Contains(guid);
  }

  /**
   * Returns true if a name is a real player-visible string.
   * Filters out null/whitespace and localization sentinels the game emits for missing keys.
   */
  public static bool IsValidName(string name) {
    if (string.IsNullOrWhiteSpace(name)) return false;
    if (name == "<null>") return false;
    if (name.StartsWith("[unknown key:")) return false;

    return true;
  }

  private static bool GetBool(Dictionary<string, object> fields, string key) {
    if (!fields.TryGetValue(key, out var value)) return false;

    return value is true or "True";
  }
}