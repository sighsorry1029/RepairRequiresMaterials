using System;
using System.Collections.Generic;
using Jotunn.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RepairRequiresMaterials;

internal static class RepairSelectionState
{
    private const float RefreshIntervalSeconds = 0.15f;
    private static readonly List<ItemDrop.ItemData> Candidates = new();

    private static Player? _player;
    private static ItemDrop.ItemData? _selectedItem;
    private static RepairPreview? _preview;
    private static RepairPreview? _displayedPreview;
    private static int _selectedIndex;
    private static int _lastRefreshFrame = -1;
    private static float _lastRefreshTime = float.NegativeInfinity;

    internal static int CandidateCount => Candidates.Count;
    internal static int SelectedIndex => _selectedIndex;

    internal static bool Refresh(Player player, bool force = false)
    {
        if ((Object)(object)player == null)
        {
            Reset();
            return false;
        }

        if (!ReferenceEquals(_player, player))
        {
            Candidates.Clear();
            _player = player;
            _selectedItem = null;
            _preview = null;
            _displayedPreview = null;
            _selectedIndex = 0;
            _lastRefreshFrame = -1;
            _lastRefreshTime = float.NegativeInfinity;
        }

        if (!force && _lastRefreshFrame == Time.frameCount)
        {
            return _preview != null;
        }

        if (!force && Time.unscaledTime - _lastRefreshTime < RefreshIntervalSeconds)
        {
            return _preview != null;
        }

        ItemDrop.ItemData? previousSelection = _selectedItem;
        int fallbackIndex = _selectedIndex;

        RepairCostSystem.GetRepairableItems(player, Candidates);
        if (Candidates.Count == 0)
        {
            _selectedItem = null;
            _preview = null;
            _selectedIndex = 0;
            _lastRefreshFrame = Time.frameCount;
            _lastRefreshTime = Time.unscaledTime;
            return false;
        }

        int index = FindCandidateIndex(previousSelection);
        if (index < 0)
        {
            index = Mathf.Clamp(fallbackIndex, 0, Candidates.Count - 1);
        }

        _selectedIndex = index;
        _selectedItem = Candidates[index];
        _preview = RepairCostSystem.TryGetRepairPreview(player, _selectedItem, out RepairPreview? preview) ? preview : null;
        _lastRefreshFrame = Time.frameCount;
        _lastRefreshTime = Time.unscaledTime;
        return _preview != null;
    }

    internal static bool TryGetSelectedPreview(Player player, out RepairPreview? preview, bool force = false)
    {
        bool available = Refresh(player, force);
        preview = _preview;
        return available && preview != null;
    }

    internal static bool TryGetDisplayedPreview(Player player, out RepairPreview? preview)
    {
        preview = ReferenceEquals(_player, player) ? _displayedPreview : null;
        return preview != null;
    }

    internal static void MarkDisplayedPreview(RepairPreview preview)
    {
        _displayedPreview = ReferenceEquals(_selectedItem, preview.Item) ? preview : null;
    }

    internal static void ClearDisplayedPreview()
    {
        _displayedPreview = null;
    }

    internal static bool TryGetPreviewForRepair(Player player, out RepairPreview? preview)
    {
        preview = null;
        if ((Object)(object)player == null)
        {
            return false;
        }

        if (!ReferenceEquals(_player, player) || _selectedItem == null)
        {
            if (!Refresh(player, force: true) || _selectedItem == null)
            {
                return false;
            }
        }

        ItemDrop.ItemData requestedItem = _selectedItem;
        RepairCostSystem.GetRepairableItems(player, Candidates);
        int requestedIndex = FindCandidateIndex(requestedItem);
        if (requestedIndex < 0)
        {
            _selectedItem = null;
            _preview = null;
            _selectedIndex = 0;
            _lastRefreshFrame = Time.frameCount;
            return false;
        }

        _selectedIndex = requestedIndex;
        _selectedItem = requestedItem;
        if (!RepairCostSystem.TryGetRepairPreview(player, requestedItem, out preview) || preview == null)
        {
            _preview = null;
            _lastRefreshFrame = Time.frameCount;
            return false;
        }

        _preview = preview;
        _lastRefreshFrame = Time.frameCount;
        _lastRefreshTime = Time.unscaledTime;
        return true;
    }

    internal static bool SelectOffset(Player player, int offset)
    {
        if (!Refresh(player, force: true) || Candidates.Count == 0)
        {
            return false;
        }

        int nextIndex = (_selectedIndex + offset) % Candidates.Count;
        if (nextIndex < 0)
        {
            nextIndex += Candidates.Count;
        }

        _selectedIndex = nextIndex;
        _selectedItem = Candidates[nextIndex];
        _preview = null;
        _displayedPreview = null;
        _lastRefreshFrame = -1;
        _lastRefreshTime = float.NegativeInfinity;
        return Refresh(player, force: true);
    }

    internal static void OnItemRepaired(ItemDrop.ItemData repairedItem)
    {
        int repairedIndex = FindCandidateIndex(repairedItem);
        if (repairedIndex >= 0 && Candidates.Count > 1)
        {
            int nextIndex = (repairedIndex + 1) % Candidates.Count;
            _selectedItem = Candidates[nextIndex];
            _selectedIndex = nextIndex;
        }
        else
        {
            _selectedItem = null;
            _selectedIndex = 0;
        }

        _preview = null;
        _displayedPreview = null;
        _lastRefreshFrame = -1;
        _lastRefreshTime = float.NegativeInfinity;
    }

    internal static void Reset()
    {
        Candidates.Clear();
        _player = null;
        _selectedItem = null;
        _preview = null;
        _displayedPreview = null;
        _selectedIndex = 0;
        _lastRefreshFrame = -1;
        _lastRefreshTime = float.NegativeInfinity;
    }

    private static int FindCandidateIndex(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return -1;
        }

        for (int i = 0; i < Candidates.Count; ++i)
        {
            if (ReferenceEquals(Candidates[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}

internal static class RepairPanelController
{
    private sealed class MaterialSlot
    {
        internal MaterialSlot(GameObject root, Image icon, TMP_Text name, TMP_Text amount, UITooltip? tooltip)
        {
            Root = root;
            Icon = icon;
            Name = name;
            Amount = amount;
            Tooltip = tooltip;
        }

        internal GameObject Root { get; }
        internal Image Icon { get; }
        internal TMP_Text Name { get; }
        internal TMP_Text Amount { get; }
        internal UITooltip? Tooltip { get; }
    }

    private const float PanelWidth = 320f;
    private const float PanelHeight = 280f;
    private const float PanelGap = 12f;

    private static readonly List<MaterialSlot> MaterialSlots = new();

    private static InventoryGui? _owner;
    private static InventoryGui? _failedOwner;
    private static GameObject? _root;
    private static RectTransform? _rootRect;
    private static Image? _itemIcon;
    private static TMP_Text? _itemName;
    private static TMP_Text? _itemDetails;
    private static TMP_Text? _modeTitle;
    private static TMP_Text? _statusText;
    private static TMP_Text? _emptyMaterialsText;
    private static GuiBar? _durabilityBar;
    private static Button? _previousButton;
    private static Button? _nextButton;
    private static RectTransform? _materialContent;
    private static ScrollRect? _materialScroll;
    private static ItemDrop.ItemData? _lastRenderedItem;
    private static int _lastVisualKey = int.MinValue;
    private static Navigation _originalRepairNavigation;
    private static bool _repairNavigationChanged;
    private static bool _navigationConfigured;

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
            UpdateRepairTooltip(gui, null);
            return;
        }

        EnsureCreated(gui);
        if (_root == null || _rootRect == null)
        {
            return;
        }

        if (!_root.activeSelf)
        {
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
        }

        bool showAvailable = RepairRequiresMaterialsPlugin.ShowAvailableAmountInTooltip.Value.IsOn();
        int visualKey = BuildVisualKey(preview, showAvailable);
        if (_lastVisualKey != visualKey)
        {
            UpdateContents(player, preview, showAvailable);
            _lastVisualKey = visualKey;
        }

        if (_previousButton != null)
        {
            _previousButton.interactable = RepairSelectionState.CandidateCount > 1;
        }

        if (_nextButton != null)
        {
            _nextButton.interactable = RepairSelectionState.CandidateCount > 1;
        }

        ConfigureNavigation(gui);
        PositionPanel(gui);
        UpdateRepairTooltip(gui, preview);
        RepairSelectionState.MarkDisplayedPreview(preview);
    }

    internal static void Hide()
    {
        RepairSelectionState.ClearDisplayedPreview();
        RestoreFocusIfNeeded();
        RestoreRepairNavigation();

        if (_root != null)
        {
            _root.SetActive(false);
        }
    }

    internal static void Destroy()
    {
        RestoreRepairNavigation();

        if (_root != null)
        {
            Object.Destroy(_root);
        }

        _owner = null;
        _failedOwner = null;
        _root = null;
        _rootRect = null;
        _itemIcon = null;
        _itemName = null;
        _itemDetails = null;
        _modeTitle = null;
        _statusText = null;
        _emptyMaterialsText = null;
        _durabilityBar = null;
        _previousButton = null;
        _nextButton = null;
        _materialContent = null;
        _materialScroll = null;
        _lastRenderedItem = null;
        _lastVisualKey = int.MinValue;
        _navigationConfigured = false;
        MaterialSlots.Clear();
    }

    private static void EnsureCreated(InventoryGui gui)
    {
        if (_root != null && ReferenceEquals(_owner, gui))
        {
            return;
        }

        if (ReferenceEquals(_failedOwner, gui))
        {
            return;
        }

        Destroy();
        _owner = gui;

        try
        {
            // The vanilla repair-panel objects are disabled when no station is
            // available. Keep this panel under the always-present inventory root
            // so field-repair previews remain visible in that state.
            Transform parent = gui.m_inventoryRoot != null ? gui.m_inventoryRoot : gui.transform;
            GUIManager jotunnGui = GUIManager.Instance;
            _root = jotunnGui.CreateWoodpanel(
                parent,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                PanelWidth,
                PanelHeight,
                false);
            _root.name = "RepairRequiresMaterialsPanel";
            _root.transform.SetAsLastSibling();

            _rootRect = (RectTransform)_root.transform;
            _rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(1f, 0f);
            _rootRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            LayoutElement rootLayoutElement = GetOrAddComponent<LayoutElement>(_root);
            rootLayoutElement.ignoreLayout = true;

            Image background = GetOrAddComponent<Image>(_root);
            background.raycastTarget = true;

            VerticalLayoutGroup rootLayout = GetOrAddComponent<VerticalLayoutGroup>(_root);
            rootLayout.padding = new RectOffset(10, 10, 10, 10);
            rootLayout.spacing = 5f;
            rootLayout.childAlignment = TextAnchor.UpperLeft;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;

            CreateHeader(gui);

            _modeTitle = CreateLabel(gui.m_recipeName, _root.transform, "MaterialsTitle", 19f, 24f);
            _modeTitle.alignment = TextAlignmentOptions.Left;
            _modeTitle.color = new Color(1f, 0.78f, 0.35f, 1f);

            CreateMaterialList(gui);

            _statusText = CreateLabel(gui.m_recipeName, _root.transform, "RepairStatus", 16f, 22f);
            _statusText.alignment = TextAlignmentOptions.Center;

            SetLayerRecursively(_root, gui.m_repairButton.gameObject.layer);
            _root.SetActive(false);
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Could not create the repair selection panel for this InventoryGui instance: {exception.Message}");
            Destroy();
            _failedOwner = gui;
        }
    }

    private static void CreateHeader(InventoryGui gui)
    {
        GameObject header = new("Header", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        header.transform.SetParent(_root!.transform, false);

        LayoutElement headerElement = header.GetComponent<LayoutElement>();
        headerElement.preferredHeight = 64f;

        HorizontalLayoutGroup headerLayout = header.GetComponent<HorizontalLayoutGroup>();
        headerLayout.spacing = 6f;
        headerLayout.childAlignment = TextAnchor.MiddleCenter;
        headerLayout.childControlWidth = true;
        headerLayout.childControlHeight = true;
        headerLayout.childForceExpandWidth = false;
        headerLayout.childForceExpandHeight = false;

        _previousButton = CreateNavigationButton(gui.m_qualityLevelDown, header.transform, "PreviousRepairItem", gui, -1);

        GameObject iconObject = new("ItemIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        iconObject.transform.SetParent(header.transform, false);
        _itemIcon = iconObject.GetComponent<Image>();
        _itemIcon.preserveAspect = true;
        _itemIcon.raycastTarget = false;
        LayoutElement iconLayout = iconObject.GetComponent<LayoutElement>();
        iconLayout.preferredWidth = 50f;
        iconLayout.preferredHeight = 50f;

        GameObject info = new("ItemInfo", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        info.transform.SetParent(header.transform, false);
        LayoutElement infoElement = info.GetComponent<LayoutElement>();
        infoElement.flexibleWidth = 1f;
        infoElement.preferredHeight = 60f;

        VerticalLayoutGroup infoLayout = info.GetComponent<VerticalLayoutGroup>();
        infoLayout.spacing = 3f;
        infoLayout.childAlignment = TextAnchor.MiddleLeft;
        infoLayout.childControlWidth = true;
        infoLayout.childControlHeight = true;
        infoLayout.childForceExpandWidth = true;
        infoLayout.childForceExpandHeight = false;

        _itemName = CreateLabel(gui.m_recipeName, info.transform, "ItemName", 19f, 25f);
        _itemName.alignment = TextAlignmentOptions.Left;

        _itemDetails = CreateLabel(gui.m_recipeName, info.transform, "ItemDetails", 14f, 18f);
        _itemDetails.alignment = TextAlignmentOptions.Left;
        _itemDetails.color = Color.white;

        if (gui.m_upgradeItemDurability != null)
        {
            _durabilityBar = Object.Instantiate(gui.m_upgradeItemDurability, info.transform);
            _durabilityBar.name = "ItemDurability";
            _durabilityBar.gameObject.SetActive(true);
            LayoutElement barLayout = GetOrAddComponent<LayoutElement>(_durabilityBar.gameObject);
            barLayout.ignoreLayout = false;
            barLayout.preferredHeight = 8f;
            barLayout.minHeight = 8f;
            _durabilityBar.SetMaxValue(1f);
        }

        _nextButton = CreateNavigationButton(gui.m_qualityLevelUp, header.transform, "NextRepairItem", gui, 1);
    }

    private static Button CreateNavigationButton(Button template, Transform parent, string name, InventoryGui gui, int offset)
    {
        Button button = Object.Instantiate(template != null ? template : gui.m_repairButton, parent);
        button.name = name;
        button.gameObject.SetActive(true);
        button.enabled = true;
        button.interactable = true;
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => SelectOffset(gui, offset));

        LayoutElement layout = GetOrAddComponent<LayoutElement>(button.gameObject);
        layout.ignoreLayout = false;
        layout.minWidth = 34f;
        layout.preferredWidth = 34f;
        layout.minHeight = 38f;
        layout.preferredHeight = 38f;
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;

        button.navigation = Navigation.defaultNavigation;
        return button;
    }

    private static void CreateMaterialList(InventoryGui gui)
    {
        GameObject scrollRoot = new("MaterialScroll", typeof(RectTransform), typeof(ScrollRect), typeof(LayoutElement));
        scrollRoot.transform.SetParent(_root!.transform, false);

        LayoutElement materialScrollLayout = scrollRoot.GetComponent<LayoutElement>();
        materialScrollLayout.minHeight = 88f;
        materialScrollLayout.flexibleHeight = 1f;

        GameObject viewport = new("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        viewport.transform.SetParent(scrollRoot.transform, false);
        RectTransform viewportRect = (RectTransform)viewport.transform;
        StretchToParent(viewportRect);
        Image viewportImage = viewport.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.2f);
        viewportImage.raycastTarget = true;

        GameObject content = new("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        content.transform.SetParent(viewport.transform, false);
        _materialContent = (RectTransform)content.transform;
        _materialContent.anchorMin = new Vector2(0f, 1f);
        _materialContent.anchorMax = new Vector2(1f, 1f);
        _materialContent.pivot = new Vector2(0.5f, 1f);
        _materialContent.anchoredPosition = Vector2.zero;
        _materialContent.sizeDelta = Vector2.zero;

        VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
        contentLayout.padding = new RectOffset(5, 5, 5, 5);
        contentLayout.spacing = 4f;
        contentLayout.childAlignment = TextAnchor.UpperLeft;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;

        ContentSizeFitter contentFitter = content.GetComponent<ContentSizeFitter>();
        contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _materialScroll = scrollRoot.GetComponent<ScrollRect>();
        _materialScroll.viewport = viewportRect;
        _materialScroll.content = _materialContent;
        _materialScroll.horizontal = false;
        _materialScroll.vertical = true;
        _materialScroll.movementType = ScrollRect.MovementType.Clamped;
        _materialScroll.scrollSensitivity = 24f;

        _emptyMaterialsText = CreateLabel(gui.m_recipeName, _materialContent, "NoMaterials", 15f, 36f);
        _emptyMaterialsText.alignment = TextAlignmentOptions.Center;
        _emptyMaterialsText.text = Localization.instance.Localize("$rrm_ui_no_materials_required");
    }

    private static void UpdateContents(Player player, RepairPreview preview, bool showAvailable)
    {
        if (_itemIcon == null || _itemName == null || _itemDetails == null || _statusText == null)
        {
            return;
        }

        bool itemChanged = !ReferenceEquals(_lastRenderedItem, preview.Item);
        _lastRenderedItem = preview.Item;

        _itemIcon.sprite = preview.Item.GetIcon();
        _itemName.text = Localization.instance.Localize(preview.Item.m_shared.m_name);
        if (_modeTitle != null)
        {
            string modeToken = preview.PaymentKind switch
            {
                RepairPaymentKind.FieldPowder => "$rrm_ui_field_powder",
                RepairPaymentKind.Free => "$rrm_ui_free_repair",
                _ => "$rrm_ui_repair_materials"
            };
            _modeTitle.text = Localization.instance.Localize(modeToken);
        }

        int durabilityPercent = Mathf.RoundToInt(preview.Item.GetDurabilityPercentage() * 100f);
        _itemDetails.text = $"Q{preview.Item.m_quality}  |  {durabilityPercent}%  |  {RepairSelectionState.SelectedIndex + 1}/{RepairSelectionState.CandidateCount}";

        if (_durabilityBar != null)
        {
            _durabilityBar.SetMaxValue(1f);
            _durabilityBar.SetValue(preview.Item.GetDurabilityPercentage());
        }

        EnsureMaterialSlotCount(preview.Costs.Count);
        for (int i = 0; i < MaterialSlots.Count; ++i)
        {
            MaterialSlot slot = MaterialSlots[i];
            if (i >= preview.Costs.Count)
            {
                slot.Root.SetActive(false);
                continue;
            }

            RepairMaterialCost cost = preview.Costs[i];
            slot.Root.SetActive(true);
            slot.Icon.sprite = cost.Icon;
            slot.Icon.color = Color.white;
            slot.Name.text = Localization.instance.Localize(cost.DisplayName);
            slot.Amount.text = showAvailable ? $"{cost.AvailableAmount}/{cost.RequiredAmount}" : cost.RequiredAmount.ToString();
            slot.Amount.color = cost.IsAffordable ? new Color(0.72f, 0.94f, 0.72f) : new Color(1f, 0.45f, 0.45f);

            if (slot.Tooltip != null)
            {
                slot.Tooltip.Set(cost.DisplayName, slot.Amount.text);
            }
        }

        if (_emptyMaterialsText != null)
        {
            _emptyMaterialsText.text = Localization.instance.Localize("$rrm_ui_no_materials_required");
            _emptyMaterialsText.gameObject.SetActive(preview.Costs.Count == 0);
        }

        bool affordable = IsAffordable(player, preview);
        bool stationUnavailable = preview.PaymentKind == RepairPaymentKind.StationMaterials
            && !preview.StationReady;
        _statusText.text = stationUnavailable
            ? Localization.instance.Localize(RepairPowderRegistry.StationUnavailableToken)
            : affordable
                ? Localization.instance.Localize("$inventory_repair")
                : Localization.instance.Localize("$msg_missingrequirement");
        _statusText.color = affordable ? new Color(0.72f, 0.94f, 0.72f) : new Color(1f, 0.45f, 0.45f);

        if (itemChanged && _materialScroll != null)
        {
            Canvas.ForceUpdateCanvases();
            _materialScroll.verticalNormalizedPosition = 1f;
        }
    }

    private static void EnsureMaterialSlotCount(int count)
    {
        if (_owner == null || _materialContent == null)
        {
            return;
        }

        while (MaterialSlots.Count < count)
        {
            MaterialSlots.Add(CreateMaterialSlot(_owner, _materialContent, MaterialSlots.Count));
        }
    }

    private static MaterialSlot CreateMaterialSlot(InventoryGui gui, Transform parent, int index)
    {
        GameObject? template = gui.m_recipeRequirementList != null && gui.m_recipeRequirementList.Length > 0
            ? gui.m_recipeRequirementList[0]
            : null;

        Transform? templateIcon = template != null ? template.transform.Find("res_icon") : null;
        Transform? templateName = template != null ? template.transform.Find("res_name") : null;
        Transform? templateAmount = template != null ? template.transform.Find("res_amount") : null;
        if (template != null
            && templateIcon != null
            && templateName != null
            && templateAmount != null
            && templateIcon.GetComponent<Image>() != null
            && templateName.GetComponent<TMP_Text>() != null
            && templateAmount.GetComponent<TMP_Text>() != null)
        {
            GameObject root = Object.Instantiate(template, parent, false);
            root.name = $"RepairMaterial_{index}";
            root.SetActive(true);

            Image icon = root.transform.Find("res_icon").GetComponent<Image>();
            TMP_Text name = root.transform.Find("res_name").GetComponent<TMP_Text>();
            TMP_Text amount = root.transform.Find("res_amount").GetComponent<TMP_Text>();
            icon.gameObject.SetActive(true);
            name.gameObject.SetActive(true);
            amount.gameObject.SetActive(true);
            icon.raycastTarget = false;
            name.raycastTarget = false;
            amount.raycastTarget = false;

            LayoutElement layout = GetOrAddComponent<LayoutElement>(root);
            layout.ignoreLayout = false;
            layout.minHeight = 42f;
            layout.preferredHeight = 42f;

            return new MaterialSlot(root, icon, name, amount, root.GetComponent<UITooltip>());
        }

        GameObject fallbackRoot = new($"RepairMaterial_{index}", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        fallbackRoot.transform.SetParent(parent, false);
        HorizontalLayoutGroup fallbackLayout = fallbackRoot.GetComponent<HorizontalLayoutGroup>();
        fallbackLayout.spacing = 8f;
        fallbackLayout.childAlignment = TextAnchor.MiddleLeft;
        fallbackLayout.childControlWidth = true;
        fallbackLayout.childControlHeight = true;
        fallbackLayout.childForceExpandWidth = false;
        fallbackLayout.childForceExpandHeight = false;

        LayoutElement fallbackElement = fallbackRoot.GetComponent<LayoutElement>();
        fallbackElement.minHeight = 42f;
        fallbackElement.preferredHeight = 42f;

        GameObject iconObject = new("res_icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        iconObject.transform.SetParent(fallbackRoot.transform, false);
        Image fallbackIcon = iconObject.GetComponent<Image>();
        fallbackIcon.preserveAspect = true;
        fallbackIcon.raycastTarget = false;
        LayoutElement fallbackIconLayout = iconObject.GetComponent<LayoutElement>();
        fallbackIconLayout.preferredWidth = 34f;
        fallbackIconLayout.preferredHeight = 34f;

        TMP_Text fallbackName = CreateLabel(gui.m_recipeName, fallbackRoot.transform, "res_name", 16f, 36f);
        LayoutElement nameLayout = GetOrAddComponent<LayoutElement>(fallbackName.gameObject);
        nameLayout.flexibleWidth = 1f;

        TMP_Text fallbackAmount = CreateLabel(gui.m_recipeName, fallbackRoot.transform, "res_amount", 16f, 36f);
        fallbackAmount.alignment = TextAlignmentOptions.Right;
        LayoutElement amountLayout = GetOrAddComponent<LayoutElement>(fallbackAmount.gameObject);
        amountLayout.preferredWidth = 78f;

        return new MaterialSlot(fallbackRoot, fallbackIcon, fallbackName, fallbackAmount, null);
    }

    private static TMP_Text CreateLabel(TMP_Text? template, Transform parent, string name, float fontSize, float height)
    {
        TMP_Text label;
        if (template != null)
        {
            label = Object.Instantiate(template, parent);
        }
        else
        {
            GameObject labelObject = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(parent, false);
            label = labelObject.GetComponent<TextMeshProUGUI>();
        }

        label.name = name;
        label.gameObject.SetActive(true);
        label.enabled = true;
        label.text = string.Empty;
        label.fontSize = fontSize;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Max(11f, fontSize - 5f);
        label.fontSizeMax = fontSize;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;

        RectTransform rect = label.rectTransform;
        rect.localScale = Vector3.one;
        LayoutElement layout = GetOrAddComponent<LayoutElement>(label.gameObject);
        layout.ignoreLayout = false;
        layout.minHeight = height;
        layout.preferredHeight = height;
        return label;
    }

    private static void SelectOffset(InventoryGui gui, int offset)
    {
        Player player = Player.m_localPlayer;
        if ((Object)(object)player == null)
        {
            return;
        }

        if (RepairSelectionState.SelectOffset(player, offset))
        {
            _lastVisualKey = int.MinValue;
            Refresh(gui);
        }
    }

    private static bool IsAffordable(Player player, RepairPreview preview)
    {
        return RepairCostSystem.CanAfford(player, preview);
    }

    private static void PositionPanel(InventoryGui gui)
    {
        if (_rootRect == null || _rootRect.parent == null)
        {
            return;
        }

        RectTransform repairRect = (RectTransform)gui.m_repairButton.transform;
        Vector3[] corners = new Vector3[4];
        repairRect.GetWorldCorners(corners);

        Transform parent = _rootRect.parent;
        Vector3 localTopRight = parent.InverseTransformPoint(corners[2]);
        float buttonClearanceY = localTopRight.y + PanelGap;
        _rootRect.localPosition = new Vector3(localTopRight.x, buttonClearanceY, 0f);
        Utils.ClampUIToScreen(_rootRect);

        // ClampUIToScreen may push a tall panel downward and overlap the repair
        // button. Preserve horizontal clamping but always keep the panel above it.
        Vector3 clampedPosition = _rootRect.localPosition;
        _rootRect.localPosition = new Vector3(clampedPosition.x, buttonClearanceY, clampedPosition.z);
    }

    private static void ConfigureNavigation(InventoryGui gui)
    {
        if (_previousButton == null || _nextButton == null)
        {
            return;
        }

        Selectable downTarget = gui.m_repairButton;
        Navigation previousNavigation = new()
        {
            mode = Navigation.Mode.Explicit,
            selectOnLeft = _nextButton,
            selectOnRight = _nextButton,
            selectOnDown = downTarget
        };
        _previousButton.navigation = previousNavigation;

        Navigation nextNavigation = new()
        {
            mode = Navigation.Mode.Explicit,
            selectOnLeft = _previousButton,
            selectOnRight = _previousButton,
            selectOnDown = downTarget
        };
        _nextButton.navigation = nextNavigation;

        if (!_navigationConfigured)
        {
            _originalRepairNavigation = gui.m_repairButton.navigation;
            if (_originalRepairNavigation.mode == Navigation.Mode.Explicit)
            {
                Navigation repairNavigation = _originalRepairNavigation;
                repairNavigation.selectOnUp = _previousButton;
                gui.m_repairButton.navigation = repairNavigation;
                _repairNavigationChanged = true;
            }

            _navigationConfigured = true;
        }
    }

    private static void RestoreRepairNavigation()
    {
        if (_repairNavigationChanged && _owner != null && _owner.m_repairButton != null)
        {
            Navigation currentNavigation = _owner.m_repairButton.navigation;
            if (currentNavigation.selectOnUp == _previousButton)
            {
                currentNavigation.selectOnUp = _originalRepairNavigation.selectOnUp;
                _owner.m_repairButton.navigation = currentNavigation;
            }
        }

        _repairNavigationChanged = false;
        _navigationConfigured = false;
    }

    private static void RestoreFocusIfNeeded()
    {
        if (_root == null || _owner == null || EventSystem.current == null)
        {
            return;
        }

        GameObject? selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || !selected.transform.IsChildOf(_root.transform))
        {
            return;
        }

        Selectable? fallback = GetActiveSelectable(_owner.m_repairButton)
            ?? GetActiveSelectable(_owner.m_tabCraft)
            ?? GetActiveSelectable(_owner.m_tabUpgrade)
            ?? GetActiveSelectable(_owner.m_craftButton);
        EventSystem.current.SetSelectedGameObject(fallback != null ? fallback.gameObject : null);
    }

    private static Selectable? GetActiveSelectable(Selectable? selectable)
    {
        return selectable != null
            && selectable.gameObject.activeInHierarchy
            && selectable.IsActive()
            && selectable.IsInteractable()
            ? selectable
            : null;
    }

    private static void UpdateRepairTooltip(InventoryGui gui, RepairPreview? preview)
    {
        UITooltip? existing = gui.m_repairButton.GetComponent<UITooltip>();
        if (!RepairRequiresMaterialsPlugin.ShowRepairTooltip.Value.IsOn())
        {
            if (existing != null)
            {
                existing.Set("$inventory_repair", string.Empty);
            }

            return;
        }

        UITooltip? tooltip = existing ?? CreateRepairTooltip(gui);
        if (tooltip == null)
        {
            return;
        }

        if (preview == null)
        {
            tooltip.Set("$inventory_repair", "$rrm_ui_no_repairable_item");
            return;
        }

        tooltip.Set(preview.Item.m_shared.m_name, RepairCostSystem.BuildTooltipText(preview));
    }

    private static UITooltip? CreateRepairTooltip(InventoryGui gui)
    {
        UITooltip? source = gui.m_craftButton.GetComponent<UITooltip>();
        if (source == null)
        {
            return null;
        }

        UITooltip tooltip = gui.m_repairButton.gameObject.AddComponent<UITooltip>();
        tooltip.m_tooltipPrefab = source.m_tooltipPrefab;
        tooltip.m_gamepadFocusObject = null;
        return tooltip;
    }

    private static int BuildVisualKey(RepairPreview preview, bool showAvailable)
    {
        unchecked
        {
            int hash = preview.VisualKey;
            hash = (hash * 397) ^ preview.Item.m_quality;
            hash = (hash * 397) ^ Mathf.RoundToInt(preview.Item.GetDurabilityPercentage() * 1000f);
            hash = (hash * 397) ^ RepairSelectionState.SelectedIndex;
            hash = (hash * 397) ^ RepairSelectionState.CandidateCount;
            hash = (hash * 397) ^ (int)preview.PaymentKind;
            hash = (hash * 397) ^ (showAvailable ? 1 : 0);
            if (Localization.instance != null)
            {
                hash = (hash * 397) ^ Localization.instance.GetSelectedLanguage().GetHashCode();
            }

            return hash;
        }
    }

    private static void StretchToParent(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
    {
        T component = gameObject.GetComponent<T>();
        return component != null ? component : gameObject.AddComponent<T>();
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
