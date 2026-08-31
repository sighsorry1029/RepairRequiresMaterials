using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RepairRequiresMaterials;

[HarmonyPatch]
internal static class IncineratorBuildRecipeLifecyclePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.Awake));
        yield return AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        IncineratorBuildRecipeSystem.Apply();
    }
}

internal static class IncineratorBuildRecipeSystem
{
    internal const string DefaultRecipe = "Iron:8,Copper:4,Thunderstone:1";

    private const string IncineratorPrefabName = "incinerator";
    private static Piece? _trackedPiece;
    private static Piece.Requirement[] _originalRequirements = Array.Empty<Piece.Requirement>();
    private static string _lastWarningKey = string.Empty;

    internal static void Apply()
    {
        ZNetScene? scene = ZNetScene.instance;
        if (scene == null)
        {
            return;
        }

        GameObject? prefab = scene.GetPrefab(IncineratorPrefabName);
        if (prefab == null)
        {
            return;
        }

        Piece? piece = prefab.GetComponent<Piece>();
        if (piece == null || prefab.GetComponent<Incinerator>() == null)
        {
            return;
        }

        TrackOriginalRecipe(piece);

        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return;
        }

        string recipe = RepairRequiresMaterialsPlugin.IncineratorBuildRecipe.Value?.Trim()
            ?? string.Empty;
        if (recipe.Length == 0)
        {
            ApplyRequirements(piece, CloneRequirements(_originalRequirements));
            _lastWarningKey = string.Empty;
            return;
        }

        if (recipe.Equals("None", StringComparison.OrdinalIgnoreCase)
            || recipe.Equals("Free", StringComparison.OrdinalIgnoreCase)
            || recipe == "-")
        {
            ApplyRequirements(piece, Array.Empty<Piece.Requirement>());
            _lastWarningKey = string.Empty;
            return;
        }

        if (!TryCreateRequirements(objectDb, recipe, out Piece.Requirement[] requirements, out string error))
        {
            ApplyRequirements(piece, CloneRequirements(_originalRequirements));
            WarnInvalidRecipeOnce(recipe, error);
            return;
        }

        ApplyRequirements(piece, requirements);
        _lastWarningKey = string.Empty;
    }

    private static void TrackOriginalRecipe(Piece piece)
    {
        if (ReferenceEquals(_trackedPiece, piece))
        {
            return;
        }

        _trackedPiece = piece;
        _originalRequirements = CloneRequirements(piece.m_resources);
        _lastWarningKey = string.Empty;
    }

    private static bool TryCreateRequirements(
        ObjectDB objectDb,
        string recipe,
        out Piece.Requirement[] requirements,
        out string error)
    {
        requirements = Array.Empty<Piece.Requirement>();
        error = string.Empty;
        string[] entries = recipe.Split(
            new[] { ',', ';', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            error = "the recipe has no ingredients";
            return false;
        }

        List<Piece.Requirement> parsed = new(entries.Length);
        HashSet<string> prefabNames = new(StringComparer.Ordinal);
        HashSet<string> sharedNames = new(StringComparer.Ordinal);
        foreach (string rawEntry in entries)
        {
            string entry = rawEntry.Trim();
            int separator = entry.IndexOf(':');
            if (separator <= 0
                || separator != entry.LastIndexOf(':')
                || separator >= entry.Length - 1)
            {
                error = $"'{entry}' is not an ItemPrefab:Amount entry";
                return false;
            }

            string prefabName = entry.Substring(0, separator).Trim();
            string amountText = entry.Substring(separator + 1).Trim();
            if (prefabName.Length == 0
                || !int.TryParse(
                    amountText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int amount)
                || amount <= 0)
            {
                error = $"'{entry}' does not contain an exact item prefab and a positive integer amount";
                return false;
            }

            GameObject itemPrefab = objectDb.GetItemPrefab(prefabName);
            ItemDrop? itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null)
            {
                error = $"item prefab '{prefabName}' is not registered";
                return false;
            }

            string sharedName = itemDrop.m_itemData.m_shared.m_name?.Trim() ?? string.Empty;
            if (!prefabNames.Add(prefabName)
                || sharedName.Length == 0
                || !sharedNames.Add(sharedName))
            {
                error = $"item prefab '{prefabName}' duplicates another ingredient";
                return false;
            }

            parsed.Add(new Piece.Requirement
            {
                m_resItem = itemDrop,
                m_amount = amount,
                m_extraAmountOnlyOneIngredient = 0,
                m_amountPerLevel = 1,
                m_recover = true
            });
        }

        requirements = parsed.ToArray();
        return true;
    }

    private static void ApplyRequirements(Piece piece, Piece.Requirement[] requirements)
    {
        piece.m_resources = requirements;
        Player.m_localPlayer?.UpdateAvailablePiecesList();
    }

    private static Piece.Requirement[] CloneRequirements(Piece.Requirement[]? source)
    {
        if (source == null || source.Length == 0)
        {
            return Array.Empty<Piece.Requirement>();
        }

        Piece.Requirement[] clone = new Piece.Requirement[source.Length];
        for (int index = 0; index < source.Length; ++index)
        {
            Piece.Requirement requirement = source[index];
            clone[index] = new Piece.Requirement
            {
                m_resItem = requirement.m_resItem,
                m_amount = requirement.m_amount,
                m_extraAmountOnlyOneIngredient = requirement.m_extraAmountOnlyOneIngredient,
                m_amountPerLevel = requirement.m_amountPerLevel,
                m_recover = requirement.m_recover
            };
        }

        return clone;
    }

    private static void WarnInvalidRecipeOnce(string recipe, string error)
    {
        string warningKey = recipe + "\0" + error;
        if (string.Equals(_lastWarningKey, warningKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastWarningKey = warningKey;
        RepairRequiresMaterialsPlugin.Log.LogWarning(
            $"Invalid Incinerator Build Recipe '{recipe}': {error}. Restored the original recipe.");
    }
}

[HarmonyPatch(typeof(Incinerator), "Awake")]
internal static class IncineratorDismantleAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Incinerator __instance)
    {
        if (!IncineratorDismantlePatches.IsSupportedIncinerator(__instance))
        {
            return;
        }

        IncineratorDismantleController? controller = __instance.GetComponent<IncineratorDismantleController>();
        if (controller == null)
        {
            controller = __instance.gameObject.AddComponent<IncineratorDismantleController>();
        }

        controller.Initialize(__instance);
    }
}

[HarmonyPatch(typeof(Switch), nameof(Switch.Interact))]
internal static class IncineratorDismantleSwitchInteractPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        Switch __instance,
        Humanoid character,
        bool hold,
        ref bool __result)
    {
        if (!RepairRequiresMaterialsPlugin.EnableIncineratorDismantling.Value.IsOn()
            || !IncineratorDismantlePatches.TryGetIncineratorLever(__instance, out Incinerator? incinerator)
            || incinerator == null)
        {
            return true;
        }

        KeyCode modifier = RepairRequiresMaterialsPlugin.DismantleModifierKey.Value;
        if (modifier == KeyCode.None || !ZInput.GetKey(modifier, false))
        {
            return true;
        }

        // Once the configured modifier path is recognized, never fall back to
        // vanilla incineration, even if the custom request cannot be completed.
        __result = false;
        if (hold)
        {
            return false;
        }

        if (character is not Player player)
        {
            return false;
        }

        if (!PrivateArea.CheckAccess(incinerator.transform.position)
            || !incinerator.m_container.CheckAccess(player.GetPlayerID()))
        {
            player.Message(MessageHud.MessageType.Center, "$piece_noaccess");
            return false;
        }

        IncineratorDismantleController? controller = incinerator.GetComponent<IncineratorDismantleController>();
        if (controller == null || !controller.RequestDismantle(player))
        {
            player.Message(MessageHud.MessageType.Center, "$rrm_dismantle_unavailable");
            return false;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(Incinerator), nameof(Incinerator.GetLeverHoverText))]
internal static class IncineratorDismantleHoverTextPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Incinerator __instance, ref string __result)
    {
        if (!RepairRequiresMaterialsPlugin.EnableIncineratorDismantling.Value.IsOn()
            || !IncineratorDismantlePatches.IsSupportedIncinerator(__instance)
            || RepairRequiresMaterialsPlugin.DismantleModifierKey.Value == KeyCode.None
            || !PrivateArea.CheckAccess(__instance.transform.position, 0f, false))
        {
            return;
        }

        string modifier = RepairRequiresMaterialsPlugin.DismantleModifierKey.Value.ToString();
        __result += Localization.instance.Localize(
            $"\n[<color=yellow><b>{modifier} + $KEY_Use</b></color>] $rrm_dismantle_action");
    }
}

internal static class IncineratorDismantlePatches
{
    private const string IncineratorPrefabName = "incinerator";

    internal static bool TryGetIncineratorLever(Switch lever, out Incinerator? incinerator)
    {
        incinerator = lever != null ? lever.GetComponentInParent<Incinerator>(true) : null;
        return incinerator != null
            && incinerator.m_incinerateSwitch == lever
            && IsSupportedIncinerator(incinerator);
    }

    internal static bool IsSupportedIncinerator(Incinerator? incinerator)
    {
        if (incinerator == null)
        {
            return false;
        }

        string prefabName = incinerator.gameObject.name?.Trim() ?? string.Empty;
        const string cloneSuffix = "(Clone)";
        if (prefabName.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
        {
            prefabName = prefabName.Substring(0, prefabName.Length - cloneSuffix.Length).Trim();
        }

        return string.Equals(prefabName, IncineratorPrefabName, StringComparison.Ordinal);
    }
}
