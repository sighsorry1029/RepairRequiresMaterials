using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace RepairRequiresMaterials;

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInDependency(AzuCraftyBoxesCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class RepairRequiresMaterialsPlugin : BaseUnityPlugin
{
    private readonly struct ConfigSection
    {
        internal ConfigSection(string name, int order)
        {
            Name = name;
            Order = order;
        }

        internal string Name { get; }
        internal int Order { get; }
    }

    private sealed class ConfigurationManagerAttributes
    {
        public int? CategoryOrder { get; set; }
        public int? Order { get; set; }
    }

    internal const string ModName = "RepairRequiresMaterials";
    internal const string ModVersion = "1.0.1";
    internal const string Author = "sighsorry";
    internal const string ModGuid = $"{Author}.{ModName}";

    private static readonly string ConfigFileName = $"{ModGuid}.cfg";
    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    private static readonly ConfigSection GeneralConfig = new("1 - General", 400);
    private static readonly ConfigSection RepairCostsConfig = new("2 - Repair Costs", 300);
    private static readonly ConfigSection CraftingSkillEffectsConfig = new("3 - Crafting Skill Effects", 200);
    private static readonly ConfigSection IncineratorDismantlingConfig = new("4 - Incinerator Dismantling", 100);
    private static readonly ConfigSync ConfigSync = new(ModGuid)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion,
        ModRequired = true
    };

    private readonly Harmony _harmony = new(ModGuid);
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastConfigReloadTime;

    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

    internal static ConfigEntry<Toggle> ServerConfigLocked = null!;
    internal static ConfigEntry<float> BaseMaterialCostPercent = null!;
    internal static ConfigEntry<float> QualityIncrementMaterialCostPercent = null!;
    internal static ConfigEntry<string> RepairMaterialBlacklist = null!;
    internal static ConfigEntry<Toggle> EnableCraftingSkillFreeRepairs = null!;
    internal static ConfigEntry<float> CraftingSkillFreeRepairChanceAtLevel0 = null!;
    internal static ConfigEntry<float> CraftingSkillFreeRepairChanceAtLevel100 = null!;
    internal static ConfigEntry<float> CraftingBonusOutputChanceAtLevel100 = null!;
    internal static ConfigEntry<string> CraftingBonusExcludedOutputPrefabs = null!;
    internal static ConfigEntry<float> CraftingEquipTimeReductionAtLevel100 = null!;
    internal static ConfigEntry<Toggle> EnableIncineratorDismantling = null!;
    internal static ConfigEntry<KeyCode> DismantleModifierKey = null!;
    internal static ConfigEntry<float> DismantleBaseReturnPercent = null!;
    internal static ConfigEntry<float> DismantleUpgradeReturnPercent = null!;
    internal static ConfigEntry<string> IncineratorBuildRecipe = null!;
    internal static ConfigEntry<string> AdditionalDismantleableItems = null!;
    internal static ConfigEntry<string> DismantleBlacklist = null!;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            InitializePlugin();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void InitializePlugin()
    {
        RepairRequiresMaterialsLocalization.Initialize();
        ServerConfigLocked = config(
            GeneralConfig,
            "Lock Configuration",
            Toggle.On,
            "If on, the configuration is locked and can be changed by server admins only.",
            100);
        _ = ConfigSync.AddLockingConfigEntry(ServerConfigLocked);

        BaseMaterialCostPercent = config(
            RepairCostsConfig,
            "Base Material Cost Percent",
            15f,
            new ConfigDescription(
                "Percent of each quality-1 recipe material amount used for a full repair before the durability bucket multiplier is applied.",
                new AcceptableValueRange<float>(0f, 100f)),
            300);

        QualityIncrementMaterialCostPercent = config(
            RepairCostsConfig,
            "Quality Increment Material Cost Percent",
            5f,
            new ConfigDescription(
                "Percent of each current quality increment, (quality - 1) times the recipe amount per level, used for a full repair before the durability bucket multiplier is applied.",
                new AcceptableValueRange<float>(0f, 100f)),
            200);

        RepairMaterialBlacklist = config(
            RepairCostsConfig,
            "Repair Material Blacklist",
            string.Empty,
            "Comma-, semicolon-, or newline-separated ingredient prefab names that are excluded from repair material costs. "
            + "A '*' matches any number of characters and each pattern must match the whole prefab name. "
            + "For example, 'Iron' excludes only Iron, while 'Simple_*_Socket' also matches Simple_Red_Socket. "
            + "Matching is case-insensitive; equipment and Trophy item types are always excluded separately.",
            100);
        RepairMaterialBlacklist.SettingChanged += (_, _) =>
        {
            RepairCostSystem.SetBlacklistedPrefabPatterns(RepairMaterialBlacklist.Value);
            RepairSelectionState.Reset();
        };
        RepairCostSystem.SetBlacklistedPrefabPatterns(RepairMaterialBlacklist.Value);

        EnableCraftingSkillFreeRepairs = config(
            CraftingSkillEffectsConfig,
            "Enable Free Repairs",
            Toggle.On,
            "If on, a deterministic per-item repair ticket can waive a non-zero material cost based on the repairing player's Crafting skill.",
            600);

        CraftingSkillFreeRepairChanceAtLevel0 = config(
            CraftingSkillEffectsConfig,
            "Free Repair Chance At Level 0",
            10f,
            new ConfigDescription(
                "Minimum percent chance for a material-cost repair to be free at Crafting skill 0. Higher skill levels interpolate linearly toward the level-100 setting; values above that maximum are capped to it.",
                new AcceptableValueRange<float>(0f, 100f)),
            500);

        CraftingSkillFreeRepairChanceAtLevel100 = config(
            CraftingSkillEffectsConfig,
            "Free Repair Chance At Level 100",
            30f,
            new ConfigDescription(
                "Maximum percent chance for a material-cost repair to be free at Crafting skill 100. Lower skill levels interpolate from the configured level-0 minimum.",
                new AcceptableValueRange<float>(0f, 100f)),
            400);

        CraftingBonusOutputChanceAtLevel100 = config(
            CraftingSkillEffectsConfig,
            "Bonus Output Chance At Level 100",
            25f,
            new ConfigDescription(
                "Independent per-item bonus-output chance at Crafting skill 100 for eligible stackable results made at a Crafting-skill station. Lower skill levels scale this chance linearly. The default 25 matches Valheim's normal maximum chance for a single-output craft. Set to 0 to disable Crafting-skill bonus output.",
                new AcceptableValueRange<float>(0f, 25f)),
            300);

        CraftingBonusExcludedOutputPrefabs = config(
            CraftingSkillEffectsConfig,
            "Bonus Output Excluded Prefabs",
            CraftingProductionBonusSystem.DefaultExcludedOutputPrefabPatterns,
            "Comma-, semicolon-, or newline-separated output prefab names that receive no Crafting production bonus. "
            + "A '*' matches any number of characters and each pattern must match the whole prefab name. "
            + "Matching is case-insensitive; exact names and wildcard patterns can be mixed.",
            200);
        CraftingBonusExcludedOutputPrefabs.SettingChanged += (_, _) =>
            CraftingProductionBonusSystem.SetExcludedOutputPrefabPatterns(
                CraftingBonusExcludedOutputPrefabs.Value);
        CraftingProductionBonusSystem.SetExcludedOutputPrefabPatterns(
            CraftingBonusExcludedOutputPrefabs.Value);

        CraftingEquipTimeReductionAtLevel100 = config(
            CraftingSkillEffectsConfig,
            "Equip Time Reduction At Level 100",
            50f,
            new ConfigDescription(
                "Maximum percent reduction to queued equipment equip and manual unequip time at Crafting skill 100. Lower skill levels scale the reduction linearly. Set to 0 to disable this feature; 100 makes the queued duration zero at Crafting skill 100.",
                new AcceptableValueRange<float>(0f, 100f)),
            100);

        EnableIncineratorDismantling = config(
            IncineratorDismantlingConfig,
            "Enabled",
            Toggle.On,
            "If on, the configured modifier plus Valheim's Use input dismantles eligible items in an incinerator while ordinary Use keeps its existing behavior.",
            600);

        DismantleModifierKey = config(
            IncineratorDismantlingConfig,
            "Modifier Key",
            KeyCode.LeftAlt,
            "Local modifier held together with Valheim's current Use binding to dismantle eligible items. Set to None to disable the shortcut on this client.",
            500,
            synchronizedSetting: false);

        DismantleBaseReturnPercent = config(
            IncineratorDismantlingConfig,
            "Base Material Return Percent",
            10f,
            new ConfigDescription(
                "Percent of each eligible item's quality-1 recipe material cost returned by incinerator dismantling.",
                new AcceptableValueRange<float>(0f, 100f)),
            400);

        DismantleUpgradeReturnPercent = config(
            IncineratorDismantlingConfig,
            "Cumulative Upgrade Material Return Percent",
            20f,
            new ConfigDescription(
                "Percent of all material costs from quality 2 through the item's current quality returned by incinerator dismantling.",
                new AcceptableValueRange<float>(0f, 100f)),
            300);

        IncineratorBuildRecipe = config(
            IncineratorDismantlingConfig,
            "Incinerator Build Recipe",
            IncineratorBuildRecipeSystem.DefaultRecipe,
            "Materials required to build the vanilla incinerator. Use comma-, semicolon-, or newline-separated "
            + "ItemPrefab:Amount entries, for example 'Iron:8,Copper:4,Thunderstone:1'. "
            + "Amounts must be positive integers and item prefab names are exact. Leave empty to restore the original build recipe, "
            + "or use 'None' for no build cost. This does not change normal incineration conversions or dismantling returns.",
            700);
        IncineratorBuildRecipe.SettingChanged += (_, _) => IncineratorBuildRecipeSystem.Apply();

        AdditionalDismantleableItems = config(
            IncineratorDismantlingConfig,
            "Additional Dismantleable Items",
            string.Empty,
            "Comma-, semicolon-, or newline-separated non-equipment prefab names that are additionally eligible for dismantling. "
            + "A '*' matches any number of characters and each pattern must match the whole prefab name. "
            + "For example, 'DragonEgg' adds that exact prefab and 'Perfect_*_Socket' adds every matching socket prefab. "
            + "Matching is case-insensitive; equipment remains eligible without being listed, while the item blacklist, quest-item exclusion, and recipe checks still take priority.",
            200);
        AdditionalDismantleableItems.SettingChanged += (_, _) =>
            IncineratorDismantleCostSystem.SetAdditionalDismantleablePrefabPatterns(
                AdditionalDismantleableItems.Value);
        IncineratorDismantleCostSystem.SetAdditionalDismantleablePrefabPatterns(
            AdditionalDismantleableItems.Value);

        DismantleBlacklist = config(
            IncineratorDismantlingConfig,
            "Item Blacklist",
            string.Empty,
            "Comma-separated exact prefab names that can never be dismantled, including otherwise eligible equipment and explicitly added items.",
            100);

        AdminCommands.Register();
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        Config.Save();
        SetupWatcher();
    }

    private void OnDestroy()
    {
        try
        {
            _watcher?.Dispose();
        }
        finally
        {
            _watcher = null;
            try
            {
                SaveWithRespectToConfigSet();
            }
            finally
            {
                try
                {
                    _harmony.UnpatchSelf();
                }
                finally
                {
                    RepairRequiresMaterialsLocalization.Shutdown();
                }
            }
        }
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        if (now.Ticks - _lastConfigReloadTime.Ticks < TimeSpan.TicksPerSecond)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                Log.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                SaveWithRespectToConfigSet(reload: true);
            }
            catch (Exception ex)
            {
                Log.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            if (reload)
            {
                Config.Reload();
            }

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }

    private ConfigEntry<T> config<T>(
        ConfigSection section,
        string name,
        T value,
        ConfigDescription description,
        int order,
        bool synchronizedSetting = true)
    {
        object[] existingTags = description.Tags ?? Array.Empty<object>();
        object[] tags = new object[existingTags.Length + 1];
        Array.Copy(existingTags, tags, existingTags.Length);
        tags[existingTags.Length] = new ConfigurationManagerAttributes
        {
            CategoryOrder = section.Order,
            Order = order
        };

        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            tags);

        ConfigEntry<T> configEntry = Config.Bind(section.Name, name, value, extendedDescription);
        ConfigSync.AddConfigEntry(configEntry).SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    private ConfigEntry<T> config<T>(
        ConfigSection section,
        string name,
        T value,
        string description,
        int order,
        bool synchronizedSetting = true)
    {
        return config(
            section,
            name,
            value,
            new ConfigDescription(description),
            order,
            synchronizedSetting);
    }

}

internal static class ToggleExtensions
{
    internal static bool IsOn(this RepairRequiresMaterialsPlugin.Toggle value)
    {
        return value == RepairRequiresMaterialsPlugin.Toggle.On;
    }
}
