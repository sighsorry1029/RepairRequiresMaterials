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
        RestoreFieldRepairControls(__instance);
        RepairPanelController.Refresh(__instance);
    }

    private static void RestoreFieldRepairControls(InventoryGui gui)
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player == null
            || !RepairSelectionState.TryGetSelectedPreview(player, out RepairPreview? preview)
            || preview == null
            || preview.PaymentKind != RepairPaymentKind.FieldPowder)
        {
            return;
        }

        bool affordable = RepairCostSystem.CanAfford(player, preview);
        gui.m_repairPanel.gameObject.SetActive(true);
        gui.m_repairPanelSelection.gameObject.SetActive(true);
        gui.m_repairButton.gameObject.SetActive(true);
        gui.m_repairButton.enabled = true;
        gui.m_repairButton.interactable = affordable;
        gui.m_repairButtonGlow.gameObject.SetActive(affordable);

        if (affordable)
        {
            Color glowColor = gui.m_repairButtonGlow.color;
            glowColor.a = 0.5f + Mathf.Sin(Time.time * 5f) * 0.5f;
            gui.m_repairButtonGlow.color = glowColor;
        }

        Canvas.ForceUpdateCanvases();
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
internal static class InventoryGuiHidePatch
{
    private static void Postfix()
    {
        RepairPanelController.Hide();
    }
}

[HarmonyPatch(typeof(InventoryGui), "OnDestroy")]
internal static class InventoryGuiOnDestroyPatch
{
    private static void Prefix()
    {
        RepairPanelController.Destroy();
        RepairSelectionState.Reset();
    }
}

[HarmonyPatch]
internal static class ObjectDBTierCachePatch
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ObjectDB), "Awake");
        yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        yield return AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        RepairRecipeCatalog.Invalidate();
        RepairTierResolver.Invalidate();
    }
}
