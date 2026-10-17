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
    private static bool _repairInProgress;

    internal static bool TryRepairSelected(Player player)
    {
        if (_repairInProgress)
        {
            return false;
        }

        _repairInProgress = true;
        try
        {
            return TryRepairSelectedCore(player);
        }
        finally
        {
            FinishRepairOperation();
        }
    }

    internal static int RepairAllAffordable(Player player)
    {
        if (_repairInProgress || (Object)(object)player == null
            || !ReferenceEquals(player, Player.m_localPlayer))
        {
            return 0;
        }

        CraftingStation? station = player.GetCurrentCraftingStation();
        if (station == null)
        {
            return 0;
        }

        _repairInProgress = true;
        int repaired = 0;
        try
        {
            // Snapshot once: inventory callbacks must not extend or restart an auto-repair pass.
            List<ItemDrop.ItemData> candidates = new();
            RepairCostSystem.GetRepairableItems(player, candidates);
            HashSet<ItemDrop.ItemData> attempted = new();
            foreach (ItemDrop.ItemData item in candidates)
            {
                if (!HasSameRepairContext(player, station))
                {
                    break;
                }

                if (!attempted.Add(item)
                    || !RepairCostSystem.TryGetRepairPreview(player, item, out RepairPreview? preview)
                    || preview == null)
                {
                    continue;
                }

                // Reprice after the previous payment, and recheck callbacks before charging.
                if (!HasSameRepairContext(player, station))
                {
                    break;
                }

                if (!TryRepairItem(player, preview))
                {
                    continue;
                }

                repaired++;
                NotifyRepairCompleted(player, preview,
                    HasSameRepairContext(player, station) ? station : null);
            }

            return repaired;
        }
        finally
        {
            try
            {
                if (repaired > 0)
                {
                    RunPostRepairAction("repair selection refresh",
                        () => RepairSelectionState.Refresh(player, force: true));
                }
            }
            finally
            {
                FinishRepairOperation();
            }
        }
    }

    private static bool HasSameRepairContext(Player player, CraftingStation station)
    {
        return (Object)(object)player != null && station != null
            && ReferenceEquals(player, Player.m_localPlayer)
            && ReferenceEquals(player.GetCurrentCraftingStation(), station);
    }

    private static void FinishRepairOperation()
    {
        try
        {
            FlushDirtyNotifications();
        }
        finally
        {
            _repairInProgress = false;
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

        if (!TryRepairItem(player, currentPreview))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
            RepairSelectionState.Refresh(player, force: true);
            return false;
        }

        NotifyRepairCompleted(player, currentPreview, player.GetCurrentCraftingStation());
        return true;
    }

    private static void NotifyRepairCompleted(Player player, RepairPreview preview, CraftingStation? station)
    {
        RepairSelectionState.OnItemRepaired(preview.Item);
        if (ArtisanMasteryCompat.IsRepairStation(station))
        {
            RunPostRepairAction("galleon repair effect", () => ArtisanMasteryCompat.PlayRepairEffect(player));
        }
        else if ((preview.PaymentKind == RepairPaymentKind.StationMaterials
                || preview.PaymentKind == RepairPaymentKind.CraftingSkillFree)
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
                Localization.instance.Localize("$msg_repaired", preview.Item.m_shared.m_name)));
    }

    private static bool TryRepairItem(Player player, RepairPreview preview)
    {
        if (!RepairCostSystem.CanRepairStructurally(player, preview.Item))
        {
            return false;
        }

        float maxDurability = preview.Item.GetMaxDurability();
        if (maxDurability <= 0f || !ConsumeRequirements(player, preview))
        {
            return false;
        }

        float repairedFraction = 1f - preview.Item.m_durability / maxDurability;
        preview.Item.m_durability = maxDurability;
        MarkInventoryDirty(player.GetInventory());
        RepairCostRoundingSystem.CompleteSuccessfulRepair(player, preview);
        CraftingFreeRepairSystem.CompleteSuccessfulRepair(player, preview);
        RunPostRepairAction(
            "crafting skill gain",
            () => player.RaiseSkill(Skills.SkillType.Crafting, repairedFraction));
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
