// Validates companions.json count, required companions, and structural completeness.

using ExportValidator.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExportValidator.Tests;

public class CompanionsTests {
  private static readonly JArray Items = ExportLoader.LoadItems("companions");

  private static readonly HashSet<string> RequiredCompanions = [
    "Argenta", "Cassia", "Idira", "Pasqal", "Abelard", "Yrliet", "Jae", "Heinrix", "Ulfar", "Marazhai",
  ];

  [Fact]
  public void Count_IsAtLeast10() {
    Assert.True(Items.Count >= 10,
      $"Expected >= 10 companions, got {Items.Count}");
  }

  [Fact]
  public void AllCompanions_HaveNonEmptyName() {
    var missing = Items
      .Where(item => string.IsNullOrWhiteSpace(item["name"]?.Value<string>()))
      .Select(item => item["guid"]?.Value<string>() ?? "(no guid)")
      .ToList();

    Assert.True(missing.Count == 0,
      $"Companions with empty name: {string.Join(", ", missing)}");
  }

  [Fact]
  public void RequiredCompanions_ArePresent() {
    var exportedNames = Items
      .Select(item => item["name"]?.Value<string>())
      .Where(name => name != null)
      .ToHashSet();

    var absent = RequiredCompanions
      .Where(name => !exportedNames.Any(exported => exported!.Contains(name)))
      .ToList();

    Assert.True(absent.Count == 0,
      $"Missing required companions: {string.Join(", ", absent)}");
  }

  [Fact]
  public void AllCompanions_HaveAtLeastOneCareerProgression() {
    var withoutCareers = Items
      .Where(item => ((item["careerProgressions"] as JArray)?.Count ?? 0) == 0)
      .Select(item => item["name"]?.Value<string>() ?? "(unnamed)")
      .ToList();

    Assert.True(withoutCareers.Count == 0,
      $"Companions with no career progressions: {string.Join(", ", withoutCareers)}");
  }

  [Fact]
  public void Names_AreUnique() {
    var names = Items
      .Select(item => item["name"]?.Value<string>())
      .Where(name => name != null)
      .ToList();

    Assert.Equal(names.Count, names.Distinct().Count());
  }
}