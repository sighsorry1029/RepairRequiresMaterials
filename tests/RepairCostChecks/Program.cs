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
        Equal(0, Candidates(player).Count);
        player.Station!.Level = 3;
        Equal(20, Total(Preview(player, item)));
        Equal(1, Candidates(player).Count);
        player.Station.m_name = "forge";
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
        Equal(0, Candidates(player).Count);
        player.Station = null;
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
        Equal(0, Candidates(player).Count);
        OpenAnvil(player);
        Equal(20, Total(Preview(player, item)));
        Equal(1, Candidates(player).Count);
    }),
    ("Minimum and threshold use the same policy at Galleon anvils", () =>
    {
        Plugin.MinimumOneMaterialPerRepair.Value = Toggle.On;
        Plugin.BaseMaterialCostPercent.Value = 1f;
        Plugin.FreeRepairDamageThresholdPercent.Value = 20f;
        var (player, item, _) = Setup(19f, Resource("Iron", 1));
        OpenAnvil(player);
        Equal(0, Total(Preview(player, item)));
        item.m_durability = 80f;
        Equal(1, Total(Preview(player, item)));
    }),
    ("Galleon cannot grant recipe-less free repairs", () =>
    {
        var (player, item, _) = Setup(20f, Resource("Iron", 1));
        RepairRecipeCatalog.Recipes.Clear();
        ObjectDB.instance = new ObjectDB();
        InventoryGui.instance.ExternalRepairAllowed = true;
        OpenAnvil(player);
        False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
        Equal(0, Candidates(player).Count);
    }),
    ("Invalid Galleon session blocks both candidates and previews including no-cost cheat", () =>
    {
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        OpenAnvil(player);
        ArtisanMasteryCompat.Usable = false;
        foreach (bool noCost in new[] { false, true })
        {
            player.NoCost = noCost;
            False(RepairCostSystem.TryGetRepairPreview(player, item, out _));
            Equal(0, Candidates(player).Count);
        }
    }),
    ("Galleon candidates include unaffordable gear but exclude full and unrepairable gear", () =>
    {
        var (player, item, recipe) = Setup(20f, Resource("Iron", 100));
        OpenAnvil(player);
        var full = new ItemDrop.ItemData { m_durability = 100 };
        var unrepairable = new ItemDrop.ItemData { m_durability = 20 };
        unrepairable.m_shared.m_canBeReparied = false;
        player.Inventory.Items.AddRange(new[] { full, unrepairable });
        RepairRecipeCatalog.Recipes[full] = new[] { recipe };
        RepairRecipeCatalog.Recipes[unrepairable] = new[] { recipe };
        True(Candidates(player).SequenceEqual(new[] { item }));
        False(RepairCostSystem.CanAfford(player, Preview(player, item)));
    }),
    ("Existing selection and wheel policy work at a Galleon without a second UI state", () =>
    {
        var (player, first, recipe) = Setup(20f, Resource("Iron", 100));
        OpenAnvil(player);
        var second = new ItemDrop.ItemData { m_durability = 50 };
        player.Inventory.Items.Add(second);
        RepairRecipeCatalog.Recipes[second] = new[] { recipe };
        True(RepairSelectionState.TryGetSelectedPreview(player, out var preview, force: true));
        Equal(first, preview!.Item);
        Equal(2, RepairSelectionState.CandidateCount);
        True(RepairSelectionState.SelectOffset(player, 1));
        True(RepairSelectionState.TryGetSelectedPreview(player, out preview));
        Equal(second, preview!.Item);
        Equal(50, Total(preview));
        True(RepairSelectionState.SelectOffset(player, 1));
        True(RepairSelectionState.TryGetSelectedPreview(player, out preview));
        Equal(first, preview!.Item);
    }),
    ("Click revalidation rejects a cached anvil preview after the session expires", () =>
    {
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        OpenAnvil(player);
        True(RepairSelectionState.TryGetSelectedPreview(player, out var preview, force: true));
        RepairSelectionState.MarkDisplayedPreview(preview!);
        ArtisanMasteryCompat.Usable = false;
        False(RepairSelectionState.TryGetPreviewForRepair(player, out _));
        Equal(80f, item.m_durability);
    }),
    ("Leaving the anvil does not grant its station exception to a normal station", () =>
    {
        var (player, _, recipe) = Setup(20f, Resource("Iron", 100));
        recipe.m_minStationLevel = 3;
        OpenAnvil(player);
        True(RepairSelectionState.Refresh(player, force: true));
        player.Station = new CraftingStation { m_name = "forge", Level = 1 };
        False(RepairSelectionState.TryGetPreviewForRepair(player, out _));
        Equal(0, Candidates(player).Count);
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
    ("Anvil bonus adds 15 percentage points to both skill endpoints", () =>
    {
        Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.On;
        Plugin.GalleonAnvilFreeRepairBonus.Value = 15f;
        foreach ((float skill, double roll, char outcome) in new[]
        {
            (0f, 0.09, 'F'), (0f, 0.24, 'A'), (0f, 0.26, 'P'),
            (1f, 0.29, 'F'), (1f, 0.44, 'A'), (1f, 0.46, 'P')
        })
        {
            var (player, item, _) = Setup(20f, Resource("Iron", 100));
            player.Skill = skill;
            SeedFreeRoll(item, roll, roll + 0.005);
            OpenAnvil(player);
            var preview = Preview(player, item);
            Equal(outcome.ToString(), preview.SkillFreeTicketToken.Split('|')[3]);
            Equal(outcome == 'P' ? RepairPaymentKind.StationMaterials : RepairPaymentKind.CraftingSkillFree,
                preview.PaymentKind);
        }
    }),
    ("Anvil bonus caps at 100 percent and zero adds no free outcomes", () =>
    {
        foreach ((float chance, float bonus, char outcome) in new[]
        {
            (90f, 25f, 'A'), (0f, 100f, 'A'), (0f, 0f, 'P'), (100f, 100f, 'F')
        })
        {
            EnableFreeChance(chance, bonus);
            var (player, item, _) = Setup(20f, Resource("Iron", 100));
            SeedFreeRoll(item, 0.99, 1);
            OpenAnvil(player);
            Equal(outcome.ToString(), Preview(player, item).SkillFreeTicketToken.Split('|')[3]);
        }
    }),
    ("Anvil-only outcome is fixed before visiting either station and never leaks outside", () =>
    {
        EnableFreeChance(10f, 15f);
        foreach (bool anvilFirst in new[] { false, true })
        {
            var (player, item, _) = Setup(20f, Resource("Iron", 100));
            SeedFreeRoll(item, 0.15, 0.20);
            if (anvilFirst) OpenAnvil(player);
            var initial = Preview(player, item);
            Equal("A", initial.SkillFreeTicketToken.Split('|')[3]);
            Equal(anvilFirst ? RepairPaymentKind.CraftingSkillFree : RepairPaymentKind.StationMaterials,
                initial.PaymentKind);
            for (int visit = 0; visit < 3; ++visit)
            {
                player.Station = new CraftingStation();
                var normal = Preview(player, item);
                Equal(RepairPaymentKind.StationMaterials, normal.PaymentKind);
                Equal(initial.SkillFreeTicketToken, normal.SkillFreeTicketToken);
                OpenAnvil(player);
                var anvil = Preview(player, item);
                Equal(RepairPaymentKind.CraftingSkillFree, anvil.PaymentKind);
                Equal(initial.SkillFreeTicketToken, anvil.SkillFreeTicketToken);
            }
        }
    }),
    ("Existing Free and Paid tickets survive bonus changes without new decisions", () =>
    {
        foreach (bool initiallyFree in new[] { false, true })
        {
            EnableFreeChance(initiallyFree ? 100f : 0f);
            var (player, item, _) = Setup(20f, Resource("Iron", 100));
            var initial = Preview(player, item);
            Equal(initiallyFree ? "F" : "P", initial.SkillFreeTicketToken.Split('|')[3]);
            EnableFreeChance(initiallyFree ? 0f : 100f, 100f);
            player.Skill = 1f;
            OpenAnvil(player);
            var changed = Preview(player, item);
            Equal(initial.SkillFreeTicketToken, changed.SkillFreeTicketToken);
            Equal(initial.PaymentKind, changed.PaymentKind);
        }
    }),
    ("Anvil-only ticket snapshots skill and bonus settings for its repair cycle", () =>
    {
        EnableFreeChance(10f, 15f);
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        SeedFreeRoll(item, 0.15, 0.20);
        var initial = Preview(player, item);
        Equal("A", initial.SkillFreeTicketToken.Split('|')[3]);
        EnableFreeChance(100f, 0f);
        player.Skill = 1f;
        var normal = Preview(player, item);
        Equal(initial.SkillFreeTicketToken, normal.SkillFreeTicketToken);
        Equal(RepairPaymentKind.StationMaterials, normal.PaymentKind);
        OpenAnvil(player);
        var anvil = Preview(player, item);
        Equal(initial.SkillFreeTicketToken, anvil.SkillFreeTicketToken);
        Equal(RepairPaymentKind.CraftingSkillFree, anvil.PaymentKind);
    }),
    ("Disabling free repairs suppresses anvil bonuses without creating or replacing tickets", () =>
    {
        EnableFreeChance(0f, 100f);
        Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.Off;
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        OpenAnvil(player);
        Equal(RepairPaymentKind.StationMaterials, Preview(player, item).PaymentKind);
        False(item.m_customData.ContainsKey(TicketKey));
        Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.On;
        var free = Preview(player, item);
        Equal("A", free.SkillFreeTicketToken.Split('|')[3]);
        Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.Off;
        var disabled = Preview(player, item);
        Equal(free.SkillFreeTicketToken, disabled.SkillFreeTicketToken);
        Equal(RepairPaymentKind.StationMaterials, disabled.PaymentKind);
        Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.On;
        Equal(RepairPaymentKind.CraftingSkillFree, Preview(player, item).PaymentKind);
    }),
    ("Hidden and revealed anvil-only outcomes lock Paid when their cost plan changes", () =>
    {
        EnableFreeChance(0f, 100f);
        foreach (bool revealed in new[] { false, true })
        {
            var (player, item, _) = Setup(20f, Resource("Iron", 100));
            if (revealed) OpenAnvil(player);
            var initial = Preview(player, item);
            Equal("A", initial.SkillFreeTicketToken.Split('|')[3]);
            item.m_durability = 70f;
            var changed = Preview(player, item);
            Equal("P", changed.SkillFreeTicketToken.Split('|')[3]);
            Equal(initial.SkillFreeTicketToken.Split('|')[1], changed.SkillFreeTicketToken.Split('|')[1]);
            item.m_durability = 80f;
            OpenAnvil(player);
            Equal(RepairPaymentKind.StationMaterials, Preview(player, item).PaymentKind);
        }
    }),
    ("Anvil-only ticket survives item persistence and cannot be promoted by another player's skill", () =>
    {
        EnableFreeChance(10f, 15f);
        var (player, item, recipe) = Setup(20f, Resource("Iron", 100));
        SeedFreeRoll(item, 0.15, 0.20);
        var initial = Preview(player, item);
        var restored = new ItemDrop.ItemData
        {
            m_durability = item.m_durability,
            m_dropPrefab = item.m_dropPrefab,
            m_shared = item.m_shared,
            m_customData = new Dictionary<string, string>(item.m_customData)
        };
        var recipient = new Player { Skill = 1f };
        recipient.Inventory.Items.Add(restored);
        RepairRecipeCatalog.Recipes[restored] = new[] { recipe };
        EnableFreeChance(100f, 100f);
        var normal = Preview(recipient, restored);
        Equal(initial.SkillFreeTicketToken, normal.SkillFreeTicketToken);
        Equal(RepairPaymentKind.StationMaterials, normal.PaymentKind);
        OpenAnvil(recipient);
        Equal(RepairPaymentKind.CraftingSkillFree, Preview(recipient, restored).PaymentKind);
    }),
    ("Anvil-only free payment requires a currently usable station", () =>
    {
        EnableFreeChance(0f, 100f);
        var (player, item, _) = Setup(20f, Resource("Iron", 100));
        var normal = Preview(player, item);
        OpenAnvil(player);
        ArtisanMasteryCompat.Usable = false;
        Equal(RepairPaymentKind.StationMaterials,
            CraftingFreeRepairSystem.ResolvePreview(player, normal).PaymentKind);
        ArtisanMasteryCompat.Usable = true;
        Equal(RepairPaymentKind.CraftingSkillFree,
            CraftingFreeRepairSystem.ResolvePreview(player, normal).PaymentKind);
    }),
    ("Completing an anvil-only cycle advances once for paid and free repairs", () =>
    {
        EnableFreeChance(0f, 100f);
        foreach (bool atAnvil in new[] { false, true })
        {
            var (player, item, _) = Setup(20f, Resource("Iron", 100));
            if (atAnvil) OpenAnvil(player);
            var initial = Preview(player, item);
            Equal("A", initial.SkillFreeTicketToken.Split('|')[3]);
            for (int repeat = 0; repeat < 3; ++repeat)
                Equal(initial.SkillFreeTicketToken, Preview(player, item).SkillFreeTicketToken);
            CraftingFreeRepairSystem.CompleteSuccessfulRepair(player, initial);
            CraftingFreeRepairSystem.CompleteSuccessfulRepair(player, initial);
            Equal("1", item.m_customData[TicketKey].Split('|')[2]);
            Equal("N", item.m_customData[TicketKey].Split('|')[3]);
            EnableFreeChance(0f, 0f);
            var next = Preview(player, item);
            Equal("1", next.SkillFreeTicketToken.Split('|')[2]);
            Equal("P", next.SkillFreeTicketToken.Split('|')[3]);
            EnableFreeChance(0f, 100f);
        }
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
    Plugin.GalleonAnvilFreeRepairBonus.Value = 15f;
    RepairCostSystem.SetBlacklistedPrefabPatterns("");
    RepairRecipeCatalog.Recipes.Clear();
    AzuCraftyBoxesCompat.Enabled = false;
    AzuCraftyBoxesCompat.FailCount = false;
    AzuCraftyBoxesCompat.NearbyCounts.Clear();
    ObjectDB.instance = null;
    InventoryGui.instance.ExternalRepairAllowed = false;
    Game.m_worldLevel = 0;
    ArtisanMasteryCompat.RepairStation = null;
    ArtisanMasteryCompat.Usable = true;
    UnityEngine.Time.unscaledTime = 0;
    RepairSelectionState.Reset();
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

static void OpenAnvil(Player player)
{
    player.Station = new CraftingStation { m_name = "RRM Galleon repair station" };
    ArtisanMasteryCompat.RepairStation = player.Station;
    ArtisanMasteryCompat.Usable = true;
}

static List<ItemDrop.ItemData> Candidates(Player player)
{
    var candidates = new List<ItemDrop.ItemData>();
    RepairCostSystem.GetRepairableItems(player, candidates);
    return candidates;
}

static RepairPreview Preview(Player player, ItemDrop.ItemData item)
{
    if (!RepairCostSystem.TryGetRepairPreview(player, item, out var preview) || preview == null)
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

static void SeedFreeRoll(ItemDrop.ItemData item, double minimum, double maximum)
{
    var roll = typeof(CraftingFreeRepairSystem)
        .GetMethod("GetDeterministicRoll", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .CreateDelegate<Func<string, ulong, double>>();
    for (int attempt = 0; attempt < 10000; ++attempt)
    {
        string id = Guid.NewGuid().ToString("N");
        double value = roll(id, 0UL);
        if (value < minimum || value >= maximum) continue;
        item.m_customData[TicketKey] = $"v1|{id}|0|N|";
        return;
    }
    throw new InvalidOperationException("Could not find a deterministic free-roll fixture in the requested interval.");
}

static void EnableFreeChance(float percent, float anvilBonus = 0f)
{
    Plugin.EnableCraftingSkillFreeRepairs.Value = Toggle.On;
    Plugin.CraftingSkillFreeRepairChanceAtLevel0.Value = percent;
    Plugin.CraftingSkillFreeRepairChanceAtLevel100.Value = percent;
    Plugin.GalleonAnvilFreeRepairBonus.Value = anvilBonus;
}
static int Total(RepairPreview preview) => preview.Costs.Sum(cost => cost.RequiredAmount);
static void True(bool actual) { if (!actual) throw new InvalidOperationException("Expected true."); }
static void False(bool actual) => True(!actual);
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
