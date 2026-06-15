// Cross-file integrity checks: prerequisite GUIDs, encyclopedia link keys, companion equipment IDs.

using System.Text.RegularExpressions;
using ExportValidator.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExportValidator.Tests;

public class CrossReferenceTests {
  private static readonly JArray Features = ExportLoader.LoadItems("features");
  private static readonly JArray Encyclopedia = ExportLoader.LoadItems("encyclopedia");
  private static readonly JArray Companions = ExportLoader.LoadItems("companions");
  private static readonly JArray Weapons = ExportLoader.LoadItems("weapons");
  private static readonly JArray Armor = ExportLoader.LoadItems("armor");

  /**
   * Encyclopedia keys that are referenced in feature descriptions but genuinely absent
   * from the game's encyclopedia at runtime (hidden, removed, or engine-internal entries).
   * Update this set when a game patch adds or removes entries.
   */
  private static readonly HashSet<string> KnownMissingEncyclopediaKeys = [
    "Castigating", "Devastating", "HitSequenceGlossary", "Infusing", "Opening", "ParryReduction", "Prey",
  ];

  /**
   * Companion starting equipment item IDs known to reference internal test/pregen items.
   */
  private static readonly HashSet<string> KnownMissingEquipmentIds = [
    "ca7fd37ca4724b9cb2a37d2fb21c45a5", // Solomorne -- internal item
    "8613568ce9e94816a2df0031d1216fa6", // Uralon the Cruel -- pregen test item
    "2aa0511b34c04914982bf76465f61e6a", // Uralon the Cruel -- pregen test item
  ];

  [Fact]
  public void FeaturePrerequisites_RequiredFeatureIds_AreResolvable() {
    var featureIds = Features
      .Select(feature => feature["id"]?.Value<string>())
      .Where(id => id != null)
      .ToHashSet();

    // Career paths appear as prerequisites ("must have taken this career") but live in careers.json.
    // Origin paths may similarly appear for companion-specific features.
    var careerIds = ExportLoader.LoadItems("careers")
      .Select(career => career["id"]?.Value<string>())
      .Where(id => id != null)
      .ToHashSet();

    var originIds = ExportLoader.LoadItems("origins")
      .Select(origin => origin["id"]?.Value<string>())
      .Where(id => id != null)
      .ToHashSet();

    var allKnownIds = featureIds.Concat(careerIds).Concat(originIds).ToHashSet();

    var unresolvable = Features
      .SelectMany(feature => feature["prerequisites"] as JArray ?? new JArray())
      .Select(prereq => prereq["requiredFeatureId"]?.Value<string>())
      .Where(id => id != null)
      .Where(id => !allKnownIds.Contains(id!))
      .Distinct()
      .ToList();

    Assert.True(unresolvable.Count == 0,
      $"Unresolvable prerequisite GUIDs (not in features.json, careers.json, or origins.json): " +
      $"{string.Join(", ", unresolvable)}");
  }

  [Fact]
  public void FeatureDescriptions_EncyclopediaLinks_AreResolvable() {
    var encyclopediaKeys = Encyclopedia
      .Select(entry => entry["key"]?.Value<string>())
      .Where(key => key != null)
      .ToHashSet();

    var allDescriptions = Features
      .Select(feature => feature["description"]?.Value<string>() ?? "")
      .ToList();

    var referencedKeys = allDescriptions
      .SelectMany(description => Regex.Matches(description, @"Encyclopedia:([^""]+)"""))
      .Select(match => match.Groups[1].Value)
      .Distinct();

    var newlyMissing = referencedKeys
      .Where(key => !encyclopediaKeys.Contains(key) && !KnownMissingEncyclopediaKeys.Contains(key))
      .ToList();

    Assert.True(newlyMissing.Count == 0,
      $"New encyclopedia keys referenced in features but absent from encyclopedia.json " +
      $"(add to allowlist if genuinely hidden): {string.Join(", ", newlyMissing)}");
  }

  [Fact]
  public void Companions_StartingEquipment_ItemIds_AreResolvable() {
    var allItemIds = Weapons
      .Concat(Armor)
      .Select(item => item["assetGuid"]?.Value<string>())
      .Where(id => id != null)
      .ToHashSet();

    var equipmentFields = new[] {
      "primaryWeaponId", "secondaryWeaponId", "primaryWeaponAlt1Id", "armorId",
    };

    var newlyMissing = Companions
      .SelectMany(companion => {
        var equipment = companion["startingEquipment"] as JObject;
        var companionName = companion["name"]?.Value<string>() ?? "(unnamed)";

        return equipmentFields
          .Select(field => (companionName, id: equipment?[field]?.Value<string>()))
          .Where(entry => entry.id != null);
      })
      .Where(entry =>
        !allItemIds.Contains(entry.id!) &&
        !KnownMissingEquipmentIds.Contains(entry.id!))
      .ToList();

    Assert.True(newlyMissing.Count == 0,
      $"New unresolvable equipment IDs (add to allowlist if internal items): " +
      $"{string.Join(", ", newlyMissing.Select(entry => $"{entry.companionName}:{entry.id}"))}");
  }
}