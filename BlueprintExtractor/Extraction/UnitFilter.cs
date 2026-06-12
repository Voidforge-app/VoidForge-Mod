// Filter predicate for identifying player-party companion units by asset name.

using Kingmaker.Blueprints;

namespace BlueprintExtractor.Extraction;

/**
 * Shared filter for identifying base companion unit blueprints.
 * Used by CompanionExporter, ItemReachabilityIndex, and IconExporter to target
 * the same set of companion units without duplicating the predicate.
 */
public static class UnitFilter {
  /**
   * Returns true for the canonical base companion unit blueprints.
   * 
   * Inclusion: asset name must end with "Companion" (covers both "ArgentaCompanion" and "Argenta_Companion").
   * Exclusion: chapter variants (e.g. Ulfar_Ch03End_Companion) and dev/test units (TESTArgentaCompanion).
   */
  public static bool IsBaseCompanionUnit(BlueprintUnit unit) {
    var assetName = unit.name;

    if (!assetName.EndsWith("Companion")) return false;
    if (assetName.Contains("_Ch")) return false;

    return !assetName.StartsWith("TEST");
  }
}