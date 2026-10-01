# Repair-cost policy checks

Run with:

```powershell
dotnet run --project tests/RepairCostChecks/RepairCostChecks.csproj
```

This dependency-free .NET 10 console harness links the production `RepairCostSystem`,
`RepairSelectionState`, `RepairCostRoundingSystem`, `CraftingFreeRepairSystem`, `EquipmentTypeRules`, and
`PrefabPatternMatcher` source files. The cost, material exclusions, alternative
selection, stochastic hash/rounding, ticket persistence/fingerprinting, and cycle
completion policies are exercised directly, not copied or simulated.

Checks cover threshold boundaries (including 0, 5, 10, 15, 20 and 100), unchanged
10-percent cost buckets above the first bucket, minimum-cost behavior, zero rates,
excluded ingredients, only-one-ingredient recipes, station/Galleon gates,
no-cost cheat, Crafting free repairs, stable previews/config changes, completed
cycles, and nearby-container availability/failure fallback. Galleon checks also
exercise candidate filtering, shared wheel selection, click-time rejection of an
expired session, no-cost-cheat rejection in an invalid ship session, and the
absence of station-exception leakage when switching to a normal station.

Anvil free-repair bonus checks exercise the same deterministic roll against the
base and additive, 100-percent-capped thresholds. They cover both station visit
orders, anvil-only eligibility, persistence/another player's skill, zero bonus,
the master toggle, unchanged existing Free/Paid decisions, setting snapshots,
cost-plan invalidation (including anvil-only results not yet revealed at an
anvil), and advancing each completed paid/free cycle only once. The v1 ticket
keeps its five-field layout and adds outcome `A` for anvil-only free repairs;
existing decisions are not recalculated when chance settings change. Fixtures
select item IDs using the production deterministic-roll method, not a duplicate
hash implementation.

`Stubs.cs` supplies minimal game, Unity, config, catalog and AzuCraftyBoxes contract
doubles, including an explicit valid/invalid Galleon-session contract. The real
Artisan compatibility component's lifetime, distance, and GUI checks are not
simulated by this contract. Requirement amounts model base cost at quality 1 and incremental
cost at later qualities; this is not a test of live Valheim recipe registration.
Most cases deliberately set base cost to 100% for exact bucket assertions; cases
testing fractional costs set their own percentages. The harness does not validate
BepInEx config binding/sync, Unity object lifetime, actual resource consumption,
Harmony patch installation, rendering, networking, or the real AzuCraftyBoxes or
Artisan Mastery DLLs. No game assemblies are loaded or publicized. Production
Debug build/deployment and real in-game checks remain separate validations.
