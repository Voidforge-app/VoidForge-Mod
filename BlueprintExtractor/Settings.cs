using UnityModManagerNet;

namespace BlueprintExtractor;

/**
 * Persistent user settings for the mod. Currently stores only the base output directory override.
 */
public class Settings : UnityModManager.ModSettings {
  public string BaseOutputDirectory = "";

  public override void Save(UnityModManager.ModEntry modEntry) {
    Save(this, modEntry);
  }
}