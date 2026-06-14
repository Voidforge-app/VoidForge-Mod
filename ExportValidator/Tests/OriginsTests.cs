// Validates origins.json -- exactly 4 player-facing chargen paths with expected GUIDs.

using ExportValidator.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExportValidator.Tests;

public class OriginsTests {
  private static readonly JArray Items = ExportLoader.LoadItems("origins");

  private static readonly HashSet<string> ExpectedOriginGuids = [
    "45181a40472441a8904a5282f83693f4", // CustomCharacterChargenPath
    "bf7b6a4da7fe4b69accac3506f0dd561", // NavigatorCharacterChargenPath
    "e9215686ca994c3a8fd2558b9e91c649", // NewCompanion_CustomCharacterChargenPath
    "0d601087389648529d352883cd1d5a55", // NewCompanion_Navigator_CustomCharacterChargenPath
  ];

  [Fact]
  public void Count_IsExactly4() {
    Assert.Equal(4, Items.Count);
  }

  [Fact]
  public void ExpectedGuids_AreAllPresent() {
    var exportedIds = Items
      .Select(item => item["id"]?.Value<string>())
      .Where(id => id != null)
      .ToHashSet();

    var absent = ExpectedOriginGuids.Where(guid => !exportedIds.Contains(guid)).ToList();

    Assert.True(absent.Count == 0,
      $"Missing expected origin GUIDs: {string.Join(", ", absent)}");
  }

  [Fact]
  public void NoUnexpected_Guids_ArePresent() {
    var exportedIds = Items
      .Select(item => item["id"]?.Value<string>())
      .Where(id => id != null)
      .ToList();

    var unexpected = exportedIds.Where(id => !ExpectedOriginGuids.Contains(id!)).ToList();

    Assert.True(unexpected.Count == 0,
      $"Unexpected origin GUIDs (pregen/deleted paths leaked?): {string.Join(", ", unexpected)}");
  }

  [Fact]
  public void AllOrigins_HaveChargenHomeworldGroup() {
    var missing = Items
      .Where(item => {
        var groups = item["chargenGroups"] as JObject;

        return groups?["chargenHomeworld"] == null;
      })
      .Select(item => item["id"]?.Value<string>() ?? "(no id)")
      .ToList();

    Assert.True(missing.Count == 0,
      $"Origins without ChargenHomeworld group: {string.Join(", ", missing)}");
  }
}