// Validates encyclopedia.json count, required keys, and item completeness.

using ExportValidator.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExportValidator.Tests;

public class EncyclopediaTests {
  private static readonly JArray Items = ExportLoader.LoadItems("encyclopedia");

  private static readonly HashSet<string> RequiredKeys = [
    "ProneEffect", "BleedingEffect", "BlindedEffect", "SlowedEffect", "Deflection", "Exploit", "ResistanceTest",
    "DamageGlossary", "HeroicAct", "DesperateMeasure",
  ];

  [Fact]
  public void Count_IsAtLeast300() {
    Assert.True(Items.Count >= 300,
      $"Expected >= 300 encyclopedia entries, got {Items.Count} (game version: {ExportLoader.LatestVersion})");
  }

  [Fact]
  public void AllItems_HaveNonEmptyKey() {
    var missing = Items
      .Where(item => string.IsNullOrWhiteSpace(item["key"]?.Value<string>()))
      .Select(item => item["guid"]?.Value<string>() ?? "(no guid)")
      .ToList();

    Assert.Empty(missing);
  }

  [Fact]
  public void AllItems_HaveNonEmptyTitle() {
    var missing = Items
      .Where(item => string.IsNullOrWhiteSpace(item["title"]?.Value<string>()))
      .Select(item => item["key"]?.Value<string>() ?? "(no key)")
      .ToList();

    Assert.True(missing.Count == 0,
      $"Entries with empty title: {string.Join(", ", missing)}");
  }

  [Fact]
  public void RequiredKeys_ArePresent() {
    var exportedKeys = Items
      .Select(item => item["key"]?.Value<string>())
      .Where(key => key != null)
      .ToHashSet();

    var absent = RequiredKeys.Where(key => !exportedKeys.Contains(key)).ToList();

    Assert.True(absent.Count == 0,
      $"Missing required encyclopedia keys: {string.Join(", ", absent)}");
  }

  [Fact]
  public void Keys_AreUnique() {
    var keys = Items
      .Select(item => item["key"]?.Value<string>())
      .Where(key => key != null)
      .ToList();

    Assert.Equal(keys.Count, keys.Distinct().Count());
  }
}