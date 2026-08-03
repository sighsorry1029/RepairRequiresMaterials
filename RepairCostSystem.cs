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
    FieldPowder
}

internal sealed class RepairPreview
{
    internal RepairPreview(
        ItemDrop.ItemData item,
        Recipe? recipe,
        RepairPaymentKind paymentKind,
        IReadOnlyList<RepairMaterialCost> costs,
        Piece.Requirement[] consumableRequirements,
        int durabilityBucketPercent,
        bool usesNearbyContainers,
        bool stationReady = true,
        RepairBiome? powderBiome = null,
        string powderPrefabName = "")
    {
        Item = item;
        Recipe = recipe;
        PaymentKind = paymentKind;
        Costs = costs;
        ConsumableRequirements = consumableRequirements;
        DurabilityBucketPercent = durabilityBucketPercent;
        UsesNearbyContainers = usesNearbyContainers;
        StationReady = stationReady;
        PowderBiome = powderBiome;
        PowderPrefabName = powderPrefabName;
    }

    internal ItemDrop.ItemData Item { get; }
    internal Recipe? Recipe { get; }
    internal RepairPaymentKind PaymentKind { get; }
    internal IReadOnlyList<RepairMaterialCost> Costs { get; }
    internal Piece.Requirement[] ConsumableRequirements { get; }
    internal int DurabilityBucketPercent { get; }
    internal bool UsesNearbyContainers { get; }
    internal bool StationReady { get; }
    internal RepairBiome? PowderBiome { get; }
    internal string PowderPrefabName { get; }

    internal int VisualKey
    {
        get
        {
            unchecked
            {
                int hash = Item.GetHashCode();
                hash = (hash * 397) ^ DurabilityBucketPercent;
                hash = (hash * 397) ^ (int)PaymentKind;
                hash = (hash * 397) ^ (StationReady ? 1 : 0);
                hash = (hash * 397) ^ (PowderBiome?.GetHashCode() ?? 0);
                foreach (RepairMaterialCost cost in Costs)
                {
                    hash = (hash * 397) ^ cost.ResourcePrefabName.GetHashCode();
                    hash = (hash * 397) ^ cost.RequiredAmount;
                    hash = (hash * 397) ^ cost.AvailableAmount;
                }

                return hash;
            }
        }
    }

    internal bool HasSamePaymentPlan(RepairPreview other)
    {
        if (!ReferenceEquals(Item, other.Item)
            || PaymentKind != other.PaymentKind
            || UsesNearbyContainers != other.UsesNearbyContainers
            || StationReady != other.StationReady
            || PowderBiome != other.PowderBiome
            || !string.Equals(PowderPrefabName, other.PowderPrefabName, StringComparison.Ordinal)
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
    internal string DisplayName => SourceRequirement.m_resItem.m_itemData.m_shared.m_name;
    internal Sprite Icon => SourceRequirement.m_resItem.m_itemData.GetIcon();
}

internal static class RepairCostSystem
{
    private sealed class AggregatedRequirement
    {
        internal AggregatedRequirement(Piece.Requirement sourceRequirement, int recipeAmount, string prefabName)
        {
            SourceRequirement = sourceRequirement;
            RecipeAmount = recipeAmount;
            PrefabName = prefabName;
        }

        internal Piece.Requirement SourceRequirement { get; }
        internal int RecipeAmount { get; set; }
        internal string PrefabName { get; }
    }

    private static readonly List<ItemDrop.ItemData> WornItems = new();

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

        Recipe? recipe = GetRepresentativeRecipe(item);
        if (player.NoCostCheat())
        {
            preview = new RepairPreview(
                item,
                recipe,
                RepairPaymentKind.Free,
                Array.Empty<RepairMaterialCost>(),
                Array.Empty<Piece.Requirement>(),
                GetDurabilityBucketPercent(item),
                usesNearbyContainers: false);
            return true;
        }

        CraftingStation? station = player.GetCurrentCraftingStation();
        if (station != null)
        {
            Recipe? stationRecipe = FindMaterialRepairRecipe(player, item, station);
            if (stationRecipe == null)
            {
                return false;
            }

            preview = BuildStationPreview(
                player,
                item,
                stationRecipe,
                CanRepairAtStation(player, item, station, stationRecipe));
            return true;
        }

        if (!IsFieldRepairEnabled()
            || !RepairTierResolver.TryResolve(item, recipe, out RepairBiome biome)
            || !RepairPowderRegistry.TryGetPowderPrefab(biome, out GameObject powderPrefab))
        {
            return false;
        }

        ItemDrop powderItem = powderPrefab.GetComponent<ItemDrop>();
        if (powderItem == null)
        {
            return false;
        }

        preview = BuildPowderPreview(player, item, recipe, biome, powderItem);
        return true;
    }

    internal static bool CanAfford(Player player, RepairPreview preview)
    {
        if (player.NoCostCheat() || preview.PaymentKind == RepairPaymentKind.Free)
        {
            return true;
        }

        return (preview.PaymentKind != RepairPaymentKind.StationMaterials || preview.StationReady)
            && preview.Costs.All(cost => cost.IsAffordable);
    }

    internal static bool ConsumeRequirements(Player player, RepairPreview preview)
    {
        if (player.NoCostCheat() || preview.PaymentKind == RepairPaymentKind.Free)
        {
            return true;
        }

        if (!CanAfford(player, preview))
        {
            return false;
        }

        if (preview.PaymentKind == RepairPaymentKind.FieldPowder)
        {
            if (preview.Costs.Count != 1 || string.IsNullOrWhiteSpace(preview.PowderPrefabName))
            {
                return false;
            }

            return TryConsumePowder(
                player.GetInventory(),
                preview.PowderPrefabName,
                preview.Costs[0].RequiredAmount);
        }

        if (preview.ConsumableRequirements.Length == 0)
        {
            return true;
        }

        if (preview.UsesNearbyContainers)
        {
            bool consumed = AzuCraftyBoxesCompat.TryConsume(
                player,
                preview.ConsumableRequirements,
                out bool shouldCompleteRepair);
            return consumed || shouldCompleteRepair;
        }

        return ConsumeInventoryRequirementsSafely(player.GetInventory(), preview.ConsumableRequirements);
    }

    internal static string BuildTooltipText(RepairPreview preview)
    {
        string mode = preview.PaymentKind switch
        {
            RepairPaymentKind.FieldPowder => Localize("$rrm_ui_field_powder"),
            RepairPaymentKind.StationMaterials => Localize("$rrm_ui_repair_materials"),
            _ => Localize("$rrm_ui_free_repair")
        };

        List<string> lines = new()
        {
            mode,
            $"{Localize("$rrm_ui_durability")}: {preview.DurabilityBucketPercent}%"
        };

        if (preview.PaymentKind == RepairPaymentKind.StationMaterials && !preview.StationReady)
        {
            lines.Add(Localize(RepairPowderRegistry.StationUnavailableToken));
        }

        foreach (RepairMaterialCost cost in preview.Costs)
        {
            string amountText = RepairRequiresMaterialsPlugin.ShowAvailableAmountInTooltip.Value.IsOn()
                ? $"{cost.AvailableAmount}/{cost.RequiredAmount}"
                : cost.RequiredAmount.ToString();
            lines.Add($"{Localize(cost.DisplayName)}: {amountText}");
        }

        return string.Join("\n", lines);
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

        Recipe? recipe = GetRepresentativeRecipe(item);
        if (station != null)
        {
            return FindMaterialRepairRecipe(player, item, station) != null;
        }

        return IsFieldRepairEnabled()
            && RepairTierResolver.TryResolve(item, recipe, out RepairBiome biome)
            && RepairPowderRegistry.IsRegistered(biome);
    }

    private static Recipe? GetRepresentativeRecipe(ItemDrop.ItemData item)
    {
        IReadOnlyList<Recipe> exactRecipes = RepairRecipeCatalog.GetRecipes(item);
        foreach (Recipe recipe in exactRecipes)
        {
            if (recipe != null && recipe.m_enabled)
            {
                return recipe;
            }
        }

        if (exactRecipes.Count > 0)
        {
            return exactRecipes[0];
        }

        // Inventory ItemData normally carries m_dropPrefab. Retain the vanilla
        // shared-name lookup only as a compatibility fallback for unusual items
        // that do not, because it is ambiguous for normal modded prefabs.
        return exactRecipes.Count == 0 && item.m_dropPrefab == null && ObjectDB.instance != null
            ? ObjectDB.instance.GetRecipe(item)
            : null;
    }

    private static Recipe? FindMaterialRepairRecipe(
        Player player,
        ItemDrop.ItemData item,
        CraftingStation station)
    {
        IReadOnlyList<Recipe> exactRecipes = RepairRecipeCatalog.GetRecipes(item);
        bool hasEnabledExactRecipe = false;
        Recipe? firstEnabledSafeRecipe = null;
        Recipe? firstEnabledMatchingRecipe = null;
        foreach (Recipe recipe in exactRecipes)
        {
            if (recipe == null || !recipe.m_enabled)
            {
                continue;
            }

            hasEnabledExactRecipe = true;
            if (!IsSafeMaterialRepairRecipe(item, recipe))
            {
                continue;
            }

            firstEnabledSafeRecipe ??= recipe;
            if (MatchesStationType(item, station, recipe))
            {
                firstEnabledMatchingRecipe ??= recipe;
                if (CanRepairAtStation(player, item, station, recipe))
                {
                    return recipe;
                }
            }
        }

        if (firstEnabledMatchingRecipe != null)
        {
            return firstEnabledMatchingRecipe;
        }

        if (firstEnabledSafeRecipe != null)
        {
            return firstEnabledSafeRecipe;
        }

        if (!hasEnabledExactRecipe)
        {
            Recipe? firstDisabledSafeRecipe = null;
            Recipe? firstDisabledMatchingRecipe = null;
            foreach (Recipe recipe in exactRecipes)
            {
                if (recipe != null
                    && !recipe.m_enabled
                    && IsSafeMaterialRepairRecipe(item, recipe))
                {
                    firstDisabledSafeRecipe ??= recipe;
                    if (MatchesStationType(item, station, recipe))
                    {
                        firstDisabledMatchingRecipe ??= recipe;
                        if (CanRepairAtStation(player, item, station, recipe))
                        {
                            return recipe;
                        }
                    }
                }
            }

            if (firstDisabledMatchingRecipe != null)
            {
                return firstDisabledMatchingRecipe;
            }

            if (firstDisabledSafeRecipe != null)
            {
                return firstDisabledSafeRecipe;
            }
        }

        Recipe? fallback = GetRepresentativeRecipe(item);
        return exactRecipes.Count == 0
            && fallback != null
            && IsSafeMaterialRepairRecipe(item, fallback)
                ? fallback
                : null;
    }

    private static bool MatchesStationType(
        ItemDrop.ItemData item,
        CraftingStation station,
        Recipe recipe)
    {
        return (recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name)
            || (recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == station.m_name)
            || item.m_worldLevel < Game.m_worldLevel;
    }

    private static bool IsSafeMaterialRepairRecipe(ItemDrop.ItemData item, Recipe recipe)
    {
        string itemPrefabName = CleanPrefabName(item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty);
        string itemSharedName = item.m_shared.m_name;
        foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
        {
            if (requirement?.m_resItem == null || requirement.GetAmount(item.m_quality) <= 0)
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
        }

        return true;
    }

    private static RepairPreview BuildStationPreview(
        Player player,
        ItemDrop.ItemData item,
        Recipe recipe,
        bool stationReady)
    {
        int bucketPercent = GetDurabilityBucketPercent(item);
        float missingDurabilityMultiplier = 1f - bucketPercent / 100f;
        float repairPercent = Mathf.Clamp(RepairRequiresMaterialsPlugin.RepairCostPercent.Value, 0f, 100f) / 100f;

        Piece.Requirement[] recipeResources = recipe.m_resources ?? Array.Empty<Piece.Requirement>();
        Dictionary<string, AggregatedRequirement> requirementsByName = new(StringComparer.Ordinal);
        List<AggregatedRequirement> orderedRequirements = new(recipeResources.Length);

        foreach (Piece.Requirement requirement in recipeResources)
        {
            if (requirement?.m_resItem == null)
            {
                continue;
            }

            int recipeAmount = Mathf.Max(0, requirement.GetAmount(item.m_quality));
            if (recipeAmount <= 0)
            {
                continue;
            }

            string resourceName = requirement.m_resItem.m_itemData.m_shared.m_name;
            if (requirementsByName.TryGetValue(resourceName, out AggregatedRequirement? existing))
            {
                existing.RecipeAmount += recipeAmount;
                continue;
            }

            string resourcePrefabName = ResolveItemDropPrefabName(requirement.m_resItem);
            AggregatedRequirement aggregated = new(requirement, recipeAmount, resourcePrefabName);
            requirementsByName.Add(resourceName, aggregated);
            orderedRequirements.Add(aggregated);
        }

        bool useNearbyContainers = AzuCraftyBoxesCompat.ShouldUseNearbyContainers();
        bool nearbyCountFailed = false;
        List<RepairMaterialCost> costs = new(orderedRequirements.Count);
        List<Piece.Requirement> consumableRequirements = new(orderedRequirements.Count);

        foreach (AggregatedRequirement aggregated in orderedRequirements)
        {
            int requiredAmount = CalculateRepairAmount(aggregated.RecipeAmount, repairPercent, missingDurabilityMultiplier);
            if (requiredAmount <= 0)
            {
                continue;
            }

            Piece.Requirement requirement = aggregated.SourceRequirement;
            int inventoryAmount = CountInventory(player, requirement);
            int availableAmount = inventoryAmount;
            if (useNearbyContainers
                && !AzuCraftyBoxesCompat.TryCountAvailable(player, requirement, inventoryAmount, out availableAmount))
            {
                useNearbyContainers = false;
                nearbyCountFailed = true;
                availableAmount = inventoryAmount;
            }

            costs.Add(new RepairMaterialCost(
                requirement,
                requiredAmount,
                availableAmount,
                aggregated.PrefabName));

            consumableRequirements.Add(new Piece.Requirement
            {
                m_resItem = requirement.m_resItem,
                m_amount = requiredAmount,
                m_amountPerLevel = 0,
                m_recover = false
            });
        }

        if (nearbyCountFailed)
        {
            foreach (RepairMaterialCost cost in costs)
            {
                cost.AvailableAmount = CountInventory(player, cost.SourceRequirement);
            }
        }

        return new RepairPreview(
            item,
            recipe,
            RepairPaymentKind.StationMaterials,
            costs,
            consumableRequirements.ToArray(),
            bucketPercent,
            useNearbyContainers,
            stationReady);
    }

    private static RepairPreview BuildPowderPreview(
        Player player,
        ItemDrop.ItemData item,
        Recipe? recipe,
        RepairBiome biome,
        ItemDrop powderItem)
    {
        string powderPrefabName = RepairPowderRegistry.GetPowderPrefabName(biome);
        int requiredAmount = CalculatePowderAmount(item);
        int availableAmount = CountPowder(player.GetInventory(), powderPrefabName);
        Piece.Requirement requirement = new()
        {
            m_resItem = powderItem,
            m_amount = requiredAmount,
            m_amountPerLevel = 0,
            m_recover = false
        };

        RepairMaterialCost cost = new(
            requirement,
            requiredAmount,
            availableAmount,
            powderPrefabName);

        return new RepairPreview(
            item,
            recipe,
            RepairPaymentKind.FieldPowder,
            new[] { cost },
            new[] { requirement },
            GetDurabilityBucketPercent(item),
            usesNearbyContainers: false,
            powderBiome: biome,
            powderPrefabName: powderPrefabName);
    }

    private static int CountInventory(Player player, Piece.Requirement requirement)
    {
        string name = requirement.m_resItem.m_itemData.m_shared.m_name;
        return player.GetInventory().CountItems(name, -1, true);
    }

    private static int CountPowder(Inventory inventory, string powderPrefabName)
    {
        int total = 0;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (string.Equals(ResolveItemDataPrefabName(item), powderPrefabName, StringComparison.Ordinal))
            {
                total += item.m_stack;
            }
        }

        return total;
    }

    private static bool TryConsumePowder(Inventory inventory, string powderPrefabName, int amount)
    {
        if (amount <= 0)
        {
            return true;
        }

        List<ItemDrop.ItemData> stacks = inventory.GetAllItems()
            .Where(item => string.Equals(ResolveItemDataPrefabName(item), powderPrefabName, StringComparison.Ordinal))
            .ToList();

        if (stacks.Sum(item => item.m_stack) < amount)
        {
            return false;
        }

        int remaining = amount;
        bool confirmedConsumption = false;
        foreach (ItemDrop.ItemData stack in stacks)
        {
            int remove = Mathf.Min(stack.m_stack, remaining);
            int beforeStepAmount = CountPowder(inventory, powderPrefabName);
            bool removeSucceeded;
            int removedAmount;
            try
            {
                removeSucceeded = inventory.RemoveItem(stack, remove);
                removedAmount = beforeStepAmount - CountPowder(inventory, powderPrefabName);
            }
            catch (Exception exception)
            {
                try
                {
                    confirmedConsumption |= beforeStepAmount - CountPowder(inventory, powderPrefabName) > 0;
                }
                catch
                {
                    // The mutation was attempted and its result can no longer be
                    // measured safely, so favor avoiding possible material loss.
                    confirmedConsumption = true;
                }

                LogConsumptionMismatch(
                    $"powder '{powderPrefabName}' removal threw {exception.GetType().Name}: {exception.Message}",
                    confirmedConsumption);
                return confirmedConsumption;
            }

            confirmedConsumption |= removedAmount > 0;
            if (!removeSucceeded || removedAmount != remove)
            {
                LogConsumptionMismatch(
                    $"powder '{powderPrefabName}' removed {removedAmount} instead of {remove}",
                    confirmedConsumption);
                return confirmedConsumption;
            }

            remaining -= removedAmount;
            if (remaining <= 0)
            {
                return true;
            }
        }

        LogConsumptionMismatch(
            $"powder '{powderPrefabName}' removal ended with {remaining} still required",
            confirmedConsumption);
        return confirmedConsumption;
    }

    private static bool ConsumeInventoryRequirementsSafely(
        Inventory inventory,
        IEnumerable<Piece.Requirement> requirements)
    {
        Piece.Requirement[] source = requirements.ToArray();
        if (source.Any(requirement => requirement == null || requirement.m_resItem == null))
        {
            return false;
        }

        Piece.Requirement[] plan = source
            .Where(requirement => requirement.m_amount > 0)
            .ToArray();

        foreach (Piece.Requirement requirement in plan)
        {
            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            if (inventory.CountItems(itemName, -1, true) < requirement.m_amount)
            {
                return false;
            }
        }

        bool confirmedConsumption = false;
        foreach (Piece.Requirement requirement in plan)
        {
            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            int beforeAmount = inventory.CountItems(itemName, -1, true);
            if (beforeAmount < requirement.m_amount)
            {
                LogConsumptionMismatch(
                    $"inventory amount for '{itemName}' changed before removal",
                    confirmedConsumption);
                return confirmedConsumption;
            }

            int removedAmount;
            try
            {
                inventory.RemoveItem(itemName, requirement.m_amount, -1, true);
                removedAmount = beforeAmount - inventory.CountItems(itemName, -1, true);
            }
            catch (Exception exception)
            {
                try
                {
                    confirmedConsumption |= beforeAmount - inventory.CountItems(itemName, -1, true) > 0;
                }
                catch
                {
                    confirmedConsumption = true;
                }

                LogConsumptionMismatch(
                    $"inventory removal threw {exception.GetType().Name}: {exception.Message}",
                    confirmedConsumption);
                return confirmedConsumption;
            }

            confirmedConsumption |= removedAmount > 0;
            if (removedAmount != requirement.m_amount)
            {
                LogConsumptionMismatch(
                    $"inventory removed {removedAmount} instead of {requirement.m_amount} for '{itemName}'",
                    confirmedConsumption);
                return confirmedConsumption;
            }
        }

        return true;
    }

    private static void LogConsumptionMismatch(string reason, bool repairWillComplete)
    {
        string outcome = repairWillComplete
            ? "The selected item will still be repaired because consumption may already have started."
            : "The repair was cancelled before consumption started.";
        RepairRequiresMaterialsPlugin.Log.LogWarning($"Repair material consumption mismatch: {reason}. {outcome}");
    }

    private static int CalculateRepairAmount(int recipeAmount, float repairPercent, float missingDurabilityMultiplier)
    {
        if (recipeAmount <= 0 || repairPercent <= 0f || missingDurabilityMultiplier <= 0f)
        {
            return 0;
        }

        return Mathf.Max(0, Mathf.RoundToInt(recipeAmount * repairPercent * missingDurabilityMultiplier));
    }

    private static int CalculatePowderAmount(ItemDrop.ItemData item)
    {
        float maxDurability = item.GetMaxDurability();
        if (maxDurability <= 0f)
        {
            return 0;
        }

        float missingFraction = 1f - Mathf.Clamp01(item.m_durability / maxDurability);
        float repairPerPowder = Mathf.Clamp(
            RepairRequiresMaterialsPlugin.PowderRepairPercent.Value,
            1f,
            100f) / 100f;
        return Mathf.Max(1, Mathf.CeilToInt(missingFraction / repairPerPowder));
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

    private static bool IsFieldRepairEnabled()
    {
        return RepairRequiresMaterialsPlugin.EnableFieldRepair.Value.IsOn();
    }

    private static string ResolveItemDataPrefabName(ItemDrop.ItemData item)
    {
        return CleanPrefabName(item?.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty);
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

    private static string Localize(string token)
    {
        return Localization.instance != null ? Localization.instance.Localize(token) : token;
    }
}
