using RepairRequiresMaterials;

var checks = new (string Name, Action Run)[]
{
    ("each selected repair reprices after earlier payment and does not repair other gear", FreshPayment),
    ("insufficient materials leave durability and cycles untouched", Insufficient),
    ("free repair completes each cycle once and persists zero-cost durability", FreeRepair),
    ("unsupported or nonrepairable selection leaves inventory unchanged", Unsupported),
    ("Galleon repair uses its own effect instead of dummy station effects", GalleonEffect),
    ("partial consumption still completes repair without charging twice", PartialConsumption),
    ("failure before consumption does not grant repair", FailureBeforeConsumption),
    ("container partial-consumption contract is preserved", ContainerConsumption),
    ("selected repair retains message, station effect, and preview guard", SelectedRepair),
    ("effect failure does not roll back or double-charge a successful repair", EffectFailure),
    ("callbacks cannot reenter selected repair", Reentrancy),
    ("auto repair skips unaffordable and unsupported gear instead of stopping", BatchSkipsUnaffordable),
    ("auto repair reprices later items after material depletion", BatchFreshPayment),
    ("auto repair preserves paid, skill-free, and zero-cost completion hooks", BatchMixedPayments),
    ("auto and selected repair share a guard through payment and notification callbacks", BatchReentrancy),
    ("auto repair stops after a station-changing payment without losing the paid repair", BatchStationChange),
    ("auto repair rechecks station context after pricing callbacks", BatchPricingStationChange),
    ("auto repair uses a finite inventory snapshot and skips removed gear", BatchInventoryChanges),
    ("auto repair requires the current local player and a station", BatchContextRequired),
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
    RepairSelectionState.Reset();
    RepairSelectionState.ResetCount = 0;
    RepairSelectionState.RefreshCount = 0;
    RepairSelectionState.Repaired.Clear();
    RepairCostSystem.PreviewBuilder = null;
    RepairCostRoundingSystem.Completed.Clear();
    CraftingFreeRepairSystem.Completed.Clear();
    AzuCraftyBoxesCompat.Consumed = false;
    AzuCraftyBoxesCompat.ShouldComplete = false;
    AzuCraftyBoxesCompat.Calls = 0;
    RepairRequiresMaterialsPlugin.Log.Warnings.Clear();
    ArtisanMasteryCompat.RepairStation = null;
    ArtisanMasteryCompat.EffectCount = 0;
    ArtisanMasteryCompat.ThrowEffect = false;
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
    RepairSelectionState.Current = Plan(player, first, 2);
    RepairSelectionState.PreviewBuilder = p =>
    {
        observed.Add(p.Inventory.CountItems("Iron", -1, true));
        return Plan(p, RepairSelectionState.Current!.Item, 2);
    };
    Assert(RepairService.TryRepairSelected(player), "first selection should repair");
    Assert(second.m_durability == 25, "a single click repaired unselected gear");
    RepairSelectionState.Current = Plan(player, second, 2);
    Assert(!RepairService.TryRepairSelected(player), "second selection should require remaining material");
    Assert(observed.SequenceEqual(new[] { 3, 1 }), "costs were not refreshed per item");
    Assert(first.m_durability == 100 && second.m_durability == 25, "incorrect durability");
    Assert(player.Inventory.CountItems("Iron", -1, true) == 1, "incorrect consumption");
    Assert(player.Messages.Count == 2, "each attempted selection should report its result");
}
static void Insufficient()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    RepairSelectionState.Current = Plan(player, item);
    Assert(!RepairService.TryRepairSelected(player), "missing cost should not repair");
    Assert(item.m_durability == 25 && player.Inventory.RemovalCount == 0, "repair or removal happened");
    Assert(CraftingFreeRepairSystem.Completed.Count == 0 && RepairCostRoundingSystem.Completed.Count == 0,
        "failed repair advanced a cycle");
}
static void FreeRepair()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    var other = Gear(player, "Axe");
    var plan = Plan(player, item, 99);
    plan.PaymentKind = RepairPaymentKind.CraftingSkillFree;
    RepairSelectionState.Current = plan;
    Assert(RepairService.TryRepairSelected(player), "free plan should repair selected gear");
    Assert(!RepairService.TryRepairSelected(player), "full item was repaired again");
    Assert(other.m_durability == 25, "free repair mutated an unselected item");
    Assert(player.Inventory.RemovalCount == 0, "free plan consumed material");
    Assert(CraftingFreeRepairSystem.Completed.Count == 1 && RepairCostRoundingSystem.Completed.Count == 1,
        "cycles must complete exactly once per repaired item");
    Assert(player.SkillGains.SequenceEqual(new[] { 0.75f }), "wrong crafting XP fraction");
    Assert(player.Inventory.ChangedCount == 1, "successful repair must publish durability changes once");
}
static void Unsupported()
{
    var player = new Player();
    var unsupported = Gear(player, "NoRecipe");
    var full = Gear(player, "Full", 100);
    var unrepairable = Gear(player, "Unrepairable");
    unrepairable.m_shared.m_canBeReparied = false;
    Material(player, "Iron", 2);
    Assert(!RepairService.TryRepairSelected(player), "missing preview should not repair");
    foreach (var item in new[] { full, unrepairable })
    {
        RepairSelectionState.Current = Plan(player, item);
        Assert(!RepairService.TryRepairSelected(player), "structurally invalid item was repaired");
    }
    Assert(unsupported.m_durability == 25 && player.Inventory.RemovalCount == 0, "unsupported handling changed inventory");
}
static void GalleonEffect()
{
    var player = new Player { Station = new CraftingStation() };
    ArtisanMasteryCompat.RepairStation = player.Station;
    var item = Gear(player, "Sword");
    RepairSelectionState.Current = Plan(player, item);
    Assert(!RepairService.TryRepairSelected(player), "unpaid repair should fail");
    Assert(ArtisanMasteryCompat.EffectCount == 0, "failed payment played the repair effect");
    Material(player, "Iron", 1);
    RepairSelectionState.Current = Plan(player, item);
    Assert(RepairService.TryRepairSelected(player), "paid anvil repair failed");
    Assert(ArtisanMasteryCompat.EffectCount == 1, "anvil effect not played once");
    Assert(player.Station.m_repairItemDoneEffects.Count == 0, "dummy station effect was used");
    Assert(player.Inventory.Items.Count == 1, "material stack should have disappeared");
}
static void PartialConsumption()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    Material(player, "Iron", 1);
    Material(player, "Tin", 1);
    player.Inventory.RejectRemoval = "Tin";
    RepairSelectionState.Current = new RepairPreview
    { Item = item, Costs = new[] { Cost(player, 1, "Iron"), Cost(player, 1, "Tin") } };
    Assert(RepairService.TryRepairSelected(player), "partial payment must not lose paid repair");
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
    RepairSelectionState.Current = Plan(player, item);
    Assert(!RepairService.TryRepairSelected(player), "pre-payment failure should not repair");
    Assert(item.m_durability == 25 && CraftingFreeRepairSystem.Completed.Count == 0, "failure advanced state");
}
static void ContainerConsumption()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    var plan = Plan(player, item);
    plan.Costs[0].AvailableAmount = 1;
    plan.UsesNearbyContainers = true;
    RepairSelectionState.Current = plan;
    Assert(!RepairService.TryRepairSelected(player), "unconfirmed container payment granted repair");
    AzuCraftyBoxesCompat.ShouldComplete = true;
    Assert(RepairService.TryRepairSelected(player), "confirmed partial container payment lost repair");
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
    RepairSelectionState.Current = Plan(player, item);
    int callbacks = 0;
    void Reenter()
    {
        callbacks++;
        Assert(!RepairService.TryRepairSelected(player), "nested selected repair entered the payment path");
    }
    player.Inventory.AfterRemoval = _ => Reenter();
    player.Inventory.ChangedCallback = Reenter;
    Assert(RepairService.TryRepairSelected(player), "outer repair failed");
    Assert(callbacks == 2 && player.Inventory.RemovalCount == 1, "nested repair changed payment or missed notification");
    Assert(CraftingFreeRepairSystem.Completed.Count == 1, "nested repair advanced the cycle twice");
}

static void EffectFailure()
{
    var player = new Player { Station = new CraftingStation() };
    ArtisanMasteryCompat.RepairStation = player.Station;
    ArtisanMasteryCompat.ThrowEffect = true;
    var item = Gear(player, "Sword");
    Material(player, "Iron", 1);
    RepairSelectionState.Current = Plan(player, item);
    Assert(RepairService.TryRepairSelected(player), "cosmetic effect error discarded a paid repair");
    Assert(item.m_durability == 100 && player.Inventory.RemovalCount == 1, "effect error changed payment");
    Assert(CraftingFreeRepairSystem.Completed.Count == 1 && RepairRequiresMaterialsPlugin.Log.Warnings.Count == 1,
        "effect error must be logged after one completed repair");
}

static void BatchSkipsUnaffordable()
{
    var player = new Player { Station = new CraftingStation() };
    var expensive = Gear(player, "Expensive");
    var unsupported = Gear(player, "Unsupported");
    var affordable = Gear(player, "Affordable");
    Material(player, "Iron", 1);
    RepairCostSystem.PreviewBuilder = (p, item) => ReferenceEquals(item, unsupported)
        ? null : Plan(p, item, ReferenceEquals(item, expensive) ? 2 : 1);
    Assert(RepairService.RepairAllAffordable(player) == 1, "later affordable gear was skipped");
    Assert(expensive.m_durability == 25 && unsupported.m_durability == 25 && affordable.m_durability == 100,
        "incorrect batch candidate repaired");
    Assert(player.Inventory.RemovalCount == 1 && player.Messages.Count == 1,
        "unaffordable/unsupported candidates were charged or generated failed-selection messages");
    Assert(RepairSelectionState.RefreshCount == 1, "batch must refresh selection after completion");
}

static void BatchFreshPayment()
{
    var player = new Player { Station = new CraftingStation() };
    var first = Gear(player, "Sword");
    var second = Gear(player, "Axe");
    var third = Gear(player, "Knife");
    Material(player, "Iron", 3);
    var observed = new List<int>();
    RepairCostSystem.PreviewBuilder = (p, item) =>
    {
        observed.Add(p.Inventory.CountItems("Iron", -1, true));
        return Plan(p, item, ReferenceEquals(item, third) ? 1 : 2);
    };
    Assert(RepairService.RepairAllAffordable(player) == 2, "depleted material should skip expensive gear but allow cheaper gear");
    Assert(observed.SequenceEqual(new[] { 3, 1, 1 }), "batch reused stale payment amounts");
    Assert(first.m_durability == 100 && second.m_durability == 25 && third.m_durability == 100,
        "batch did not preserve inventory order");
    Assert(player.Inventory.CountItems("Iron", -1, true) == 0 && player.Inventory.RemovalCount == 2,
        "batch overcharged remaining material");
    Assert(player.Inventory.ChangedCount == 1, "batch should flush dirty inventory once");
}

static void BatchMixedPayments()
{
    var player = new Player { Station = new CraftingStation() };
    var paid = Gear(player, "Paid");
    var free = Gear(player, "SkillFree", 50);
    var zero = Gear(player, "ZeroCost", 90);
    Material(player, "Iron", 1);
    RepairCostSystem.PreviewBuilder = (p, item) =>
    {
        var plan = Plan(p, item, ReferenceEquals(item, zero) ? 0 : 1);
        if (ReferenceEquals(item, free)) plan.PaymentKind = RepairPaymentKind.CraftingSkillFree;
        return plan;
    };
    // Automatic repair has no displayed per-item payment plan; a manual stale preview must not block it.
    RepairSelectionState.Displayed = new RepairPreview { Item = paid, PlanIdentity = "stale" };
    Assert(RepairService.RepairAllAffordable(player) == 3, "mixed free/paid batch did not complete");
    Assert(player.Inventory.RemovalCount == 1, "free repairs consumed materials");
    var expected = new[] { paid, free, zero };
    Assert(CraftingFreeRepairSystem.Completed.SequenceEqual(expected)
        && RepairCostRoundingSystem.Completed.SequenceEqual(expected)
        && RepairSelectionState.Repaired.SequenceEqual(expected), "repair cycles or selection completed more than once");
    Assert(player.SkillGains.Count == 3 && Math.Abs(player.SkillGains.Sum() - 1.35f) < 0.0001f,
        "mixed repair XP was duplicated or lost");
    Assert(player.Station.m_repairItemDoneEffects.Count == 3 && player.Messages.Count == 3,
        "success feedback was not retained");
    Assert(RepairService.RepairAllAffordable(player) == 0 && CraftingFreeRepairSystem.Completed.Count == 3,
        "a second pass repeated completed repairs");
}

static void BatchReentrancy()
{
    var player = new Player { Station = new CraftingStation() };
    var item = Gear(player, "Sword");
    Material(player, "Iron", 2);
    RepairCostSystem.PreviewBuilder = (p, gear) => Plan(p, gear);
    RepairSelectionState.Current = Plan(player, item);
    int callbacks = 0;
    void Reenter()
    {
        callbacks++;
        Assert(RepairService.RepairAllAffordable(player) == 0, "nested batch entered payment path");
        Assert(!RepairService.TryRepairSelected(player), "selected repair entered a batch payment callback");
    }
    player.Inventory.AfterRemoval = _ => Reenter();
    player.Inventory.ChangedCallback = Reenter;
    Assert(RepairService.RepairAllAffordable(player) == 1 && callbacks == 2,
        "outer batch failed or callback notification was lost");
    Assert(player.Inventory.RemovalCount == 1 && CraftingFreeRepairSystem.Completed.Count == 1,
        "nested callback double-charged or completed a cycle twice");
    // The same guard also blocks a batch started from the existing single-item path.
    item.m_durability = 25;
    RepairSelectionState.Current = Plan(player, item);
    Assert(RepairService.TryRepairSelected(player) && callbacks == 4, "manual-to-batch reentrancy was not blocked");
}

static void BatchStationChange()
{
    var player = new Player { Station = new CraftingStation() };
    var originalStation = player.Station;
    var first = Gear(player, "Sword");
    var second = Gear(player, "Axe");
    Material(player, "Iron", 2);
    RepairCostSystem.PreviewBuilder = (p, item) => Plan(p, item);
    player.Inventory.AfterRemoval = _ => player.Station = new CraftingStation();
    Assert(RepairService.RepairAllAffordable(player) == 1, "batch continued after station changed");
    Assert(first.m_durability == 100 && second.m_durability == 25 && player.Inventory.RemovalCount == 1,
        "paid repair was lost or later gear was charged outside original station");
    Assert(originalStation.m_repairItemDoneEffects.Count == 0 && player.Station.m_repairItemDoneEffects.Count == 0,
        "a different station's effect was played after context changed");
    Assert(CraftingFreeRepairSystem.Completed.Count == 1 && player.Inventory.ChangedCount == 1,
        "context change lost paid completion or inventory notification");
}

static void BatchPricingStationChange()
{
    var player = new Player { Station = new CraftingStation() };
    var item = Gear(player, "Sword");
    Material(player, "Iron", 1);
    RepairCostSystem.PreviewBuilder = (p, gear) =>
    {
        p.Station = null;
        return Plan(p, gear);
    };
    Assert(RepairService.RepairAllAffordable(player) == 0, "pricing callback changed station but batch repaired anyway");
    Assert(item.m_durability == 25 && player.Inventory.RemovalCount == 0 && player.Inventory.ChangedCount == 0,
        "cancelled batch mutated inventory");
}

static void BatchInventoryChanges()
{
    var player = new Player { Station = new CraftingStation() };
    var first = Gear(player, "Sword");
    var removed = Gear(player, "Axe");
    Material(player, "Iron", 3);
    ItemDrop.ItemData? added = null;
    RepairCostSystem.PreviewBuilder = (p, item) => Plan(p, item);
    player.Inventory.AfterRemoval = _ =>
    {
        player.Inventory.Items.Remove(removed);
        added = Gear(player, "AddedDuringPayment");
    };
    Assert(RepairService.RepairAllAffordable(player) == 1, "batch followed inventory changes beyond its initial snapshot");
    Assert(first.m_durability == 100 && removed.m_durability == 25 && added?.m_durability == 25,
        "removed or newly added gear was repaired");
}

static void BatchContextRequired()
{
    var player = new Player();
    var item = Gear(player, "Sword");
    Material(player, "Iron", 1);
    RepairCostSystem.PreviewBuilder = (p, gear) => Plan(p, gear);
    Assert(RepairService.RepairAllAffordable(player) == 0, "station-less batch started");
    player.Station = new CraftingStation();
    Player.m_localPlayer = new Player();
    Assert(RepairService.RepairAllAffordable(player) == 0, "non-local batch started");
    Assert(item.m_durability == 25 && player.Inventory.RemovalCount == 0,
        "invalid context changed inventory");
}
