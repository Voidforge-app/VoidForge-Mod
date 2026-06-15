// Validates features.json count, source attribution, prerequisites, and known spot-checks.

using ExportValidator.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExportValidator.Tests;

public class FeaturesTests {
  private static readonly JArray Items = ExportLoader.LoadItems("features");

  [Fact]
  public void Count_IsAtLeast750() {
    Assert.True(Items.Count >= 750,
      $"Expected >= 750 features, got {Items.Count} (game version: {ExportLoader.LatestVersion})");
  }

  [Fact]
  public void AllItems_HaveNonEmptyId() {
    var missing = Items
      .Count(item => string.IsNullOrWhiteSpace(item["id"]?.Value<string>()));

    Assert.Equal(0, missing);
  }

  [Fact]
  public void AllItems_HaveNonEmptyName() {
    var missing = Items
      .Where(item => string.IsNullOrWhiteSpace(item["name"]?.Value<string>()))
      .Select(item => item["id"]?.Value<string>() ?? "(no id)")
      .ToList();

    Assert.True(missing.Count == 0,
      $"Features with empty name: {string.Join(", ", missing.Take(10))}");
  }

  [Fact]
  public void SomeFeatures_HavePrerequisites() {
    var withPrereqs = Items
      .Count(item => (item["prerequisites"] as JArray)?.Count > 0);

    Assert.True(withPrereqs >= 50,
      $"Expected >= 50 features with prerequisites, got {withPrereqs}");
  }

  [Fact]
  public void CareerSelection_Sources_AllHaveNonEmptyCareerId() {
    var violations = Items
      .SelectMany(item => item["sources"] as JArray ?? new JArray())
      .Where(source => source["type"]?.Value<string>() == "careerSelection")
      .Count(source => string.IsNullOrWhiteSpace(source["careerId"]?.Value<string>()));

    Assert.Equal(0, violations);
  }

  [Fact]
  public void KnownFeature_YouServeMeIsPresent() {
    var feature = Items.FirstOrDefault(item =>
      item["name"]?.Value<string>() == "You. Serve Me.");

    Assert.NotNull(feature);
  }

  [Fact]
  public void KnownFeature_YouServeMeIsOccupationGranted() {
    var feature = Items.First(item => item["name"]?.Value<string>() == "You. Serve Me.");
    var sources = (JArray?)feature["sources"] ?? new JArray();
    var sourceTypes = sources.Select(source => source["type"]?.Value<string>()).ToList();

    Assert.Contains("occupationGranted", sourceTypes);
  }

  [Fact]
  public void SomeFeatures_HaveBaseCharacterSource() {
    var count = Items.Count(item =>
      (item["sources"] as JArray ?? [])
      .Any(source => source["type"]?.Value<string>() == "baseCharacter"));

    Assert.True(count >= 30,
      $"Expected >= 30 features with baseCharacter source (universal talent pool), got {count}");
  }

  [Fact]
  public void SomeFeatures_HaveCompanionGrantedSource() {
    var count = Items.Count(item =>
      (item["sources"] as JArray ?? [])
      .Any(source => source["type"]?.Value<string>() == "companionGranted"));

    Assert.True(count >= 5,
      $"Expected >= 5 features with companionGranted source, got {count}");
  }

  [Fact]
  public void Ids_AreUnique() {
    var ids = Items
      .Select(item => item["id"]?.Value<string>())
      .Where(id => id != null)
      .ToList();

    Assert.Equal(ids.Count, ids.Distinct().Count());
  }
}