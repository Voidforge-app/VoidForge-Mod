using StaticExtractor.Blueprints;

namespace StaticExtractor.Localization;

/**
 * Resolves localized strings (names, descriptions) for blueprints using the StringCatalog.
 * Handles both the direct m_Key pattern and the Shared indirection pattern.
 *
 * The game uses two localization patterns in .jbp files:
 * 1. Direct: m_Key contains the localization key directly
 * 2. Shared: m_Key is empty, but Shared.stringkey holds the key
 *
 * At runtime the game resolves these through LocalizationManager. Statically, we resolve
 * through the Strings/Mechanics/ JSON files loaded by StringCatalog.
 */
public class LocalizedStringResolver(StringCatalog catalog) {

  /**
   * Resolves a LocalizedStringField to English text.
   *
   * Tries the direct m_Key first, then Shared.stringkey, then direct stringkey
   * (used by BlueprintUnit.LocalizedName which is a SharedStringAsset, not a LocalizedString).
   */
  public string Resolve(LocalizedStringField? field) {
    if (field == null) return "";

    if (!string.IsNullOrEmpty(field.Key)) {
      var text = catalog.ResolveByKey(field.Key);

      if (!string.IsNullOrEmpty(text)) return text;
    }

    if (field.Shared?.StringKey != null) {
      var text = catalog.ResolveByKey(field.Shared.StringKey);

      if (!string.IsNullOrEmpty(text)) return text;
    }

    if (string.IsNullOrEmpty(field.DirectStringKey)) return "";

    {
      var text = catalog.ResolveByKey(field.DirectStringKey);

      if (!string.IsNullOrEmpty(text)) return text;
    }

    return "";
  }

  /**
   * Resolves the display name of a blueprint.
   *
   * Tries m_DisplayName first, then falls back to LocalizedName (used by some types).
   */
  public string ResolveName(BlueprintDocument document) {
    var name = Resolve(document.Data.DisplayName);

    return !string.IsNullOrEmpty(name) ? name : Resolve(document.Data.LocalizedName);
  }

  /**
   * Resolves the description of a blueprint.
   *
   * Tries m_Description first, then falls back to LocalizedDescription.
   */
  public string ResolveDescription(BlueprintDocument document) {
    var description = Resolve(document.Data.Description);

    return !string.IsNullOrEmpty(description) ? description : Resolve(document.Data.LocalizedDescription);
  }

  /**
   * Sanitizes a localized string the same way the runtime extractor does
   */
  public static string Sanitize(string? value) {
    if (string.IsNullOrWhiteSpace(value)) return "";
    if (value == "<null>") return "";

    return value.StartsWith("[unknown key:") ? "" : value;
  }
}