# Path of Valheiman compatibility checks

This isolated harness checks the optional RRM bridge against the **original**, unmodified game and PoV assemblies, using the game's bundled Unity Mono runtime. It does not initialize either BepInEx plugin, launch Unity, connect to a server, or touch a character/world save. The test project is not shipped in the mod package.

```powershell
./tools/PathOfValheimanChecks/Run.ps1 `
  -OriginalGame 'C:/Users/blizz/.codex/references/valheim/snapshots/client-b25909357-windows-x64-20261015T103728Z/original' `
  -PathOfValheimanDll 'C:/path/to/PathOfValheiman.dll' `
  -RepairDll './bin/Release/RepairRequiresMaterials.dll'
```

`OriginalGame` is the snapshot root containing `MonoBleedingEdge` and either `valheim_Data/Managed` or `valheim_server_Data/Managed`. `BepInExCore` defaults to the local Steam game's `BepInEx/core`. `RepairDll` is optional: without it the production-bridge checks are skipped. Each invocation runs in a fresh hidden child process, retains stdout/stderr in a newly created temporary directory, and prints the input hashes.

## What it checks

- PoV Auto Repair and high-station patch signatures and original game public APIs.
- The Auto Repair loop has one availability query and one single-item repair call.
- The original game skill factor calls `GetSkillLevel`, so PoV's effective-level patch remains authoritative.
- A Harmony wrapper for the **real `CraftingStation.Interact`** first calls the **real PoV postfix**. After that wrapper has already executed/JIT-compiled, transpiling the PoV postfix changes the body reached by the existing wrapper. Removing that transpiler restores the foreign postfix without removing it.
- The narrow availability-call substitution compiles under original Mono and retains PoV's `repeat` guard.
- When supplied, the actual RRM transpiler accepts the original PoV body, rejects a missing loop, and installs on the real postfix. RRM's cached private eligibility delegate is generated through `AccessTools.MethodDelegate` and invoked with a null item. Shutdown clears that delegate and diagnostic state.
- A synthetic foreign transpiler removes the expected loop. The RRM hook rejects it, and targeted cleanup removes only the failed RRM hook while retaining the foreign transpiler and a callable real interaction wrapper.
- Embedded-English tooltip text includes frozen effective levels 0 and 37, omits the freeze note when absent, is idempotent, and remains unchanged when all RRM skill bonuses are disabled.

## Fixture boundaries and remaining validation

The real interaction body is suppressed by a test prefix; no player inventory is opened. `repeat=true` exits PoV's guard before world/Unity object access. `Player` static initialization still needs `Animator.StringToHash_Injected`, so the disposable host registers **one test-only native hash sentinel**. This does not alter input DLL bytes or publicize any member, and it is unrelated to the Harmony call-chain assertion. The sentinel is not a substitute for validating animation, Unity object lifetime, station eligibility, or repairs in the game.

Tooltip tests temporarily suppress `Localization.instance` creation to exercise RRM's actual embedded-English fallback without Unity resource loading. Actual batch repair execution is covered separately by the isolated `tests/GalleonRepairChecks` service tests. The runtime probe does **not** prove in-game Auto Repair behavior, UI layout, active station/recipe matching, material consumption, optional container behavior, skill freezing, or multiplayer operation. Those still require a real client/host/dedicated-server session with PoV installed.

## Checked inputs

On 2026-10-18, all 30 checks passed for both original Valheim 1.0.20 snapshots with the final merged RRM 1.1.2 Release DLL:

| Input | SHA-256 |
| --- | --- |
| Client `assembly_valheim.dll`, build 25909357 | `94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A` |
| Dedicated `assembly_valheim.dll`, build 25909431 | `EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3` |
| PoV plugin 5.0.4 (`AssemblyVersion` 1.0.0.0) | `36E45B65B44667AD596446CAC2AE989041669948C11710B05185E4FE16697DB4` |
| BepInEx `0Harmony.dll` 2.9.0.0 | `1A21CC03424FC82C3DD1346905D16494536B9595AE4162228D99FB7C285C1031` |
| RRM 1.1.2 Release | `D69D79933C603D90CB0C62A5B27E8498FBAF744BF617CAF6D82975F470BECF35` |

The server snapshot root used was `dedicated-server-b25909431-windows-x64-20261015T103728Z-depot-restored/original`. These are isolated contract results, not a claim that a live dedicated server was tested.
