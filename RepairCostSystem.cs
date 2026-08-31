using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RepairRequiresMaterials;

internal enum RepairPaymentKind
{
    Free,
    StationMaterials,
    CraftingSkillFree
}

internal sealed class RepairPreview
{
    internal RepairPreview(
        ItemDrop.ItemData item,
        RepairPaymentKind paymentKind,
        IReadOnlyList<RepairMaterialCost> costs,
        int durabilityBucketPercent,
        bool usesNearbyContainers,
        bool hasRawMaterialCost = false,
        string repairCostRoundingToken = "",
        string skillFreeTicketToken = "")
    {
        Item = item;
        PaymentKind = paymentKind;
        Costs = costs;
        DurabilityBucketPercent = durabilityBucketPercent;
        UsesNearbyContainers = usesNearbyContainers;
        HasRawMaterialCost = hasRawMaterialCost;
        RepairCostRoundingToken = repairCostRoundingToken;
        SkillFreeTicketToken = skillFreeTicketToken;
    }

    internal ItemDrop.ItemData Item { get; }
    internal RepairPaymentKind PaymentKind { get; }
    internal IReadOnlyList<RepairMaterialCost> Costs { get; }
    internal int DurabilityBucketPercent { get; }
    internal bool UsesNearbyContainers { get; }
    internal bool HasRawMaterialCost { get; }
    internal string RepairCostRoundingToken { get; }
    internal string SkillFreeTicketToken { get; }

    internal RepairPreview WithPayment(RepairPaymentKind paymentKind, string ticketToken)
    {
        return new RepairPreview(
            Item,
            paymentKind,
            Costs,
            DurabilityBucketPercent,
            UsesNearbyContainers,
            HasRawMaterialCost,
            RepairCostRoundingToken,
            ticketToken);
    }

    internal bool HasSamePaymentPlan(RepairPreview other)
    {
        if (!ReferenceEquals(Item, other.Item)
            || PaymentKind != other.PaymentKind
            || DurabilityBucketPercent != other.DurabilityBucketPercent
            || HasRawMaterialCost != other.HasRawMaterialCost
            || !string.Equals(RepairCostRoundingToken, other.RepairCostRoundingToken, StringComparison.Ordinal)
            || !string.Equals(SkillFreeTicketToken, other.SkillFreeTicketToken, StringComparison.Ordinal)
            || (PaymentKind != RepairPaymentKind.CraftingSkillFree
                && UsesNearbyContainers != other.UsesNearbyContainers)
            || Costs.Count != other.Costs.Count)
        {
            return false;
        }

        for (int i = 0; i < Costs.Count; ++i)
        {
            RepairMaterialCost left = Costs[i];
            RepairMaterialCost right = other.Costs[i];
            if (!string.Equals(left.ResourcePrefabName, right.ResourcePrefabName, StringComparison.Ordinal)
                || left.RequiredAmount != right.RequiredAmount)
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class RepairMaterialCost
{
    internal RepairMaterialCost(
        Piece.Requirement sourceRequirement,
        int requiredAmount,
        int availableAmount,
        string resourcePrefabName)
    {
        SourceRequirement = sourceRequirement;
        RequiredAmount = requiredAmount;
        AvailableAmount = availableAmount;
        ResourcePrefabName = resourcePrefabName;
    }

    internal Piece.Requirement SourceRequirement { get; }
    internal int RequiredAmount { get; }
    internal int AvailableAmount { get; set; }
    internal string ResourcePrefabName { get; }
    internal bool IsAffordable => AvailableAmount >= RequiredAmount;
}

internal static class RepairCostSystem
{
    private const string OnlyOneIngredientRoundingKey = "#only-one-ingredient-group";
    private static volatile PrefabPatternMatcher _blacklistedMaterialPrefabs = PrefabPatternMatcher.Empty;

    private sealed class AggregatedRequirement
    {
        internal AggregatedRequirement(
            Piece.Requirement sourceRequirement,
            long baseRecipeAmount,
            long qualityIncrementRecipeAmount,
            string prefabName)
        {
            SourceRequirement = sourceRequirement;
            BaseRecipeAmount = baseRecipeAmount;
            QualityIncrementRecipeAmount = qualityIncrementRecipeAmount;
            PrefabName = prefabName;
        }

        internal Piece.Requirement SourceRequirement { get; }
        internal long BaseRecipeAmount { get; set; }
        internal long QualityIncrementRecipeAmount { get; set; }
        internal string PrefabName { get; }
    }

    private static readonly List<ItemDrop.ItemData> WornItems = new();

    internal static void SetBlacklistedPrefabPatterns(string? patterns)
    {
        _blacklistedMaterialPrefabs = PrefabPatternMatcher.Parse(patterns);
    }

    internal static void GetRepairableItems(Player player, List<ItemDrop.ItemData> results)
    {
        results.Clear();
        if ((Object)(object)player == null)
        {
            return;
        }

        CraftingStation? station = player.GetCurrentCraftingStation();
        WornItems.Clear();
        player.GetInventory().GetWornItems(WornItems);

        foreach (ItemDrop.ItemData item in WornItems)
        {
            if (CanBuildPaymentPlan(player, item, station))
            {
                results.Add(item);
            }
        }
    }

    internal static bool TryGetRepairPreview(Player player, ItemDrop.ItemData item, out RepairPreview? preview)
    {
        preview = null;
        if (!CanRepairStructurally(player, item))
        {
            return false;
        }

        if (player.NoCostCheat())
        {
            preview = new RepairPreview(
                item,
                RepairPaymentKind.Free,
                Array.Empty<RepairMaterialCost>(),
                GetDurabilityBucketPercent(item),
                usesNearbyContainers: false);
            return true;
        }

        CraftingStation? station = player.GetCurrentCraftingStation();
        if (station == null)
        {
            return false;
        }

        Recipe? recipe = FindStationRepairRecipe(player, item, station);
        if (recipe == null)
        {
            return false;
        }

        RepairPreview? stationPreview = BuildStationPreview(player, item, recipe);
        if (stationPreview == null)
        {
            return false;
        }

        preview = CraftingFreeRepairSystem.ResolvePreview(player, stationPreview);
        return true;
    }

    internal static bool CanAfford(Player player, RepairPreview preview)
    {
        if (player.NoCostCheat())
        {
            return true;
        }

        return preview.PaymentKind switch
        {
            RepairPaymentKind.Free => true,
            RepairPaymentKind.CraftingSkillFree => true,
            RepairPaymentKind.StationMaterials => preview.Costs.All(cost => cost.IsAffordable),
            _ => false
        };
    }

    internal static bool CanRepairStructurally(Player player, ItemDrop.ItemData item)
    {
        if ((Object)(object)player == null
            || item == null
            || !player.GetInventory().ContainsItem(item)
            || !item.m_shared.m_useDurability
            || !item.m_shared.m_canBeReparied)
        {
            return false;
        }

        float maxDurability = item.GetMaxDurability();
        return maxDurability > 0f && item.m_durability < maxDurability;
    }

    internal static bool CanRepairAtStation(
        Player player,
        ItemDrop.ItemData item,
        CraftingStation? station,
        Recipe recipe)
    {
        if (station == null
            || recipe == null
            || !station.CheckUsable(player, false)
            || ((Object)(object)recipe.m_craftingStation == null && (Object)(object)recipe.m_repairStation == null))
        {
            return false;
        }

        bool validStation = (recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name)
            || (recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == station.m_name)
            || item.m_worldLevel < Game.m_worldLevel;

        return validStation && Mathf.Min(station.GetLevel(), 4) >= recipe.m_minStationLevel;
    }

    private static bool CanBuildPaymentPlan(Player player, ItemDrop.ItemData item, CraftingStation? station)
    {
        if (!CanRepairStructurally(player, item))
        {
            return false;
        }

        if (player.NoCostCheat())
        {
            return true;
        }

        return station != null && FindStationRepairRecipe(player, item, station) != null;
    }

    private static Recipe? FindStationRepairRecipe(
        Player player,
        ItemDrop.ItemData item,
        CraftingStation station)
    {
        IReadOnlyList<Recipe> exactRecipes = RepairRecipeCatalog.GetRecipes(item);
        bool hasEnabledExactRecipe = false;

        foreach (Recipe recipe in exactRecipes)
        {
            if (recipe == null || !recipe.m_enabled)
            {
                continue;
            }

            hasEnabledExactRecipe = true;
            if (CanUseMaterialRepairRecipe(item, recipe)
                && CanRepairAtStation(player, item, station, recipe))
            {
                return recipe;
            }
        }

        if (!hasEnabledExactRecipe)
        {
            foreach (Recipe recipe in exactRecipes)
            {
                if (recipe != null
                    && !recipe.m_enabled
                    && CanUseMaterialRepairRecipe(item, recipe)
                    && CanRepairAtStation(player, item, station, recipe))
                {
                    return recipe;
                }
            }
        }

        // ItemData normally carries m_dropPrefab. Retain Valheim's shared-name
        // lookup only for unusual items without an exact prefab recipe.
        if (exactRecipes.Count == 0 && item.m_dropPrefab == null && ObjectDB.instance != null)
        {
            Recipe? fallback = ObjectDB.instance.GetRecipe(item);
            if (fallback != null
                && CanUseMaterialRepairRecipe(item, fallback)
                && CanRepairAtStation(player, item, station, fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private static bool CanUseMaterialRepairRecipe(ItemDrop.ItemData item, Recipe recipe)
    {
        string itemPrefabName = CleanPrefabName(item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty);
        string itemSharedName = item.m_shared.m_name;
        bool hasAllowedMaterial = false;
        foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
        {
            if (requirement?.m_resItem == null
                || (GetBaseRecipeAmount(requirement) <= 0
                    && GetQualityIncrementRecipeAmount(requirement, item.m_quality) <= 0))
            {
                continue;
            }

            string resourcePrefabName = ResolveItemDropPrefabName(requirement.m_resItem);
            string resourceSharedName = requirement.m_resItem.m_itemData.m_shared.m_name;
            if ((itemPrefabName.Length > 0
                 && string.Equals(resourcePrefabName, itemPrefabName, StringComparison.Ordinal))
                || string.Equals(resourceSharedName, itemSharedName, StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsExcludedRepairMaterial(
                    requirement.m_resItem.m_itemData.m_shared.m_itemType,
                    resourcePrefabName))
            {
                hasAllowedMaterial = true;
            }
        }

        return hasAllowedMaterial;
    }

    private static RepairPreview? BuildStationPreview(
        Player player,
        ItemDrop.ItemData item,
        Recipe recipe)
    {
        int bucketPercent = GetDurabilityBucketPercent(item);
        int missingDurabilityPercent = 100 - bucketPercent;
        double baseMaterialPercent = Mathf.Clamp(
            RepairRequiresMaterialsPlugin.BaseMaterialCostPercent.Value,
            0f,
            100f);
        double qualityIncrementMaterialPercent = Mathf.Clamp(
            RepairRequiresMaterialsPlugin.QualityIncrementMaterialCostPercent.Value,
            0f,
            100f);

        Piece.Requirement[] recipeResources = recipe.m_resources ?? Array.Empty<Piece.Requirement>();
        Dictionary<string, AggregatedRequirement> requirementsByName = new(StringComparer.Ordinal);
        List<AggregatedRequirement> orderedRequirements = new(recipeResources.Length);

        foreach (Piece.Requirement requirement in recipeResources)
        {
            if (requirement?.m_resItem == null)
            {
                continue;
            }

            long baseRecipeAmount = GetBaseRecipeAmount(requirement);
            long qualityIncrementRecipeAmount = GetQualityIncrementRecipeAmount(requirement, item.m_quality);
            if (baseRecipeAmount <= 0 && qualityIncrementRecipeAmount <= 0)
            {
                continue;
            }

            string resourcePrefabName = ResolveItemDropPrefabName(requirement.m_resItem);
            if (IsExcludedRepairMaterial(
                    requirement.m_resItem.m_itemData.m_shared.m_itemType,
                    resourcePrefabName))
            {
                continue;
            }

            string resourceName = requirement.m_resItem.m_itemData.m_shared.m_name;
            if (recipe.m_requireOnlyOneIngredient)
            {
                orderedRequirements.Add(
                    new AggregatedRequirement(
                        requirement,
                        baseRecipeAmount,
                        qualityIncrementRecipeAmount,
                        resourcePrefabName));
                continue;
            }

            if (requirementsByName.TryGetValue(resourceName, out AggregatedRequirement? existing))
            {
                existing.BaseRecipeAmount += baseRecipeAmount;
                existing.QualityIncrementRecipeAmount += qualityIncrementRecipeAmount;
                continue;
            }

            AggregatedRequirement aggregated = new(
                requirement,
                baseRecipeAmount,
                qualityIncrementRecipeAmount,
                resourcePrefabName);
            requirementsByName.Add(resourceName, aggregated);
            orderedRequirements.Add(aggregated);
        }

        if (orderedRequirements.Count == 0)
        {
            return null;
        }

        bool useNearbyContainers = AzuCraftyBoxesCompat.ShouldUseNearbyContainers();
        bool nearbyCountFailed = false;
        List<RepairMaterialCost> costs = new(orderedRequirements.Count);
        List<(RepairMaterialCost Cost, bool HasRawCost)>? alternatives = recipe.m_requireOnlyOneIngredient
            ? new List<(RepairMaterialCost Cost, bool HasRawCost)>(orderedRequirements.Count)
            : null;
        RepairCostRoundingContext? roundingContext = null;
        bool selectedPlanHasRawCost = false;

        foreach (AggregatedRequirement aggregated in orderedRequirements)
        {
            double rawRepairAmount = CalculateRawRepairAmount(
                aggregated.BaseRecipeAmount,
                aggregated.QualityIncrementRecipeAmount,
                baseMaterialPercent,
                qualityIncrementMaterialPercent,
                missingDurabilityPercent);
            bool hasRawCost = rawRepairAmount > 0d;
            if (hasRawCost)
            {
                roundingContext ??= RepairCostRoundingSystem.CreateContext(item, player.GetInventory());
                selectedPlanHasRawCost = true;
            }

            string roundingKey = recipe.m_requireOnlyOneIngredient
                ? OnlyOneIngredientRoundingKey
                : aggregated.SourceRequirement.m_resItem.m_itemData.m_shared.m_name;
            int requiredAmount = hasRawCost
                ? roundingContext!.Round(rawRepairAmount, roundingKey)
                : 0;
            if (requiredAmount <= 0)
            {
                if (recipe.m_requireOnlyOneIngredient)
                {
                    alternatives!.Add((new RepairMaterialCost(
                        aggregated.SourceRequirement,
                        requiredAmount: 0,
                        availableAmount: 0,
                        aggregated.PrefabName), hasRawCost));
                }

                continue;
            }

            Piece.Requirement requirement = aggregated.SourceRequirement;
            int inventoryAmount = CountInventory(player, requirement);
            RepairMaterialCost cost = new(
                requirement,
                requiredAmount,
                inventoryAmount,
                aggregated.PrefabName);
            int availableAmount = inventoryAmount;
            if (useNearbyContainers
                && !AzuCraftyBoxesCompat.TryCountAvailable(player, cost, inventoryAmount, out availableAmount))
            {
                useNearbyContainers = false;
                nearbyCountFailed = true;
                availableAmount = inventoryAmount;
            }

            cost.AvailableAmount = availableAmount;
            if (alternatives != null)
            {
                alternatives.Add((cost, hasRawCost));
            }
            else
            {
                costs.Add(cost);
            }
        }

        if (nearbyCountFailed)
        {
            IEnumerable<RepairMaterialCost> countedCosts = alternatives != null
                ? alternatives.Select(alternative => alternative.Cost)
                : costs;
            foreach (RepairMaterialCost cost in countedCosts)
            {
                cost.AvailableAmount = CountInventory(player, cost.SourceRequirement);
            }
        }

        if (alternatives is { Count: > 0 })
        {
            int selectedIndex = alternatives.FindIndex(alternative => alternative.Cost.IsAffordable);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            (RepairMaterialCost selectedCost, bool selectedHasRawCost) = alternatives[selectedIndex];
            selectedPlanHasRawCost = selectedHasRawCost;
            costs = selectedCost.RequiredAmount > 0
                ? new List<RepairMaterialCost> { selectedCost }
                : new List<RepairMaterialCost>();
        }

        return new RepairPreview(
            item,
            RepairPaymentKind.StationMaterials,
            costs,
            bucketPercent,
            useNearbyContainers && costs.Count > 0,
            selectedPlanHasRawCost,
            selectedPlanHasRawCost ? roundingContext?.Token ?? string.Empty : string.Empty);
    }

    private static int CountInventory(Player player, Piece.Requirement requirement)
    {
        string name = requirement.m_resItem.m_itemData.m_shared.m_name;
        return player.GetInventory().CountItems(name, -1, true);
    }

    private static bool IsExcludedRepairMaterial(
        ItemDrop.ItemData.ItemType itemType,
        string? prefabName)
    {
        return EquipmentTypeRules.IsEquipment(itemType)
               || itemType == ItemDrop.ItemData.ItemType.Trophy
               || _blacklistedMaterialPrefabs.IsMatch(prefabName);
    }

    private static long GetBaseRecipeAmount(Piece.Requirement requirement)
    {
        return Math.Max(0L, requirement.GetAmount(1));
    }

    private static long GetQualityIncrementRecipeAmount(Piece.Requirement requirement, int quality)
    {
        return quality > 1
            ? Math.Max(0L, requirement.GetAmount(quality))
            : 0L;
    }

    private static double CalculateRawRepairAmount(
        long baseRecipeAmount,
        long qualityIncrementRecipeAmount,
        double baseMaterialPercent,
        double qualityIncrementMaterialPercent,
        int missingDurabilityPercent)
    {
        if (missingDurabilityPercent <= 0
            || ((baseRecipeAmount <= 0 || baseMaterialPercent <= 0d)
                && (qualityIncrementRecipeAmount <= 0 || qualityIncrementMaterialPercent <= 0d)))
        {
            return 0d;
        }

        double weightedPercentAmount = baseRecipeAmount * baseMaterialPercent
            + qualityIncrementRecipeAmount * qualityIncrementMaterialPercent;
        double scaledRepairAmount = weightedPercentAmount * missingDurabilityPercent / 10000d;
        if (!(scaledRepairAmount > 0d))
        {
            return 0d;
        }

        return scaledRepairAmount;
    }

    private static int GetDurabilityBucketPercent(ItemDrop.ItemData item)
    {
        float maxDurability = item.GetMaxDurability();
        if (maxDurability <= 0f)
        {
            return 0;
        }

        float durabilityPercent = Mathf.Clamp01(item.m_durability / maxDurability);
        int bucket = Mathf.CeilToInt(durabilityPercent * 10f);
        return Mathf.Clamp(bucket, 0, 10) * 10;
    }

    private static string ResolveItemDropPrefabName(ItemDrop itemDrop)
    {
        string dropPrefabName = itemDrop?.m_itemData?.m_dropPrefab != null
            ? itemDrop.m_itemData.m_dropPrefab.name
            : string.Empty;
        return CleanPrefabName(string.IsNullOrWhiteSpace(dropPrefabName) ? itemDrop?.name ?? string.Empty : dropPrefabName);
    }

    private static string CleanPrefabName(string value)
    {
        string result = value?.Trim() ?? string.Empty;
        const string cloneSuffix = "(Clone)";
        return result.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
            ? result.Substring(0, result.Length - cloneSuffix.Length).Trim()
            : result;
    }
}
