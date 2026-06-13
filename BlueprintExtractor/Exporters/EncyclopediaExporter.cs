using System.Reflection;
using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
using Kingmaker.Localization;
using Kingmaker.UI.Models.Tooltip.Base;

namespace BlueprintExtractor.Exporters;

/**
 * Exports all BlueprintEncyclopediaGlossaryEntry instances used by feature description {g|Encyclopedia:X} links.
 * Output keyed by blueprint asset name (e.g. "ActionPoints") matching the X in {g|Encyclopedia:X}.
 */
public static class EncyclopediaExporter {
  private const string Source = "encyclopedia";
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  public static void Export(ModLogger logger, string gameVersion, string gameRevision, string outputDirectory) {
    var entries = new List<Dictionary<string, object>>();
    var skippedCount = 0;

    foreach (var blueprint in BlueprintsCatalog.AllBlueprints<SimpleBlueprint>()) {
      if (blueprint.GetType().Name != "BlueprintEncyclopediaGlossaryEntry") continue;

      try {
        if (IsHiddenInEncyclopedia(blueprint)) {
          skippedCount++;

          continue;
        }

        var key = blueprint.name;

        if (string.IsNullOrWhiteSpace(key)) continue;

        var title = ResolveTitle(blueprint);
        var description = ResolveDescription(blueprint);

        if (!ItemFilter.IsValidName(title)) {
          skippedCount++;

          continue;
        }

        entries.Add(new Dictionary<string, object> {
          ["key"] = key,
          ["guid"] = blueprint.AssetGuid,
          ["title"] = title,
          ["description"] = description,
        });
      }
      catch (Exception exception) {
        logger.Warn(Source, $"skipped name={blueprint.name} reason={exception.Message}");
      }
    }

    entries = entries.OrderBy(entry => entry["key"] as string ?? "").ToList();

    var envelope = ExportEnvelope<Dictionary<string, object>>.Create(gameVersion, gameRevision, entries);
    ExportWriter.WriteEnvelope(outputDirectory, "encyclopedia", envelope);

    logger.Result(Source, "export done", ("count", entries.Count), ("filtered", skippedCount));
  }

  private static bool IsHiddenInEncyclopedia(SimpleBlueprint blueprint) {
    var field = blueprint.GetType().GetField("HideInEncyclopedia", AllInstanceFlags);

    return field?.GetValue(blueprint) is true;
  }

  private static string ResolveTitle(SimpleBlueprint blueprint) {
    // Sanitize before the guard: IUIDataProvider.Name can return "<null>" sentinels that pass
    // IsNullOrWhiteSpace but reduce to "" after sanitization, causing us to miss the Shared path.
    if (blueprint is not IUIDataProvider uiData) return ResolveLocalizedField(blueprint, "Title");
    var name = FeatureExtractor.SanitizeLocalizedString(uiData.Name);

    return !string.IsNullOrWhiteSpace(name) ? name : ResolveLocalizedField(blueprint, "Title");
  }

  private static string ResolveDescription(SimpleBlueprint blueprint) {
    if (blueprint is not IUIDataProvider uiData) return ResolveLocalizedField(blueprint, "Description");
    var description = FeatureExtractor.SanitizeLocalizedString(uiData.Description);

    return !string.IsNullOrWhiteSpace(description) ? description : ResolveLocalizedField(blueprint, "Description");
  }

  private static string ResolveLocalizedField(SimpleBlueprint blueprint, string fieldName) {
    var field = blueprint.GetType().GetField(fieldName, AllInstanceFlags);
    var localizedString = field?.GetValue(blueprint);

    if (localizedString == null) return "";

    var keyField = localizedString.GetType().GetField("m_Key", AllInstanceFlags);
    var key = keyField?.GetValue(localizedString) as string;

    if (string.IsNullOrEmpty(key)) {
      // Shared indirection: at runtime Shared is a SharedStringAsset (not a plain {assetguid,stringkey} object).
      // SharedStringAsset.String is a LocalizedString whose m_Key holds the resolved localization key.
      var sharedField = localizedString.GetType().GetField("Shared", AllInstanceFlags);
      var sharedAsset = sharedField?.GetValue(localizedString);

      if (sharedAsset != null) {
        var stringField = sharedAsset.GetType().GetField("String", AllInstanceFlags);
        var sharedLocalizedString = stringField?.GetValue(sharedAsset);

        if (sharedLocalizedString != null) {
          var sharedKeyField = sharedLocalizedString.GetType().GetField("m_Key", AllInstanceFlags);
          key = sharedKeyField?.GetValue(sharedLocalizedString) as string;
        }
      }
    }

    if (string.IsNullOrEmpty(key)) return "";

    try {
      var text = LocalizationManager.Instance.CurrentPack.GetText(key);

      return FeatureExtractor.SanitizeLocalizedString(text);
    }
    catch {
      return "";
    }
  }
}