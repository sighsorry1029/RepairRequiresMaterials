using System;
using System.Collections.Generic;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace RepairRequiresMaterials;

/// <summary>
/// Registers the fixed set of repair-powder prefabs.  The set deliberately does
/// not depend on configuration: every peer must have the same prefab hashes.
/// </summary>
internal static class RepairPowderRegistry
{
    private const string BasePrefabName = "PowderedDragonEgg";
    private const string PrefabNamePrefix = "RRM_RepairPowder_";
    private const int CraftAmount = 4;
    private const int StackSize = 50;
    private const float ItemWeight = 0.1f;

    internal const string RepairMaterialsToken = "$rrm_ui_repair_materials";
    internal const string FieldRepairPowderToken = "$rrm_ui_field_repair_powder";
    internal const string FieldPowderToken = "$rrm_ui_field_powder";
    internal const string NoPowderMappingToken = "$rrm_ui_no_powder_mapping";
    internal const string NoRepairableItemToken = "$rrm_ui_no_repairable_item";
    internal const string NoMaterialsRequiredToken = "$rrm_ui_no_materials_required";
    internal const string DurabilityToken = "$rrm_ui_durability";
    internal const string QualityToken = "$rrm_ui_quality";
    internal const string MissingRequirementToken = "$rrm_ui_missing_requirement";
    internal const string AvailableToken = "$rrm_ui_available";
    internal const string RequiredToken = "$rrm_ui_required";
    internal const string MaterialRepairToken = "$rrm_ui_material_repair";
    internal const string MatchingPowderRequiredToken = "$rrm_ui_matching_powder_required";
    internal const string PowderRequirementToken = "$rrm_ui_powder_requirement";
    internal const string RepairToken = "$rrm_ui_repair";
    internal const string FieldRepairHintToken = "$rrm_ui_field_repair_hint";
    internal const string StationRepairHintToken = "$rrm_ui_station_repair_hint";
    internal const string StationUnavailableToken = "$rrm_ui_station_unavailable";
    internal const string FreeRepairToken = "$rrm_ui_free_repair";
    internal const string PlanChangedToken = "$rrm_ui_plan_changed";

    private static readonly Dictionary<RepairBiome, GameObject> PowderPrefabs = new();

    private static readonly PowderDefinition[] Definitions =
    {
        new(
            RepairBiome.Meadows,
            "Resin",
            4,
            CraftingStations.Workbench,
            1,
            new Color(0.48f, 0.84f, 0.28f),
            "Meadows Repair Powder",
            "목초지 수리 분말",
            "Meadows",
            "목초지"),
        new(
            RepairBiome.BlackForest,
            "Bronze",
            1,
            CraftingStations.Forge,
            1,
            new Color(0.16f, 0.58f, 0.36f),
            "Black Forest Repair Powder",
            "검은 숲 수리 분말",
            "Black Forest",
            "검은 숲"),
        new(
            RepairBiome.Swamp,
            "Iron",
            1,
            CraftingStations.Forge,
            2,
            new Color(0.65f, 0.70f, 0.18f),
            "Swamp Repair Powder",
            "늪 수리 분말",
            "Swamp",
            "늪"),
        new(
            RepairBiome.Ocean,
            "Chitin",
            1,
            CraftingStations.Workbench,
            2,
            new Color(0.18f, 0.58f, 1.00f),
            "Ocean Repair Powder",
            "대양 수리 분말",
            "Ocean",
            "대양"),
        new(
            RepairBiome.Mountain,
            "Obsidian",
            2,
            CraftingStations.Forge,
            3,
            new Color(0.68f, 0.90f, 1.00f),
            "Mountain Repair Powder",
            "산 수리 분말",
            "Mountain",
            "산"),
        new(
            RepairBiome.Plains,
            "BlackMetal",
            1,
            CraftingStations.Forge,
            4,
            new Color(1.00f, 0.70f, 0.16f),
            "Plains Repair Powder",
            "평원 수리 분말",
            "Plains",
            "평원"),
        new(
            RepairBiome.Mistlands,
            "Eitr",
            1,
            CraftingStations.BlackForge,
            1,
            new Color(0.68f, 0.34f, 1.00f),
            "Mistlands Repair Powder",
            "안개 지대 수리 분말",
            "Mistlands",
            "안개 지대"),
        new(
            RepairBiome.AshLands,
            "ProustitePowder",
            1,
            CraftingStations.BlackForge,
            3,
            new Color(1.00f, 0.24f, 0.07f),
            "Ashlands Repair Powder",
            "잿가루 지대 수리 분말",
            "Ashlands",
            "잿가루 지대"),
        new(
            RepairBiome.DeepNorth,
            null,
            0,
            null,
            0,
            new Color(0.82f, 0.97f, 1.00f),
            "Deep North Repair Powder",
            "북부 심층 수리 분말",
            "Deep North",
            "북부 심층")
    };

    private static bool _initialized;
    private static bool _registrationAttempted;
    private static bool _localizationInitialized;

    /// <summary>
    /// Initializes localization and schedules prefab cloning for the point at
    /// which all vanilla prefabs are safe to use as clone sources.
    /// </summary>
    internal static void Initialize()
    {
        InitializeLocalization();

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        PrefabManager.OnVanillaPrefabsAvailable += RegisterPowders;
    }

    /// <summary>
    /// Adds both item and repair-panel translations to Jotunn's localization.
    /// Safe to call more than once.
    /// </summary>
    internal static void InitializeLocalization()
    {
        if (_localizationInitialized)
        {
            return;
        }

        CustomLocalization localization = LocalizationManager.Instance.GetLocalization();

        foreach (PowderDefinition definition in Definitions)
        {
            localization.AddTranslation("English", definition.NameToken, definition.EnglishName);
            localization.AddTranslation("Korean", definition.NameToken, definition.KoreanName);
            localization.AddTranslation(
                "English",
                definition.DescriptionToken,
                $"A repair powder attuned to {definition.EnglishBiome} equipment. It can repair matching equipment away from a crafting station.");
            localization.AddTranslation(
                "Korean",
                definition.DescriptionToken,
                $"{definition.KoreanBiome} 등급 장비와 조율된 수리 분말입니다. 제작대가 없어도 해당 등급 장비를 수리할 수 있습니다.");
        }

        AddUiTranslation(localization, RepairMaterialsToken, "Repair materials", "수리 재료");
        AddUiTranslation(localization, FieldRepairPowderToken, "Field repair powder", "야외 수리 분말");
        AddUiTranslation(localization, FieldPowderToken, "Field repair powder", "야외 수리 분말");
        AddUiTranslation(localization, NoPowderMappingToken, "No repair powder mapping", "수리 분말 매핑 없음");
        AddUiTranslation(localization, NoRepairableItemToken, "No repairable item", "수리할 장비 없음");
        AddUiTranslation(localization, NoMaterialsRequiredToken, "No materials required", "필요한 재료 없음");
        AddUiTranslation(localization, DurabilityToken, "Durability", "내구도");
        AddUiTranslation(localization, QualityToken, "Quality", "품질");
        AddUiTranslation(localization, MissingRequirementToken, "Missing requirement", "요구 재료 부족");
        AddUiTranslation(localization, AvailableToken, "Available", "보유량");
        AddUiTranslation(localization, RequiredToken, "Required", "필요량");
        AddUiTranslation(localization, MaterialRepairToken, "Material repair", "재료 수리");
        AddUiTranslation(localization, MatchingPowderRequiredToken, "Matching repair powder required", "장비 등급에 맞는 수리 분말 필요");
        AddUiTranslation(localization, PowderRequirementToken, "Repair powder", "수리 분말");
        AddUiTranslation(localization, RepairToken, "Repair", "수리");
        AddUiTranslation(
            localization,
            FieldRepairHintToken,
            "No usable crafting station; matching repair powder will be consumed.",
            "사용 가능한 제작대가 없어 장비 등급에 맞는 수리 분말을 소비합니다.");
        AddUiTranslation(
            localization,
            StationRepairHintToken,
            "A usable crafting station is available; repair materials will be consumed.",
            "사용 가능한 제작대가 있어 수리 재료를 소비합니다.");
        AddUiTranslation(
            localization,
            StationUnavailableToken,
            "Required crafting station is unavailable or too low level.",
            "필요한 제작대를 사용할 수 없거나 제작대 레벨이 부족합니다.");
        AddUiTranslation(localization, FreeRepairToken, "Free repair", "무료 수리");
        AddUiTranslation(
            localization,
            PlanChangedToken,
            "Repair requirements changed. Try again.",
            "수리 조건이 변경되었습니다. 다시 시도하세요.");

        _localizationInitialized = true;
    }

    internal static bool TryGetPowderPrefab(RepairBiome biome, out GameObject prefab)
    {
        if (PowderPrefabs.TryGetValue(biome, out GameObject? found) && found != null)
        {
            prefab = found;
            return true;
        }

        prefab = null!;
        return false;
    }

    internal static string GetPowderPrefabName(RepairBiome biome)
    {
        return PrefabNamePrefix + biome;
    }

    internal static bool IsRegistered(RepairBiome biome)
    {
        return TryGetPowderPrefab(biome, out _);
    }

    internal static bool IsRegistered()
    {
        return _registrationAttempted && PowderPrefabs.Count == Definitions.Length;
    }

    private static void RegisterPowders()
    {
        PrefabManager.OnVanillaPrefabsAvailable -= RegisterPowders;

        if (_registrationAttempted)
        {
            return;
        }

        _registrationAttempted = true;

        foreach (PowderDefinition definition in Definitions)
        {
            RegisterPowder(definition);
        }

        if (PowderPrefabs.Count == Definitions.Length)
        {
            RepairRequiresMaterialsPlugin.Log.LogInfo($"Registered all {Definitions.Length} repair powder prefabs.");
        }
        else
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Registered {PowderPrefabs.Count}/{Definitions.Length} repair powder prefabs. See earlier errors for failed clones.");
        }
    }

    private static void RegisterPowder(PowderDefinition definition)
    {
        string prefabName = GetPowderPrefabName(definition.Biome);

        try
        {
            CustomItem? existing = ItemManager.Instance.GetItem(prefabName);
            if (existing != null && existing.ItemPrefab != null)
            {
                PowderPrefabs[definition.Biome] = existing.ItemPrefab;
                return;
            }

            ItemConfig config = CreateItemConfig(definition);
            CustomItem customItem = new(prefabName, BasePrefabName, config);

            if (customItem.ItemPrefab == null || customItem.ItemDrop == null)
            {
                RepairRequiresMaterialsPlugin.Log.LogError(
                    $"Could not clone repair powder '{prefabName}' from '{BasePrefabName}'.");
                return;
            }

            ItemDrop.ItemData.SharedData shared = customItem.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material;

            IsolateAndTintVisuals(customItem.ItemPrefab, definition.Tint, prefabName);
            TryAssignRenderedIcon(customItem.ItemPrefab, customItem.ItemDrop, definition.Biome);

            if (!ItemManager.Instance.AddItem(customItem))
            {
                RepairRequiresMaterialsPlugin.Log.LogError($"Jotunn rejected repair powder item '{prefabName}'.");
                return;
            }

            PowderPrefabs[definition.Biome] = customItem.ItemPrefab;
        }
        catch (Exception ex)
        {
            RepairRequiresMaterialsPlugin.Log.LogError(
                $"Failed to register repair powder '{prefabName}' from '{BasePrefabName}': {ex}");
        }
    }

    private static ItemConfig CreateItemConfig(PowderDefinition definition)
    {
        ItemConfig config = new()
        {
            Name = definition.NameToken,
            Description = definition.DescriptionToken,
            Amount = CraftAmount,
            StackSize = StackSize,
            Weight = ItemWeight,
            Enabled = true
        };

        // Deep North is registered for stable prefab hashes but intentionally
        // has no active recipe until that biome has progression materials.
        if (!string.IsNullOrEmpty(definition.Ingredient) && !string.IsNullOrEmpty(definition.CraftingStation))
        {
            config.CraftingStation = definition.CraftingStation;
            config.MinStationLevel = definition.MinStationLevel;
            config.AddRequirement(new RequirementConfig(definition.Ingredient, definition.IngredientAmount));
        }

        return config;
    }

    private static void IsolateAndTintVisuals(GameObject prefab, Color tint, string prefabName)
    {
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; ++rendererIndex)
        {
            Renderer renderer = renderers[rendererIndex];
            Material[] sourceMaterials = renderer.sharedMaterials;
            Material[] isolatedMaterials = new Material[sourceMaterials.Length];

            for (int materialIndex = 0; materialIndex < sourceMaterials.Length; ++materialIndex)
            {
                Material source = sourceMaterials[materialIndex];
                if (source == null)
                {
                    isolatedMaterials[materialIndex] = null!;
                    continue;
                }

                Material isolated = new(source)
                {
                    name = $"{source.name}_{prefabName}_{rendererIndex}_{materialIndex}"
                };

                TintMaterial(isolated, tint);
                isolatedMaterials[materialIndex] = isolated;
            }

            renderer.sharedMaterials = isolatedMaterials;
        }

        ParticleSystem[] particleSystems = prefab.GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem particleSystem in particleSystems)
        {
            ParticleSystem.MainModule main = particleSystem.main;
            main.startColor = new ParticleSystem.MinMaxGradient(tint);
        }
    }

    private static void TintMaterial(Material material, Color tint)
    {
        if (material.HasProperty("_Color"))
        {
            Color original = material.GetColor("_Color");
            Color blended = Color.Lerp(original, tint, 0.68f);
            blended.a = original.a;
            material.SetColor("_Color", blended);
        }

        if (material.HasProperty("_BaseColor"))
        {
            Color original = material.GetColor("_BaseColor");
            Color blended = Color.Lerp(original, tint, 0.68f);
            blended.a = original.a;
            material.SetColor("_BaseColor", blended);
        }

        if (material.HasProperty("_EmissionColor"))
        {
            Color original = material.GetColor("_EmissionColor");
            float intensity = Mathf.Max(1f, Mathf.Max(original.r, Mathf.Max(original.g, original.b)));
            Color emission = tint * intensity;
            emission.a = original.a;
            material.SetColor("_EmissionColor", emission);
        }
    }

    private static void TryAssignRenderedIcon(GameObject prefab, ItemDrop itemDrop, RepairBiome biome)
    {
        try
        {
            RenderManager.RenderRequest request = new(prefab)
            {
                Width = 128,
                Height = 128,
                Rotation = RenderManager.IsometricRotation,
                ParticleSimulationTime = 1f,
                UseCache = false
            };

            Sprite rendered = RenderManager.Instance.Render(request);
            if (rendered != null && rendered.rect.width > 1f && rendered.rect.height > 1f)
            {
                itemDrop.m_itemData.m_shared.m_icons = new[] { rendered };
            }
            else
            {
                RepairRequiresMaterialsPlugin.Log.LogDebug(
                    $"Rendered icon for {biome} repair powder was empty; retaining the inherited icon.");
            }
        }
        catch (Exception ex)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not render a unique icon for {biome} repair powder; retaining the inherited icon: {ex.Message}");
        }
    }

    private static void AddUiTranslation(
        CustomLocalization localization,
        string token,
        string english,
        string korean)
    {
        localization.AddTranslation("English", token, english);
        localization.AddTranslation("Korean", token, korean);
    }

    private sealed class PowderDefinition
    {
        internal PowderDefinition(
            RepairBiome biome,
            string? ingredient,
            int ingredientAmount,
            string? craftingStation,
            int minStationLevel,
            Color tint,
            string englishName,
            string koreanName,
            string englishBiome,
            string koreanBiome)
        {
            Biome = biome;
            Ingredient = ingredient;
            IngredientAmount = ingredientAmount;
            CraftingStation = craftingStation;
            MinStationLevel = minStationLevel;
            Tint = tint;
            EnglishName = englishName;
            KoreanName = koreanName;
            EnglishBiome = englishBiome;
            KoreanBiome = koreanBiome;
        }

        internal RepairBiome Biome { get; }
        internal string? Ingredient { get; }
        internal int IngredientAmount { get; }
        internal string? CraftingStation { get; }
        internal int MinStationLevel { get; }
        internal Color Tint { get; }
        internal string EnglishName { get; }
        internal string KoreanName { get; }
        internal string EnglishBiome { get; }
        internal string KoreanBiome { get; }

        internal string NameToken => "$rrm_item_repairpowder_" + Biome.ToString().ToLowerInvariant();
        internal string DescriptionToken => NameToken + "_description";
    }
}
