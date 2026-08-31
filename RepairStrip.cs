using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RepairRequiresMaterials;

internal sealed class RepairScrollHandler : MonoBehaviour, IScrollHandler
{
    private InventoryGui? _gui;

    internal void Initialize(InventoryGui gui)
    {
        _gui = gui;
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (_gui == null || Mathf.Abs(eventData.scrollDelta.y) < 0.01f)
        {
            return;
        }

        int offset = eventData.scrollDelta.y > 0f ? -1 : 1;
        if (RepairStripController.ScrollSelection(_gui, offset))
        {
            eventData.Use();
        }
    }
}

internal static class RepairStripController
{
    private sealed class MaterialSlot
    {
        internal MaterialSlot(GameObject root, Image icon, TMP_Text amount, Color normalAmountColor)
        {
            Root = root;
            Icon = icon;
            Amount = amount;
            NormalAmountColor = normalAmountColor;
        }

        internal GameObject Root { get; }
        internal Image Icon { get; }
        internal TMP_Text Amount { get; }
        internal Color NormalAmountColor { get; }
    }

    private const string VanillaWheelSpriteName = "mousew_icon";
    private const float StripGap = 7f;
    private const float MaterialSlotWidth = 42f;
    private const float MaterialSlotHeight = 50f;
    private const float ItemSlotWidth = 52f;
    private const float ItemSlotHeight = 56f;
    private const float WheelWidth = 30f;
    private const float WheelHeight = 44f;

    private static readonly Color MissingAmountColor = new(1f, 0.32f, 0.32f, 1f);
    private static readonly List<MaterialSlot> MaterialSlots = new();

    private static InventoryGui? _owner;
    private static GameObject? _root;
    private static GameObject? _materialsRoot;
    private static RectTransform? _materialsTransform;
    private static GameObject? _skillFreeRoot;
    private static TMP_Text? _skillFreeText;
    private static Image? _itemIcon;
    private static TMP_Text? _itemQuality;
    private static GuiBar? _itemDurability;
    private static Image? _wheelImage;
    private static RepairScrollHandler? _scrollHandler;
    private static Sprite? _wheelSprite;
    private static bool _wheelLookupWarningLogged;
    private static int _lastVisualKey = int.MinValue;

    internal static void Refresh(InventoryGui gui)
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player == null || gui.m_repairButton == null)
        {
            Hide();
            return;
        }

        if (!RepairSelectionState.TryGetSelectedPreview(player, out RepairPreview? preview) || preview == null)
        {
            Hide();
            return;
        }

        EnsureCreated(gui);
        if (_root == null)
        {
            return;
        }

        RefreshWheelSprite();
        if (_skillFreeText != null)
        {
            string localizedText = RepairRequiresMaterialsLocalization.Localize("$rrm_ui_free");
            if (!string.Equals(_skillFreeText.text, localizedText, StringComparison.Ordinal))
            {
                _skillFreeText.text = localizedText;
            }
        }

        if (!_root.activeSelf)
        {
            _root.SetActive(true);
        }

        int visualKey = BuildVisualKey(preview);
        if (_lastVisualKey != visualKey)
        {
            UpdateContents(preview);
            _lastVisualKey = visualKey;
            RepairSelectionState.MarkDisplayedPreview(preview);
        }
    }

    internal static void Hide()
    {
        RepairSelectionState.ClearDisplayedPreview();
        _lastVisualKey = int.MinValue;
        if (_root != null)
        {
            _root.SetActive(false);
        }
    }

    internal static void Destroy()
    {
        if (_scrollHandler != null)
        {
            Object.Destroy(_scrollHandler);
        }

        if (_root != null)
        {
            Object.Destroy(_root);
        }

        _owner = null;
        _root = null;
        _materialsRoot = null;
        _materialsTransform = null;
        _skillFreeRoot = null;
        _skillFreeText = null;
        _itemIcon = null;
        _itemQuality = null;
        _itemDurability = null;
        _wheelImage = null;
        _scrollHandler = null;
        _wheelSprite = null;
        _wheelLookupWarningLogged = false;
        _lastVisualKey = int.MinValue;
        MaterialSlots.Clear();
    }

    internal static bool ScrollSelection(InventoryGui gui, int offset)
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player == null
            || RepairSelectionState.CandidateCount <= 1
            || !RepairSelectionState.SelectOffset(player, offset))
        {
            return false;
        }

        _lastVisualKey = int.MinValue;
        gui.UpdateRepair();
        return true;
    }

    private static void EnsureCreated(InventoryGui gui)
    {
        if (_root != null && ReferenceEquals(_owner, gui))
        {
            return;
        }

        Destroy();
        _owner = gui;

        try
        {
            _root = new GameObject(
                "RepairRequiresMaterialsStrip",
                typeof(RectTransform),
                typeof(HorizontalLayoutGroup),
                typeof(ContentSizeFitter));
            _root.transform.SetParent(gui.m_repairButton.transform, false);

            RectTransform rootRect = (RectTransform)_root.transform;
            rootRect.anchorMin = new Vector2(0f, 0.5f);
            rootRect.anchorMax = new Vector2(0f, 0.5f);
            rootRect.pivot = new Vector2(1f, 0.5f);
            rootRect.anchoredPosition = new Vector2(-StripGap, 0f);

            HorizontalLayoutGroup rootLayout = _root.GetComponent<HorizontalLayoutGroup>();
            rootLayout.spacing = 5f;
            rootLayout.childAlignment = TextAnchor.MiddleRight;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = false;
            rootLayout.childForceExpandHeight = false;

            ContentSizeFitter rootFitter = _root.GetComponent<ContentSizeFitter>();
            rootFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            rootFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            CreateMaterialsRoot();
            CreateSkillFreeLabel(gui);
            CreateItemSlot(gui);
            CreateWheelHint();

            _scrollHandler = gui.m_repairButton.GetComponent<RepairScrollHandler>()
                ?? gui.m_repairButton.gameObject.AddComponent<RepairScrollHandler>();
            _scrollHandler.Initialize(gui);

            SetLayerRecursively(_root, gui.m_repairButton.gameObject.layer);
            _root.SetActive(false);
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not create the compact repair strip: {exception.GetType().Name}: {exception.Message}");
            Destroy();
        }
    }

    private static void CreateMaterialsRoot()
    {
        _materialsRoot = new GameObject(
            "Materials",
            typeof(RectTransform),
            typeof(HorizontalLayoutGroup),
            typeof(ContentSizeFitter),
            typeof(LayoutElement));
        _materialsRoot.transform.SetParent(_root!.transform, false);
        _materialsTransform = (RectTransform)_materialsRoot.transform;

        HorizontalLayoutGroup layout = _materialsRoot.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 4f;
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = _materialsRoot.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        LayoutElement element = _materialsRoot.GetComponent<LayoutElement>();
        element.minHeight = MaterialSlotHeight;
        element.preferredHeight = MaterialSlotHeight;
    }

    private static void CreateItemSlot(InventoryGui gui)
    {
        GameObject itemRoot = CreateSlotRoot("SelectedRepairItem", ItemSlotWidth, ItemSlotHeight, _root!.transform);

        GameObject iconObject = new("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        iconObject.transform.SetParent(itemRoot.transform, false);
        _itemIcon = iconObject.GetComponent<Image>();
        _itemIcon.preserveAspect = true;
        _itemIcon.raycastTarget = false;
        StretchToParent(_itemIcon.rectTransform, 2f, 7f);

        TMP_Text? qualityTemplate = FindInventoryTextTemplate(gui, "quality");
        _itemQuality = CreateOverlayText(qualityTemplate, itemRoot.transform, "Quality");
        _itemQuality.alignment = TextAlignmentOptions.TopRight;
        _itemQuality.rectTransform.offsetMin = new Vector2(0f, 0f);
        _itemQuality.rectTransform.offsetMax = new Vector2(-1f, -1f);

        if (gui.m_upgradeItemDurability != null)
        {
            _itemDurability = Object.Instantiate(gui.m_upgradeItemDurability, itemRoot.transform);
            _itemDurability.name = "Durability";
            _itemDurability.gameObject.SetActive(true);
            RectTransform durabilityRect = (RectTransform)_itemDurability.transform;
            durabilityRect.anchorMin = new Vector2(0.08f, 0f);
            durabilityRect.anchorMax = new Vector2(0.92f, 0f);
            durabilityRect.pivot = new Vector2(0.5f, 0f);
            durabilityRect.anchoredPosition = new Vector2(0f, 2f);
            durabilityRect.sizeDelta = new Vector2(0f, 5f);
            _itemDurability.SetMaxValue(1f);
        }
    }

    private static void CreateSkillFreeLabel(InventoryGui gui)
    {
        _skillFreeRoot = CreateSlotRoot(
            "CraftingSkillFreeRepair",
            MaterialSlotWidth,
            MaterialSlotHeight,
            _materialsTransform!);

        TMP_Text? template = FindInventoryTextTemplate(gui, "amount")
            ?? FindRequirementAmountTemplate(gui);
        _skillFreeText = CreateOverlayText(template, _skillFreeRoot.transform, "FreeRepairLabel");
        float fontSize = _skillFreeText.fontSize > 0f ? _skillFreeText.fontSize : 16f;
        _skillFreeText.text = RepairRequiresMaterialsLocalization.Localize("$rrm_ui_free");
        _skillFreeText.alignment = TextAlignmentOptions.Center;
        _skillFreeText.enableAutoSizing = true;
        _skillFreeText.fontSizeMin = 10f;
        _skillFreeText.fontSizeMax = Mathf.Max(18f, fontSize);
        _skillFreeText.fontStyle |= FontStyles.Bold;
        _skillFreeText.color = new Color(1f, 0.82f, 0.2f, 1f);
        _skillFreeText.rectTransform.offsetMin = Vector2.zero;
        _skillFreeText.rectTransform.offsetMax = Vector2.zero;
        _skillFreeRoot.SetActive(false);
    }

    private static void CreateWheelHint()
    {
        GameObject wheelRoot = CreateSlotRoot("MouseWheelHint", WheelWidth, WheelHeight, _root!.transform);
        _wheelImage = wheelRoot.AddComponent<Image>();
        _wheelImage.raycastTarget = false;
        _wheelImage.preserveAspect = true;
        wheelRoot.SetActive(false);
        RefreshWheelSprite();
    }

    private static MaterialSlot CreateMaterialSlot(InventoryGui gui, int index)
    {
        GameObject root = CreateSlotRoot(
            $"RepairMaterial_{index}",
            MaterialSlotWidth,
            MaterialSlotHeight,
            _materialsTransform!);

        GameObject iconObject = new("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        iconObject.transform.SetParent(root.transform, false);
        Image icon = iconObject.GetComponent<Image>();
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        StretchToParent(icon.rectTransform, 2f, 3f);

        TMP_Text? amountTemplate = FindInventoryTextTemplate(gui, "amount")
            ?? FindRequirementAmountTemplate(gui);
        TMP_Text amount = CreateOverlayText(amountTemplate, root.transform, "AvailableRequiredAmount");
        float amountFontSize = amount.fontSize > 0f ? amount.fontSize : 16f;
        amount.alignment = TextAlignmentOptions.Bottom;
        amount.enableAutoSizing = true;
        amount.fontSizeMin = 8f;
        amount.fontSizeMax = Mathf.Max(8f, amountFontSize);
        amount.rectTransform.offsetMin = new Vector2(0f, 0f);
        amount.rectTransform.offsetMax = new Vector2(0f, -1f);

        return new MaterialSlot(root, icon, amount, amount.color);
    }

    private static GameObject CreateSlotRoot(string name, float width, float height, Transform parent)
    {
        GameObject root = new(name, typeof(RectTransform), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        LayoutElement layout = root.GetComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;
        return root;
    }

    private static void UpdateContents(RepairPreview preview)
    {
        bool showSkillFree = preview.PaymentKind == RepairPaymentKind.CraftingSkillFree;
        if (!showSkillFree)
        {
            EnsureMaterialSlotCount(preview.Costs.Count);
        }

        if (_skillFreeRoot != null)
        {
            _skillFreeRoot.SetActive(showSkillFree);
        }

        for (int i = 0; i < MaterialSlots.Count; ++i)
        {
            MaterialSlot slot = MaterialSlots[i];
            if (showSkillFree || i >= preview.Costs.Count)
            {
                slot.Root.SetActive(false);
                continue;
            }

            // The strip grows leftward from the selected item. Reverse the
            // recipe order so the first requirement stays closest to it.
            RepairMaterialCost cost = preview.Costs[preview.Costs.Count - 1 - i];
            slot.Root.SetActive(true);
            slot.Icon.sprite = cost.SourceRequirement.m_resItem.m_itemData.GetIcon();
            slot.Icon.color = Color.white;
            slot.Amount.text = $"{cost.AvailableAmount}/{cost.RequiredAmount}";
            slot.Amount.color = cost.IsAffordable ? slot.NormalAmountColor : MissingAmountColor;
        }

        if (_materialsRoot != null)
        {
            _materialsRoot.SetActive(showSkillFree || preview.Costs.Count > 0);
        }

        if (_itemIcon != null)
        {
            _itemIcon.sprite = preview.Item.GetIcon();
            _itemIcon.color = Color.white;
        }

        if (_itemQuality != null)
        {
            bool showQuality = preview.Item.m_shared.m_maxQuality > 1;
            _itemQuality.gameObject.SetActive(showQuality);
            if (showQuality)
            {
                _itemQuality.text = preview.Item.m_quality.ToString();
            }
        }

        if (_itemDurability != null)
        {
            _itemDurability.gameObject.SetActive(preview.Item.m_shared.m_useDurability);
            _itemDurability.SetValue(preview.Item.GetDurabilityPercentage());
        }

        if (_wheelImage != null)
        {
            Color wheelColor = Color.white;
            wheelColor.a = RepairSelectionState.CandidateCount > 1 ? 1f : 0.38f;
            _wheelImage.color = wheelColor;
        }

        Canvas.ForceUpdateCanvases();
    }

    private static void EnsureMaterialSlotCount(int count)
    {
        if (_owner == null || _materialsTransform == null)
        {
            return;
        }

        while (MaterialSlots.Count < count)
        {
            MaterialSlots.Add(CreateMaterialSlot(_owner, MaterialSlots.Count));
        }
    }

    private static TMP_Text? FindInventoryTextTemplate(InventoryGui gui, string childName)
    {
        Transform? child = gui.m_playerGrid?.m_elementPrefab?.transform.Find(childName);
        return child != null ? child.GetComponent<TMP_Text>() : null;
    }

    private static TMP_Text? FindRequirementAmountTemplate(InventoryGui gui)
    {
        if (gui.m_recipeRequirementList == null || gui.m_recipeRequirementList.Length == 0)
        {
            return null;
        }

        Transform? child = gui.m_recipeRequirementList[0]?.transform.Find("res_amount");
        return child != null ? child.GetComponent<TMP_Text>() : null;
    }

    private static TMP_Text CreateOverlayText(TMP_Text? template, Transform parent, string name)
    {
        TMP_Text text;
        if (template != null)
        {
            text = Object.Instantiate(template, parent, false);
        }
        else
        {
            GameObject textObject = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);
            text = textObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = 16f;
            text.color = Color.white;
        }

        text.name = name;
        text.gameObject.SetActive(true);
        text.enabled = true;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        RectTransform rect = text.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.localScale = Vector3.one;
        return text;
    }

    private static void RefreshWheelSprite()
    {
        if (_wheelImage == null || _wheelImage.sprite != null)
        {
            return;
        }

        Sprite? sprite = FindVanillaWheelSprite();
        if (sprite == null)
        {
            return;
        }

        _wheelImage.sprite = sprite;
        _wheelImage.gameObject.SetActive(true);
    }

    private static Sprite? FindVanillaWheelSprite()
    {
        if (_wheelSprite != null)
        {
            return _wheelSprite;
        }

        GameObject? buildHints = KeyHints.instance?.m_buildHints;
        if (buildHints == null)
        {
            return null;
        }

        foreach (Image image in buildHints.GetComponentsInChildren<Image>(includeInactive: true))
        {
            Sprite? sprite = image.sprite;
            if (sprite != null && string.Equals(sprite.name, VanillaWheelSpriteName, StringComparison.Ordinal))
            {
                _wheelSprite = sprite;
                return _wheelSprite;
            }
        }

        if (!_wheelLookupWarningLogged)
        {
            _wheelLookupWarningLogged = true;
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Valheim's '{VanillaWheelSpriteName}' sprite was not found; the mouse-wheel hint will be hidden.");
        }

        return null;
    }

    private static int BuildVisualKey(RepairPreview preview)
    {
        unchecked
        {
            int hash = preview.Item.GetHashCode();
            hash = (hash * 397) ^ preview.DurabilityBucketPercent;
            hash = (hash * 397) ^ (int)preview.PaymentKind;
            hash = (hash * 397) ^ (preview.HasRawMaterialCost ? 1 : 0);
            hash = (hash * 397) ^ preview.RepairCostRoundingToken.GetHashCode();
            hash = (hash * 397) ^ preview.SkillFreeTicketToken.GetHashCode();
            foreach (RepairMaterialCost cost in preview.Costs)
            {
                hash = (hash * 397) ^ cost.ResourcePrefabName.GetHashCode();
                hash = (hash * 397) ^ cost.RequiredAmount;
                hash = (hash * 397) ^ cost.AvailableAmount;
            }

            hash = (hash * 397) ^ preview.Item.m_quality;
            hash = (hash * 397) ^ Mathf.RoundToInt(preview.Item.GetDurabilityPercentage() * 1000f);
            hash = (hash * 397) ^ RepairSelectionState.SelectedIndex;
            hash = (hash * 397) ^ RepairSelectionState.CandidateCount;
            return hash;
        }
    }

    private static void StretchToParent(RectTransform rect, float horizontalPadding, float bottomPadding)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(horizontalPadding, bottomPadding);
        rect.offsetMax = new Vector2(-horizontalPadding, -horizontalPadding);
        rect.localScale = Vector3.one;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}
