using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace RepairRequiresMaterials;

internal static class AdminCommands
{
    private const string SetDurabilityCommand = "rrm_setdurability";
    private const string SetDurabilityRequestRpc =
        RepairRequiresMaterialsPlugin.ModGuid + ".RequestSetDurability.v1";
    private const string SetDurabilityResponseRpc =
        RepairRequiresMaterialsPlugin.ModGuid + ".SetDurabilityResponse.v1";
    private const float AuthorizationTimeoutSeconds = 10f;

    private static bool _registered;
    private static int _nextRequestId;
    private static int _pendingRequestId;
    private static float _pendingPercentage;
    private static float _pendingRequestTime;
    private static ZRpc? _pendingServerRpc;
    private static Terminal? _pendingContext;

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

        ZNet zNet = ZNet.instance;
        if (zNet == null)
        {
            return "A network session is not available.";
        }

        if (!zNet.IsServer())
        {
            ZRpc? serverRpc = zNet.GetServerRPC();
            if (serverRpc == null)
            {
                return "The server connection is not available.";
            }

            int requestId = NextRequestId();
            _pendingRequestId = requestId;
            _pendingPercentage = percentage;
            _pendingRequestTime = Time.realtimeSinceStartup;
            _pendingServerRpc = serverRpc;
            _pendingContext = args.Context;

            serverRpc.Invoke(SetDurabilityRequestRpc, requestId, percentage);
            player.StartCoroutine(ExpirePendingRequest(requestId));
            args.Context?.AddString($"{RepairRequiresMaterialsPlugin.ModName}: requesting administrator authorization...");
            return true;
        }

        ApplyInventoryEquipmentDurability(player, percentage, args.Context);
        return true;
    }

    private static void ApplyInventoryEquipmentDurability(Player player, float percentage, Terminal? context)
    {
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
        AddCommandMessage(
            context,
            $"{RepairRequiresMaterialsPlugin.ModName}: set {changedCount} of {eligibleCount} eligible equipment items to {formattedPercentage}% durability.");
    }

    private static int NextRequestId()
    {
        unchecked
        {
            ++_nextRequestId;
        }

        if (_nextRequestId <= 0)
        {
            _nextRequestId = 1;
        }

        return _nextRequestId;
    }

    private static void RegisterPeerRpcs(ZNet zNet, ZRpc rpc)
    {
        if (zNet.IsServer())
        {
            rpc.Register<int, float>(
                SetDurabilityRequestRpc,
                (senderRpc, requestId, percentage) =>
                    HandleSetDurabilityRequest(zNet, senderRpc, requestId, percentage));
            return;
        }

        ZNetPeer? peer = zNet.GetPeer(rpc);
        if (peer == null || !peer.m_server)
        {
            return;
        }

        ClearPendingRequest();
        rpc.Register<int, bool, string>(
            SetDurabilityResponseRpc,
            (senderRpc, requestId, approved, message) =>
                HandleSetDurabilityResponse(zNet, senderRpc, requestId, approved, message));
    }

    private static void HandleSetDurabilityRequest(
        ZNet zNet,
        ZRpc rpc,
        int requestId,
        float percentage)
    {
        if (!zNet.IsServer())
        {
            return;
        }

        string error = ValidateRemoteRequest(zNet, rpc, requestId, percentage);
        rpc.Invoke(SetDurabilityResponseRpc, requestId, error.Length == 0, error);
    }

    private static string ValidateRemoteRequest(
        ZNet zNet,
        ZRpc rpc,
        int requestId,
        float percentage)
    {
        if (requestId <= 0
            || float.IsNaN(percentage)
            || float.IsInfinity(percentage)
            || percentage < 0f
            || percentage > 100f)
        {
            return "Invalid durability request.";
        }

        ZNetPeer? peer = zNet.GetPeer(rpc);
        if (!rpc.IsConnected() || peer == null || !peer.IsReady())
        {
            return "The requesting player is not available.";
        }

        string hostName = rpc.GetSocket().GetHostName();
        return !string.IsNullOrWhiteSpace(hostName) && zNet.IsAdmin(hostName)
            ? string.Empty
            : "Administrator or host privileges are required.";
    }

    private static void HandleSetDurabilityResponse(
        ZNet zNet,
        ZRpc rpc,
        int requestId,
        bool approved,
        string message)
    {
        if (zNet.IsServer()
            || requestId != _pendingRequestId
            || !ReferenceEquals(rpc, _pendingServerRpc))
        {
            return;
        }

        float percentage = _pendingPercentage;
        bool expired = Time.realtimeSinceStartup - _pendingRequestTime > AuthorizationTimeoutSeconds;
        Terminal? context = _pendingContext;
        ClearPendingRequest();

        if (expired)
        {
            AddCommandMessage(context, "The administrator authorization request expired.");
            return;
        }

        if (!approved)
        {
            AddCommandMessage(
                context,
                string.IsNullOrWhiteSpace(message)
                    ? "Administrator or host privileges are required."
                    : message);
            return;
        }

        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            AddCommandMessage(context, "A local player is not available.");
            return;
        }

        ApplyInventoryEquipmentDurability(player, percentage, context);
    }

    private static IEnumerator ExpirePendingRequest(int requestId)
    {
        yield return new WaitForSecondsRealtime(AuthorizationTimeoutSeconds);

        if (requestId != _pendingRequestId)
        {
            yield break;
        }

        Terminal? context = _pendingContext;
        ClearPendingRequest();
        AddCommandMessage(context, "The administrator authorization request expired.");
    }

    private static void ClearPendingRequest()
    {
        _pendingRequestId = 0;
        _pendingPercentage = 0f;
        _pendingRequestTime = 0f;
        _pendingServerRpc = null;
        _pendingContext = null;
    }

    private static void AddCommandMessage(Terminal? context, string message)
    {
        if (context != null)
        {
            context.AddString(message);
            return;
        }

        Console.instance?.AddString(message);
    }

    private static List<string> GetDurabilityOptions()
    {
        return new List<string> { "0", "25", "50", "75", "100" };
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
    private static class ZNetRpcPeerInfoPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            RegisterPeerRpcs(__instance, rpc);
        }
    }
}
