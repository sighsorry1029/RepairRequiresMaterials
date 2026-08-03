# RepairRequiresMaterials

RepairRequiresMaterials replaces Valheim's free equipment repair with two synchronized payment paths:

- While any crafting station is open, the selected item's exact recipe materials take priority. Repair is enabled only when Valheim's vanilla station-eligibility and minimum-level checks pass for that recipe.
- When no crafting station is open, a biome-matched repair powder is consumed from the player's inventory.

Field repair restores and reuses Valheim's vanilla repair button instead of adding a separate powder button. The compact repair panel stays above that button without covering it; only the background uses Jötunn's Valheim-style wood panel texture and border.

## Requirements

- BepInExPack Valheim 5.4.2333 or newer
- Jötunn 2.29.2 or newer
- AzuCraftyBoxes is optional and applies only to station-material repairs

The mod and matching version must be installed on the server and every client.

## Repair powders

All powders are fixed, network-stable clones of `PowderedDragonEgg`. Each craft produces four powders.

| Equipment tier | Ingredient | Station |
|---|---:|---|
| Meadows | 4 Resin | Workbench 1 |
| Black Forest | 1 Bronze | Forge 1 |
| Swamp | 1 Iron | Forge 2 |
| Ocean | 1 Chitin | Workbench 2 |
| Mountain | 2 Obsidian | Forge 3 |
| Plains | 1 Black Metal | Forge 4 |
| Mistlands | 1 Refined Eitr | Black Forge 1 |
| Ashlands | 1 Proustite Powder | Black Forge 3 |
| Deep North | Registered item; no default recipe yet | — |

With the default `Durability Repaired Per Powder = 25%`, a full repair consumes one to four powders according to the missing durability. The repair itself always restores maximum durability.

## Automatic tier resolution

The built-in ingredient-to-biome map is adapted from ValheimEnchantmentSystem without taking a runtime dependency on it. The resolver scans all enabled recipes for the exact output prefab and uses the highest mapped ingredient tier. If an item has no enabled recipe, its disabled drop/shop recipe is used as a fallback so it can still be repaired. This avoids collisions between mod items that share a localization name and supports alternate recipes.

Server-synchronized overrides are available in the config:

```ini
Item Biome Overrides = ModSword=AshLands
Ingredient Biome Overrides = CustomOre=Mistlands
```

Multiple entries can be separated with commas, semicolons, or new lines.

## Main configuration

- `Repair Material Percent`: base percentage of recipe materials used at a station.
- `Enable Field Repair`: enables repair powder use when no crafting station is open.
- `Durability Repaired Per Powder`: durability coverage represented by one powder.
- `Use AzuCraftyBoxes Containers`: includes nearby allowed containers for station repairs.
- `Show Repair Tooltip` and `Show Available Amounts`: panel/tooltip display options.

All gameplay and UI settings currently synchronize through ServerSync.

## Build

The project targets .NET Framework 4.8. `ServerSync.dll` is merged into the output; Jötunn remains an external hard dependency and is not merged.
