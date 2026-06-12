// Exports item icons, feature icons, and companion portraits as PNG files to the output directory.

using System.Collections;
using System.Reflection;
using BlueprintExtractor.Extraction;
using BlueprintExtractor.Infrastructure;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items.Equipment;
using Kingmaker.UI.Models.Tooltip.Base;
using Kingmaker.UnitLogic.Progression.Features;
using UnityEngine;

namespace BlueprintExtractor.Exporters;

/**
 * Exports sprite icons for items and features as PNG files, one per blueprint GUID.
 * Companion portrait export (small/half/full) runs separately since portraits use the addressable
 * asset system and may not be ready during BlueprintsCache.Init.
 * 
 * Output layout:
 * icons/items/{id}.png           — item icons (named by blueprint AssetGuid)
 * icons/features/{id}.png        — feature/ability icons
 * icons/talentGroups/{name}.png  — per-group fallback icons (e.g. Occupation, Homeworld)
 * portraits/{id}/                — companion portrait variants (small.png, half.png, full.png)
 */
public static class IconExporter {
  private const string Source = "icons";
  private const BindingFlags AllInstanceFlags = ReflectionHelpers.AllInstanceFlags;

  /**
   * Exports item and feature icons. Safe to call at BlueprintsCache.Init time since icons are
   * direct sprite references loaded alongside their blueprints.
   */
  public static void ExportIcons(ModLogger logger, string outputDirectory) {
    var itemIconsDir = Path.Combine(outputDirectory, "icons", "items");
    var featureIconsDir = Path.Combine(outputDirectory, "icons", "features");
    var groupIconsDir = Path.Combine(outputDirectory, "icons", "talentGroups");
    Directory.CreateDirectory(itemIconsDir);
    Directory.CreateDirectory(featureIconsDir);
    Directory.CreateDirectory(groupIconsDir);

    var talentGroupIcons = BuildTalentGroupIconMap();
    var groupCount = ExportTalentGroupIcons(talentGroupIcons, groupIconsDir);
    var itemCount = ExportItemIcons(itemIconsDir);
    var featureCount = ExportFeatureIcons(featureIconsDir, talentGroupIcons);

    logger.Result(Source, "icon export done", ("items", itemCount), ("features", featureCount), ("groups", groupCount));
  }

  /**
   * Exports companion portraits (small, half, full). Must be called after assets are fully loaded
   * (e.g. from OnGUI after the main menu is ready), since portraits are addressable assets.
   */
  public static void ExportPortraits(ModLogger logger, string outputDirectory, HashSet<string> exportedCompanionGuids) {
    var portraitsDir = Path.Combine(outputDirectory, "portraits");
    Directory.CreateDirectory(portraitsDir);

    var successCount = 0;
    var failCount = 0;

    foreach (var unit in BlueprintsCatalog.AllBlueprints<BlueprintUnit>()) {
      if (!UnitFilter.IsBaseCompanionUnit(unit)) continue;
      if (!exportedCompanionGuids.Contains(unit.AssetGuid)) continue;

      try {
        var portrait = ResolvePortrait(unit);

        if (portrait == null) {
          failCount++;

          continue;
        }

        var companionDir = Path.Combine(portraitsDir, unit.AssetGuid);
        Directory.CreateDirectory(companionDir);

        var savedAny = false;
        savedAny |= TextureExtractor.SaveSpriteToPng(portrait.SmallPortrait, Path.Combine(companionDir, "small.png"));
        savedAny |= TextureExtractor.SaveSpriteToPng(portrait.HalfLengthPortrait,
          Path.Combine(companionDir, "half.png"));
        savedAny |= TextureExtractor.SaveSpriteToPng(portrait.FullLengthPortrait,
          Path.Combine(companionDir, "full.png"));

        if (savedAny) {
          successCount++;
        }
        else {
          failCount++;
        }
      }
      catch (Exception exception) {
        logger.Warn(Source, $"portrait failed guid={unit.AssetGuid} reason={exception.Message}");
        failCount++;
      }
    }

    logger.Result(Source, "portrait export done", ("saved", successCount), ("failed", failCount));
  }

  /**
   * Builds a map of TalentGroup enum value name → Sprite by walking
   * BlueprintRoot.Instance → m_UIConfig (UIConfig) → TalentGroups → Groups.
   */
  private static Dictionary<string, Sprite> BuildTalentGroupIconMap() {
    var result = new Dictionary<string, Sprite>();

    try {
      var rootType = AppDomain.CurrentDomain.GetAssemblies()
        .SelectMany(assembly => {
          try {
            return assembly.GetTypes();
          }
          catch {
            return Type.EmptyTypes;
          }
        })
        .FirstOrDefault(type => type.Name == "BlueprintRoot");

      if (rootType == null) return result;

      var instanceProp = rootType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
      var rootInstance = instanceProp?.GetValue(null);

      if (rootInstance == null) return result;

      var uiConfigRef = rootType.GetField("m_UIConfig", AllInstanceFlags)?.GetValue(rootInstance);
      var uiConfig = RankEntryExtractor.Dereference(uiConfigRef);

      if (uiConfig == null) return result;

      var talentGroups = uiConfig.GetType().GetField("TalentGroups", AllInstanceFlags)?.GetValue(uiConfig);

      if (talentGroups == null) return result;

      var groupsField = talentGroups.GetType().GetField("Groups", AllInstanceFlags);

      if (groupsField?.GetValue(talentGroups) is not IEnumerable groupsList) return result;

      var groupField = (FieldInfo)null;
      var iconField = (FieldInfo)null;

      foreach (var entry in groupsList.Cast<object>()) {
        if (entry == null) continue;
        groupField ??= entry.GetType().GetField("Group", AllInstanceFlags);
        iconField ??= entry.GetType().GetField("Icon", AllInstanceFlags);

        if (groupField == null || iconField == null) continue;

        var groupName = groupField.GetValue(entry)?.ToString();
        var sprite = iconField.GetValue(entry) as Sprite;
        if (groupName != null && sprite != null) result[groupName] = sprite;
      }
    }
    catch { }

    return result;
  }

  private static int ExportTalentGroupIcons(Dictionary<string, Sprite> groupIcons, string iconsDir) {
    var count = 0;

    foreach (var pair in groupIcons) {
      var outputPath = Path.Combine(iconsDir, $"{pair.Key}.png");
      if (TextureExtractor.SaveSpriteToPng(pair.Value, outputPath, 64, 64)) count++;
    }

    return count;
  }

  private static int ExportItemIcons(string iconsDir) {
    var count = 0;

    foreach (var item in BlueprintsCatalog.AllBlueprints<BlueprintItemEquipment>())
      try {
        if (item is not IUIDataProvider uiData) continue;

        if (!ItemFilter.IsValidName(uiData.Name)) continue;

        var icon = uiData.Icon ?? ResolveIconField(item);

        if (icon == null) continue;

        var outputPath = Path.Combine(iconsDir, $"{item.AssetGuid}.png");

        if (TextureExtractor.SaveSpriteToPng(icon, outputPath, 128, 128)) count++;
      }
      catch { }

    return count;
  }

  private static int ExportFeatureIcons(string iconsDir, Dictionary<string, Sprite> talentGroupIcons) {
    var count = 0;

    foreach (var feature in BlueprintsCatalog.AllBlueprints<BlueprintFeature>())
      try {
        if (feature is not IUIDataProvider uiData) continue;
        if (!ItemFilter.IsValidName(uiData.Name)) continue;

        var icon = uiData.Icon ?? ResolveIconField(feature) ?? ResolveTalentGroupIcon(feature, talentGroupIcons);

        if (icon == null) continue;

        var outputPath = Path.Combine(iconsDir, $"{feature.AssetGuid}.png");

        if (TextureExtractor.SaveSpriteToPng(icon, outputPath, 128, 128)) count++;
      }
      catch { }

    return count;
  }

  /**
   * Resolves the icon for a feature that has no m_Icon by looking up its TalentIconInfo.MainGroup
   * in the UIConfig TalentGroups map (e.g. Occupation, Homeworld).
   */
  private static Sprite ResolveTalentGroupIcon(BlueprintFeature feature, Dictionary<string, Sprite> talentGroupIcons) {
    if (talentGroupIcons.Count == 0) return null;

    var talentIconInfoField = feature.GetType().GetField("TalentIconInfo", AllInstanceFlags);
    var talentIconInfo = talentIconInfoField?.GetValue(feature);

    if (talentIconInfo == null) return null;

    var mainGroupField = talentIconInfo.GetType().GetField("MainGroup", AllInstanceFlags);
    var mainGroupName = mainGroupField?.GetValue(talentIconInfo)?.ToString();

    if (mainGroupName == null) return null;

    talentGroupIcons.TryGetValue(mainGroupName, out var sprite);

    return sprite;
  }

  /**
   * Fallback icon resolution via direct reflection on m_Icon when IUIDataProvider.Icon returns null.
   * Some features (e.g. occupation features) store their sprite in m_Icon but don't surface it
   * through the interface -- likely because the sprite is loaded from an addressable bundle.
   */
  private static Sprite ResolveIconField(object blueprint) {
    var iconField = blueprint.GetType().GetField("m_Icon", AllInstanceFlags);

    return iconField?.GetValue(blueprint) as Sprite;
  }

  private static BlueprintPortrait ResolvePortrait(BlueprintUnit unit) {
    var portraitField = unit.GetType().GetField("m_Portrait", AllInstanceFlags);
    var portraitRef = portraitField?.GetValue(unit);

    if (portraitRef == null) return null;

    return RankEntryExtractor.Dereference(portraitRef) as BlueprintPortrait;
  }
}