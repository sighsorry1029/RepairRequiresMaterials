using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RepairRequiresMaterials;

internal static class RepairService
{
    internal static bool TryRepairSelected(Player player)
    {
        if ((Object)(object)player == null)
        {
            return false;
        }

        RepairSelectionState.TryGetDisplayedPreview(player, out RepairPreview? displayedPreview);
        if (!RepairSelectionState.TryGetPreviewForRepair(player, out RepairPreview? currentPreview)
            || currentPreview == null)
        {
            player.Message(MessageHud.MessageType.Center, "$rrm_ui_no_repairable_item");
            return false;
        }

        if (displayedPreview != null && !displayedPreview.HasSamePaymentPlan(currentPreview))
        {
            player.Message(MessageHud.MessageType.Center, "$rrm_ui_plan_changed");
            RepairSelectionState.Refresh(player, force: true);
            return false;
        }

        float maxDurability = currentPreview.Item.GetMaxDurability();
        if (maxDurability <= 0f)
        {
            return false;
        }

        if (!RepairCostSystem.CanAfford(player, currentPreview)
            || !RepairCostSystem.ConsumeRequirements(player, currentPreview))
        {
            string messageToken = currentPreview.PaymentKind == RepairPaymentKind.StationMaterials
                && !currentPreview.StationReady
                    ? RepairPowderRegistry.StationUnavailableToken
                    : "$msg_missingrequirement";
            player.Message(MessageHud.MessageType.Center, messageToken);
            RepairSelectionState.Refresh(player, force: true);
            return false;
        }

        float repairedFraction = 1f - currentPreview.Item.m_durability / maxDurability;
        currentPreview.Item.m_durability = maxDurability;
        RepairSelectionState.OnItemRepaired(currentPreview.Item);

        RunPostRepairAction(
            "crafting skill gain",
            () => player.RaiseSkill(Skills.SkillType.Crafting, repairedFraction));

        CraftingStation? station = player.GetCurrentCraftingStation();
        if (currentPreview.PaymentKind == RepairPaymentKind.StationMaterials && station != null)
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
