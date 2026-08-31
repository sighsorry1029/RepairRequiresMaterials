using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RepairRequiresMaterials;

internal readonly struct IncineratorKnownRecipeToken : IEquatable<IncineratorKnownRecipeToken>
{
    private const string HashDomain = RepairRequiresMaterialsPlugin.ModGuid + ".KnownDismantleRecipe.v1\0";

    internal IncineratorKnownRecipeToken(ulong first, ulong second)
    {
        First = first;
        Second = second;
    }

    internal ulong First { get; }
    internal ulong Second { get; }

    internal static IncineratorKnownRecipeToken Create(string recipeName)
    {
        byte[] digest;
        using (SHA256 sha256 = SHA256.Create())
        {
            digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(HashDomain + recipeName));
        }

        ulong first = 0UL;
        ulong second = 0UL;
        for (int index = 0; index < sizeof(ulong); ++index)
        {
            first = (first << 8) | digest[index];
            second = (second << 8) | digest[index + sizeof(ulong)];
        }

        return new IncineratorKnownRecipeToken(first, second);
    }

    public bool Equals(IncineratorKnownRecipeToken other)
    {
        return First == other.First && Second == other.Second;
    }

    public override bool Equals(object? obj)
    {
        return obj is IncineratorKnownRecipeToken other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = First.GetHashCode();
            return (hash * 397) ^ Second.GetHashCode();
        }
    }
}

internal readonly struct IncineratorDismantleRollSeed
{
    internal IncineratorDismantleRollSeed(ulong first, ulong second)
    {
        First = first;
        Second = second;
    }

    internal ulong First { get; }
    internal ulong Second { get; }

    internal static IncineratorDismantleRollSeed CreateRandom()
    {
        byte[] bytes = new byte[sizeof(ulong) * 2];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        ulong first = 0UL;
        ulong second = 0UL;
        for (int index = 0; index < sizeof(ulong); ++index)
        {
            first = (first << 8) | bytes[index];
            second = (second << 8) | bytes[index + sizeof(ulong)];
        }

        return new IncineratorDismantleRollSeed(first, second);
    }
}

internal sealed class IncineratorDismantleOutput
{
    internal IncineratorDismantleOutput(ItemDrop resource, string prefabName, int amount)
    {
        Resource = resource;
        PrefabName = prefabName;
        Amount = amount;
    }

    internal ItemDrop Resource { get; }
    internal string PrefabName { get; }
    internal int Amount { get; }
}

internal sealed class IncineratorDismantlePlan
{
    internal IncineratorDismantlePlan(
        IReadOnlyList<ItemDrop.ItemData> sourceItems,
        IReadOnlyList<IncineratorDismantleOutput> outputs)
    {
        SourceItems = sourceItems;
        Outputs = outputs;

        int sourceUnitCount = 0;
        foreach (ItemDrop.ItemData sourceItem in sourceItems)
        {
            int stack = Math.Max(0, sourceItem.m_stack);
            sourceUnitCount = sourceUnitCount >= int.MaxValue - stack
                ? int.MaxValue
                : sourceUnitCount + stack;
        }

        SourceUnitCount = sourceUnitCount;
    }

    internal IReadOnlyList<ItemDrop.ItemData> SourceItems { get; }
    internal IReadOnlyList<IncineratorDismantleOutput> Outputs { get; }
    internal int SourceUnitCount { get; }
}

internal static class IncineratorDismantleCostSystem
{
    private const string FractionalReturnHashDomain =
        RepairRequiresMaterialsPlugin.ModGuid + ".DismantleFractionalReturn.v1\0";
    private const decimal UInt64Range = 18446744073709551616m;
    private static volatile PrefabPatternMatcher _additionalDismantleablePrefabs =
        PrefabPatternMatcher.Empty;

    private sealed class RecipeMaterialAmount
    {
        internal RecipeMaterialAmount(ItemDrop resource, string prefabName)
        {
            Resource = resource;
            PrefabName = prefabName;
        }

        internal ItemDrop Resource { get; }
        internal string PrefabName { get; }
        internal decimal BaseAmount { get; set; }
        internal decimal UpgradeAmount { get; set; }
    }

    private sealed class RawMaterialTotal
    {
        internal RawMaterialTotal(ItemDrop resource, string prefabName)
        {
            Resource = resource;
            PrefabName = prefabName;
        }

        internal ItemDrop Resource { get; }
        internal string PrefabName { get; }
        internal decimal Amount { get; set; }
    }

    private sealed class ItemReferenceComparer : IEqualityComparer<ItemDrop.ItemData>
    {
        internal static readonly ItemReferenceComparer Instance = new();

        public bool Equals(ItemDrop.ItemData? x, ItemDrop.ItemData? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(ItemDrop.ItemData obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    internal static void SetAdditionalDismantleablePrefabPatterns(string? patterns)
    {
        _additionalDismantleablePrefabs = PrefabPatternMatcher.Parse(patterns);
    }

    internal static bool TryBuildPlan(
        Inventory inventory,
        HashSet<IncineratorKnownRecipeToken>? knownRecipeTokens,
        IncineratorDismantleRollSeed rollSeed,
        out IncineratorDismantlePlan? plan)
    {
        plan = null;
        if (inventory == null)
        {
            return false;
        }

        decimal baseReturnRate = (decimal)Mathf.Clamp(
            RepairRequiresMaterialsPlugin.DismantleBaseReturnPercent.Value,
            0f,
            100f) / 100m;
        decimal upgradeReturnRate = (decimal)Mathf.Clamp(
            RepairRequiresMaterialsPlugin.DismantleUpgradeReturnPercent.Value,
            0f,
            100f) / 100m;
        if (baseReturnRate <= 0m && upgradeReturnRate <= 0m)
        {
            return false;
        }

        Dictionary<string, RawMaterialTotal> totals = new(StringComparer.Ordinal);
        List<ItemDrop.ItemData> candidates = new();

        foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToList())
        {
            if (!TryGetRecipeMaterials(
                    item,
                    knownRecipeTokens,
                    out List<RecipeMaterialAmount>? recipeMaterials)
                || recipeMaterials == null)
            {
                continue;
            }

            bool hasReturn = false;
            foreach (RecipeMaterialAmount material in recipeMaterials)
            {
                decimal rawAmount = material.BaseAmount * baseReturnRate
                    + material.UpgradeAmount * upgradeReturnRate;
                if (rawAmount <= 0m)
                {
                    continue;
                }

                hasReturn = true;
                if (!totals.TryGetValue(material.PrefabName, out RawMaterialTotal? total))
                {
                    total = new RawMaterialTotal(material.Resource, material.PrefabName);
                    totals.Add(material.PrefabName, total);
                }

                total.Amount += rawAmount;
            }

            if (hasReturn)
            {
                candidates.Add(item);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        List<IncineratorDismantleOutput> outputs = new(totals.Count);
        foreach (RawMaterialTotal total in totals.Values.OrderBy(value => value.PrefabName, StringComparer.Ordinal))
        {
            decimal floored = decimal.Floor(total.Amount);
            int amount;
            if (floored >= int.MaxValue)
            {
                amount = int.MaxValue;
            }
            else
            {
                amount = decimal.ToInt32(floored);
                decimal fraction = total.Amount - floored;
                if (fraction > 0m && ShouldRoundFractionUp(rollSeed, total.PrefabName, fraction))
                {
                    ++amount;
                }
            }

            if (amount > 0)
            {
                outputs.Add(new IncineratorDismantleOutput(total.Resource, total.PrefabName, amount));
            }
        }

        // Every eligible source stack is consumed even when all fractional rolls fail.
        // Keeping a failed item would let the player repeat Alt+Use until the
        // same source item produced a successful return.
        plan = new IncineratorDismantlePlan(candidates, outputs);
        return true;
    }

    private static bool ShouldRoundFractionUp(
        IncineratorDismantleRollSeed rollSeed,
        string materialPrefabName,
        decimal fraction)
    {
        if (fraction <= 0m)
        {
            return false;
        }

        if (fraction >= 1m)
        {
            return true;
        }

        byte[] materialBytes = Encoding.UTF8.GetBytes(FractionalReturnHashDomain + materialPrefabName);
        byte[] input = new byte[sizeof(ulong) * 2 + materialBytes.Length];
        WriteUInt64BigEndian(input, 0, rollSeed.First);
        WriteUInt64BigEndian(input, sizeof(ulong), rollSeed.Second);
        Buffer.BlockCopy(materialBytes, 0, input, sizeof(ulong) * 2, materialBytes.Length);

        byte[] digest;
        using (SHA256 sha256 = SHA256.Create())
        {
            digest = sha256.ComputeHash(input);
        }

        ulong sample = 0UL;
        for (int index = 0; index < sizeof(ulong); ++index)
        {
            sample = (sample << 8) | digest[index];
        }

        return sample / UInt64Range < fraction;
    }

    private static void WriteUInt64BigEndian(byte[] destination, int offset, ulong value)
    {
        for (int index = sizeof(ulong) - 1; index >= 0; --index)
        {
            destination[offset + index] = (byte)value;
            value >>= 8;
        }
    }

    internal static bool CanApplyPlan(Inventory inventory, IncineratorDismantlePlan plan)
    {
        Inventory candidate = new(
            inventory.GetName(),
            inventory.GetBkg(),
            inventory.GetWidth(),
            inventory.GetHeight());
        HashSet<ItemDrop.ItemData> removed = new(plan.SourceItems, ItemReferenceComparer.Instance);

        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (!removed.Contains(item))
            {
                candidate.GetAllItems().Add(item.Clone());
            }
        }

        candidate.Changed();
        foreach (IncineratorDismantleOutput output in plan.Outputs)
        {
            if (!HasCompatibleSharedName(candidate, output)
                || !TryAddOutput(candidate, output))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryApplyPlan(
        Container container,
        Inventory inventory,
        IncineratorDismantlePlan plan)
    {
        if (container == null || inventory == null || container.m_loading)
        {
            return false;
        }

        List<ItemDrop.ItemData> rollbackItems = inventory.GetAllItems()
            .Select(item => item.Clone())
            .ToList();
        bool originalLoading = container.m_loading;
        bool success = false;
        container.m_loading = true;

        try
        {
            foreach (ItemDrop.ItemData sourceItem in plan.SourceItems)
            {
                if (!inventory.RemoveItem(sourceItem))
                {
                    throw new InvalidOperationException("The incinerator inventory changed before dismantling completed.");
                }
            }

            foreach (IncineratorDismantleOutput output in plan.Outputs)
            {
                if (!TryAddOutput(inventory, output))
                {
                    throw new InvalidOperationException(
                        $"The incinerator could not accept dismantle output '{output.PrefabName}'.");
                }
            }

            success = true;
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Dismantle transaction rolled back: {exception.GetType().Name}: {exception.Message}");
            inventory.GetAllItems().Clear();
            foreach (ItemDrop.ItemData rollbackItem in rollbackItems)
            {
                inventory.GetAllItems().Add(rollbackItem);
            }
        }
        finally
        {
            container.m_loading = originalLoading;
            inventory.Changed();
        }

        return success;
    }

    internal static byte[] GetFingerprint(Inventory inventory)
    {
        ZPackage package = new();
        inventory.Save(package);
        return package.GetArray();
    }

    internal static bool IsDismantleCandidate(ItemDrop.ItemData? item)
    {
        return TryGetDismantleCandidatePrefab(item, out _, out _);
    }

    private static bool TryGetRecipeMaterials(
        ItemDrop.ItemData item,
        HashSet<IncineratorKnownRecipeToken>? knownRecipeTokens,
        out List<RecipeMaterialAmount>? selectedMaterials)
    {
        selectedMaterials = null;
        if (!TryGetDismantleCandidatePrefab(
                item,
                out string itemPrefabName,
                out bool isEquipment)
            || string.IsNullOrEmpty(item.m_shared.m_name)
            || knownRecipeTokens == null
            || !knownRecipeTokens.Contains(
                IncineratorKnownRecipeToken.Create(item.m_shared.m_name)))
        {
            return false;
        }

        List<List<RecipeMaterialAmount>> validRecipes = new();
        foreach (Recipe recipe in RepairRecipeCatalog.GetRecipes(item))
        {
            if (recipe == null
                || !recipe.m_enabled
                || recipe.m_amount <= 0
                || (isEquipment && recipe.m_amount != 1)
                || recipe.m_requireOnlyOneIngredient
                || !TryBuildRecipeMaterialAmounts(item, itemPrefabName, recipe, out List<RecipeMaterialAmount>? materials)
                || materials == null)
            {
                continue;
            }

            validRecipes.Add(materials);
        }

        if (validRecipes.Count == 0)
        {
            return false;
        }

        List<RecipeMaterialAmount> first = validRecipes[0];
        for (int index = 1; index < validRecipes.Count; ++index)
        {
            if (!HaveEquivalentMaterialCosts(first, validRecipes[index]))
            {
                return false;
            }
        }

        selectedMaterials = first;
        return true;
    }

    private static bool TryBuildRecipeMaterialAmounts(
        ItemDrop.ItemData item,
        string itemPrefabName,
        Recipe recipe,
        out List<RecipeMaterialAmount>? materials)
    {
        materials = null;
        Dictionary<string, RecipeMaterialAmount> byPrefab = new(StringComparer.Ordinal);
        int maxQuality = Math.Max(1, item.m_shared.m_maxQuality);
        int quality = Math.Max(1, Math.Min(item.m_quality, maxQuality));
        int sourceStack = Math.Max(1, item.m_stack);
        int recipeOutputAmount = recipe.m_amount;

        foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
        {
            if (requirement == null || requirement.m_resItem == null)
            {
                return false;
            }

            ItemDrop resource = requirement.m_resItem;

            string resourcePrefabName = ResolveItemDropPrefabName(resource);
            if (resourcePrefabName.Length == 0
                || string.Equals(resourcePrefabName, itemPrefabName, StringComparison.Ordinal))
            {
                return false;
            }

            decimal baseAmount = ScaleRecipeAmount(
                Math.Max(0, requirement.GetAmount(1)),
                sourceStack,
                recipeOutputAmount);
            decimal upgradeAmountPerCraft = 0m;
            for (int level = 2; level <= quality; ++level)
            {
                upgradeAmountPerCraft += Math.Max(0, requirement.GetAmount(level));
            }

            decimal upgradeAmount = ScaleRecipeAmount(
                upgradeAmountPerCraft,
                sourceStack,
                recipeOutputAmount);

            if (baseAmount <= 0m && upgradeAmount <= 0m)
            {
                continue;
            }

            if (!byPrefab.TryGetValue(resourcePrefabName, out RecipeMaterialAmount? material))
            {
                material = new RecipeMaterialAmount(resource, resourcePrefabName);
                byPrefab.Add(resourcePrefabName, material);
            }

            material.BaseAmount += baseAmount;
            material.UpgradeAmount += upgradeAmount;
        }

        if (byPrefab.Count == 0)
        {
            return false;
        }

        materials = byPrefab.Values
            .OrderBy(material => material.PrefabName, StringComparer.Ordinal)
            .ToList();
        return true;
    }

    internal static decimal ScaleRecipeAmount(
        decimal amountPerCraft,
        int sourceStack,
        int recipeOutputAmount)
    {
        return amountPerCraft <= 0m || sourceStack <= 0 || recipeOutputAmount <= 0
            ? 0m
            : amountPerCraft * sourceStack / recipeOutputAmount;
    }

    private static bool HaveEquivalentMaterialCosts(
        IReadOnlyList<RecipeMaterialAmount> first,
        IReadOnlyList<RecipeMaterialAmount> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Count; ++index)
        {
            RecipeMaterialAmount left = first[index];
            RecipeMaterialAmount right = second[index];
            if (!string.Equals(left.PrefabName, right.PrefabName, StringComparison.Ordinal)
                || left.BaseAmount != right.BaseAmount
                || left.UpgradeAmount != right.UpgradeAmount)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCompatibleSharedName(
        Inventory inventory,
        IncineratorDismantleOutput output)
    {
        string sharedName = output.Resource.m_itemData.m_shared.m_name;
        foreach (ItemDrop.ItemData existing in inventory.GetAllItems())
        {
            if (!string.Equals(existing.m_shared.m_name, sharedName, StringComparison.Ordinal))
            {
                continue;
            }

            string existingPrefabName = CleanPrefabName(
                existing.m_dropPrefab != null ? existing.m_dropPrefab.name : string.Empty);
            if (!string.Equals(existingPrefabName, output.PrefabName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAddOutput(Inventory inventory, IncineratorDismantleOutput output)
    {
        GameObject prefab = output.Resource.gameObject;
        int maxStack = Math.Max(1, output.Resource.m_itemData.m_shared.m_maxStackSize);
        int remaining = output.Amount;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, maxStack);
            if (!inventory.AddItem(prefab, chunk))
            {
                return false;
            }

            remaining -= chunk;
        }

        return true;
    }

    private static bool IsBlacklisted(string prefabName)
    {
        string value = RepairRequiresMaterialsPlugin.DismantleBlacklist.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string entry in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(CleanPrefabName(entry), prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetDismantleCandidatePrefab(
        ItemDrop.ItemData? item,
        out string prefabName,
        out bool isEquipment)
    {
        prefabName = string.Empty;
        isEquipment = false;
        if (item == null || item.m_shared.m_questItem || item.m_stack <= 0)
        {
            return false;
        }

        prefabName = CleanPrefabName(item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty);
        if (prefabName.Length == 0 || IsBlacklisted(prefabName))
        {
            return false;
        }

        isEquipment = EquipmentTypeRules.IsEquipment(item.m_shared.m_itemType);
        if (isEquipment)
        {
            return item.m_shared.m_maxStackSize == 1 && item.m_stack == 1;
        }

        return _additionalDismantleablePrefabs.IsMatch(prefabName);
    }

    private static string ResolveItemDropPrefabName(ItemDrop itemDrop)
    {
        string dropPrefabName = itemDrop.m_itemData.m_dropPrefab != null
            ? itemDrop.m_itemData.m_dropPrefab.name
            : string.Empty;
        return CleanPrefabName(string.IsNullOrWhiteSpace(dropPrefabName) ? itemDrop.name : dropPrefabName);
    }

    private static string CleanPrefabName(string? value)
    {
        string result = value?.Trim() ?? string.Empty;
        const string cloneSuffix = "(Clone)";
        return result.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
            ? result.Substring(0, result.Length - cloneSuffix.Length).Trim()
            : result;
    }

}
