using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace RepairRequiresMaterials;

// Optional boundary: keep PoV's node/skill policies without a binary dependency.
internal static class PathOfValheimanCompat
{
    internal const string PluginGuid = "treextr.pathofvalheiman";
    private delegate void RepairEligibility(ItemDrop.ItemData item, ref bool result);
    private static RepairEligibility? _repairEligibility;
    private static ConfigEntry<bool>? _disableVanillaSkills;
    private static bool _autoRepairFailureLogged;
    private static bool _stationFailureLogged;

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin) || plugin.Instance == null)
        {
            return;
        }

        Assembly assembly = plugin.Instance.GetType().Assembly;
        MethodInfo? autoRepairPostfix = null;
        MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), nameof(ReplaceAutoRepairLoop));
        bool patchAttempted = false;
        try
        {
            Type? type = assembly.GetType("PassiveTree.CraftSmith+Station_Interact");
            MethodInfo? postfix = type == null ? null : AccessTools.DeclaredMethod(type, "Postfix",
                new[] { typeof(CraftingStation), typeof(Humanoid), typeof(bool) });
            MethodInfo interact = AccessTools.DeclaredMethod(typeof(CraftingStation), "Interact",
                new[] { typeof(Humanoid), typeof(bool), typeof(bool) });
            if (postfix == null || !postfix.IsStatic || postfix.ReturnType != typeof(void)
                || Harmony.GetPatchInfo(interact)?.Postfixes.Any(patch => patch.PatchMethod == postfix) != true)
            {
                throw new MissingMethodException("PoV's active station Auto Repair postfix was not found.");
            }

            // Validate before patching. Replacing only the loop's condition leaves
            // PoV's repeat/local-player/station/node guards in the original method.
            ReplaceAutoRepairLoop(PatchProcessor.GetOriginalInstructions(postfix));
            autoRepairPostfix = postfix;
            patchAttempted = true;
            harmony.Patch(postfix, transpiler: new HarmonyMethod(transpiler));
            RepairRequiresMaterialsPlugin.Log.LogInfo("Path of Valheiman Auto Repair now skips unaffordable RRM repairs.");
        }
        catch (Exception exception)
        {
            LogFailure("Auto Repair integration", exception);
            if (patchAttempted)
            {
                // Harmony may retain the added patch even when wrapper generation fails.
                // Remove only our transpiler, leaving PoV and other mods installed.
                try { harmony.Unpatch(autoRepairPostfix!, transpiler); }
                catch (Exception cleanupException) { LogFailure("Auto Repair hook cleanup", cleanupException); }
            }
        }

        try
        {
            Type? type = assembly.GetType("PassiveTree.RepairHighStation");
            MethodInfo? postfix = type == null ? null : AccessTools.DeclaredMethod(type, "Postfix",
                new[] { typeof(ItemDrop.ItemData), typeof(bool).MakeByRefType() });
            MethodInfo canRepair = AccessTools.DeclaredMethod(typeof(InventoryGui), "CanRepair",
                new[] { typeof(ItemDrop.ItemData) });
            if (postfix == null || !postfix.IsStatic || postfix.ReturnType != typeof(void)
                || Harmony.GetPatchInfo(canRepair)?.Postfixes.Any(patch => patch.PatchMethod == postfix) != true)
            {
                throw new MissingMethodException("PoV's active high-station repair predicate was not found.");
            }

            _repairEligibility = AccessTools.MethodDelegate<RepairEligibility>(postfix);
        }
        catch (Exception exception)
        {
            LogFailure("station repair integration", exception);
        }

        try
        {
            FieldInfo? field = AccessTools.DeclaredField(plugin.Instance.GetType(), "DisableVanillaSkills");
            if (field == null || !field.IsStatic || field.FieldType != typeof(ConfigEntry<bool>)
                || field.GetValue(null) is not ConfigEntry<bool> config)
            {
                throw new MissingFieldException("PoV's vanilla skill progression setting was not found.");
            }

            _disableVanillaSkills = config;
        }
        catch (Exception exception)
        {
            LogFailure("Crafting tooltip integration", exception);
        }
    }

    internal static void Shutdown()
    {
        _repairEligibility = null;
        _disableVanillaSkills = null;
        _autoRepairFailureLogged = false;
        _stationFailureLogged = false;
    }

    private static IEnumerable<CodeInstruction> ReplaceAutoRepairLoop(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo condition = AccessTools.DeclaredMethod(typeof(InventoryGui), "HaveRepairableItems", Type.EmptyTypes);
        MethodInfo repair = AccessTools.DeclaredMethod(typeof(InventoryGui), "RepairOneItem", Type.EmptyTypes);
        if (code.Count(instruction => instruction.Calls(condition)) != 1
            || code.Count(instruction => instruction.Calls(repair)) != 1)
        {
            throw new InvalidOperationException("PoV's Auto Repair loop no longer matches the supported call pattern.");
        }

        CodeInstruction call = code.Single(instruction => instruction.Calls(condition));
        call.opcode = OpCodes.Call;
        call.operand = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), nameof(RepairAffordableItems));
        return code;
    }

    private static bool RepairAffordableItems(InventoryGui gui)
    {
        try
        {
            if (gui != null && gui == InventoryGui.instance && Player.m_localPlayer != null)
            {
                RepairService.RepairAllAffordable(Player.m_localPlayer);
            }
        }
        catch (Exception exception)
        {
            if (!_autoRepairFailureLogged)
            {
                _autoRepairFailureLogged = true;
                LogFailure("automatic repair", exception);
            }
        }

        // The bounded RRM pass has already run. Never run PoV's second repair loop.
        return false;
    }

    internal static bool AllowsStationRepair(Player player, ItemDrop.ItemData item, CraftingStation station, Recipe recipe)
    {
        if (_repairEligibility == null || player != Player.m_localPlayer
            || station != player.GetCurrentCraftingStation() || ObjectDB.instance == null
            || ObjectDB.instance.GetRecipe(item) != recipe)
        {
            return false;
        }

        // PoV resolves ObjectDB.GetRecipe internally. Only accept its verdict for
        // that exact recipe, never for a cheaper alternative RRM happened to find.
        bool allowed = false;
        try
        {
            _repairEligibility(item, ref allowed);
        }
        catch (Exception exception)
        {
            if (!_stationFailureLogged)
            {
                _stationFailureLogged = true;
                LogFailure("station repair eligibility", exception);
            }

            return false;
        }

        return allowed;
    }

    internal static float? GetFrozenCraftingLevel(Player player)
    {
        return _disableVanillaSkills?.Value == true && player != null && player == Player.m_localPlayer
            ? player.GetSkillFactor(Skills.SkillType.Crafting) * 100f
            : null;
    }

    private static void LogFailure(string operation, Exception exception)
    {
        RepairRequiresMaterialsPlugin.Log.LogWarning(
            $"Path of Valheiman {operation} could not be applied: {exception.GetBaseException().Message}");
    }
}
