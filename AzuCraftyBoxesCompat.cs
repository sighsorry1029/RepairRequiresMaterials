using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace RepairRequiresMaterials;

internal static class AzuCraftyBoxesCompat
{
    private const string DeferredKgDrawerTypeName = "AzuCraftyBoxes.IContainers.kgDrawer";

    private sealed class ContainerConsumption
    {
        internal ContainerConsumption(
            object container,
            MethodInfo getPrefabNameMethod,
            MethodInfo itemCountMethod,
            MethodInfo processInventoryMethod,
            int amount)
        {
            Container = container;
            GetPrefabNameMethod = getPrefabNameMethod;
            ItemCountMethod = itemCountMethod;
            ProcessInventoryMethod = processInventoryMethod;
            Amount = amount;
        }

        internal object Container { get; }
        internal MethodInfo GetPrefabNameMethod { get; }
        internal MethodInfo ItemCountMethod { get; }
        internal MethodInfo ProcessInventoryMethod { get; }
        internal int Amount { get; }
    }

    private sealed class RequirementConsumption
    {
        internal RequirementConsumption(string itemName, string prefabName, int inventoryAmount)
        {
            ItemName = itemName;
            PrefabName = prefabName;
            InventoryAmount = inventoryAmount;
        }

        internal string ItemName { get; }
        internal string PrefabName { get; }
        internal int InventoryAmount { get; }
        internal List<ContainerConsumption> Containers { get; } = new();
    }

    internal const string PluginGuid = "Azumatt.AzuCraftyBoxes";

    private static bool _initialized;
    private static bool _available;
    private static bool _failureLogged;
    private static FieldInfo? _rangeField;
    private static MethodInfo? _queryFrameGetMethod;
    private static MethodInfo? _shouldPreventMethod;
    private static MethodInfo? _canItemBePulledMethod;
    private static MethodInfo? _checkAndDecrementMethod;

    internal static bool ShouldUseNearbyContainers()
    {
        try
        {
            return EnsureInitialized() && !ShouldPrevent();
        }
        catch (Exception exception)
        {
            DisableAfterFailure("availability check", exception);
            return false;
        }
    }

    internal static bool TryCountAvailable(
        Player player,
        RepairMaterialCost cost,
        int currentAmount,
        out int totalAmount)
    {
        totalAmount = currentAmount;
        if (!_available)
        {
            return false;
        }

        try
        {
            Piece.Requirement requirement = cost.SourceRequirement;
            if (requirement?.m_resItem == null)
            {
                return false;
            }

            IList? nearbyContainers = GetNearbyContainers(player);
            if (nearbyContainers == null)
            {
                return false;
            }

            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            string prefabName = cost.ResourcePrefabName;

            foreach (object? container in nearbyContainers)
            {
                if (container == null
                    || !TryGetContainerMethods(
                        container,
                        out MethodInfo getPrefabNameMethod,
                        out MethodInfo itemCountMethod,
                        out MethodInfo processInventoryMethod))
                {
                    continue;
                }

                if (!CanPull(container, getPrefabNameMethod, prefabName))
                {
                    continue;
                }

                totalAmount += GetPullableAmount(container, itemCountMethod, itemName);
            }

            return true;
        }
        catch (Exception exception)
        {
            DisableAfterFailure("container count", exception);
            totalAmount = currentAmount;
            return false;
        }
    }

    internal static bool TryConsume(
        Player player,
        IReadOnlyList<RepairMaterialCost> costs,
        out bool shouldCompleteRepair)
    {
        shouldCompleteRepair = false;
        if (!ShouldUseNearbyContainers())
        {
            return false;
        }

        try
        {
            IList? nearbyContainers = GetNearbyContainers(player);
            if (nearbyContainers == null)
            {
                return false;
            }

            Inventory inventory = player.GetInventory();
            List<RequirementConsumption> plan = new(costs.Count);
            foreach (RepairMaterialCost cost in costs)
            {
                Piece.Requirement requirement = cost.SourceRequirement;
                if (requirement?.m_resItem == null)
                {
                    return false;
                }

                int requiredAmount = Math.Max(0, cost.RequiredAmount);
                if (requiredAmount <= 0)
                {
                    continue;
                }

                string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
                string prefabName = cost.ResourcePrefabName;
                int inventoryAmount = Math.Min(requiredAmount, inventory.CountItems(itemName, -1, true));
                int remainingAmount = requiredAmount - inventoryAmount;
                RequirementConsumption requirementPlan = new(itemName, prefabName, inventoryAmount);

                foreach (object? container in nearbyContainers)
                {
                    if (remainingAmount <= 0)
                    {
                        break;
                    }

                    if (container == null
                        || !TryGetContainerMethods(
                            container,
                            out MethodInfo getPrefabNameMethod,
                            out MethodInfo itemCountMethod,
                            out MethodInfo processInventoryMethod)
                        || !CanPull(container, getPrefabNameMethod, prefabName))
                    {
                        continue;
                    }

                    int takeAmount = Math.Min(
                        remainingAmount,
                        GetPullableAmount(container, itemCountMethod, itemName));
                    if (takeAmount <= 0)
                    {
                        continue;
                    }

                    requirementPlan.Containers.Add(
                        new ContainerConsumption(
                            container,
                            getPrefabNameMethod,
                            itemCountMethod,
                            processInventoryMethod,
                            takeAmount));
                    remainingAmount -= takeAmount;
                }

                if (remainingAmount > 0)
                {
                    return false;
                }

                plan.Add(requirementPlan);
            }

            if (!ValidateConsumptionPlan(inventory, plan))
            {
                return false;
            }

            return ExecuteConsumptionPlan(inventory, plan, out shouldCompleteRepair);
        }
        catch (Exception exception)
        {
            DisableAfterFailure("material consumption", exception, shouldCompleteRepair);
            return false;
        }
    }

    private static bool ValidateConsumptionPlan(Inventory inventory, IEnumerable<RequirementConsumption> plan)
    {
        foreach (RequirementConsumption requirement in plan)
        {
            if (inventory.CountItems(requirement.ItemName, -1, true) < requirement.InventoryAmount)
            {
                return false;
            }

            foreach (ContainerConsumption container in requirement.Containers)
            {
                if (!CanPull(container.Container, container.GetPrefabNameMethod, requirement.PrefabName)
                    || GetPullableAmount(
                        container.Container,
                        container.ItemCountMethod,
                        requirement.ItemName) < container.Amount)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ExecuteConsumptionPlan(
        Inventory inventory,
        IEnumerable<RequirementConsumption> plan,
        out bool shouldCompleteRepair)
    {
        shouldCompleteRepair = false;
        foreach (RequirementConsumption requirement in plan)
        {
            if (requirement.InventoryAmount > 0)
            {
                int beforeAmount = inventory.CountItems(requirement.ItemName, -1, true);
                int removedAmount;
                try
                {
                    inventory.RemoveItem(requirement.ItemName, requirement.InventoryAmount, -1, true);
                    removedAmount = beforeAmount - inventory.CountItems(requirement.ItemName, -1, true);
                }
                catch (Exception exception)
                {
                    try
                    {
                        shouldCompleteRepair |= beforeAmount - inventory.CountItems(requirement.ItemName, -1, true) > 0;
                    }
                    catch
                    {
                        shouldCompleteRepair = true;
                    }

                    DisableAfterFailure("player inventory material removal", exception, shouldCompleteRepair);
                    return false;
                }

                shouldCompleteRepair |= removedAmount > 0;
                if (removedAmount != requirement.InventoryAmount)
                {
                    return DisableAfterConsumptionMismatch(
                        "player inventory changed during material removal",
                        shouldCompleteRepair);
                }
            }

            foreach (ContainerConsumption container in requirement.Containers)
            {
                int beforeAmount = GetRawContainerAmount(
                    container.Container,
                    container.ItemCountMethod,
                    requirement.ItemName);
                int reportedAmount;
                int afterAmount;
                try
                {
                    reportedAmount = Convert.ToInt32(
                        container.ProcessInventoryMethod.Invoke(
                            container.Container,
                            new object[] { requirement.ItemName, 0, container.Amount }) ?? 0);
                    afterAmount = GetRawContainerAmount(
                        container.Container,
                        container.ItemCountMethod,
                        requirement.ItemName);
                }
                catch (Exception exception)
                {
                    try
                    {
                        shouldCompleteRepair |= beforeAmount - GetRawContainerAmount(
                            container.Container,
                            container.ItemCountMethod,
                            requirement.ItemName) > 0;
                    }
                    catch
                    {
                        shouldCompleteRepair = true;
                    }

                    DisableAfterFailure("container material removal", exception, shouldCompleteRepair);
                    return false;
                }

                int removedAmount = beforeAmount - afterAmount;
                shouldCompleteRepair |= removedAmount > 0;
                if (reportedAmount != container.Amount || removedAmount != container.Amount)
                {
                    return DisableAfterConsumptionMismatch(
                        $"container '{GetContainerName(container)}' did not remove the planned amount of {requirement.ItemName}",
                        shouldCompleteRepair);
                }
            }
        }

        return true;
    }

    private static bool EnsureInitialized()
    {
        if (_initialized)
        {
            return _available;
        }

        _initialized = true;

        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var pluginInfo) || pluginInfo.Instance == null)
        {
            return false;
        }

        Assembly assembly = pluginInfo.Instance.GetType().Assembly;
        Type? pluginType = assembly.GetType("AzuCraftyBoxes.AzuCraftyBoxesPlugin");
        Type? boxesType = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
        Type? queryFrameType = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame");
        Type? miscFunctionsType = assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions");

        _rangeField = pluginType?.GetField("mRange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _queryFrameGetMethod = queryFrameType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "Get" && method.IsGenericMethodDefinition);
        _shouldPreventMethod = miscFunctionsType?.GetMethod(
            "ShouldPrevent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _canItemBePulledMethod = boxesType?.GetMethod(
            "CanItemBePulled",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _checkAndDecrementMethod = boxesType?.GetMethod(
            "CheckAndDecrement",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        _available = pluginType != null
            && boxesType != null
            && queryFrameType != null
            && miscFunctionsType != null
            && _rangeField != null
            && _queryFrameGetMethod != null
            && _shouldPreventMethod != null
            && _canItemBePulledMethod != null
            && _checkAndDecrementMethod != null;

        if (!_available && !_failureLogged)
        {
            _failureLogged = true;
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                "AzuCraftyBoxes was found, but its compatible container API was not available. Nearby-container repair support is disabled.");
        }

        return _available;
    }

    private static bool TryGetContainerMethods(
        object container,
        out MethodInfo getPrefabNameMethod,
        out MethodInfo itemCountMethod,
        out MethodInfo processInventoryMethod)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = container.GetType();
        getPrefabNameMethod = type.GetMethod("GetPrefabName", flags)!;
        itemCountMethod = type.GetMethod("ItemCount", flags)!;
        processInventoryMethod = type.GetMethod("ProcessContainerInventory", flags)!;
        return getPrefabNameMethod != null
               && itemCountMethod != null
               && processInventoryMethod != null
               && SupportsVerifiedRemoval(container);
    }

    private static bool CanPull(object container, MethodInfo getPrefabNameMethod, string prefabName)
    {
        string containerPrefab = Convert.ToString(getPrefabNameMethod.Invoke(container, null)) ?? string.Empty;
        return Convert.ToBoolean(
            _canItemBePulledMethod!.Invoke(
                null,
                new object[] { containerPrefab, prefabName, string.Empty }) ?? false);
    }

    private static bool SupportsVerifiedRemoval(object container)
    {
        // kg_ItemDrawers removal is an asynchronous RPC and Azu's wrapper keeps
        // a snapshot count, so the same wrapper cannot confirm that removal.
        // Exclude it rather than risk consuming materials while cancelling the
        // repair, or trusting an unacknowledged RPC and granting a free repair.
        return !string.Equals(
            container.GetType().FullName,
            DeferredKgDrawerTypeName,
            StringComparison.Ordinal);
    }

    private static int GetPullableAmount(object container, MethodInfo itemCountMethod, string itemName)
    {
        int count = GetRawContainerAmount(container, itemCountMethod, itemName);
        return Math.Max(
            0,
            Convert.ToInt32(_checkAndDecrementMethod!.Invoke(null, new object[] { count }) ?? 0));
    }

    private static int GetRawContainerAmount(object container, MethodInfo itemCountMethod, string itemName)
    {
        return Math.Max(
            0,
            Convert.ToInt32(itemCountMethod.Invoke(container, new object[] { itemName }) ?? 0));
    }

    private static string GetContainerName(ContainerConsumption container)
    {
        return Convert.ToString(container.GetPrefabNameMethod.Invoke(container.Container, null)) ?? "unknown";
    }

    private static bool ShouldPrevent()
    {
        return Convert.ToBoolean(_shouldPreventMethod!.Invoke(null, null) ?? true);
    }

    private static IList? GetNearbyContainers(Player player)
    {
        object? rangeConfigEntry = _rangeField!.GetValue(null);
        PropertyInfo? valueProperty = rangeConfigEntry?.GetType().GetProperty("Value");
        float range = Convert.ToSingle(valueProperty?.GetValue(rangeConfigEntry) ?? 0f);

        MethodInfo method = _queryFrameGetMethod!.MakeGenericMethod(typeof(Player));
        return method.Invoke(null, new object[] { player, range }) as IList;
    }

    private static bool DisableAfterConsumptionMismatch(string reason, bool repairWillComplete)
    {
        _available = false;
        if (!_failureLogged)
        {
            _failureLogged = true;
            string safetyOutcome = repairWillComplete
                ? "The selected item will still be repaired because materials were already consumed."
                : "The repair was cancelled because no material removal was confirmed.";
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"AzuCraftyBoxes consumption verification failed; nearby-container repair support is disabled for this session. {safetyOutcome} Reason: {reason}");
        }

        return false;
    }

    private static void DisableAfterFailure(
        string operation,
        Exception exception,
        bool repairWillComplete = false)
    {
        _available = false;
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        Exception details = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;
        string safetySuffix = repairWillComplete
            ? " The selected item will still be repaired because consumption may already have started."
            : string.Empty;
        RepairRequiresMaterialsPlugin.Log.LogWarning(
            $"AzuCraftyBoxes {operation} failed; nearby-container repair support is disabled for this session: {details.Message}.{safetySuffix}");
    }
}
