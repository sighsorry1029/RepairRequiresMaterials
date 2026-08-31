using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RepairRequiresMaterials;

internal sealed class RepairCostRoundingContext
{
    private readonly string _itemId;
    private readonly ulong _cycle;
    private readonly bool _failClosed;

    internal RepairCostRoundingContext(string itemId, ulong cycle, string token, bool failClosed)
    {
        _itemId = itemId;
        _cycle = cycle;
        Token = token;
        _failClosed = failClosed;
    }

    internal string Token { get; }

    internal int Round(double rawAmount, string materialKey)
    {
        double roll = _failClosed
            ? 0d
            : RepairCostRoundingSystem.GetDeterministicRoll(_itemId, _cycle, materialKey);
        return RepairCostRoundingSystem.StochasticRound(rawAmount, roll);
    }
}

internal static class RepairCostRoundingSystem
{
    private const string StateKey = RepairRequiresMaterialsPlugin.ModGuid + ".RepairCostRoundingState";
    private const string StateSchema = "v1";
    private const string RollDomain = RepairRequiresMaterialsPlugin.ModGuid + ".RepairCostRoundingRoll.v1";

    private sealed class RoundingState
    {
        internal RoundingState(string itemId, ulong cycle, bool forceCeiling)
        {
            ItemId = itemId;
            Cycle = cycle;
            ForceCeiling = forceCeiling;
        }

        internal string ItemId { get; set; }
        internal ulong Cycle { get; set; }
        internal bool ForceCeiling { get; set; }

        internal string Serialize()
        {
            return string.Join(
                "|",
                StateSchema,
                ItemId,
                Cycle.ToString(CultureInfo.InvariantCulture),
                ForceCeiling ? "C" : "N");
        }
    }

    private static readonly Dictionary<string, ulong> SessionMinimumCycles = new(StringComparer.Ordinal);

    internal static RepairCostRoundingContext CreateContext(ItemDrop.ItemData item, Inventory inventory)
    {
        RoundingState? state = ReadState(item, inventory, out bool corruptState);
        if (state == null)
        {
            state = new RoundingState(
                Guid.NewGuid().ToString("N"),
                0UL,
                forceCeiling: corruptState);
            if (!WriteState(item, inventory, state))
            {
                return CreateFailClosedContext();
            }
        }

        if (SessionMinimumCycles.TryGetValue(state.ItemId, out ulong minimumCycle)
            && state.Cycle < minimumCycle)
        {
            state.Cycle = minimumCycle;
            if (!WriteState(item, inventory, state))
            {
                return CreateFailClosedContext();
            }
        }

        return new RepairCostRoundingContext(
            state.ItemId,
            state.Cycle,
            state.Serialize(),
            failClosed: state.ForceCeiling);
    }

    internal static void CompleteSuccessfulRepair(Player player, RepairPreview preview)
    {
        if (string.IsNullOrEmpty(preview.RepairCostRoundingToken))
        {
            return;
        }

        try
        {
            if (!TryParseState(preview.RepairCostRoundingToken, out RoundingState? expected)
                || expected == null)
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
            RoundingState? current = ReadState(item, inventory, out _);
            if (current != null
                && (!string.Equals(current.ItemId, expected.ItemId, StringComparison.Ordinal)
                    || current.Cycle != expected.Cycle))
            {
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    "A repair-cost rounding cycle changed during a completed repair; the newer item state was preserved.");
                return;
            }

            RoundingState completed = new(nextItemId, nextCycle, forceCeiling: false);
            if (!WriteState(item, inventory, completed))
            {
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    "A completed repair-cost rounding cycle could not be persisted; its cycle remains advanced for this session.");
            }
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not complete the repair-cost rounding cycle: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static double GetDeterministicRoll(string itemId, ulong cycle, string materialKey)
    {
        string input = string.Join(
            "|",
            RollDomain,
            itemId,
            cycle.ToString(CultureInfo.InvariantCulture),
            materialKey ?? string.Empty);
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

        return (value >> 11) / 9007199254740992d;
    }

    internal static int StochasticRound(double rawAmount, double roll)
    {
        if (double.IsNaN(rawAmount) || rawAmount <= 0d)
        {
            return 0;
        }

        if (double.IsPositiveInfinity(rawAmount) || rawAmount >= int.MaxValue)
        {
            return int.MaxValue;
        }

        int wholeAmount = (int)Math.Floor(rawAmount);
        double fractionalAmount = rawAmount - wholeAmount;
        if (fractionalAmount <= 0d)
        {
            return wholeAmount;
        }

        // Rolls produced by the deterministic hash are always in [0, 1).
        // Treat any invalid caller input as zero so a failure cannot reduce cost.
        double normalizedRoll = roll >= 0d && roll < 1d ? roll : 0d;
        return normalizedRoll < fractionalAmount
            ? wholeAmount + 1
            : wholeAmount;
    }

    private static RepairCostRoundingContext CreateFailClosedContext()
    {
        return new RepairCostRoundingContext(string.Empty, 0UL, string.Empty, failClosed: true);
    }

    private static RoundingState? ReadState(
        ItemDrop.ItemData item,
        Inventory inventory,
        out bool corruptState)
    {
        corruptState = false;
        if (item.m_customData == null || !item.m_customData.TryGetValue(StateKey, out string? serialized))
        {
            return null;
        }

        if (TryParseState(serialized, out RoundingState? state))
        {
            return state;
        }

        corruptState = true;
        try
        {
            item.m_customData.Remove(StateKey);
            RepairService.MarkInventoryDirty(inventory);
        }
        catch
        {
            // Creating and persisting a replacement below will fail closed if needed.
        }

        return null;
    }

    private static bool TryParseState(string serialized, out RoundingState? state)
    {
        state = null;
        string[] parts = serialized.Split(new[] { '|' }, 4);
        if (parts.Length != 4
            || !string.Equals(parts[0], StateSchema, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[1], "N", out _)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong cycle)
            || (parts[3] != "C" && parts[3] != "N"))
        {
            return false;
        }

        state = new RoundingState(parts[1], cycle, forceCeiling: parts[3] == "C");
        return true;
    }

    private static bool WriteState(ItemDrop.ItemData item, Inventory inventory, RoundingState state)
    {
        try
        {
            item.m_customData ??= new Dictionary<string, string>();
            string serialized = state.Serialize();
            if (item.m_customData.TryGetValue(StateKey, out string? existing)
                && string.Equals(existing, serialized, StringComparison.Ordinal))
            {
                return true;
            }

            item.m_customData[StateKey] = serialized;
            RepairService.MarkInventoryDirty(inventory);
            return true;
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not persist a repair-cost rounding cycle: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }
}
