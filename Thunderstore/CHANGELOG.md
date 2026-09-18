# Changelog

## 1.0.7

- Added the server-synchronized `Thunderstone Required Global Key` setting, defaulting to `defeated_eikthyr` so traders sell Thunderstone after Eikthyr instead of The Elder. Leave it empty to preserve the original requirement.
- Changed the default incinerator build recipe to `Tin:8,Copper:4,Bronze:2,Thunderstone:1` and documented the vanilla recipe in the config description. Existing saved recipe settings are preserved.

## 1.0.6

- Added the server-synchronized `Allow Equipment Changes While Running` option, enabled by default. Queued equip and manual unequip actions now temporarily suspend sprint so Valheim's original action time and animation can complete, then resume held or toggled Run input.

## 1.0.5

- Updated Crafting bonus patching and administrator command visibility for Valheim 1.0.12.
- Excluded upgrader-only catalysts such as Battle and Protection Idols from repair costs and incinerator dismantling returns.

## 1.0.4

- Updated console commands and Crafting bonus handling for Valheim 1.0.7.
- Updated the bundled ServerSync implementation for Valheim 1.0.7 networking and administrator checks.

## 1.0.3

- Moved the extended Crafting skill tooltip beside the Skills panel with safe-area positioning, gamepad support, and localized UI compatibility.
- Reduced repeated repair preview, affordability, UI lookup, and incinerator blacklist work without changing repair or dismantling rules.
- Added opt-in Debug deployment through `DeployToGame=true` so only the final merged DLL is copied to the game.

## 1.0.2

- Added server-authoritative administrator verification for `rrm_setdurability` on dedicated servers.
- Added safe repair support for recipe-less equipment exposed by other mods, including Homestead's Dvergr circlet.
- Used the first upgrade material amount as the base repair cost for upgrade-only recipes.

## 1.0.1

- Added subtle square backgrounds behind the selected repair item, each material cost, and the free-repair indicator for better readability.
- Simplified the repair cost, selection, and UI flow while preserving existing repair behavior.
- Kept Crafting bonus output chance within its configured level-100 maximum when other mods alter skill factors.
- Improved config reload lifecycle and incinerator dismantling validation and success handling.

## 1.0.0

- Initial release.
