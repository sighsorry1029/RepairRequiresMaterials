using System;
using System.Globalization;
using System.Text;
using HarmonyLib;
using TMPro;

namespace RepairRequiresMaterials;

internal static class CraftingSkillTooltipText
{
    internal const string HeadingToken = "$rrm_skill_crafting_heading";
    internal const string FreeRepairToken = "$rrm_skill_crafting_free_repair";
    internal const string BonusOutputToken = "$rrm_skill_crafting_bonus_output";
    internal const string EquipSpeedToken = "$rrm_skill_crafting_equip_speed";

    internal static string Append(
        string? original,
        bool freeRepairEnabled,
        float freeRepairChanceAtLevel0,
        float freeRepairChanceAtLevel100,
        float bonusOutputChanceAtLevel100,
        float equipTimeReductionAtLevel100)
    {
        original ??= string.Empty;
        if (HasRepairRequiresMaterialsHeading(original))
        {
            return original;
        }

        float effectiveFreeRepairChanceAtLevel0 = (float)(
            CraftingFreeRepairSystem.CalculateFreeRepairChance(
                0f,
                freeRepairChanceAtLevel0,
                freeRepairChanceAtLevel100) * 100d);
        float effectiveFreeRepairChanceAtLevel100 = (float)(
            CraftingFreeRepairSystem.CalculateFreeRepairChance(
                1f,
                freeRepairChanceAtLevel0,
                freeRepairChanceAtLevel100) * 100d);
        float normalizedBonusOutputChance = NormalizePercent(
            bonusOutputChanceAtLevel100,
            25f);
        float normalizedEquipTimeReduction = NormalizePercent(
            equipTimeReductionAtLevel100,
            100f);
        bool showFreeRepair = freeRepairEnabled && effectiveFreeRepairChanceAtLevel100 > 0f;
        bool showBonusOutput = normalizedBonusOutputChance > 0f;
        bool showEquipSpeed = normalizedEquipTimeReduction > 0f;
        if (!showFreeRepair && !showBonusOutput && !showEquipSpeed)
        {
            return original;
        }

        StringBuilder extra = new(HeadingToken);
        if (showFreeRepair)
        {
            extra.Append('\n').Append(RepairRequiresMaterialsLocalization.Localize(
                FreeRepairToken,
                FormatPercent(effectiveFreeRepairChanceAtLevel0),
                FormatPercent(effectiveFreeRepairChanceAtLevel100)));
        }

        if (showBonusOutput)
        {
            extra.Append('\n').Append(RepairRequiresMaterialsLocalization.Localize(
                BonusOutputToken,
                FormatPercent(normalizedBonusOutputChance)));
        }

        if (showEquipSpeed)
        {
            extra.Append('\n').Append(RepairRequiresMaterialsLocalization.Localize(
                EquipSpeedToken,
                FormatPercent(normalizedEquipTimeReduction)));
        }

        return original.Length > 0
            ? original + "\n\n" + extra
            : extra.ToString();
    }

    internal static bool MatchesSkillDescription(
        string? tooltipText,
        string? skillDescription)
    {
        return !string.IsNullOrWhiteSpace(tooltipText)
               && !string.IsNullOrWhiteSpace(skillDescription)
               && tooltipText!.IndexOf(skillDescription!, StringComparison.Ordinal) >= 0;
    }

    internal static bool HasRepairRequiresMaterialsHeading(string? tooltipText)
    {
        return !string.IsNullOrEmpty(tooltipText)
               && tooltipText!.IndexOf(HeadingToken, StringComparison.Ordinal) >= 0;
    }

    private static float NormalizePercent(float value, float maximum)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0f;
        }

        return float.IsPositiveInfinity(value) || value >= maximum ? maximum : value;
    }

    private static string FormatPercent(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

[HarmonyPatch(typeof(UITooltip), nameof(UITooltip.UpdateTextElements))]
internal static class CraftingSkillTooltipAlignmentPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(UITooltip __instance)
    {
        if (__instance == null
            || !CraftingSkillTooltipText.HasRepairRequiresMaterialsHeading(__instance.m_text)
            || UITooltip.m_current != null && UITooltip.m_current != __instance
            || UITooltip.m_tooltip == null)
        {
            return;
        }

        TMP_Text[] textElements = UITooltip.m_tooltip.GetComponentsInChildren<TMP_Text>(true);
        foreach (TMP_Text textElement in textElements)
        {
            if (textElement != null
                && string.Equals(textElement.name, "Text", StringComparison.Ordinal))
            {
                textElement.horizontalAlignment = HorizontalAlignmentOptions.Left;
                return;
            }
        }
    }
}

[HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.Setup))]
internal static class CraftingSkillTooltipPatch
{
    private static bool _failureLogged;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("randyknapp.mods.epicloot")]
    private static void Postfix(SkillsDialog __instance, Player player)
    {
        if (__instance == null || player == null)
        {
            return;
        }

        try
        {
            var skills = player.GetSkills()?.GetSkillList();
            if (skills == null)
            {
                return;
            }

            Skills.Skill? craftingSkill = null;
            int craftingIndex = -1;
            for (int index = 0; index < skills.Count; index++)
            {
                Skills.Skill skill = skills[index];
                if (skill?.m_info?.m_skill == Skills.SkillType.Crafting)
                {
                    craftingSkill = skill;
                    craftingIndex = index;
                    break;
                }
            }

            if (craftingSkill?.m_info == null)
            {
                return;
            }

            UITooltip? tooltip = FindCraftingTooltip(
                __instance,
                craftingIndex,
                craftingSkill.m_info.m_description);
            if (tooltip == null)
            {
                return;
            }

            bool freeRepairEnabled =
                RepairRequiresMaterialsPlugin.EnableCraftingSkillFreeRepairs.Value.IsOn();
            string text = CraftingSkillTooltipText.Append(
                tooltip.m_text,
                freeRepairEnabled,
                RepairRequiresMaterialsPlugin.CraftingSkillFreeRepairChanceAtLevel0.Value,
                RepairRequiresMaterialsPlugin.CraftingSkillFreeRepairChanceAtLevel100.Value,
                RepairRequiresMaterialsPlugin.CraftingBonusOutputChanceAtLevel100.Value,
                RepairRequiresMaterialsPlugin.CraftingEquipTimeReductionAtLevel100.Value);
            if (!string.Equals(text, tooltip.m_text, StringComparison.Ordinal))
            {
                tooltip.Set(
                    tooltip.m_topic,
                    text,
                    tooltip.m_anchor,
                    tooltip.m_fixedPosition);
            }
        }
        catch (Exception exception)
        {
            if (_failureLogged)
            {
                return;
            }

            _failureLogged = true;
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                "Could not extend the Crafting skill tooltip: "
                + exception.GetBaseException().Message);
        }
    }

    private static UITooltip? FindCraftingTooltip(
        SkillsDialog dialog,
        int craftingIndex,
        string craftingDescription)
    {
        if (dialog.m_elements != null
            && craftingIndex >= 0
            && craftingIndex < dialog.m_elements.Count)
        {
            UITooltip? indexedTooltip = dialog.m_elements[craftingIndex]?
                .GetComponentInChildren<UITooltip>();
            if (indexedTooltip != null
                && CraftingSkillTooltipText.MatchesSkillDescription(
                    indexedTooltip.m_text,
                    craftingDescription))
            {
                return indexedTooltip;
            }
        }

        InventoryGui? inventory = dialog.GetComponentInParent<InventoryGui>();
        if (inventory == null)
        {
            return null;
        }

        UITooltip[] candidates = inventory.GetComponentsInChildren<UITooltip>(true);
        UITooltip? inactiveMatch = null;
        foreach (UITooltip candidate in candidates)
        {
            if (candidate != null
                && CraftingSkillTooltipText.MatchesSkillDescription(
                    candidate.m_text,
                    craftingDescription))
            {
                if (candidate.gameObject.activeInHierarchy)
                {
                    return candidate;
                }

                inactiveMatch ??= candidate;
            }
        }

        return inactiveMatch;
    }
}
