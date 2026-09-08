using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
        if (string.IsNullOrWhiteSpace(tooltipText)
            || string.IsNullOrWhiteSpace(skillDescription))
        {
            return false;
        }

        if (tooltipText!.IndexOf(skillDescription!, StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        if (Localization.instance == null)
        {
            return false;
        }

        string localizedDescription = Localization.instance.Localize(skillDescription!);
        return !string.IsNullOrWhiteSpace(localizedDescription)
               && !string.Equals(localizedDescription, skillDescription, StringComparison.Ordinal)
               && tooltipText.IndexOf(localizedDescription, StringComparison.Ordinal) >= 0;
    }

    internal static bool HasRepairRequiresMaterialsHeading(string? tooltipText)
    {
        if (string.IsNullOrEmpty(tooltipText))
        {
            return false;
        }

        if (tooltipText!.IndexOf(HeadingToken, StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        if (Localization.instance == null)
        {
            return false;
        }

        string localizedHeading = Localization.instance.Localize(HeadingToken);
        return !string.IsNullOrEmpty(localizedHeading)
               && !string.Equals(localizedHeading, HeadingToken, StringComparison.Ordinal)
               && tooltipText.IndexOf(localizedHeading, StringComparison.Ordinal) >= 0;
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

internal static class CraftingSkillTooltipLayoutSystem
{
    private static readonly ConditionalWeakTable<UITooltip, SkillTooltipBinding> TooltipBindings = new();
    private static readonly ConditionalWeakTable<SkillsDialog, SkillTooltipBinding> DialogBindings = new();
    private static readonly Vector3[] Corners = new Vector3[4];
    private static bool _layoutFailureLogged;

    internal static void ClearTooltipBinding(SkillsDialog dialog)
    {
        if (!DialogBindings.TryGetValue(dialog, out SkillTooltipBinding? binding))
        {
            return;
        }

        RestoreBinding(binding);
        if (UITooltip.m_current == binding.Tooltip)
        {
            UITooltip.HideTooltip();
        }
    }

    internal static void BindTooltip(SkillsDialog dialog, UITooltip tooltip)
    {
        ClearTooltipBinding(dialog);

        Canvas? canvas = tooltip.GetComponentInParent<Canvas>();
        RectTransform? panel = FindSkillPanel(dialog);
        if (canvas == null
            || canvas.transform is not RectTransform canvasRect
            || panel == null
            || dialog.m_listRoot == null)
        {
            return;
        }

        RectTransform? row = null;
        for (Transform current = tooltip.transform;
             current != null && current != dialog.m_listRoot;
             current = current.parent)
        {
            if (current.parent == dialog.m_listRoot)
            {
                row = current as RectTransform;
                break;
            }
        }

        if (row == null)
        {
            return;
        }

        if (TooltipBindings.TryGetValue(tooltip, out SkillTooltipBinding? previousBinding))
        {
            RestoreBinding(previousBinding);
        }

        SkillTooltipBinding binding = new(dialog, panel, row, canvas, canvasRect, tooltip);
        TooltipBindings.Add(tooltip, binding);
        DialogBindings.Add(dialog, binding);

        // Gamepad and mouse tooltips should share the canvas and avoid the scroll-view mask.
        tooltip.m_anchor = canvasRect;
        tooltip.m_fixedPosition = Vector2.zero;
    }

    internal static void UpdateVisibleTooltip(UITooltip tooltip)
    {
        if (UITooltip.m_current != tooltip
            || UITooltip.m_tooltip == null
            || !TooltipBindings.TryGetValue(tooltip, out SkillTooltipBinding? binding))
        {
            return;
        }

        try
        {
            if (binding.Dialog == null
                || !binding.Dialog.isActiveAndEnabled
                || binding.Row == null
                || !binding.Row.gameObject.activeInHierarchy)
            {
                UITooltip.HideTooltip();
                return;
            }

            GameObject root = UITooltip.m_tooltip;
            if (!root.activeSelf)
            {
                return; // Preserve vanilla's mouse hover delay.
            }

            Camera? camera = binding.Canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : binding.Canvas.worldCamera;
            Rect safeArea = Screen.safeArea;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    binding.CanvasRect,
                    safeArea.min,
                    camera,
                    out Vector2 screenMin)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    binding.CanvasRect,
                    safeArea.max,
                    camera,
                    out Vector2 screenMax))
            {
                RestoreBinding(binding);
                return;
            }

            if (binding.View == null || binding.View.Root != root)
            {
                binding.View = SkillTooltipView.Create(root, binding.CanvasRect);
                if (binding.View == null)
                {
                    RestoreBinding(binding);
                    return;
                }
            }

            Rect viewport = Rect.MinMaxRect(screenMin.x, screenMin.y, screenMax.x, screenMax.y);
            Rect panelBounds = GetCanvasBounds(binding.Panel, binding.CanvasRect);
            Rect rowBounds = GetCanvasBounds(binding.Row, binding.CanvasRect);
            binding.View.UpdateLayout();
            float scale = CraftingSkillTooltipLayout.GetScale(viewport, binding.View.Size);
            Vector2 topLeft = CraftingSkillTooltipLayout.GetTopLeft(
                viewport,
                panelBounds,
                rowBounds.yMax,
                binding.View.Size * scale);
            binding.View.Panel.localScale = new Vector3(scale, scale, 1f);

            // Vanilla clamps the first child, so place that child absolutely each frame.
            binding.View.Panel.position = binding.CanvasRect.TransformPoint(
                new Vector3(topLeft.x, topLeft.y, 0f));
        }
        catch (Exception exception)
        {
            RestoreBinding(binding);
            UITooltip.HideTooltip();
            if (!_layoutFailureLogged)
            {
                _layoutFailureLogged = true;
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    "Could not position the Crafting skill tooltip: "
                    + exception.GetBaseException().Message);
            }
        }
    }

    private static void RestoreBinding(SkillTooltipBinding binding)
    {
        UITooltip tooltip = binding.Tooltip;
        if (tooltip != null)
        {
            tooltip.m_anchor = binding.OriginalAnchor;
            tooltip.m_fixedPosition = binding.OriginalFixedPosition;
        }

        TooltipBindings.Remove(tooltip!);
        DialogBindings.Remove(binding.Dialog);
    }

    private static RectTransform? FindSkillPanel(SkillsDialog dialog)
    {
        if (dialog.m_listRoot == null)
        {
            return null;
        }

        for (Transform current = dialog.m_listRoot.parent;
             current != null && current != dialog.transform;
             current = current.parent)
        {
            if (current.parent == dialog.transform && current is RectTransform frame)
            {
                return frame.Find("bkg") as RectTransform ?? frame;
            }
        }

        return null;
    }

    private static Rect GetCanvasBounds(RectTransform rect, RectTransform canvas)
    {
        rect.GetWorldCorners(Corners);
        Vector2 min = canvas.InverseTransformPoint(Corners[0]);
        Vector2 max = min;
        for (int index = 1; index < Corners.Length; index++)
        {
            Vector2 point = canvas.InverseTransformPoint(Corners[index]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private sealed class SkillTooltipBinding
    {
        internal readonly SkillsDialog Dialog;
        internal readonly RectTransform Panel;
        internal readonly RectTransform Row;
        internal readonly Canvas Canvas;
        internal readonly RectTransform CanvasRect;
        internal readonly UITooltip Tooltip;
        internal readonly RectTransform? OriginalAnchor;
        internal readonly Vector2 OriginalFixedPosition;
        internal SkillTooltipView? View;

        internal SkillTooltipBinding(
            SkillsDialog dialog,
            RectTransform panel,
            RectTransform row,
            Canvas canvas,
            RectTransform canvasRect,
            UITooltip tooltip)
        {
            Dialog = dialog;
            Panel = panel;
            Row = row;
            Canvas = canvas;
            CanvasRect = canvasRect;
            Tooltip = tooltip;
            OriginalAnchor = tooltip.m_anchor;
            OriginalFixedPosition = tooltip.m_fixedPosition;
        }
    }

    private sealed class SkillTooltipView
    {
        private const float Padding = CraftingSkillTooltipLayout.Padding;
        private const float TopicGap = 8f;

        internal readonly GameObject Root;
        internal readonly RectTransform Panel;
        private readonly TMP_Text _body;
        private readonly TMP_Text? _topic;
        private string? _bodyText;
        private string? _topicText;

        internal Vector2 Size => Panel.sizeDelta;

        private SkillTooltipView(
            GameObject root,
            RectTransform panel,
            TMP_Text body,
            TMP_Text? topic)
        {
            Root = root;
            Panel = panel;
            _body = body;
            _topic = topic;
        }

        internal static SkillTooltipView? Create(GameObject root, RectTransform canvas)
        {
            TMP_Text? body = Utils.FindChild(root.transform, "Text")?.GetComponent<TMP_Text>();
            TMP_Text? topic = Utils.FindChild(root.transform, "Topic")?.GetComponent<TMP_Text>();
            if (body == null || body.font == null || body.transform == root.transform)
            {
                return null;
            }

            root.transform.SetParent(canvas, false);
            root.transform.localScale = Vector3.one;
            root.transform.localRotation = Quaternion.identity;
            DisableAutomaticLayout(root);
            CanvasGroup group = root.GetComponent<CanvasGroup>() ?? root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            GameObject panelObject = new(
                "RepairRequiresMaterials_CraftingSkillTooltip",
                typeof(RectTransform),
                typeof(Image));
            RectTransform panel = (RectTransform)panelObject.transform;
            panel.SetParent(root.transform, false);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            Image background = panelObject.GetComponent<Image>();
            background.color = new Color(0.055f, 0.045f, 0.04f, 0.94f);
            background.raycastTarget = false;

            PrepareText(body, panel, TextAlignmentOptions.TopLeft);
            if (topic != null && topic != body)
            {
                PrepareText(topic, panel, TextAlignmentOptions.Top);
            }

            for (int index = 0; index < root.transform.childCount; index++)
            {
                Transform child = root.transform.GetChild(index);
                if (child != panel)
                {
                    child.gameObject.SetActive(false);
                }
            }

            panel.SetAsFirstSibling();
            return new SkillTooltipView(root, panel, body, topic == body ? null : topic);
        }

        internal void UpdateLayout()
        {
            if (_bodyText == _body.text && _topicText == _topic?.text)
            {
                return;
            }

            _bodyText = _body.text;
            _topicText = _topic?.text;
            const float innerWidth = CraftingSkillTooltipLayout.BodyWidth;
            float y = Padding;
            if (_topic != null)
            {
                bool hasTopic = !string.IsNullOrWhiteSpace(_topicText);
                _topic.gameObject.SetActive(hasTopic);
                if (hasTopic)
                {
                    y += SetTextRect(_topic, innerWidth, y) + TopicGap;
                }
            }

            y += SetTextRect(_body, innerWidth, y);
            Panel.sizeDelta = new Vector2(CraftingSkillTooltipLayout.Width, y + Padding);
        }

        private static void PrepareText(
            TMP_Text text,
            RectTransform panel,
            TextAlignmentOptions alignment)
        {
            DisableAutomaticLayout(text.gameObject);
            RectTransform rect = text.rectTransform;
            rect.SetParent(panel, false);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            text.margin = Vector4.zero;
            text.raycastTarget = false;
            text.gameObject.SetActive(true);
        }

        private static void DisableAutomaticLayout(GameObject target)
        {
            LayoutGroup? layout = target.GetComponent<LayoutGroup>();
            ContentSizeFitter? fitter = target.GetComponent<ContentSizeFitter>();
            AspectRatioFitter? aspect = target.GetComponent<AspectRatioFitter>();
            if (layout != null)
            {
                layout.enabled = false;
            }

            if (fitter != null)
            {
                fitter.enabled = false;
            }

            if (aspect != null)
            {
                aspect.enabled = false;
            }
        }

        private static float SetTextRect(TMP_Text text, float width, float y)
        {
            float height = Mathf.Max(
                1f,
                Mathf.Ceil(text.GetPreferredValues(
                    text.text,
                    width,
                    float.PositiveInfinity).y));
            text.rectTransform.anchoredPosition = new Vector2(Padding, -y);
            text.rectTransform.sizeDelta = new Vector2(width, height);
            return height;
        }
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

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(SkillsDialog __instance)
    {
        if (__instance != null)
        {
            CraftingSkillTooltipLayoutSystem.ClearTooltipBinding(__instance);
        }
    }

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

            if (CraftingSkillTooltipText.HasRepairRequiresMaterialsHeading(tooltip.m_text))
            {
                CraftingSkillTooltipLayoutSystem.BindTooltip(__instance, tooltip);
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

[HarmonyPatch(typeof(UITooltip), "LateUpdate")]
internal static class CraftingSkillTooltipPositionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(UITooltip __instance)
    {
        CraftingSkillTooltipLayoutSystem.UpdateVisibleTooltip(__instance);
    }
}

internal static class CraftingSkillTooltipLayout
{
    internal const float BodyWidth = 250f;
    internal const float Padding = 16f;
    internal const float Width = BodyWidth + Padding * 2f;
    private const float Gap = 8f;
    private const float Margin = 12f;

    internal static float GetScale(Rect viewport, Vector2 size)
    {
        float widthScale = Mathf.Max(1f, viewport.width - Margin * 2f)
                           / Mathf.Max(1f, size.x);
        float heightScale = Mathf.Max(1f, viewport.height - Margin * 2f)
                            / Mathf.Max(1f, size.y);
        return Mathf.Min(1f, Mathf.Min(widthScale, heightScale));
    }

    internal static Vector2 GetTopLeft(
        Rect viewport,
        Rect skillPanel,
        float rowTop,
        Vector2 size)
    {
        float minX = viewport.xMin + Margin;
        float maxY = viewport.yMax - Margin;
        float x = Mathf.Clamp(
            skillPanel.xMin - Gap - size.x,
            minX,
            Mathf.Max(minX, viewport.xMax - Margin - size.x));
        float y = Mathf.Clamp(
            rowTop,
            Mathf.Min(maxY, viewport.yMin + Margin + size.y),
            maxY);
        return new Vector2(x, y);
    }
}
