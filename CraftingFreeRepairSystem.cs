using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RepairRequiresMaterials;

internal static class CraftingFreeRepairSystem
{
    private const string TicketKey = RepairRequiresMaterialsPlugin.ModGuid + ".SkillFreeRepairTicket";
    private const string TicketSchema = "v1";
    private const string RollDomain = RepairRequiresMaterialsPlugin.ModGuid + ".SkillFreeRepairRoll.v1";

    private enum TicketOutcome
    {
        None,
        Free,
        Paid
    }

    private sealed class TicketState
    {
        internal TicketState(string itemId, ulong cycle, TicketOutcome outcome, string planFingerprint)
        {
            ItemId = itemId;
            Cycle = cycle;
            Outcome = outcome;
            PlanFingerprint = planFingerprint;
        }

        internal string ItemId { get; set; }
        internal ulong Cycle { get; set; }
        internal TicketOutcome Outcome { get; set; }
        internal string PlanFingerprint { get; set; }

        internal string Serialize()
        {
            char outcome = Outcome switch
            {
                TicketOutcome.Free => 'F',
                TicketOutcome.Paid => 'P',
                _ => 'N'
            };
            return string.Join(
                "|",
                TicketSchema,
                ItemId,
                Cycle.ToString(CultureInfo.InvariantCulture),
                outcome.ToString(),
                PlanFingerprint);
        }
    }

    private static readonly Dictionary<string, ulong> SessionMinimumCycles = new(StringComparer.Ordinal);

    internal static RepairPreview ResolvePreview(Player player, RepairPreview stationPreview)
    {
        if (stationPreview.PaymentKind != RepairPaymentKind.StationMaterials)
        {
            return stationPreview;
        }

        ItemDrop.ItemData item = stationPreview.Item;
        Inventory inventory = player.GetInventory();
        string currentPlan = BuildPlanFingerprint(stationPreview);
        bool hasMaterialCost = HasMaterialCost(stationPreview);

        TicketState? state = ReadState(item, inventory, out bool corruptState);
        if (corruptState)
        {
            if (!hasMaterialCost)
            {
                return stationPreview;
            }

            state = new TicketState(
                Guid.NewGuid().ToString("N"),
                0UL,
                TicketOutcome.Paid,
                currentPlan);
            return WriteState(item, inventory, state)
                ? stationPreview.WithPayment(RepairPaymentKind.StationMaterials, state.Serialize())
                : stationPreview;
        }

        if (state != null)
        {
            NormalizeSessionCycle(item, inventory, state);
        }

        if (state is { Outcome: TicketOutcome.Paid })
        {
            return stationPreview.WithPayment(RepairPaymentKind.StationMaterials, state.Serialize());
        }

        if (state is { Outcome: TicketOutcome.Free })
        {
            if (!string.Equals(state.PlanFingerprint, currentPlan, StringComparison.Ordinal))
            {
                // A revealed free result is valid only for the exact cost snapshot
                // that produced it. Any quality, durability bucket, or material
                // requirement change permanently locks this repair cycle to Paid.
                state.Outcome = TicketOutcome.Paid;
                if (!WriteState(item, inventory, state))
                {
                    return stationPreview;
                }

                return stationPreview.WithPayment(RepairPaymentKind.StationMaterials, state.Serialize());
            }

            RepairPaymentKind paymentKind = IsFeatureEnabled()
                ? RepairPaymentKind.CraftingSkillFree
                : RepairPaymentKind.StationMaterials;
            return stationPreview.WithPayment(paymentKind, state.Serialize());
        }

        if (!hasMaterialCost || !IsFeatureEnabled())
        {
            return stationPreview;
        }

        state ??= new TicketState(Guid.NewGuid().ToString("N"), 0UL, TicketOutcome.None, string.Empty);
        double chance = CalculateFreeRepairChance(
            player.GetSkillFactor(Skills.SkillType.Crafting),
            RepairRequiresMaterialsPlugin.CraftingSkillFreeRepairChanceAtLevel0.Value,
            RepairRequiresMaterialsPlugin.CraftingSkillFreeRepairChanceAtLevel100.Value);

        state.Outcome = GetDeterministicRoll(state.ItemId, state.Cycle) < chance
            ? TicketOutcome.Free
            : TicketOutcome.Paid;
        state.PlanFingerprint = currentPlan;
        if (!WriteState(item, inventory, state))
        {
            // Failure to persist a decision must never grant a rerollable free repair.
            return stationPreview;
        }

        return stationPreview.WithPayment(
            state.Outcome == TicketOutcome.Free
                ? RepairPaymentKind.CraftingSkillFree
                : RepairPaymentKind.StationMaterials,
            state.Serialize());
    }

    internal static void CompleteSuccessfulRepair(Player player, RepairPreview preview)
    {
        if (!preview.HasRawMaterialCost
            || string.IsNullOrEmpty(preview.SkillFreeTicketToken))
        {
            return;
        }

        try
        {
            if (!TryParseState(preview.SkillFreeTicketToken, out TicketState? expected) || expected == null)
            {
                return;
            }

            ulong nextCycle = expected.Cycle == ulong.MaxValue ? 0UL : expected.Cycle + 1UL;
            string nextItemId = expected.Cycle == ulong.MaxValue
                ? Guid.NewGuid().ToString("N")
                : expected.ItemId;
            if (!SessionMinimumCycles.TryGetValue(expected.ItemId, out ulong sessionMinimum)
                || nextCycle > sessionMinimum)
            {
                SessionMinimumCycles[expected.ItemId] = nextCycle;
            }

            ItemDrop.ItemData item = preview.Item;
            Inventory inventory = player.GetInventory();
            TicketState? current = ReadState(item, inventory, out _);
            if (current != null
                && (!string.Equals(current.ItemId, expected.ItemId, StringComparison.Ordinal)
                    || current.Cycle != expected.Cycle))
            {
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    "A skill-free repair ticket changed during a completed repair; the newer item state was preserved.");
                return;
            }

            TicketState completed = new(nextItemId, nextCycle, TicketOutcome.None, string.Empty);
            if (!WriteState(item, inventory, completed))
            {
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    "A completed skill-free repair ticket could not be persisted; its cycle remains advanced for this session.");
            }
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not complete the skill-free repair ticket: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool IsFeatureEnabled()
    {
        return RepairRequiresMaterialsPlugin.EnableCraftingSkillFreeRepairs.Value.IsOn();
    }

    internal static double CalculateFreeRepairChance(
        float skillFactor,
        float chanceAtLevel0Percent,
        float chanceAtLevel100Percent)
    {
        double maximumChance = NormalizePercent(chanceAtLevel100Percent) / 100d;
        double minimumChance = Math.Min(
            NormalizePercent(chanceAtLevel0Percent) / 100d,
            maximumChance);
        double skill = NormalizeSkillFactor(skillFactor);
        return minimumChance + (maximumChance - minimumChance) * skill;
    }

    private static double NormalizeSkillFactor(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0d;
        }

        return float.IsPositiveInfinity(value) || value >= 1f ? 1d : value;
    }

    private static double NormalizePercent(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0d;
        }

        return float.IsPositiveInfinity(value) || value >= 100f ? 100d : value;
    }

    private static bool HasMaterialCost(RepairPreview preview)
    {
        return preview.Costs.Any(cost => cost.RequiredAmount > 0);
    }

    private static TicketState? ReadState(
        ItemDrop.ItemData item,
        Inventory inventory,
        out bool corruptState)
    {
        corruptState = false;
        if (item.m_customData == null || !item.m_customData.TryGetValue(TicketKey, out string? serialized))
        {
            return null;
        }

        if (TryParseState(serialized, out TicketState? state))
        {
            return state;
        }

        corruptState = true;
        try
        {
            item.m_customData.Remove(TicketKey);
            RepairService.MarkInventoryDirty(inventory);
        }
        catch
        {
            // The caller will fail closed to a paid repair when a cost exists.
        }

        return null;
    }

    private static bool TryParseState(string serialized, out TicketState? state)
    {
        state = null;
        string[] parts = serialized.Split(new[] { '|' }, 5);
        if (parts.Length != 5
            || !string.Equals(parts[0], TicketSchema, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[1], "N", out _)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong cycle)
            || parts[3].Length != 1)
        {
            return false;
        }

        TicketOutcome outcome = parts[3][0] switch
        {
            'F' => TicketOutcome.Free,
            'P' => TicketOutcome.Paid,
            'N' => TicketOutcome.None,
            _ => (TicketOutcome)(-1)
        };
        if ((int)outcome < 0
            || (outcome != TicketOutcome.None && parts[4].Length == 0)
            || (outcome == TicketOutcome.None && parts[4].Length != 0))
        {
            return false;
        }

        state = new TicketState(parts[1], cycle, outcome, parts[4]);
        return true;
    }

    private static void NormalizeSessionCycle(
        ItemDrop.ItemData item,
        Inventory inventory,
        TicketState state)
    {
        if (!SessionMinimumCycles.TryGetValue(state.ItemId, out ulong minimumCycle)
            || state.Cycle >= minimumCycle)
        {
            return;
        }

        state.Cycle = minimumCycle;
        state.Outcome = TicketOutcome.None;
        state.PlanFingerprint = string.Empty;
        _ = WriteState(item, inventory, state);
    }

    private static bool WriteState(ItemDrop.ItemData item, Inventory inventory, TicketState state)
    {
        try
        {
            item.m_customData ??= new Dictionary<string, string>();
            string serialized = state.Serialize();
            if (item.m_customData.TryGetValue(TicketKey, out string? existing)
                && string.Equals(existing, serialized, StringComparison.Ordinal))
            {
                return true;
            }

            item.m_customData[TicketKey] = serialized;
            RepairService.MarkInventoryDirty(inventory);
            return true;
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not persist a skill-free repair ticket: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static string BuildPlanFingerprint(RepairPreview preview)
    {
        StringBuilder canonical = new();
        AppendPart(canonical, "plan-v1");
        AppendPart(canonical, ResolveItemPrefabName(preview.Item));
        AppendPart(canonical, preview.Item.m_quality.ToString(CultureInfo.InvariantCulture));
        AppendPart(canonical, preview.DurabilityBucketPercent.ToString(CultureInfo.InvariantCulture));

        IEnumerable<RepairMaterialCost> costs = preview.Costs
            .Where(cost => cost.RequiredAmount > 0)
            .OrderBy(cost => cost.ResourcePrefabName, StringComparer.Ordinal)
            .ThenBy(cost => cost.RequiredAmount);
        foreach (RepairMaterialCost cost in costs)
        {
            AppendPart(canonical, cost.ResourcePrefabName);
            AppendPart(canonical, cost.RequiredAmount.ToString(CultureInfo.InvariantCulture));
        }

        return Sha256Hex(canonical.ToString());
    }

    private static void AppendPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private static string ResolveItemPrefabName(ItemDrop.ItemData item)
    {
        string name = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
        const string cloneSuffix = "(Clone)";
        name = name?.Trim() ?? string.Empty;
        return name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - cloneSuffix.Length).Trim()
            : name;
    }

    private static double GetDeterministicRoll(string itemId, ulong cycle)
    {
        string input = string.Join(
            "|",
            RollDomain,
            itemId,
            cycle.ToString(CultureInfo.InvariantCulture));
        byte[] hash;
        using (SHA256 sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        }

        ulong value = 0UL;
        for (int index = 0; index < sizeof(ulong); ++index)
        {
            value = (value << 8) | hash[index];
        }

        // Converting the full ulong directly to double rounds the largest values
        // to 2^64, which can produce 1.0 and make an exact 100% chance fail. Keep
        // the 53 high-quality bits that double can represent exactly instead.
        return (value >> 11) / 9007199254740992d;
    }

    private static string Sha256Hex(string value)
    {
        byte[] hash;
        using (SHA256 sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        }

        StringBuilder result = new(hash.Length * 2);
        foreach (byte part in hash)
        {
            result.Append(part.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }
}
