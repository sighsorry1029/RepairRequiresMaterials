using RepairRequiresMaterials;
using Plugin = RepairRequiresMaterials.RepairRequiresMaterialsPlugin;
using ItemType = ItemDrop.ItemData.ItemType;

const string RoundingKey = Plugin.ModGuid + ".RepairCostRoundingState";
const string TicketKey = Plugin.ModGuid + ".SkillFreeRepairTicket";
int passed = 0;
var tests = new (string Name, Action Run)[]
{
    ("Default threshold preserves 7/10/17/37-percent buckets", () =>
    {
        foreach ((float damage, int amount) in new[] { (7f, 0), (10f, 10), (17f, 10), (37f, 30) })
        {
            var (player, item, _) = Setup(damage, Resource("Iron", 100));
            Equal(amount, Total(Preview(player, item)));
        }
    }),
    ("Threshold 20 is exclusive and not a deductible", () =>
    {
        Plugin.FreeRepairDamageThresholdPercent.Value = 20f;
        foreach ((float damage, int amount) in new[] { (19.99f, 0), (20f, 20), (37f, 30) })
        {
            var (player, item, _) = Setup(damage, Resource("Iron", 100));
            Equal(amount, Total(Preview(player, item)));
        }
    }),
    ("Threshold 15 preserves the 10-percent priced bucket", () =>
    {
        Plugin.FreeRepairDamageThresholdPercent.Value = 15f;
        var (player, item, _) = Setup(15f, Resource("Iron", 100));
        Equal(10, Total(Preview(player, item)));
    }),
    ("Threshold 5 uses actual sub-10 damage at its boundary", () =>
    {
        Plugin.FreeRepairDamageThresholdPercent.Value = 5f;
        foreach ((float damage, int amount) in new[] { (4.99f, 0), (5f, 5), (7f, 7), (17f, 10) })
        {
            var (player, item, _) = Setup(damage, Resource("Iron", 100));
            Equal(amount, Total(Preview(player, item)));
        }
    }),
    ("Threshold 0 allows minor-damage costs; full items remain ineligible", () =>
    {
        Plugin.FreeRepairDamageThresholdPercent.Value = 0f;
        var (player, item, _) = Setup(1f, Resource("Iron", 100));
        Equal(1, Total(Preview(player, item)));
        item.m_durability = 100f;
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
    }),
    ("Threshold 100 only prices completely broken items", () =>
    {
        Plugin.FreeRepairDamageThresholdPercent.Value = 100f;
        var (player, item, _) = Setup(99f, Resource("Iron", 100));
        Equal(0, Total(Preview(player, item)));
        item.m_durability = 0f;
        Equal(100, Total(Preview(player, item)));
    }),
    ("Minimum Off retains stochastic zero and On raises it to one", () =>
    {
        Plugin.BaseMaterialCostPercent.Value = 15f;
        var (player, item, _) = Setup(100f, Resource("Iron", 1));
        SetHighRoll(item, "Iron");
        var before = Preview(player, item);
        Equal(0, Total(before));
        True(before.HasRawMaterialCost);
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Equal(1, Total(Preview(player, item)));
    }),
    ("Minimum applies per retained resource", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        var (player, item, _) = Setup(10f, Resource("Iron", 1), Resource("Wood", 1));
        var preview = Preview(player, item);
        Equal(2, preview.Costs.Count);
        True(preview.Costs.All(cost => cost.RequiredAmount == 1));
    }),
    ("Minimum does not round every positive amount upward", () =>
    {
        Plugin.BaseMaterialCostPercent.Value = 20f;
        var (player, item, _) = Setup(100f, Resource("Iron", 6));
        SetHighRoll(item, "Iron");
        Equal(1, Total(Preview(player, item)));
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Equal(1, Total(Preview(player, item)));
    }),
    ("Minimum cannot override the free threshold", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        var (player, item, _) = Setup(7f, Resource("Iron", 100));
        var preview = Preview(player, item);
        Equal(0, Total(preview));
        False(preview.HasRawMaterialCost);
        Equal("", preview.RepairCostRoundingToken);
    }),
    ("Zero cost percentages stay free even with minimum enabled", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 0f;
        Plugin.QualityIncrementMaterialCostPercent.Value = 0f;
        var (player, item, _) = Setup(100f, Resource("Iron", 10, upgrade: 20));
        item.m_quality = 4;
        var preview = Preview(player, item);
        Equal(0, Total(preview));
        False(preview.HasRawMaterialCost);
    }),
    ("Quality-only costs obey minimum while base zero remains excluded", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 0f;
        Plugin.QualityIncrementMaterialCostPercent.Value = 1f;
        var (player, item, _) = Setup(10f, Resource("Iron", 10), Resource("Wood", 10, upgrade: 1));
        item.m_quality = 2;
        var preview = Preview(player, item);
        Equal(1, preview.Costs.Count);
        Equal("Wood", preview.Costs[0].ResourcePrefabName);
        Equal(1, Total(preview));
    }),
    ("Equipment, trophies, upgrader resources and blacklist remain excluded", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        RepairCostSystem.SetBlacklistedPrefabPatterns("Blocked_*, ExactExcluded");
        var idol = Resource("Upgrader0Armor", 100);
        idol.m_upgraderResource = true;
        var (player, item, _) = Setup(100f,
            Resource("Iron", 1), Resource("LeatherArmor", 100, ItemType.Chest),
            Resource("TrophyDeer", 100, ItemType.Trophy), Resource("Blocked_Material", 100),
            Resource("ExactExcluded", 100), idol);
        var preview = Preview(player, item);
        Equal(1, preview.Costs.Count);
        Equal("Iron", preview.Costs[0].ResourcePrefabName);
    }),
    ("Only-one recipe selects one affordable nonzero alternative", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        var (player, item, recipe) = Setup(10f, Resource("Iron", 1), Resource("Wood", 1));
        recipe.m_requireOnlyOneIngredient = true;
        SetHighRoll(item, "#only-one-ingredient-group");
        player.Inventory.Counts["Wood"] = 1;
        var preview = Preview(player, item);
        Equal(1, preview.Costs.Count);
        Equal("Wood", preview.Costs[0].ResourcePrefabName);
        Equal(1, Total(preview));
    }),
    ("Only-one recipe retains a truly zero-rate alternative", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 0f;
        Plugin.QualityIncrementMaterialCostPercent.Value = 1f;
        var (player, item, recipe) = Setup(10f, Resource("Iron", 1), Resource("Wood", 1, upgrade: 1));
        item.m_quality = 2;
        recipe.m_requireOnlyOneIngredient = true;
        var preview = Preview(player, item);
        Equal(0, Total(preview));
        False(preview.HasRawMaterialCost);
    }),
    ("Normal station restrictions and Galleon exception remain distinct", () =>
    {
        var (player, item, recipe) = Setup(20f, Resource("Iron", 100));
        recipe.m_minStationLevel = 3;
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
        Equal(20, Total(Preview(player, item, true)));
        player.Station!.Level = 3;
        Equal(20, Total(Preview(player, item)));
        player.Station.m_name = "forge";
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
        player.Station = null;
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
        Equal(20, Total(Preview(player, item, true)));
    }),
    ("Minimum and threshold use the same policy at Galleon anvils", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        Plugin.FreeRepairDamageThresholdPercent.Value = 20f;
        var (player, item, _) = Setup(19f, Resource("Iron", 1));
        player.Station = null;
        Equal(0, Total(Preview(player, item, true)));
        item.m_durability = 80f;
        Equal(1, Total(Preview(player, item, true)));
    }),
    ("Galleon cannot grant recipe-less free repairs", () =>
    {
        var (player, item, _) = Setup(20f, Resource("Iron", 1));
        RepairRecipeCatalog.Recipes.Clear();
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _, true));
    }),
    ("No-cost cheat bypass remains free", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        player.NoCost = true;
        player.Station = null;
        var preview = Preview(player, item);
        Equal(RepairPaymentKind.Free, preview.PaymentKind);
        Equal(0, Total(preview));
    }),
    ("Crafting free ticket takes precedence over the minimum", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        EnableFreeChance(100f);
        var (player, item, _) = Setup(20f, Resource("Iron", 1));
        var first = Preview(player, item);
        Equal(RepairPaymentKind.CraftingSkillFree, first.PaymentKind);
        Equal(1, Total(first));
        True(RepairCostSystem.CanAfford(player, first));
        Equal(first.SkillFreeTicketToken, Preview(player, item).SkillFreeTicketToken);
    }),
    ("Config changes do not reroll the rounding ID or Paid ticket", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        EnableFreeChance(0f);
        var (player, item, _) = Setup(20f, Resource("Iron", 1));
        SetHighRoll(item, "Iron");
        var initial = Preview(player, item);
        Equal(RepairPaymentKind.StationMaterials, initial.PaymentKind);
        True(initial.SkillFreeTicketToken.Contains("|P|"));
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.Off;
        Plugin.FreeRepairDamageThresholdPercent.Value = 30f;
        EnableFreeChance(100f);
        Equal(initial.SkillFreeTicketToken, Preview(player, item).SkillFreeTicketToken);
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.FreeRepairDamageThresholdPercent.Value = 10f;
        var restored = Preview(player, item);
        Equal(initial.RepairCostRoundingToken, restored.RepairCostRoundingToken);
        Equal(initial.SkillFreeTicketToken, restored.SkillFreeTicketToken);
        Equal(RepairPaymentKind.StationMaterials, restored.PaymentKind);
    }),
    ("An invalidated Free plan stays Paid instead of rerolling", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        EnableFreeChance(100f);
        var (player, item, _) = Setup(20f, Resource("Iron", 1));
        var free = Preview(player, item);
        Equal(RepairPaymentKind.CraftingSkillFree, free.PaymentKind);
        string originalIdAndCycle = string.Join("|", free.SkillFreeTicketToken.Split('|').Take(3));
        Plugin.FreeRepairDamageThresholdPercent.Value = 30f;
        var changed = Preview(player, item);
        Equal(RepairPaymentKind.StationMaterials, changed.PaymentKind);
        Equal(originalIdAndCycle, string.Join("|", changed.SkillFreeTicketToken.Split('|').Take(3)));
        Plugin.FreeRepairDamageThresholdPercent.Value = 10f;
        Equal(RepairPaymentKind.StationMaterials, Preview(player, item).PaymentKind);
    }),
    ("Repeated previews do not advance cycles; completed repair does", () =>
    {
        EnableFreeChance(0f);
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        var initial = Preview(player, item);
        var repeat = Preview(player, item);
        Equal(initial.RepairCostRoundingToken, repeat.RepairCostRoundingToken);
        Equal(initial.SkillFreeTicketToken, repeat.SkillFreeTicketToken);
        RepairCostRoundingSystem.CompleteSuccessfulRepair(player, initial);
        CraftingFreeRepairSystem.CompleteSuccessfulRepair(player, initial);
        Equal("1", item.m_customData[RoundingKey].Split('|')[2]);
        Equal("1", item.m_customData[TicketKey].Split('|')[2]);
    }),
    ("Nearby-container counts still participate and safely fall back", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        AzuCraftyBoxesCompat.Enabled = true;
        AzuCraftyBoxesCompat.NearbyCounts["Iron"] = 2;
        var (player, item, _) = Setup(20f, Resource("Iron", 1));
        var nearby = Preview(player, item);
        True(nearby.UsesNearbyContainers);
        True(RepairCostSystem.CanAfford(player, nearby));
        AzuCraftyBoxesCompat.FailCount = true;
        var local = Preview(player, item);
        False(local.UsesNearbyContainers);
        False(RepairCostSystem.CanAfford(player, local));
    })
};

foreach (var (name, run) in tests)
{
    Reset();
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
Console.WriteLine($"{passed}/{tests.Length} checks passed.");
return passed == tests.Length ? 0 : 1;

static void Reset()
{
    Plugin.BaseMaterialCostPercent.Value = 100f;
    Plugin.QualityIncrementMaterialCostPercent.Value = 5f;
    Plugin.FreeRepairDamageThresholdPercent.Value = 10f;
    Plugin.MinimumOneMaterialPerRepair.Value = Toggle.Off;
    Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.Off;
    Plugin.CraftingSkillFreeRepairChanceAtLevel0.Value = 10f;
    Plugin.CraftingSkillFreeRepairChanceAtLevel100.Value = 30f;
    RepairCostSystem.SetBlacklistedPrefabPatterns("");
    RepairRecipeCatalog.Recipes.Clear();
    AzuCraftyBoxesCompat.Enabled = false;
    AzuCraftyBoxesCompat.FailCount = false;
    AzuCraftyBoxesCompat.NearbyCounts.Clear();
    ObjectDB.instance = null;
    InventoryGui.instance.ExternalRepairAllowed = false;
    Game.m_worldLevel = 0;
}

static (Player Player, ItemDrop.ItemData Item, Recipe Recipe) Setup(float damage, params Piece.Requirement[] resources)
{
    var player = new Player();
    var item = new ItemDrop.ItemData
    {
        m_durability = 100f - damage,
        m_dropPrefab = new UnityEngine.GameObject { name = "TestArmor" },
        m_shared = new ItemDrop.SharedData { m_name = "TestArmor", m_itemType = ItemType.Chest }
    };
    player.Inventory.Items.Add(item);
    var recipe = new Recipe { m_craftingStation = new CraftingStation(), m_resources = resources };
    RepairRecipeCatalog.Recipes[item] = new[] { recipe };
    return (player, item, recipe);
}

static Piece.Requirement Resource(string name, int amount, ItemType type = ItemType.Material, int upgrade = 0)
{
    return new Piece.Requirement
    {
        m_amount = amount,
        m_amountPerLevel = upgrade,
        m_resItem = new ItemDrop
        {
            name = name,
            m_itemData = new ItemDrop.ItemData
            {
                m_dropPrefab = new UnityEngine.GameObject { name = name },
                m_shared = new ItemDrop.SharedData { m_name = name, m_itemType = type }
            }
        }
    };
}

static RepairPreview Preview(Player player, ItemDrop.ItemData item, bool atGalleon = false)
{
    if (!RepairCostSystem.TryGetRepairPreview(player, item, out var preview, atGalleon) || preview == null)
        throw new InvalidOperationException("Expected a repair preview.");
    return preview;
}

static void SetHighRoll(ItemDrop.ItemData item, string materialKey)
{
    for (int candidate = 1; candidate < 10000; ++candidate)
    {
        string id = candidate.ToString("x32");
        if (RepairCostRoundingSystem.GetDeterministicRoll(id, 0UL, materialKey) < 0.8d) continue;
        item.m_customData[RoundingKey] = $"v1|{id}|0|N";
        return;
    }
    throw new InvalidOperationException("Could not find deterministic high-roll fixture.");
}

static void EnableFreeChance(float percent)
{
    Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.On;
    Plugin.CraftingSkillFreeRepairChanceAtLevel0.Value = percent;
    Plugin.CraftingSkillFreeRepairChanceAtLevel100.Value = percent;
}
static int Total(RepairPreview preview) => preview.Costs.Sum(cost => cost.RequiredAmount);
static void True(bool actual) { if (!actual) throw new InvalidOperationException("Expected true."); }
static void False(bool actual) => True(!actual);
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
