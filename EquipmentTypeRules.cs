namespace RepairRequiresMaterials;

internal static class EquipmentTypeRules
{
    internal static bool IsEquipment(ItemDrop.ItemData.ItemType itemType)
    {
        return itemType switch
        {
            ItemDrop.ItemData.ItemType.Tool => true,
            ItemDrop.ItemData.ItemType.OneHandedWeapon => true,
            ItemDrop.ItemData.ItemType.TwoHandedWeapon => true,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft => true,
            ItemDrop.ItemData.ItemType.Bow => true,
            ItemDrop.ItemData.ItemType.Shield => true,
            ItemDrop.ItemData.ItemType.Torch => true,
            ItemDrop.ItemData.ItemType.Helmet => true,
            ItemDrop.ItemData.ItemType.Chest => true,
            ItemDrop.ItemData.ItemType.Legs => true,
            ItemDrop.ItemData.ItemType.Shoulder => true,
            ItemDrop.ItemData.ItemType.Utility => true,
            ItemDrop.ItemData.ItemType.Trinket => true,
            _ => false
        };
    }
}
