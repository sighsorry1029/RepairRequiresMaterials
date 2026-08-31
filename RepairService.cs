using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RepairRequiresMaterials;

internal static class RepairService
{
    private static readonly HashSet<Inventory> DirtyInventories = new();
    private static bool _isFlushingStateChanges;

    internal static bool TryRepairSelected(Player player)
    {
        try
        {
            return TryRepairSelectedCore(player);
        }
        finally
        {
            FlushDirtyNotifications();
        }
    }

    internal static void MarkInventoryDirty(Inventory inventory)
    {
        DirtyInventories.Add(inventory);
    }

    internal static void FlushDirtyNotifications()
    {
        if (_isFlushingStateChanges || DirtyInventories.Count == 0)
        {
            return;
        }

        Inventory[] inventories = DirtyInventories.ToArray();
        DirtyInventories.Clear();
        _isFlushingStateChanges = true;
        try
        {
            foreach (Inventory inventory in inventories)
            {
                try
                {
                    inventory.Changed();
                }
                catch (Exception exception)
                {
                    DirtyInventories.Add(inventory);
                    RepairRequiresMaterialsPlugin.Log.LogWarning(
                        $"Could not publish a persisted repair-item state change: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        finally
        {
            _isFlushingStateChanges = false;
        }
    }

    private static bool TryRepairSelectedCore(Player player)
    {
        if ((Object)(object)player == null)
        {
            return false;
        }

        RepairSelectionState.TryGetDisplayedPreview(player, out RepairPreview? displayedPreview);
        if (!RepairSelectionState.TryGetPreviewForRepair(player, out RepairPreview? currentPreview)
            || currentPreview == null)
        {
            player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
            return false;
        }

        if (displayedPreview != null && !displayedPreview.HasSamePaymentPlan(currentPreview))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
            RepairSelectionState.Refresh(player, force: true);
            return false;
        }

        float maxDurability = currentPreview.Item.GetMaxDurability();
        if (maxDurability <= 0f)
        {
            return false;
        }

        if (!ConsumeRequirements(player, currentPreview))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
            RepairSelectionState.Refresh(player, force: true);
            return false;
        }

        float repairedFraction = 1f - currentPreview.Item.m_durability / maxDurability;
        currentPreview.Item.m_durability = maxDurability;
        RepairCostRoundingSystem.CompleteSuccessfulRepair(player, currentPreview);
        CraftingFreeRepairSystem.CompleteSuccessfulRepair(player, currentPreview);
        RepairSelectionState.OnItemRepaired(currentPreview.Item);

        RunPostRepairAction(
            "crafting skill gain",
            () => player.RaiseSkill(Skills.SkillType.Crafting, repairedFraction));

        CraftingStation? station = player.GetCurrentCraftingStation();
        if ((currentPreview.PaymentKind == RepairPaymentKind.StationMaterials
                || currentPreview.PaymentKind == RepairPaymentKind.CraftingSkillFree)
            && station != null)
        {
            RunPostRepairAction(
                "station repair effect",
                () => station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity));
        }

        RunPostRepairAction(
            "repair message",
            () => player.Message(
                MessageHud.MessageType.Center,
                Localization.instance.Localize("$msg_repaired", currentPreview.Item.m_shared.m_name)));
        return true;
    }

    private static bool ConsumeRequirements(Player player, RepairPreview preview)
    {
        if (player.NoCostCheat()
            || preview.PaymentKind == RepairPaymentKind.Free
            || preview.PaymentKind == RepairPaymentKind.CraftingSkillFree)
        {
            return true;
        }

        if (preview.PaymentKind != RepairPaymentKind.StationMaterials)
        {
            return false;
        }

        if (!RepairCostSystem.CanAfford(player, preview))
        {
            return false;
        }

        if (preview.Costs.Count == 0)
        {
            return true;
        }

        if (preview.UsesNearbyContainers)
        {
            bool consumed = AzuCraftyBoxesCompat.TryConsume(
                player,
                preview.Costs,
                out bool shouldCompleteRepair);
            return consumed || shouldCompleteRepair;
        }

        return ConsumeInventoryRequirementsSafely(player.GetInventory(), preview.Costs);
    }

    private static bool ConsumeInventoryRequirementsSafely(
        Inventory inventory,
        IReadOnlyList<RepairMaterialCost> costs)
    {
        RepairMaterialCost[] source = costs.ToArray();
        if (source.Any(cost => cost == null || cost.SourceRequirement?.m_resItem == null))
        {
            return false;
        }

        RepairMaterialCost[] plan = source
            .Where(cost => cost.RequiredAmount > 0)
            .ToArray();

        foreach (RepairMaterialCost cost in plan)
        {
            string itemName = cost.SourceRequirement.m_resItem.m_itemData.m_shared.m_name;
            if (inventory.CountItems(itemName, -1, true) < cost.RequiredAmount)
            {
                return false;
            }
        }

        bool confirmedConsumption = false;
        foreach (RepairMaterialCost cost in plan)
        {
            string itemName = cost.SourceRequirement.m_resItem.m_itemData.m_shared.m_name;
            int beforeAmount = inventory.CountItems(itemName, -1, true);
            if (beforeAmount < cost.RequiredAmount)
            {
                LogConsumptionMismatch(
                    $"inventory amount for '{itemName}' changed before removal",
                    confirmedConsumption);
                return confirmedConsumption;
            }

            int removedAmount;
            try
            {
                inventory.RemoveItem(itemName, cost.RequiredAmount, -1, true);
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
            if (removedAmount != cost.RequiredAmount)
            {
                LogConsumptionMismatch(
                    $"inventory removed {removedAmount} instead of {cost.RequiredAmount} for '{itemName}'",
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

    private static void RunPostRepairAction(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Repair completed, but the {operation} failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
