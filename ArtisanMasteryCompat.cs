using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace RepairRequiresMaterials;

// This boundary deliberately has no compile-time reference to Artisan Mastery.
internal static class ArtisanMasteryCompat
{
    internal const string PluginGuid = "Dreanegade.Artisan_Mastery";
    private const string AnvilPath = "Upgrades/Anvil/x_FurnitureAnvil";
    private static MethodInfo? _playFx;
    private static bool _failureLogged;

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin) || plugin.Instance == null)
        {
            return;
        }

        Type? table = plugin.Instance.GetType().Assembly.GetType("Artisan_Mastery.RepairAllTable");
        MethodInfo? interact = table == null ? null : AccessTools.DeclaredMethod(
            table, "Interact", new[] { typeof(Humanoid), typeof(bool), typeof(bool) });
        MethodInfo? hover = table == null ? null : AccessTools.DeclaredMethod(table, "GetHoverText", Type.EmptyTypes);
        _playFx = table == null ? null : AccessTools.DeclaredMethod(table, "PlayFxEverybody", new[] { typeof(string) });
        if (table == null || !typeof(Component).IsAssignableFrom(table)
            || interact == null || interact.IsStatic || interact.ReturnType != typeof(bool)
            || hover == null || hover.IsStatic || hover.ReturnType != typeof(string)
            || _playFx == null || _playFx.IsStatic || _playFx.ReturnType != typeof(void)
            || !HasField(table, "_nextUseTime", typeof(float))
            || !HasField(table, "CooldownSeconds", typeof(float))
            || !HasField(table, "RpcSuccessFx", typeof(string))
            || !HasField(table, "RpcFailFx", typeof(string)))
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                "Artisan Mastery repair-table API was not recognized. Galleon repair compatibility could not be enabled.");
            return;
        }

        try
        {
            harmony.Patch(interact, prefix: new HarmonyMethod(
                typeof(ArtisanMasteryCompat), nameof(InteractPrefix)) { priority = Priority.First });
            harmony.Patch(hover, postfix: new HarmonyMethod(typeof(ArtisanMasteryCompat), nameof(HoverPostfix)));
            RepairRequiresMaterialsPlugin.Log.LogInfo("Artisan Mastery galleon anvil now uses RRM repair costs.");
        }
        catch (Exception exception)
        {
            LogFailure("patch installation", exception);
        }
    }

    private static bool HasField(Type type, string name, Type fieldType)
    {
        FieldInfo? field = AccessTools.DeclaredField(type, name);
        return field != null && !field.IsStatic && field.FieldType == fieldType;
    }

    private static bool IsGalleonAnvil(Component table)
    {
        if (table == null)
        {
            return false;
        }

        Ship? ship = table.GetComponentInParent<Ship>(true);
        return ship != null
            && string.Equals(Utils.GetPrefabName(ship.gameObject), "ShipGalleon_DO", StringComparison.Ordinal)
            && ship.transform.Find(AnvilPath) == table.transform;
    }

    // Own the interaction wrapper as well as payment so Artisan's unconditional
    // "repaired everything" message cannot overwrite the partial-repair summary.
    private static bool InteractPrefix(
        Component __instance,
        Humanoid user,
        bool hold,
        ref bool __result,
        ref float ____nextUseTime,
        float ___CooldownSeconds,
        string ___RpcSuccessFx,
        string ___RpcFailFx)
    {
        if (!IsGalleonAnvil(__instance))
        {
            return true;
        }

        __result = !hold;
        if (hold || user is not Player player || player != Player.m_localPlayer
            || !__instance.gameObject.activeInHierarchy || Time.time < ____nextUseTime)
        {
            return false;
        }

        ____nextUseTime = Time.time + Mathf.Max(0.1f, ___CooldownSeconds);
        try
        {
            (int repaired, int skipped) = RepairService.RepairAllAtGalleonAnvil(player);
            string message = repaired == 0 && skipped == 0
                ? RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_nothing")
                : skipped == 0
                    ? RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_repaired", repaired)
                    : RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_partial", repaired, skipped);
            player.Message(MessageHud.MessageType.Center, message);

            try
            {
                // Reuse the original effect RPC; inventory changes stay on the
                // interacting player's client, regardless of who owns the ship.
                _playFx!.Invoke(__instance, new object[] { repaired > 0 ? ___RpcSuccessFx : ___RpcFailFx });
            }
            catch (Exception exception)
            {
                LogFailure("repair effect", exception);
            }
        }
        catch (Exception exception)
        {
            LogFailure("repair interaction", exception);
            player.Message(MessageHud.MessageType.Center,
                RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_failed"));
        }

        // Never fall back to the original free repair after a payment or failure.
        return false;
    }

    private static void HoverPostfix(Component __instance, ref string __result)
    {
        if (!IsGalleonAnvil(__instance))
        {
            return;
        }

        string useKey = Localization.instance?.Localize("$KEY_Use") ?? "E";
        if (useKey == "$KEY_Use")
        {
            useKey = "E";
        }

        __result = $"[<color=yellow><b>{useKey}</b></color>] "
            + RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_action");
    }

    private static void LogFailure(string operation, Exception exception)
    {
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        RepairRequiresMaterialsPlugin.Log.LogWarning(
            $"Artisan Mastery {operation} failed: {exception.GetType().Name}: {exception.Message}");
    }
}
