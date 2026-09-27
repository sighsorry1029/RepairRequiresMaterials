using RepairRequiresMaterials;

var checks = new (string Name, Action Run)[]
{
    ("batch recalculates availability after each paid repair", FreshPayment),
    ("insufficient materials leave durability and cycles untouched", Insufficient),
    ("free repair completes each cycle once and persists zero-cost durability", FreeRepair),
    ("unsupported recipe skips; full and nonrepairable items are ignored", Unsupported),
    ("consuming an inventory stack cannot invalidate candidate iteration", Snapshot),
    ("partial consumption still completes repair without charging twice", PartialConsumption),
    ("failure before consumption does not grant repair", FailureBeforeConsumption),
    ("container partial-consumption contract is preserved", ContainerConsumption),
    ("selected repair retains message, station effect, and preview guard", SelectedRepair),
    ("callbacks cannot reenter batch or selected repair", Reentrancy),
};
int failures = 0;
foreach (var check in checks)
{
    Reset();
    try { check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception exception) { failures++; Console.WriteLine("FAIL " + check.Name + ": " + exception.Message); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} isolated service checks passed.");
return failures == 0 ? 0 : 1;

static void Reset()
{
    RepairService.FlushDirtyNotifications();
    RepairCostSystem.PreviewBuilder = (_, _, _) => null;
    RepairSelectionState.Reset();
    RepairSelectionState.ResetCount = 0;
    RepairSelectionState.RefreshCount = 0;
    RepairSelectionState.Repaired.Clear();
    RepairCostRoundingSystem.Completed.Clear();
    CraftingFreeRepairSystem.Completed.Clear();
    AzuCraftyBoxesCompat.Consumed = false;
    AzuCraftyBoxesCompat.ShouldComplete = false;
    AzuCraftyBoxesCompat.Calls = 0;
    RepairRequiresMaterialsPlugin.Log.Warnings.Clear();
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static ItemDrop.ItemData Gear(Player player, string name, float durability = 25)
{
    var item = new ItemDrop.ItemData { m_shared = new() { m_name = name }, m_durability = durability };
    player.Inventory.Items.Add(item);
    return item;
}
static void Material(Player player, string name, int amount)
{
    player.Inventory.Items.Add(new ItemDrop.ItemData
    {
        m_shared = new() { m_name = name, m_useDurability = false, m_canBeReparied = false },
        m_stack = amount
    });
}
static RepairPreview Plan(Player player, ItemDrop.ItemData item, int amount = 1, string material = "Iron")
{
    return new RepairPreview
    {
        Item = item,
        Costs = new[] { Cost(player, amount, material) }
    };
}
static RepairMaterialCost Cost(Player player, int amount, string material)
{
    return new RepairMaterialCost
    {
        SourceRequirement = new Piece.Requirement
        { m_resItem = new ItemDrop { m_itemData = new ItemDrop.ItemData { m_shared = new() { m_name = material } } } },
        RequiredAmount = amount,
        AvailableAmount = player.Inventory.CountItems(material, -1, true)
    };
}
static void FreshPayment()
{
    var player = new Player();
    var first = Gear(player, "Sword");
    var second = Gear(player, "Axe");
    Material(player, "Iron", 3);
    var observed = new List<int>();
    RepairCostSystem.PreviewBuilder = (p, item, anvil) =>
    {
        Assert(anvil, "batch did not request the special anvil policy");
        observed.Add(p.Inventory.CountItems("Iron", -1, true));
        return Plan(p, item, 2);
    };
    var result = RepairService.RepairAllAtGalleonAnvil(player);
    Assert(result == (1, 1), "expected one success and one skip");
    Assert(observed.SequenceEqual(new[] { 3, 1 }), "costs were not refreshed per item");
    Assert(first.m_durability == 100 && second.m_durability == 25, "incorrect durability");
    Assert(player.Inventory.CountItems("Iron", -1, true) == 1, "incorrect consumption");
    Assert(player.Messages.Count == 0, "batch leaked individual repair messages");
}
static void Insufficient()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    RepairCostSystem.PreviewBuilder = (p, i, _) => Plan(p, i);
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (0, 1), "missing cost should skip");
    Assert(item.m_durability == 25 && player.Inventory.RemovalCount == 0, "repair or removal happened");
    Assert(CraftingFreeRepairSystem.Completed.Count == 0 && RepairCostRoundingSystem.Completed.Count == 0,
        "failed repair advanced a cycle");
}
static void FreeRepair()
{
    var player = new Player();
    Gear(player, "Sword");
    Gear(player, "Axe");
    RepairCostSystem.PreviewBuilder = (p, item, _) =>
    {
        var plan = Plan(p, item, 99);
        plan.PaymentKind = RepairPaymentKind.CraftingSkillFree;
        return plan;
    };
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (2, 0), "free plan should repair both");
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (0, 0), "full items were repaired again");
    Assert(player.Inventory.RemovalCount == 0, "free plan consumed material");
    Assert(CraftingFreeRepairSystem.Completed.Count == 2 && RepairCostRoundingSystem.Completed.Count == 2,
        "cycles must complete exactly once per repaired item");
    Assert(player.SkillGains.SequenceEqual(new[] { 0.75f, 0.75f }), "wrong crafting XP fractions");
    Assert(player.Inventory.ChangedCount == 1, "successful batch must publish durability changes once");
}
static void Unsupported()
{
    var player = new Player();
    var unsupported = Gear(player, "NoRecipe");
    Gear(player, "Full", 100);
    Gear(player, "Unrepairable").m_shared.m_canBeReparied = false;
    Material(player, "Iron", 2);
    int previews = 0;
    RepairCostSystem.PreviewBuilder = (_, _, _) => { previews++; return null; };
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (0, 1), "only structurally worn items should count");
    Assert(previews == 1 && unsupported.m_durability == 25, "wrong unsupported handling");
}
static void Snapshot()
{
    var player = new Player();
    Material(player, "Iron", 1);
    Gear(player, "Sword");
    Gear(player, "Axe");
    RepairCostSystem.PreviewBuilder = (p, item, _) => Plan(p, item, item.m_shared.m_name == "Sword" ? 1 : 0);
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (2, 0), "candidate enumeration broke after material removal");
    Assert(player.Inventory.Items.Count == 2, "material stack should have disappeared");
}
static void PartialConsumption()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    Material(player, "Iron", 1);
    Material(player, "Tin", 1);
    player.Inventory.RejectRemoval = "Tin";
    RepairCostSystem.PreviewBuilder = (p, i, _) => new RepairPreview
    { Item = i, Costs = new[] { Cost(p, 1, "Iron"), Cost(p, 1, "Tin") } };
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (1, 0), "partial payment must not lose paid repair");
    Assert(item.m_durability == 100 && player.Inventory.CountItems("Iron", -1, true) == 0,
        "partial payment result wrong");
    Assert(player.Inventory.CountItems("Tin", -1, true) == 1 && player.Inventory.RemovalCount == 2,
        "retried or overcharged partial payment");
    Assert(RepairRequiresMaterialsPlugin.Log.Warnings.Count == 1, "partial payment should be logged");
}
static void FailureBeforeConsumption()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    Material(player, "Iron", 1);
    player.Inventory.ThrowBeforeRemoval = "Iron";
    RepairCostSystem.PreviewBuilder = (p, i, _) => Plan(p, i);
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (0, 1), "pre-payment failure should not repair");
    Assert(item.m_durability == 25 && CraftingFreeRepairSystem.Completed.Count == 0, "failure advanced state");
}
static void ContainerConsumption()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    RepairCostSystem.PreviewBuilder = (p, i, _) =>
    {
        var plan = Plan(p, i);
        plan.Costs[0].AvailableAmount = 1;
        plan.UsesNearbyContainers = true;
        return plan;
    };
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (0, 1), "unconfirmed container payment granted repair");
    AzuCraftyBoxesCompat.ShouldComplete = true;
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (1, 0), "confirmed partial container payment lost repair");
    Assert(item.m_durability == 100 && AzuCraftyBoxesCompat.Calls == 2, "unexpected container call count");
}
static void SelectedRepair()
{
    var player = new Player { Station = new CraftingStation() };
    var item = Gear(player, "Sword");
    Material(player, "Iron", 2);
    RepairSelectionState.Current = Plan(player, item);
    RepairSelectionState.Displayed = new RepairPreview { Item = item, PlanIdentity = "stale" };
    Assert(!RepairService.TryRepairSelected(player), "stale HUD plan must be rejected");
    Assert(player.Inventory.RemovalCount == 0 && RepairSelectionState.RefreshCount == 1, "stale rejection mutated payment");
    RepairSelectionState.Displayed = RepairSelectionState.Current;
    Assert(RepairService.TryRepairSelected(player), "matching selected repair failed");
    Assert(player.Station.m_repairItemDoneEffects.Count == 1, "station effect not preserved");
    Assert(player.Messages.Count == 2 && player.Messages.Last().StartsWith("$msg_repaired"), "messages not preserved");
    Assert(RepairSelectionState.Repaired.Contains(item), "selection was not advanced");
}
static void Reentrancy()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    Material(player, "Iron", 2);
    RepairCostSystem.PreviewBuilder = (p, i, _) => Plan(p, i);
    RepairSelectionState.Current = Plan(player, item);
    int callbacks = 0;
    void Reenter()
    {
        callbacks++;
        Assert(RepairService.RepairAllAtGalleonAnvil(player) == (0, 0), "nested batch entered the payment path");
        Assert(!RepairService.TryRepairSelected(player), "nested selected repair entered the payment path");
    }
    player.Inventory.AfterRemoval = _ => Reenter();
    player.Inventory.ChangedCallback = Reenter;
    Assert(RepairService.RepairAllAtGalleonAnvil(player) == (1, 0), "outer repair failed");
    Assert(callbacks == 2 && player.Inventory.RemovalCount == 1, "nested repair changed payment or missed notification");
    Assert(CraftingFreeRepairSystem.Completed.Count == 1, "nested repair advanced the cycle twice");
}
