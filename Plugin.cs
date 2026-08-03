using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace RepairRequiresMaterials;

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInDependency(Jotunn.Main.ModGuid, Jotunn.Main.Version)]
[BepInDependency(AzuCraftyBoxesCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class RepairRequiresMaterialsPlugin : BaseUnityPlugin
{
    internal const string ModName = "RepairRequiresMaterials";
    internal const string ModVersion = "0.1.0";
    internal const string Author = "sighsorry";
    internal const string ModGuid = $"{Author}.{ModName}";

    private static readonly string ConfigFileName = $"{ModGuid}.cfg";
    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    private static readonly ConfigSync ConfigSync = new(ModGuid)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };

    private readonly Harmony _harmony = new(ModGuid);
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastConfigReloadTime;

    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

    internal static RepairRequiresMaterialsPlugin Instance = null!;
    internal static string ConnectionError = string.Empty;
    internal static ConfigEntry<Toggle> ServerConfigLocked = null!;
    internal static ConfigEntry<float> RepairCostPercent = null!;
    internal static ConfigEntry<Toggle> EnableFieldRepair = null!;
    internal static ConfigEntry<float> PowderRepairPercent = null!;
    internal static ConfigEntry<string> ItemBiomeOverrides = null!;
    internal static ConfigEntry<string> IngredientBiomeOverrides = null!;
    internal static ConfigEntry<Toggle> UseAzuCraftyBoxesContainers = null!;
    internal static ConfigEntry<Toggle> ShowRepairTooltip = null!;
    internal static ConfigEntry<Toggle> ShowAvailableAmountInTooltip = null!;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        Instance = this;

        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        ServerConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
        _ = ConfigSync.AddLockingConfigEntry(ServerConfigLocked);

        RepairCostPercent = config(
            "2 - Repair",
            "Repair Material Percent",
            50f,
            new ConfigDescription(
                "Percent of the recipe material cost used as the base repair cost before the durability bucket multiplier is applied.",
                new AcceptableValueRange<float>(0f, 100f)));

        EnableFieldRepair = config(
            "2 - Repair",
            "Enable Field Repair",
            Toggle.On,
            "If on, damaged items can be repaired without a matching usable crafting station by consuming their biome repair powder.");

        PowderRepairPercent = config(
            "2 - Repair",
            "Durability Repaired Per Powder",
            25f,
            new ConfigDescription(
                "Percentage of an item's maximum durability covered by one biome repair powder. The repair always restores the item fully and rounds the powder cost up.",
                new AcceptableValueRange<float>(1f, 100f)));

        ItemBiomeOverrides = config(
            "2 - Repair",
            "Item Biome Overrides",
            string.Empty,
            "Optional item-prefab overrides in the form ItemPrefab=Biome. Separate multiple entries with commas, semicolons, or new lines.");

        IngredientBiomeOverrides = config(
            "2 - Repair",
            "Ingredient Biome Overrides",
            string.Empty,
            "Optional ingredient-prefab mappings in the form IngredientPrefab=Biome. Separate multiple entries with commas, semicolons, or new lines.");

        UseAzuCraftyBoxesContainers = config(
            "2 - Repair",
            "Use AzuCraftyBoxes Containers",
            Toggle.On,
            "If on, nearby containers from AzuCraftyBoxes are counted and consumed for repair costs when that mod is loaded.");

        ShowRepairTooltip = config(
            "3 - UI",
            "Show Repair Tooltip",
            Toggle.On,
            "If on, hovering the repair hammer shows the next repair target and its required materials.");

        ShowAvailableAmountInTooltip = config(
            "3 - UI",
            "Show Available Amounts",
            Toggle.On,
            "If on, the repair panel and tooltip show available and required amounts for each material.");

        ReloadTierOverrides();
        ItemBiomeOverrides.SettingChanged += (_, _) => ReloadTierOverrides();
        IngredientBiomeOverrides.SettingChanged += (_, _) => ReloadTierOverrides();

        RepairPowderRegistry.InitializeLocalization();
        RepairPowderRegistry.Initialize();

        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        SetupWatcher();

        Config.Save();
        Config.SaveOnConfigSet = saveOnSet;
    }

    private void OnDestroy()
    {
        SaveWithRespectToConfigSet();
        _watcher?.Dispose();
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.IncludeSubdirectories = true;
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

        if (reload)
        {
            Config.Reload();
        }

        Config.Save();
        Config.SaveOnConfigSet = originalSaveOnSet;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            description.Tags);

        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        ConfigSync.AddConfigEntry(configEntry).SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    private static void ReloadTierOverrides()
    {
        foreach (string warning in RepairTierResolver.ReloadOverrides(
                     ItemBiomeOverrides.Value,
                     IngredientBiomeOverrides.Value))
        {
            Log.LogWarning(warning);
        }
    }
}

internal static class ToggleExtensions
{
    internal static bool IsOn(this RepairRequiresMaterialsPlugin.Toggle value)
    {
        return value == RepairRequiresMaterialsPlugin.Toggle.On;
    }
}
