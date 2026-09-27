# Repair-cost policy checks

Run with:

```powershell
dotnet run --project tests/RepairCostChecks/RepairCostChecks.csproj
```

This dependency-free .NET 10 console harness links the production `RepairCostSystem`,
`RepairCostRoundingSystem`, `CraftingFreeRepairSystem`, `EquipmentTypeRules`, and
`PrefabPatternMatcher` source files. The cost, material exclusions, alternative
selection, stochastic hash/rounding, ticket persistence/fingerprinting, and cycle
completion policies are exercised directly, not copied or simulated.

Checks cover threshold boundaries (including 0, 5, 10, 15, 20 and 100), unchanged
10-percent cost buckets above the first bucket, minimum-cost behavior, zero rates,
excluded ingredients, only-one-ingredient recipes, station/Galleon gates,
no-cost cheat, Crafting free repairs, stable previews/config changes, completed
cycles, and nearby-container availability/failure fallback.

`Stubs.cs` supplies minimal game, Unity, config, catalog and AzuCraftyBoxes contract
doubles. Its requirement amounts model base cost at quality 1 and incremental
cost at later qualities; this is not a test of live Valheim recipe registration.
Most cases deliberately set base cost to 100% for exact bucket assertions; cases
testing fractional costs set their own percentages. The harness does not validate
BepInEx config binding/sync, Unity object lifetime, actual resource consumption,
Harmony patch installation, rendering, networking, or the real AzuCraftyBoxes or
Artisan Mastery DLLs. No game assemblies are loaded or publicized. Production
Debug build/deployment and real in-game checks remain separate validations.
