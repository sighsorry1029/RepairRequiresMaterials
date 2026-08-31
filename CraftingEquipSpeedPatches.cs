using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace RepairRequiresMaterials;

[HarmonyPatch]
internal static class PlayerCraftingEquipSpeedPatch
{
    private static bool _missingTargetWarningLogged;

    private readonly struct QueueState
    {
        internal readonly Player.MinorActionData? ExistingAction;
        internal readonly Player.MinorActionData.ActionType ActionType;

        internal QueueState(
            Player.MinorActionData? existingAction,
            Player.MinorActionData.ActionType actionType)
        {
            ExistingAction = existingAction;
            ActionType = actionType;
        }
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase? equipMethod = AccessTools.Method(
            typeof(Player),
            nameof(Player.QueueEquipAction),
            new[] { typeof(ItemDrop.ItemData) });
        if (equipMethod != null)
        {
            yield return equipMethod;
        }
        else
        {
            LogMissingTarget(nameof(Player.QueueEquipAction));
        }

        MethodBase? unequipMethod = AccessTools.Method(
            typeof(Player),
            nameof(Player.QueueUnequipAction),
            new[] { typeof(ItemDrop.ItemData) });
        if (unequipMethod != null)
        {
            yield return unequipMethod;
        }
        else
        {
            LogMissingTarget(nameof(Player.QueueUnequipAction));
        }
    }

    private static void Prefix(
        Player __instance,
        ItemDrop.ItemData item,
        MethodBase __originalMethod,
        out QueueState __state)
    {
        Player.MinorActionData.ActionType actionType =
            __originalMethod.Name == nameof(Player.QueueUnequipAction)
                ? Player.MinorActionData.ActionType.Unequip
                : Player.MinorActionData.ActionType.Equip;
        __state = new QueueState(FindAction(__instance, item, actionType), actionType);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(
        Player __instance,
        ItemDrop.ItemData item,
        QueueState __state)
    {
        if ((UnityObject)(object)__instance == null
            || !ReferenceEquals(__instance, Player.m_localPlayer)
            || item == null)
        {
            return;
        }

        float maximumReductionPercent =
            RepairRequiresMaterialsPlugin.CraftingEquipTimeReductionAtLevel100.Value;
        if (maximumReductionPercent <= 0f || float.IsNaN(maximumReductionPercent))
        {
            return;
        }

        Player.MinorActionData? action = FindAction(__instance, item, __state.ActionType);
        if (action == null || ReferenceEquals(action, __state.ExistingAction))
        {
            return;
        }

        action.m_duration = CalculateAdjustedDuration(
            action.m_duration,
            __instance.GetSkillFactor(Skills.SkillType.Crafting),
            maximumReductionPercent);

        if (action.m_duration < 1f)
        {
            action.m_startEffect = null;
        }
    }

    private static Player.MinorActionData? FindAction(
        Player player,
        ItemDrop.ItemData? item,
        Player.MinorActionData.ActionType actionType)
    {
        if (item == null)
        {
            return null;
        }

        for (int i = player.m_actionQueue.Count - 1; i >= 0; i--)
        {
            Player.MinorActionData action = player.m_actionQueue[i];
            if (action.m_type == actionType && ReferenceEquals(action.m_item, item))
            {
                return action;
            }
        }

        return null;
    }

    private static float CalculateAdjustedDuration(
        float baseDuration,
        float skillFactor,
        float maximumReductionPercent)
    {
        if (float.IsNaN(baseDuration)
            || float.IsInfinity(baseDuration)
            || baseDuration <= 0f)
        {
            return 0f;
        }

        double skill = ClampUnit(skillFactor);
        double maximumReduction = ClampPercent(maximumReductionPercent) / 100d;
        if (skill <= 0d || maximumReduction <= 0d)
        {
            return baseDuration;
        }

        double remainingFraction = Math.Max(0d, 1d - skill * maximumReduction);
        return (float)(baseDuration * remainingFraction);
    }

    private static double ClampUnit(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0d;
        }

        return float.IsPositiveInfinity(value) || value >= 1f ? 1d : value;
    }

    private static double ClampPercent(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0d;
        }

        return float.IsPositiveInfinity(value) || value >= 100f ? 100d : value;
    }

    private static void LogMissingTarget(string methodName)
    {
        if (_missingTargetWarningLogged)
        {
            return;
        }

        _missingTargetWarningLogged = true;
        RepairRequiresMaterialsPlugin.Log.LogWarning(
            $"Crafting equip-time reduction was not fully applied because Player.{methodName} was not found.");
    }
}
