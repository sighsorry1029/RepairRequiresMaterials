// Isolated service contract doubles; no Unity/game assembly is loaded or modified.
namespace UnityEngine
{
    public class Object { }
    public struct Vector3 { }
    public struct Quaternion { public static Quaternion identity => default; }
    public class Transform { public Vector3 position; }
}

public class ItemDrop
{
    public ItemData m_itemData = new();
    public class SharedData
    {
        public string m_name = "";
        public bool m_useDurability = true;
        public bool m_canBeReparied = true;
    }
    public class ItemData
    {
        public SharedData m_shared = new();
        public float m_durability = 25;
        public float MaxDurability = 100;
        public int m_stack = 1;
        public float GetMaxDurability() => MaxDurability;
    }
}
public class Piece
{
    public class Requirement { public ItemDrop m_resItem = new(); }
}
public class Inventory
{
    public readonly List<ItemDrop.ItemData> Items = new();
    public Action? ChangedCallback;
    public Action<string>? AfterRemoval;
    public string? RejectRemoval;
    public string? ThrowBeforeRemoval;
    public int ChangedCount;
    public int RemovalCount;
    public List<ItemDrop.ItemData> GetAllItems() => Items;
    public bool ContainsItem(ItemDrop.ItemData item) => Items.Contains(item);
    public void GetWornItems(List<ItemDrop.ItemData> output) => output.AddRange(Items.Where(
        item => item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability()));
    public int CountItems(string name, int quality, bool matchWorldLevel) => Items
        .Where(item => item.m_shared.m_name == name).Sum(item => item.m_stack);
    public void RemoveItem(string name, int amount, int quality, bool matchWorldLevel)
    {
        RemovalCount++;
        if (ThrowBeforeRemoval == name) throw new InvalidOperationException("Simulated failure before mutation");
        if (RejectRemoval == name) return;
        foreach (var item in Items.Where(item => item.m_shared.m_name == name).ToArray())
        {
            int taken = Math.Min(amount, item.m_stack);
            item.m_stack -= taken;
            amount -= taken;
            if (item.m_stack == 0) Items.Remove(item);
            if (amount == 0) break;
        }
        AfterRemoval?.Invoke(name);
    }
    public void Changed()
    {
        ChangedCount++;
        ChangedCallback?.Invoke();
    }
}
public class Player : UnityEngine.Object
{
    public static Player? m_localPlayer;
    public Player() { m_localPlayer = this; }
    public readonly Inventory Inventory = new();
    public readonly List<string> Messages = new();
    public readonly List<float> SkillGains = new();
    public bool NoCost;
    public CraftingStation? Station;
    public Inventory GetInventory() => Inventory;
    public bool NoCostCheat() => NoCost;
    public CraftingStation? GetCurrentCraftingStation() => Station;
    public void Message(MessageHud.MessageType type, string text) => Messages.Add(text);
    public void RaiseSkill(Skills.SkillType skill, float amount) => SkillGains.Add(amount);
}
public class CraftingStation : UnityEngine.Object
{
    public readonly Effects m_repairItemDoneEffects = new();
    public readonly UnityEngine.Transform transform = new();
}
public class Effects
{
    public int Count;
    public void Create(UnityEngine.Vector3 position, UnityEngine.Quaternion rotation) => Count++;
}
public class MessageHud { public enum MessageType { Center } }
public class Skills { public enum SkillType { Crafting } }
public class Localization
{
    public static Localization instance = new();
    public string Localize(string token, string name) => token + ":" + name;
}

namespace RepairRequiresMaterials
{
    internal enum RepairPaymentKind { Free, StationMaterials, CraftingSkillFree }
    internal sealed class RepairPreview
    {
        internal ItemDrop.ItemData Item = null!;
        internal RepairPaymentKind PaymentKind = RepairPaymentKind.StationMaterials;
        internal IReadOnlyList<RepairMaterialCost> Costs = Array.Empty<RepairMaterialCost>();
        internal bool UsesNearbyContainers;
        internal string PlanIdentity = "same";
        internal bool HasSamePaymentPlan(RepairPreview other) => ReferenceEquals(Item, other.Item)
            && PlanIdentity == other.PlanIdentity;
    }
    internal sealed class RepairMaterialCost
    {
        internal Piece.Requirement SourceRequirement = new();
        internal int RequiredAmount;
        internal int AvailableAmount;
    }
    internal static class RepairCostSystem
    {
        internal static bool CanRepairStructurally(Player player, ItemDrop.ItemData item) => player != null
            && item != null && player.GetInventory().ContainsItem(item) && item.m_shared.m_useDurability
            && item.m_shared.m_canBeReparied && item.GetMaxDurability() > 0
            && item.m_durability < item.GetMaxDurability();
        internal static bool CanAfford(Player player, RepairPreview preview) => preview.Costs.All(
            cost => cost.AvailableAmount >= cost.RequiredAmount);
    }
    internal static class RepairSelectionState
    {
        internal static RepairPreview? Displayed;
        internal static RepairPreview? Current;
        internal static Func<Player, RepairPreview?>? PreviewBuilder;
        internal static int ResetCount;
        internal static int RefreshCount;
        internal static readonly List<ItemDrop.ItemData> Repaired = new();
        internal static bool TryGetDisplayedPreview(Player player, out RepairPreview? preview)
        { preview = Displayed; return preview != null; }
        internal static bool TryGetPreviewForRepair(Player player, out RepairPreview? preview)
        { preview = PreviewBuilder == null ? Current : PreviewBuilder(player); return preview != null; }
        internal static bool Refresh(Player player, bool force = false) { RefreshCount++; return true; }
        internal static void OnItemRepaired(ItemDrop.ItemData item) => Repaired.Add(item);
        internal static void Reset() { Displayed = null; Current = null; PreviewBuilder = null; ResetCount++; }
    }
    internal static class ArtisanMasteryCompat
    {
        internal static CraftingStation? RepairStation;
        internal static int EffectCount;
        internal static bool ThrowEffect;
        internal static bool IsRepairStation(CraftingStation? station) => station != null
            && ReferenceEquals(station, RepairStation);
        internal static void PlayRepairEffect(Player player)
        {
            if (ThrowEffect) throw new InvalidOperationException("Simulated effect failure");
            EffectCount++;
        }
    }
    internal static class RepairCostRoundingSystem
    {
        internal static readonly List<ItemDrop.ItemData> Completed = new();
        internal static void CompleteSuccessfulRepair(Player player, RepairPreview preview) => Completed.Add(preview.Item);
    }
    internal static class CraftingFreeRepairSystem
    {
        internal static readonly List<ItemDrop.ItemData> Completed = new();
        internal static void CompleteSuccessfulRepair(Player player, RepairPreview preview) => Completed.Add(preview.Item);
    }
    internal static class AzuCraftyBoxesCompat
    {
        internal static bool Consumed;
        internal static bool ShouldComplete;
        internal static int Calls;
        internal static bool TryConsume(Player player, IReadOnlyList<RepairMaterialCost> costs, out bool shouldCompleteRepair)
        { Calls++; shouldCompleteRepair = ShouldComplete; return Consumed; }
    }
    internal static class RepairRequiresMaterialsPlugin
    {
        internal static readonly Logger Log = new();
    }
    internal class Logger
    {
        internal readonly List<string> Warnings = new();
        internal void LogWarning(string message) => Warnings.Add(message);
    }
}
