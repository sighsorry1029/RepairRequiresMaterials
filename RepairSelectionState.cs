using System.Collections.Generic;
using UnityEngine;
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
            _selectedIndex = 0;
            InvalidatePreviewCache();
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
        _preview = RepairCostSystem.TryGetRepairPreview(player, _selectedItem, out RepairPreview? preview)
            ? preview
            : null;
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
            return false;
        }

        _selectedIndex = requestedIndex;
        _selectedItem = requestedItem;
        if (!RepairCostSystem.TryGetRepairPreview(player, requestedItem, out preview) || preview == null)
        {
            _preview = null;
            return false;
        }

        _preview = preview;
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
        InvalidatePreviewCache();
        _preview = RepairCostSystem.TryGetRepairPreview(player, _selectedItem, out RepairPreview? preview)
            ? preview
            : null;
        _lastRefreshTime = Time.unscaledTime;
        return _preview != null;
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

        InvalidatePreviewCache();
    }

    internal static void Reset()
    {
        Candidates.Clear();
        _player = null;
        _selectedItem = null;
        _selectedIndex = 0;
        InvalidatePreviewCache();
    }

    private static void InvalidatePreviewCache()
    {
        _preview = null;
        _displayedPreview = null;
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
