using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using UnityRandom = UnityEngine.Random;

namespace RepairRequiresMaterials;

internal static class CraftingProductionBonusSystem
{
    internal const int UseVanillaBonus = -1;
    internal const string DefaultExcludedOutputPrefabPatterns =
        "Simple_*_Socket, Advanced_*_Socket, Perfect_*_Socket";

    private const int MaximumIndependentRolls = 10_000;
    private static bool _largeOutputWarningLogged;
    private static volatile PrefabPatternMatcher _excludedOutputPrefabs = PrefabPatternMatcher.Empty;

    internal static void SetExcludedOutputPrefabPatterns(string? patterns)
    {
        _excludedOutputPrefabs = PrefabPatternMatcher.Parse(patterns);
    }

    internal static float CalculatePerItemChance(
        float skillFactor,
        float bonusOutputChanceAtLevel100Percent)
    {
        double skill = ClampProbability(skillFactor);
        double maximumChance = ClampLevel100ChancePercent(
            bonusOutputChanceAtLevel100Percent) / 100d;
        if (skill <= 0d || maximumChance <= 0d)
        {
            return 0f;
        }

        return (float)ClampProbability(skill * maximumChance);
    }

    internal static int RollBonusItems(
        int baseItemCount,
        float itemChance,
        Func<float> nextRandomValue)
    {
        if (baseItemCount <= 0)
        {
            return 0;
        }

        int maximumBonus = int.MaxValue - baseItemCount;
        if (maximumBonus <= 0)
        {
            return 0;
        }

        double chance = ClampProbability(itemChance);
        if (chance <= 0d)
        {
            return 0;
        }

        if (chance >= 1d)
        {
            return Math.Min(baseItemCount, maximumBonus);
        }

        if (nextRandomValue == null)
        {
            throw new ArgumentNullException(nameof(nextRandomValue));
        }

        int bonus = 0;
        for (int i = 0; i < baseItemCount && bonus < maximumBonus; i++)
        {
            if (nextRandomValue() < chance)
            {
                bonus++;
            }
        }

        return bonus;
    }

    internal static int CalculateCraftingSkillBonusOrUseVanilla(
        InventoryGui gui,
        Player player,
        CraftingStation station,
        int baseItemCount)
    {
        if ((UnityObject)(object)station == null || station.m_craftingSkill != Skills.SkillType.Crafting)
        {
            return UseVanillaBonus;
        }

        if ((UnityObject)(object)player == null || baseItemCount <= 0)
        {
            return 0;
        }

        Recipe recipe = gui.m_craftRecipe;
        if ((UnityObject)(object)recipe == null
            || (UnityObject)(object)recipe.m_item == null)
        {
            return 0;
        }

        string outputPrefabName = recipe.m_item.gameObject.name;
        if (_excludedOutputPrefabs.IsMatch(outputPrefabName)
            || recipe.m_item.m_itemData.m_shared.m_maxStackSize <= 1)
        {
            return 0;
        }

        float level100Chance =
            RepairRequiresMaterialsPlugin.CraftingBonusOutputChanceAtLevel100.Value;
        float itemChance = CalculatePerItemChance(
            player.GetSkillFactor(Skills.SkillType.Crafting),
            level100Chance);

        if (itemChance <= 0f)
        {
            return 0;
        }

        if (baseItemCount > MaximumIndependentRolls && itemChance < 1f)
        {
            if (!_largeOutputWarningLogged)
            {
                _largeOutputWarningLogged = true;
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    $"A crafting result exceeded {MaximumIndependentRolls} base items; "
                    + "using Valheim's production-bonus calculation to avoid a long main-thread roll loop.");
            }

            return UseVanillaBonus;
        }

        return RollBonusItems(
            baseItemCount,
            itemChance,
            NextRandomValue);
    }

    private static double ClampProbability(double value)
    {
        if (double.IsNaN(value) || value <= 0d)
        {
            return 0d;
        }

        return double.IsPositiveInfinity(value) || value >= 1d ? 1d : value;
    }

    private static double ClampLevel100ChancePercent(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0d;
        }

        return float.IsPositiveInfinity(value) || value >= 25f ? 25d : value;
    }

    private static float NextRandomValue()
    {
        return UnityRandom.value;
    }
}

[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
[HarmonyPriority(Priority.Last)]
internal static class InventoryGuiCraftingProductionBonusPatch
{
    private static readonly MethodInfo GetAmountMethod = AccessTools.Method(
        typeof(Recipe),
        nameof(Recipe.GetAmount),
        new[]
        {
            typeof(int),
            typeof(int).MakeByRefType(),
            typeof(ItemDrop.ItemData).MakeByRefType(),
            typeof(int)
        })!;

    private static readonly MethodInfo GetCurrentCraftingStationMethod = AccessTools.Method(
        typeof(Player),
        nameof(Player.GetCurrentCraftingStation))!;

    private static readonly MethodInfo BonusHelperMethod = AccessTools.Method(
        typeof(CraftingProductionBonusSystem),
        nameof(CraftingProductionBonusSystem.CalculateCraftingSkillBonusOrUseVanilla))!;

    private static readonly MethodInfo RandomValueGetter = AccessTools.PropertyGetter(
        typeof(UnityRandom),
        nameof(UnityRandom.value))!;

    private static readonly FieldInfo CraftUpgradeItemField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftUpgradeItem))!;

    private static readonly FieldInfo CraftBonusChanceField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftBonusChance))!;

    private static readonly FieldInfo CraftBonusAmountField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftBonusAmount))!;

    private static bool _patternWarningLogged;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        List<CodeInstruction> codes = new(instructions);
        try
        {
            if (!TryInject(codes, generator, out string failure))
            {
                LogPatternFailure(failure);
            }
        }
        catch (Exception ex)
        {
            LogPatternFailure($"unexpected transpiler error: {ex}");
        }

        return codes;
    }

    private static bool TryInject(
        List<CodeInstruction> codes,
        ILGenerator generator,
        out string failure)
    {
        failure = string.Empty;

        int getAmountCallIndex = FindCall(codes, GetAmountMethod, 0);
        int stationCallIndex = FindCall(codes, GetCurrentCraftingStationMethod, 0);
        if (getAmountCallIndex < 0 || stationCallIndex < 0 || getAmountCallIndex >= stationCallIndex)
        {
            failure = "could not locate Recipe.GetAmount and GetCurrentCraftingStation anchors";
            return false;
        }

        if (getAmountCallIndex + 1 >= codes.Count
            || !TryGetStoredLocal(codes[getAmountCallIndex + 1], out int resultLocal))
        {
            failure = "could not resolve the pre-bonus result amount local";
            return false;
        }

        int stationStoreIndex = stationCallIndex + 1;
        int bonusZeroIndex = stationCallIndex + 2;
        int bonusStoreIndex = stationCallIndex + 3;
        int insertionIndex = stationCallIndex + 4;
        if (insertionIndex >= codes.Count
            || !TryGetStoredLocal(codes[stationStoreIndex], out int stationLocal)
            || !IsLoadConstantZero(codes[bonusZeroIndex])
            || !TryGetStoredLocal(codes[bonusStoreIndex], out int bonusLocal)
            || !LoadsLocal(codes[insertionIndex], stationLocal))
        {
            failure = "the vanilla crafting-bonus entry pattern changed";
            return false;
        }

        int bonusChanceIndex = FindFieldLoad(codes, CraftBonusChanceField, insertionIndex);
        int bonusAmountIndex = FindFieldLoad(codes, CraftBonusAmountField, insertionIndex);
        int randomValueIndex = FindCall(codes, RandomValueGetter, insertionIndex);
        if (randomValueIndex <= insertionIndex
            || bonusChanceIndex <= randomValueIndex
            || bonusAmountIndex <= bonusChanceIndex)
        {
            failure = "could not locate the ordered vanilla crafting-bonus roll";
            return false;
        }

        int bonusLoadIndex = bonusAmountIndex - 2;
        int bonusStoreAfterIncrementIndex = bonusAmountIndex + 2;
        int resultLoadIndex = bonusAmountIndex + 3;
        int displayedBonusLoadIndex = bonusAmountIndex + 4;
        int resultStoreIndex = bonusAmountIndex + 6;
        if (bonusLoadIndex < insertionIndex
            || resultStoreIndex >= codes.Count
            || !LoadsLocal(codes[bonusLoadIndex], bonusLocal)
            || codes[bonusAmountIndex - 1].opcode != OpCodes.Ldarg_0
            || codes[bonusAmountIndex + 1].opcode != OpCodes.Add
            || !StoresLocal(codes[bonusStoreAfterIncrementIndex], bonusLocal)
            || !LoadsLocal(codes[resultLoadIndex], resultLocal)
            || !LoadsLocal(codes[displayedBonusLoadIndex], bonusLocal)
            || codes[bonusAmountIndex + 5].opcode != OpCodes.Add
            || !StoresLocal(codes[resultStoreIndex], resultLocal))
        {
            failure = "the vanilla crafting-bonus accumulation pattern changed";
            return false;
        }

        int endFieldIndex = FindFieldLoad(codes, CraftUpgradeItemField, resultStoreIndex + 1);
        int endIndex = endFieldIndex - 1;
        if (endFieldIndex <= insertionIndex
            || endIndex < 0
            || endFieldIndex + 1 >= codes.Count
            || codes[endIndex].opcode != OpCodes.Ldarg_0
            || !IsBranchTrue(codes[endFieldIndex + 1]))
        {
            failure = "could not locate the post-bonus inventory-capacity check";
            return false;
        }

        if (randomValueIndex >= endIndex
            || bonusChanceIndex >= endIndex
            || resultStoreIndex >= endIndex)
        {
            failure = "the vanilla crafting-bonus anchors crossed the capacity-check boundary";
            return false;
        }

        Label fallbackLabel = generator.DefineLabel();
        Label endLabel;
        if (codes[endIndex].labels.Count > 0)
        {
            endLabel = codes[endIndex].labels[0];
        }
        else
        {
            endLabel = generator.DefineLabel();
            codes[endIndex].labels.Add(endLabel);
        }

        CodeInstruction fallbackReset = new(OpCodes.Ldc_I4_0);
        fallbackReset.labels.Add(fallbackLabel);

        List<CodeInstruction> injected = new()
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1),
            CloneWithoutMetadata(codes[insertionIndex]),
            CloneWithoutMetadata(codes[resultLoadIndex]),
            new CodeInstruction(OpCodes.Call, BonusHelperMethod),
            CloneWithoutMetadata(codes[bonusStoreIndex]),
            CloneWithoutMetadata(codes[displayedBonusLoadIndex]),
            new CodeInstruction(OpCodes.Ldc_I4_0),
            new CodeInstruction(OpCodes.Blt, fallbackLabel),
            CloneWithoutMetadata(codes[resultLoadIndex]),
            CloneWithoutMetadata(codes[displayedBonusLoadIndex]),
            new CodeInstruction(OpCodes.Add),
            CloneWithoutMetadata(codes[getAmountCallIndex + 1]),
            new CodeInstruction(OpCodes.Br, endLabel),
            fallbackReset,
            CloneWithoutMetadata(codes[bonusStoreIndex])
        };

        injected[0].labels.AddRange(codes[insertionIndex].labels);
        codes[insertionIndex].labels.Clear();
        injected[0].blocks.AddRange(codes[insertionIndex].blocks);
        codes[insertionIndex].blocks.Clear();

        codes.InsertRange(insertionIndex, injected);
        return true;
    }

    private static int FindCall(List<CodeInstruction> codes, MethodInfo method, int startIndex)
    {
        for (int i = Math.Max(0, startIndex); i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt)
                && Equals(codes[i].operand, method))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindFieldLoad(List<CodeInstruction> codes, FieldInfo field, int startIndex)
    {
        for (int i = Math.Max(0, startIndex); i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Ldfld || codes[i].opcode == OpCodes.Ldsfld)
                && Equals(codes[i].operand, field))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool LoadsLocal(CodeInstruction instruction, int localIndex)
    {
        return TryGetLocalIndex(instruction, load: true, out int index) && index == localIndex;
    }

    private static bool StoresLocal(CodeInstruction instruction, int localIndex)
    {
        return TryGetLocalIndex(instruction, load: false, out int index) && index == localIndex;
    }

    private static bool TryGetStoredLocal(CodeInstruction instruction, out int localIndex)
    {
        return TryGetLocalIndex(instruction, load: false, out localIndex);
    }

    private static bool TryGetLocalIndex(
        CodeInstruction instruction,
        bool load,
        out int localIndex)
    {
        localIndex = -1;
        OpCode opcode = instruction.opcode;
        if (load)
        {
            if (opcode == OpCodes.Ldloc_0) { localIndex = 0; return true; }
            if (opcode == OpCodes.Ldloc_1) { localIndex = 1; return true; }
            if (opcode == OpCodes.Ldloc_2) { localIndex = 2; return true; }
            if (opcode == OpCodes.Ldloc_3) { localIndex = 3; return true; }
            if (opcode != OpCodes.Ldloc && opcode != OpCodes.Ldloc_S) { return false; }
        }
        else
        {
            if (opcode == OpCodes.Stloc_0) { localIndex = 0; return true; }
            if (opcode == OpCodes.Stloc_1) { localIndex = 1; return true; }
            if (opcode == OpCodes.Stloc_2) { localIndex = 2; return true; }
            if (opcode == OpCodes.Stloc_3) { localIndex = 3; return true; }
            if (opcode != OpCodes.Stloc && opcode != OpCodes.Stloc_S) { return false; }
        }

        switch (instruction.operand)
        {
            case LocalBuilder localBuilder:
                localIndex = localBuilder.LocalIndex;
                return true;
            case LocalVariableInfo localVariable:
                localIndex = localVariable.LocalIndex;
                return true;
            case byte byteIndex:
                localIndex = byteIndex;
                return true;
            case int intIndex:
                localIndex = intIndex;
                return true;
            default:
                return false;
        }
    }

    private static bool IsLoadConstantZero(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldc_I4_0
               || (instruction.opcode == OpCodes.Ldc_I4 && Equals(instruction.operand, 0))
               || (instruction.opcode == OpCodes.Ldc_I4_S
                   && Convert.ToInt32(instruction.operand) == 0);
    }

    private static bool IsBranchTrue(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Brtrue || instruction.opcode == OpCodes.Brtrue_S;
    }

    private static CodeInstruction CloneWithoutMetadata(CodeInstruction source)
    {
        return new CodeInstruction(source.opcode, source.operand);
    }

    private static void LogPatternFailure(string reason)
    {
        if (_patternWarningLogged)
        {
            return;
        }

        _patternWarningLogged = true;
        RepairRequiresMaterialsPlugin.Log.LogWarning(
            $"Per-item Crafting bonus patch was not applied; vanilla behavior remains active ({reason}).");
    }
}
