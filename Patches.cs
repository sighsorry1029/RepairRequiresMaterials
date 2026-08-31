using HarmonyLib;
using UnityEngine;

namespace RepairRequiresMaterials;

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.HaveRepairableItems))]
internal static class InventoryGuiHaveRepairableItemsPatch
{
    private static bool Prefix(ref bool __result)
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player == null)
        {
            __result = false;
            return false;
        }

        __result = RepairSelectionState.TryGetSelectedPreview(player, out RepairPreview? preview)
            && preview != null
            && RepairCostSystem.CanAfford(player, preview);
        return false;
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.RepairOneItem))]
internal static class InventoryGuiRepairOneItemPatch
{
    [HarmonyBefore("sighsorry.Homestead")]
    private static bool Prefix()
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player == null)
        {
            return false;
        }

        RepairService.TryRepairSelected(player);
        return false;
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRepair))]
internal static class InventoryGuiUpdateRepairPatch
{
    private static void Prefix()
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player != null)
        {
            RepairSelectionState.Refresh(player);
        }
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(InventoryGui __instance)
    {
        try
        {
            RepairStripController.Refresh(__instance);
        }
        finally
        {
            RepairService.FlushDirtyNotifications();
        }
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
internal static class InventoryGuiHidePatch
{
    private static void Postfix()
    {
        RepairStripController.Hide();
        RepairService.FlushDirtyNotifications();
    }
}

[HarmonyPatch(typeof(InventoryGui), "OnDestroy")]
internal static class InventoryGuiOnDestroyPatch
{
    private static void Prefix()
    {
        RepairStripController.Destroy();
        RepairSelectionState.Reset();
    }
}
