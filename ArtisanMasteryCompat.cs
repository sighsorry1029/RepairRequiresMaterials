using System;
using System.Collections.Generic;
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
    private static Component? _anvil;
    private static CraftingStation? _station;
    private static Player? _player;
    private static InventoryGui? _gui;
    private static string _successFx = string.Empty;
    private static bool _sessionOpen;
    private static readonly List<(GameObject Object, bool WasActive)> HiddenUi = new();
    private static UIGroupHandler? _craftingGroup;
    private static GameObject? _defaultElement;
    private static GameObject? _rightStickElement;

    // Resolve original private members once, rather than relying on publicized access.
    private static AccessTools.FieldRef<InventoryGui, float> _craftTimer = null!;
    private static AccessTools.FieldRef<InventoryGui, Recipe> _craftRecipe = null!;
    private static AccessTools.FieldRef<InventoryGui, Animator> _animator = null!;
    private static AccessTools.FieldRef<UIGroupHandler, GameObject> _rightStickSelectable = null!;
    private static Action<InventoryGui, List<Recipe>> _updateRecipeList = null!;
    private static Action<InventoryGui, int, bool> _setRecipe = null!;
    private static readonly List<Recipe> EmptyRecipes = new();

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
            || !HasField(table, "RpcSuccessFx", typeof(string)))
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                "Artisan Mastery repair-table API was not recognized. Galleon repair compatibility could not be enabled.");
            return;
        }

        try
        {
            _craftTimer = AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");
            _craftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
            _animator = AccessTools.FieldRefAccess<InventoryGui, Animator>("m_animator");
            _rightStickSelectable = AccessTools.FieldRefAccess<UIGroupHandler, GameObject>("m_rightStickSelectable");
            _updateRecipeList = AccessTools.MethodDelegate<Action<InventoryGui, List<Recipe>>>(
                AccessTools.DeclaredMethod(typeof(InventoryGui), "UpdateRecipeList", new[] { typeof(List<Recipe>) }));
            _setRecipe = AccessTools.MethodDelegate<Action<InventoryGui, int, bool>>(
                AccessTools.DeclaredMethod(typeof(InventoryGui), "SetRecipe", new[] { typeof(int), typeof(bool) }));

            PatchPrefix(harmony, typeof(InventoryGui), "Update", nameof(ValidateSessionPrefix), Type.EmptyTypes);
            PatchPrefix(harmony, typeof(InventoryGui), "UpdateCraftingPanel", nameof(RepairOnlyPanelPrefix), typeof(bool));
            PatchPrefix(harmony, typeof(InventoryGui), "UpdateRecipe", nameof(RepairOnlyPanelPrefix), typeof(Player), typeof(float));
            PatchPrefix(harmony, typeof(InventoryGui), "OnCraftPressed", nameof(BlockCraftingPrefix), Type.EmptyTypes);
            PatchPrefix(harmony, typeof(InventoryGui), "DoCrafting", nameof(BlockCraftingPrefix), typeof(Player));
            PatchPrefix(harmony, typeof(InventoryGui), "UpdateRecipeGamepadInput", nameof(GamepadSelectionPrefix), Type.EmptyTypes);
            PatchPrefix(harmony, typeof(Player), nameof(Player.SetCraftingStation), nameof(ChangeStationPrefix), typeof(CraftingStation));
            PatchPrefix(harmony, typeof(Player), nameof(Player.AddKnownStation), nameof(AddKnownStationPrefix), typeof(CraftingStation));

            // Install the entry point last: a failed UI setup must not open an unguarded station.
            harmony.Patch(interact, prefix: new HarmonyMethod(
                typeof(ArtisanMasteryCompat), nameof(InteractPrefix)) { priority = Priority.First });
            harmony.Patch(hover, postfix: new HarmonyMethod(typeof(ArtisanMasteryCompat), nameof(HoverPostfix)));
            RepairRequiresMaterialsPlugin.Log.LogInfo("Artisan Mastery galleon anvil now opens RRM's repair-only station.");
        }
        catch (Exception exception)
        {
            LogFailure("patch installation", exception);
        }
    }

    private static void PatchPrefix(Harmony harmony, Type type, string method, string prefix, params Type[] arguments)
    {
        MethodInfo target = AccessTools.DeclaredMethod(type, method, arguments)
            ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(ArtisanMasteryCompat), prefix)
        {
            priority = Priority.First
        });
    }

    internal static bool IsRepairStation(CraftingStation? station)
    {
        // Reference identity still recognizes a destroyed Unity object during teardown.
        return !ReferenceEquals(_station, null) && ReferenceEquals(station, _station);
    }

    internal static bool CanUseStation(Player player)
    {
        return _sessionOpen && player != null && player == _player && player == Player.m_localPlayer
            && !player.IsDead() && !player.IsTeleporting() && !player.InCutscene()
            && _gui != null && _gui == InventoryGui.instance
            && _gui.gameObject.activeInHierarchy && _animator(_gui).GetBool("visible")
            && _anvil != null && _anvil.gameObject.activeInHierarchy
            && _station != null && _station.gameObject.activeInHierarchy
            && IsRepairStation(player.GetCurrentCraftingStation()) && _station.InUseDistance(player);
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

    // Keep Artisan's installed-anvil interaction, but never call its free batch repair.
    private static bool InteractPrefix(
        Component __instance,
        Humanoid user,
        bool hold,
        ref bool __result,
        ref float ____nextUseTime,
        float ___CooldownSeconds,
        string ___RpcSuccessFx)
    {
        if (!IsGalleonAnvil(__instance))
        {
            return true;
        }

        __result = false;
        if (hold || user is not Player player || player != Player.m_localPlayer
            || player.IsDead() || player.IsTeleporting() || player.InCutscene()
            || !__instance.gameObject.activeInHierarchy || Time.time < ____nextUseTime
            || Vector3.Distance(player.transform.position, __instance.transform.position) >= player.m_maxInteractDistance)
        {
            return false;
        }

        ____nextUseTime = Time.time + Mathf.Max(0.1f, ___CooldownSeconds);
        try
        {
            OpenStation(__instance, player, ___RpcSuccessFx);
        }
        catch (Exception exception)
        {
            LogFailure("repair interaction", exception);
            CloseStation(hideGui: true);
            player.Message(MessageHud.MessageType.Center,
                RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_failed"));
        }

        // A UI failure must not fall back to Artisan's free repair.
        return false;
    }

    private static void OpenStation(Component anvil, Player player, string successFx)
    {
        CloseStation(hideGui: true);
        InventoryGui gui = InventoryGui.instance;
        if (gui == null)
        {
            return;
        }

        // Close any container/previous crafting operation before replacing the context.
        gui.Hide();
        _anvil = anvil;
        _player = player;
        _gui = gui;
        _successFx = successFx;
        _sessionOpen = true;
        GameObject helper = new("RRM_GalleonRepairStation");
        helper.SetActive(false);
        helper.transform.SetParent(anvil.transform, false);
        try
        {
            _station = helper.AddComponent<CraftingStation>();
        }
        catch
        {
            UnityEngine.Object.Destroy(helper);
            throw;
        }
        _station.m_name = "$rrm_galleon_station";
        _station.m_craftRequireRoof = false;
        _station.m_craftRequireFire = false;
        _station.m_discoverRange = 0f;
        _station.m_rangeBuild = 0f;
        _station.m_useDistance = player.m_maxInteractDistance;
        _station.m_showBasicRecipies = false;
        _station.m_hasCraftTab = false;
        _station.m_canRepair = true;
        helper.SetActive(true);

        CancelCrafting(gui);
        CaptureUi(gui);
        _updateRecipeList(gui, EmptyRecipes);
        _setRecipe(gui, -1, false);
        RepairSelectionState.Reset();
        player.SetCraftingStation(_station);
        gui.Show(null, 3);
    }

    internal static void PlayRepairEffect(Player player)
    {
        if (!CanUseStation(player))
        {
            return;
        }

        try
        {
            // Only the original visual/sound RPC is networked; payment remains local.
            _playFx!.Invoke(_anvil, new object[] { _successFx });
        }
        catch (Exception exception)
        {
            LogFailure("repair effect", exception);
        }
    }

    internal static void CloseStation(bool hideGui = false)
    {
        if (!_sessionOpen)
        {
            return;
        }

        _sessionOpen = false;
        InventoryGui? gui = _gui;
        if (_player != null && IsRepairStation(_player.GetCurrentCraftingStation()))
        {
            _player.SetCraftingStation(null);
        }

        foreach ((GameObject obj, bool wasActive) in HiddenUi)
        {
            if (obj != null)
            {
                obj.SetActive(wasActive);
            }
        }
        HiddenUi.Clear();
        if (_craftingGroup != null)
        {
            _craftingGroup.m_defaultElement = _defaultElement;
            _rightStickSelectable(_craftingGroup) = _rightStickElement!;
        }
        _craftingGroup = null;
        _defaultElement = null;
        _rightStickElement = null;
        if (_station != null)
        {
            UnityEngine.Object.Destroy(_station.gameObject);
        }
        _station = null;
        _anvil = null;
        _player = null;
        _gui = null;
        _successFx = string.Empty;
        RepairSelectionState.Reset();
        RepairStripController.Hide();
        if (hideGui && gui != null)
        {
            gui.Hide();
        }
    }

    private static void CaptureUi(InventoryGui gui)
    {
        // Save only objects we change; no parenting/layout changes to other mods' UI.
        Remember(gui.m_tabCraft);
        Remember(gui.m_tabUpgrade);
        Remember(gui.m_recipeListRoot);
        Remember(gui.m_recipeListScroll);
        Remember(gui.m_recipeIcon);
        Remember(gui.m_recipeName);
        Remember(gui.m_recipeDecription);
        Remember(gui.m_variantButton);
        Remember(gui.m_craftButton);
        Remember(gui.m_craftCancelButton);
        Remember(gui.m_craftProgressPanel);
        Remember(gui.m_qualityPanel);
        Remember(gui.m_minStationLevelIcon);
        Remember(gui.m_minStationLevelText);
        Remember(gui.m_itemCraftType);
        Remember(gui.m_upgradeItemIcon);
        Remember(gui.m_upgradeItemDurability);
        Remember(gui.m_upgradeItemName);
        Remember(gui.m_upgradeItemQuality);
        Remember(gui.m_upgradeItemQualityArrow?.transform);
        Remember(gui.m_upgradeItemNextQuality);
        Remember(gui.m_upgradeItemIndex);
        Remember(gui.m_craftingStationIcon);
        Remember(gui.m_craftingStationLevelRoot);
        foreach (GameObject requirement in gui.m_recipeRequirementList)
        {
            Remember(requirement != null ? requirement.transform : null);
        }

        if (gui.m_uiGroups.Length > 3 && gui.m_uiGroups[3] != null)
        {
            _craftingGroup = gui.m_uiGroups[3];
            _defaultElement = _craftingGroup.m_defaultElement;
            _rightStickElement = _rightStickSelectable(_craftingGroup);
            // UpdateRepair supplies focus once the button's active/affordable state is known.
            _craftingGroup.m_defaultElement = null;
            _rightStickSelectable(_craftingGroup) = null!;
        }
    }

    internal static void RefreshRepairFocus(InventoryGui gui)
    {
        if (_sessionOpen && gui == _gui && _craftingGroup != null)
        {
            // A disabled default can make UIGroupHandler fall through to hidden craft controls.
            // Up/down selection is independent of button focus, including when materials are missing.
            _craftingGroup.m_defaultElement = gui.m_repairButton.isActiveAndEnabled && gui.m_repairButton.interactable
                ? gui.m_repairButton.gameObject
                : null;
        }
    }

    private static void Remember(Component? component)
    {
        if (component == null)
        {
            return;
        }
        GameObject obj = component.gameObject;
        foreach (var saved in HiddenUi)
        {
            if (saved.Object == obj)
            {
                return;
            }
        }
        HiddenUi.Add((obj, obj.activeSelf));
    }

    private static void CancelCrafting(InventoryGui gui)
    {
        _craftTimer(gui) = -1f;
        _craftRecipe(gui) = null!;
        gui.OnCraftPointerExit();
    }

    private static void ValidateSessionPrefix()
    {
        if (_sessionOpen && (_player == null || !CanUseStation(_player)))
        {
            CloseStation(hideGui: true);
        }
    }

    private static bool RepairOnlyPanelPrefix(InventoryGui __instance)
    {
        if (!_sessionOpen || __instance != _gui)
        {
            return true;
        }
        CancelCrafting(__instance);
        foreach (var saved in HiddenUi)
        {
            if (saved.Object != null && saved.Object.activeSelf)
            {
                saved.Object.SetActive(false);
            }
        }
        __instance.m_craftingStationName.text = RepairRequiresMaterialsLocalization.Localize("$rrm_galleon_station");
        return false;
    }

    private static bool BlockCraftingPrefix(InventoryGui __instance)
    {
        if (!_sessionOpen || __instance != _gui)
        {
            return true;
        }
        CancelCrafting(__instance);
        return false;
    }

    private static bool GamepadSelectionPrefix(InventoryGui __instance)
    {
        if (!_sessionOpen || __instance != _gui)
        {
            return true;
        }
        if (_player != null && CanUseStation(_player))
        {
            if (ZInput.GetButtonDown("JoyLStickDown") || ZInput.GetButtonDown("JoyDPadDown"))
            {
                RepairStripController.ScrollSelection(__instance, 1);
            }
            else if (ZInput.GetButtonDown("JoyLStickUp") || ZInput.GetButtonDown("JoyDPadUp"))
            {
                RepairStripController.ScrollSelection(__instance, -1);
            }
        }
        return false;
    }

    private static void ChangeStationPrefix(Player __instance, CraftingStation station)
    {
        if (_sessionOpen && __instance == _player && !IsRepairStation(station))
        {
            CloseStation();
        }
    }

    private static bool AddKnownStationPrefix(CraftingStation station) => !IsRepairStation(station);

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
