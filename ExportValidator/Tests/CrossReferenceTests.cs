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
   * Prerequisite GUIDs that reference blueprints not in features.json by design --
   * career paths used as prerequisites, or internal engine features filtered at export.
   * Update this set when the game changes career structures.
   */
  private static readonly HashSet<string> KnownUnresolvablePrerequisiteGuids = [
    "21b0fc8cfbe940ecbef0114d5d27b44a", "33725d84e95e4323ac46d8fbf899b250",
    "35d391c624b34b1e9f19c493005158a1", "36739347ec3144daac751b48c51d1e6a",
    "37363b46061e46218f40e01ed81e9189", "3a23630530bc4d058ef2d209f5a739a4",
    "3c62693889c14328acd5a1fc19c66b5a", "777d9f9c570443b59120e78f2d9dd515",
    "899021d524224469affd02f756d60fdb", "9b090810169e4a42b22afd5995d3720d",
    "a69ab12837ae4bfea6bb56f834892d7f", "a6e871aa095f4a1fa813fab77658ab78",
    "aa932c209cdd43c9bb749d5380fc126e", "abe45adeb7d7415ca96df8fc6cd1acd2",
    "affa5fdded7e404b910b990f5d344a8c", "b53037d92c984cf3921df309241e48ca",
    "b6962fcc54054af98961dd9a6c0f9e18", "b901e045ca514d53bae43f4a9ecdf0b4",
    "c2ad04ea9a394a84b5c8485f66d83d2b", "d7953c4cbf47463090ee3025ef390063",
    "f95e3d9a049345ec918926f092ec67e2",
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

    var newlyUnresolvable = Features
      .SelectMany(feature => feature["prerequisites"] as JArray ?? new JArray())
      .Select(prereq => prereq["requiredFeatureId"]?.Value<string>())
      .Where(id => id != null)
      .Where(id => !featureIds.Contains(id!) && !KnownUnresolvablePrerequisiteGuids.Contains(id!))
      .Distinct()
      .ToList();

    Assert.True(newlyUnresolvable.Count == 0,
      $"New unresolvable prerequisite GUIDs (add to allowlist if expected): " +
      $"{string.Join(", ", newlyUnresolvable)}");
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