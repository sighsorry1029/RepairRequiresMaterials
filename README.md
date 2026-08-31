# RepairRequiresMaterials

Recipe-based equipment repair, Crafting-skill bonuses, and incinerator dismantling through Valheim's existing UI.

## Showcase

### Material-based repair

See the selected equipment, its durability, and every required material beside Valheim's original repair button.

![Recipe material requirements beside the vanilla repair button](https://i.ibb.co/dsnSzh2B/repairrequirematerials.png)

Scroll over the repair button to cycle through every item the current crafting station can repair.

![Cycling through repairable equipment with the mouse wheel](https://i.ibb.co/rKQtzCgt/repairrotation.gif)

Repair the selected item directly with its displayed recipe materials.

![Repairing equipment with recipe materials](https://i.ibb.co/60ms43mk/repairrequirematerials.gif)

### Crafting skill bonus

A deterministic Crafting-skill roll can replace the material requirements with a free repair.

![Crafting skill granting a free repair](https://i.ibb.co/nN1mtWz3/freerepair.png)

### Incinerator dismantling

Eligible equipment is converted back into a configurable share of its base and cumulative upgrade materials.

![Equipment and returned materials in the incinerator](https://i.ibb.co/XmTbcn0/incineratordismantle.png)

Hold the configured modifier and use the incinerator to dismantle items while ordinary use remains unchanged.

![Dismantling equipment with the incinerator](https://i.ibb.co/F4VLfkPj/incineratordismantle.gif)

## Features

- Repairs equipment with materials from its exact crafting recipe.
- Keeps the vanilla repair button and adds a compact material strip with subtle per-slot backgrounds.
- Uses Crafting skill for free repairs, bonus output, and faster equipment changes.
- Pulls missing repair materials from permitted AzuCraftyBoxes containers when available.
- Dismantles equipment and explicitly allowed items in an incinerator without replacing normal incineration.
- Includes English and Korean UI text.

## Repair

Open a crafting station that matches the equipment's recipe and required station level. Only damaged equipment that the current station can repair is shown.

1. Hover the vanilla repair button.
2. Use the mouse wheel to select damaged equipment.
3. Check the material icons and `available/required` counts.
4. Click the vanilla repair button.

The player inventory is used first. If AzuCraftyBoxes is installed, permitted nearby containers can provide the remainder. Snapshot-only kg Item Drawers are excluded because their removal cannot be verified safely.

### Repair cost

```text
full repair cost = base recipe amount × (Base Material Cost Percent / 100)
                 + (quality - 1) × amount per level × (Quality Increment Material Cost Percent / 100)

payment = full repair cost × (missing durability bucket / 100)
```

Durability uses 10% buckets; for example, 51-60% remaining durability counts as 40% missing. Fractional costs use stochastic rounding: `0.1` costs one item with a 10% chance, while `1.8` always costs one and has an 80% chance to cost a second. The fractional roll stays fixed for that item and repair cycle, so reopening the UI cannot reroll it; the required amount can still change when the cost itself changes.

Equipment and Trophy ingredients are excluded from repair costs. Ammunition remains valid unless `Repair Material Blacklist` excludes it. If no eligible exact recipe retains an allowed ingredient, that equipment is omitted from the repair list. Successful repairs keep Valheim's durability-based Crafting experience.

## Crafting Skill Effects

### Free repairs

Material-cost repairs can be free. The default chance scales from 10% at Crafting level 0 to 30% at level 100; a level-0 value above the level-100 value is capped to it. The ticket cannot be rerolled. `FREE` remains valid only while the repair-cost plan is unchanged; changing that plan locks the current cycle to paid.

### Bonus output

Eligible stackable outputs at Crafting-skill stations roll once per base item. With the default setting, each item has a 25% bonus chance at Crafting level 100; a 20-item recipe therefore performs 20 independent rolls. Successful rolls are combined into Valheim's existing `+K` result and one bonus effect.

Outputs matched by `Bonus Output Excluded Prefabs` receive neither this mod's bonus nor Valheim's original Crafting bonus.

### Equip speed

Equipment equip and manual unequip times decrease linearly with Crafting skill. The default setting reaches a 50% reduction at level 100. The current effects are also summarized in the in-game Crafting skill tooltip.

## Incinerator

`Incinerator Build Recipe` changes the materials required to construct the vanilla incinerator. Its default is the vanilla `Iron:8,Copper:4,Thunderstone:1` recipe.

Put items in a vanilla incinerator, hold `Modifier Key` (default `LeftAlt`), and press the current Valheim Use binding. This dismantles every eligible item currently inside, including the full stack of any matched stackable item. Ordinary Use is unchanged.

- Supported tools, weapons, shields, armor, capes, utility items, trinkets, and torches are eligible by default; ammunition is not.
- `Additional Dismantleable Items` can add non-equipment prefabs.
- The requesting player must know the item's recipe.
- Quest items, blacklisted items, and unsafe or ambiguous recipes remain untouched.
- Returned materials stay in the incinerator; if they do not fit, nothing is changed.

```text
raw return = base crafting cost × (Base Material Return Percent / 100)
           + cumulative upgrade cost × (Cumulative Upgrade Material Return Percent / 100)
```

Additional stackable items are scaled by their source stack and recipe output amount. Fractional returns are stochastically rounded after matching materials are combined. An eligible source is consumed even when every fractional roll returns zero; this prevents repeated rerolls of the same item. Enchantments and other custom item data do not increase the return and are destroyed with the source.

## Configuration

| Section | Setting | Default | Range |
|---|---|---:|---:|
| `1 - General` | `Lock Configuration` | `On` | - |
| `2 - Repair Costs` | `Base Material Cost Percent` | `15%` | `0-100%` |
|  | `Quality Increment Material Cost Percent` | `5%` | `0-100%` |
|  | `Repair Material Blacklist` | empty | - |
| `3 - Crafting Skill Effects` | `Enable Free Repairs` | `On` | - |
|  | `Free Repair Chance At Level 0` | `10%` | `0-100%` |
|  | `Free Repair Chance At Level 100` | `30%` | `0-100%` |
|  | `Bonus Output Chance At Level 100` | `25%` | `0-25%` |
|  | `Bonus Output Excluded Prefabs` | `Simple_*_Socket, Advanced_*_Socket, Perfect_*_Socket` | - |
|  | `Equip Time Reduction At Level 100` | `50%` | `0-100%` |
| `4 - Incinerator Dismantling` | `Incinerator Build Recipe` | `Iron:8,Copper:4,Thunderstone:1` | - |
|  | `Enabled` | `On` | - |
|  | `Modifier Key` | `LeftAlt` | - |
|  | `Base Material Return Percent` | `10%` | `0-100%` |
|  | `Cumulative Upgrade Material Return Percent` | `20%` | `0-100%` |
|  | `Additional Dismantleable Items` | empty | - |
|  | `Item Blacklist` | empty | - |

Gameplay settings are synchronized through ServerSync. `Modifier Key` is local to each client.

`Repair Material Blacklist`, `Bonus Output Excluded Prefabs`, and `Additional Dismantleable Items` accept comma, semicolon, or newline separators and case-insensitive whole-name `*` wildcards. `Item Blacklist` accepts comma-separated, case-insensitive exact prefab names only and takes priority over both default equipment eligibility and additional entries.

`Incinerator Build Recipe` accepts comma, semicolon, or newline-separated exact `ItemPrefab:Amount` entries. Leave it empty to restore the original recipe, or use `None` for no build cost. Invalid definitions restore the original recipe. This setting does not alter normal incineration conversions or Alt+Use dismantling returns.

AzuCraftyBoxes container use and known-recipe dismantling are always enabled when applicable; they have no separate settings.

## Admin Command

```text
rrm_setdurability <0-100>
```

Sets every durability-bearing equipment item in the invoking host or administrator's inventory to the given percentage of its quality-adjusted maximum durability. Equipped items are included; ammunition, materials, and consumables are not.
