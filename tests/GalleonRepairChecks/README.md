# Galleon repair service checks

Run from the repository root:

```powershell
dotnet run --project tests/GalleonRepairChecks/GalleonRepairChecks.csproj
```

This dependency-free .NET 10 console test links the actual `RepairService.cs`.
It checks batch sequencing, candidate snapshots, fresh payment availability,
partial-consumption safety, completed repair notifications, selected-repair
behavior, and callback reentrancy. Tests exit nonzero on a failure.

`Stubs.cs` supplies deterministic in-memory contracts for the inventory, player,
preview provider, free/rounding cycle completion, AzuCraftyBoxes result, and UI.
The free outcome and recipe support are supplied by the test; this does not test
the probability formula, persisted ticket format, recipe selection, the real
AzuCraftyBoxes container implementation, or network ownership. It only verifies
that the real service honors those contracts and calls completion exactly once.

No Valheim, Unity, Artisan Mastery, or publicized DLL is referenced or loaded.
These checks do not verify Harmony field injection, the Artisan component
hierarchy, actual effect RPCs, game initialization, or real game/server behavior.
Those still need in-game validation with the supported original assemblies.
