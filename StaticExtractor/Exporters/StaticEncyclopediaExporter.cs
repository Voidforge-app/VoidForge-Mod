using System.Text.Json;
using StaticExtractor.Blueprints;
using StaticExtractor.Extraction;
using StaticExtractor.Infrastructure;
using StaticExtractor.Localization;

namespace StaticExtractor.Exporters;

/**
 * Exports encyclopedia glossary entries referenced by Owlcat tooltip links.
 */
public static class StaticEncyclopediaExporter {
  public static void Export(
    BlueprintCatalog catalog,
    LocalizedStringResolver resolver,
    string version,
    string revision,
    string outputDirectory,
    StaticLogger logger) {
    var entries = new List<Dictionary<string, object?>>();

    foreach (var document in catalog.ByType("BlueprintEncyclopediaGlossaryEntry")) {
      try {
        if (GetBool(document.Data.ExtraFields, "HideInEncyclopedia")) continue;

        var title = ResolveField(document.Data.ExtraFields, "Title", resolver);

        if (!StaticItemFilter.IsValidName(title)) continue;

        entries.Add(new Dictionary<string, object?> {
          ["key"] = document.AssetName,
          ["guid"] = document.AssetId,
          ["title"] = title,
          ["description"] = ResolveField(document.Data.ExtraFields, "Description", resolver),
        });
      }
      catch (Exception exception) {
        logger.Warn("encyclopedia", $"skipped guid={document.AssetId} reason={exception.Message}");
      }
    }

    entries.Sort((left, right) =>
      string.Compare(left["key"] as string, right["key"] as string, StringComparison.Ordinal));

    ExportWriter.WriteEnvelope(outputDirectory, "encyclopedia",
      ExportEnvelope<Dictionary<string, object?>>.Create(version, revision, entries));

    logger.Result("encyclopedia", "static export done", ("count", entries.Count));
  }

  private static string ResolveField(
    Dictionary<string, JsonElement>? fields,
    string fieldName,
    LocalizedStringResolver resolver) {
    if (fields == null || !fields.TryGetValue(fieldName, out var element)) return "";

    try {
      var localized = element.Deserialize<LocalizedStringField>(BlueprintDocument.SerializerOptions);
      return LocalizedStringResolver.Sanitize(resolver.Resolve(localized));
    }
    catch {
      return "";
    }
  }

  private static bool GetBool(Dictionary<string, JsonElement>? fields, string fieldName) {
    return fields != null && fields.TryGetValue(fieldName, out var value) && value.ValueKind == JsonValueKind.True;
  }
}