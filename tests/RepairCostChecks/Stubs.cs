// Contract doubles only. No game/Unity assembly is loaded, modified, or publicized.
namespace UnityEngine
{
    public class Object { public string name = ""; }
    public class GameObject : Object { }
    public static class Mathf
    {
        public static int Min(int left, int right) => Math.Min(left, right);
        public static float Max(float left, float right) => Math.Max(left, right);
        public static float Clamp(float value, float minimum, float maximum) => Math.Clamp(value, minimum, maximum);
        public static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);
        public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
        public static int CeilToInt(float value) => (int)Math.Ceiling(value);
    }
}

public class ItemDrop : UnityEngine.Object
{
    public ItemData m_itemData = new();
    public class SharedData
    {
        public string m_name = "";
        public bool m_useDurability = true;
        public bool m_canBeReparied = true;
        public ItemData.ItemType m_itemType = ItemData.ItemType.Material;
    }
    public class ItemData
    {
        public enum ItemType
        {
            Material, Trophy, Tool, OneHandedWeapon, TwoHandedWeapon, TwoHandedWeaponLeft,
            Bow, Shield, Torch, Helmet, Chest, Legs, Shoulder, Utility, Trinket
        }
        public SharedData m_shared = new();
        public float m_durability;
        public float MaximumDurability = 100f;
        public int m_quality = 1;
        public int m_worldLevel;
        public UnityEngine.GameObject? m_dropPrefab;
        public Dictionary<string, string> m_customData = new();
        public float GetMaxDurability() => MaximumDurability;
    }
}

public class Piece
{
    public class Requirement
    {
        public ItemDrop m_resItem = new();
        public bool m_upgraderResource;
        public int m_amount;
        public int m_amountPerLevel;
        public int GetAmount(int quality) => quality == 1 ? m_amount : m_amountPerLevel * (quality - 1);
    }
}
public class Recipe : UnityEngine.Object
{
    public bool m_enabled = true;
    public CraftingStation m_craftingStation = null!;
    public CraftingStation m_repairStation = null!;
    public int m_minStationLevel = 1;
    public bool m_requireOnlyOneIngredient;
    public Piece.Requirement[] m_resources = Array.Empty<Piece.Requirement>();
}
public class CraftingStation : UnityEngine.Object
{
    public string m_name = "workbench";
    public int Level = 1;
    public bool Usable = true;
    public bool CheckUsable(Player player, bool showMessage) => Usable;
    public int GetLevel() => Level;
}
public class Inventory
{
    public readonly List<ItemDrop.ItemData> Items = new();
    public readonly Dictionary<string, int> Counts = new();
    public bool ContainsItem(ItemDrop.ItemData item) => Items.Contains(item);
    public void GetWornItems(List<ItemDrop.ItemData> target) => target.AddRange(Items.Where(
        item => item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability()));
    public int CountItems(string name, int quality, bool matchWorldLevel) => Counts.GetValueOrDefault(name);
}
public class Player : UnityEngine.Object
{
    public readonly Inventory Inventory = new();
    public CraftingStation? Station = new();
    public bool NoCost;
    public float Skill;
    public Inventory GetInventory() => Inventory;
    public CraftingStation? GetCurrentCraftingStation() => Station;
    public bool NoCostCheat() => NoCost;
    public float GetSkillFactor(Skills.SkillType skill) => Skill;
}
public class Skills { public enum SkillType { Crafting } }
public static class Game { public static int m_worldLevel; }
public class ObjectDB : UnityEngine.Object
{
    public static ObjectDB? instance;
    public Recipe? Fallback;
    public Recipe? GetRecipe(ItemDrop.ItemData item) => Fallback;
}
public class InventoryGui : UnityEngine.Object
{
    public static InventoryGui instance = new();
    public bool ExternalRepairAllowed;
    public bool CanRepair(ItemDrop.ItemData item) => ExternalRepairAllowed;
}

namespace RepairRequiresMaterials
{
    internal enum Toggle { Off, On }
    internal static class ToggleExtensions
    {
        internal static bool IsOn(this Toggle toggle) => toggle == Toggle.On;
    }
    internal sealed class Config<T>(T initial)
    {
        internal T Value = initial;
    }
    internal static class RepairRequiresMaterialsPlugin
    {
        internal const string ModGuid = "sighsorry.RepairRequiresMaterials";
        internal static readonly Config<float> BaseMaterialCostPercent = new(15f);
        internal static readonly Config<float> QualityIncrementMaterialCostPercent = new(5f);
        internal static readonly Config<float> FreeRepairDamageThresholdPercent = new(10f);
        internal static readonly Config<Toggle> MinimumOneMaterialPerRepair = new(Toggle.Off);
        internal static readonly Config<Toggle> EnableCraftingSkillFreeRepairs = new(Toggle.Off);
        internal static readonly Config<float> CraftingSkillFreeRepairChanceAtLevel0 = new(10f);
        internal static readonly Config<float> CraftingSkillFreeRepairChanceAtLevel100 = new(30f);
        internal static readonly Logger Log = new();
    }
    internal sealed class Logger
    {
        internal readonly List<string> Warnings = new();
        internal void LogWarning(string message) => Warnings.Add(message);
    }
    internal static class RepairService
    {
        internal static int DirtyCount;
        internal static void MarkInventoryDirty(Inventory inventory) => DirtyCount++;
    }
    internal static class RepairRecipeCatalog
    {
        internal static readonly Dictionary<ItemDrop.ItemData, IReadOnlyList<Recipe>> Recipes = new();
        internal static IReadOnlyList<Recipe> GetRecipes(ItemDrop.ItemData item) => Recipes.TryGetValue(item, out var recipes)
            ? recipes : Array.Empty<Recipe>();
    }
    internal static class AzuCraftyBoxesCompat
    {
        internal static bool Enabled;
        internal static bool FailCount;
        internal static readonly Dictionary<string, int> NearbyCounts = new();
        internal static bool ShouldUseNearbyContainers() => Enabled;
        internal static bool TryCountAvailable(Player player, RepairMaterialCost cost, int local, out int available)
        {
            available = local + NearbyCounts.GetValueOrDefault(cost.ResourcePrefabName);
            return !FailCount;
        }
    }
}
