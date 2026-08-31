using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RepairRequiresMaterials;

internal static class AdminCommands
{
    private const string SetDurabilityCommand = "rrm_setdurability";
    private static bool _registered;

    internal static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        _ = new Terminal.ConsoleCommand(
            SetDurabilityCommand,
            "<0-100> - set all durability-bearing equipment in your inventory to a percentage of its quality-adjusted maximum",
            new Terminal.ConsoleEventFailable(SetInventoryEquipmentDurability),
            isCheat: false,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: false,
            optionsFetcher: GetDurabilityOptions,
            alwaysRefreshTabOptions: false,
            remoteCommand: false,
            onlyAdmin: false);
    }

    private static object SetInventoryEquipmentDurability(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 2
            || !args.TryParameterFloat(1, out float percentage)
            || float.IsNaN(percentage)
            || float.IsInfinity(percentage)
            || percentage < 0f
            || percentage > 100f)
        {
            return $"Usage: {SetDurabilityCommand} <0-100>";
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return "A local player is not available.";
        }

        if (ZNet.instance == null || !ZNet.instance.LocalPlayerIsAdminOrHost())
        {
            return "Administrator or host privileges are required.";
        }

        Inventory inventory = player.GetInventory();
        int eligibleCount = 0;
        int changedCount = 0;
        float fraction = percentage / 100f;

        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (item == null
                || !EquipmentTypeRules.IsEquipment(item.m_shared.m_itemType)
                || !item.m_shared.m_useDurability)
            {
                continue;
            }

            float maxDurability = item.GetMaxDurability();
            if (!(maxDurability > 0f)
                || float.IsNaN(maxDurability)
                || float.IsInfinity(maxDurability))
            {
                continue;
            }

            ++eligibleCount;
            float durability = Mathf.Clamp(maxDurability * fraction, 0f, maxDurability);
            if (item.m_durability.Equals(durability))
            {
                continue;
            }

            item.m_durability = durability;
            ++changedCount;
        }

        if (changedCount > 0)
        {
            inventory.Changed();
        }

        string formattedPercentage = percentage.ToString("0.##", CultureInfo.InvariantCulture);
        args.Context?.AddString(
            $"{RepairRequiresMaterialsPlugin.ModName}: set {changedCount} of {eligibleCount} eligible equipment items to {formattedPercentage}% durability.");
        return true;
    }

    private static List<string> GetDurabilityOptions()
    {
        return new List<string> { "0", "25", "50", "75", "100" };
    }
}
