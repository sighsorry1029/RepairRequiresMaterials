# Changelog

## 0.1.0

- Added recipe-material repair with exact selected-item validation.
- Added nine biome repair-powder prefabs cloned from `PowderedDragonEgg` through Jötunn.
- Added field repair without a crafting station using automatic VES-style equipment tier resolution.
- Prioritized exact recipe materials whenever a crafting station is open; when Valheim considers that station ineligible or under-level, the panel shows those costs but keeps repair disabled instead of falling back to powder.
- Reused Valheim's restored vanilla repair button for field powder repairs instead of adding a separate panel button.
- Added synchronized item and ingredient biome overrides.
- Compacted and anchored the repair panel so it cannot cover the vanilla repair button, while using Jötunn's wood-panel style only for its background.
- Added English and Korean item/UI localization.
- Added optional AzuCraftyBoxes support for station-material repairs.
