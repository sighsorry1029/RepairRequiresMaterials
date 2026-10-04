# Artisan storage original-DLL contract checks

This standalone net48 tool builds only itself. It does not build/deploy the mod,
publicize or modify input DLLs, initialize a Unity scene, or load/save a world.
Each Artisan DLL runs in a fresh child using the supplied game's original embedded
Mono, so tests of 1.0.5 and 1.0.6 cannot reuse a same-named loaded assembly.

```powershell
.\tools\ArtisanStorageChecks\Run.ps1 `
  -OriginalGame 'C:\path\to\original-game-snapshot\original' `
  -ArtisanDll @('C:\path\to\1.0.5\Artisan_Mastery.dll', 'C:\path\to\1.0.6\Artisan_Mastery.dll') `
  -RepairDll '.\bin\Debug\RepairRequiresMaterials.dll'
```

`-BepInExCore` selects the real BepInEx core directory; its default is the local
Valheim installation. Input hashes and test results are printed and retained in
a newly created temporary report directory. `-SkipBuild` reuses the harness.
Omitting `-RepairDll` explicitly skips production RRM checks while still checking
the original API and Harmony boxing contract.

Checks cover original access modifiers, the private Artisan cleanup signature,
Harmony wrapper generation against the real target, actual boxing of the original
internal `StorageDef` into an `object def` prefix, production `ResolveContract`,
all five production prefix wrappers, rejection of an unsupported version, and
`BytesEqual` cases. Public/private access checks include the newly used inventory
callback and container template fields.
The boxing call uses a safe managed sentinel with the exact original argument
type; original Artisan/Unity component methods are never invoked. Production
prefixes are patched only to test wrapper generation, not executed.

Mono may print an unresolved `UnityEngine.Time::get_time` internal-call warning
while JIT-compiling a wrapper. This host deliberately does not register Unity's
native engine calls. Such a warning is a harness limitation, not evidence that
the supplied game installation is broken; it also means no native gameplay
compatibility claim follows from a passing wrapper-generation check.

This is **not** an inventory-transfer or persistence test. It cannot establish
in-game initialization, concurrent ownership behavior, crate spawning, failure
rollback, death reentrancy, remote replication, or disk-save durability. Those need
separate fault-injection policy tests and actual client/dedicated-server testing.

## In-game checklist (isolated world copy only)

Use the same RRM build on the dedicated server and every client. Back up the
world and conduct destruction/failure cases only in a disposable copy, never the
live world. Record each source storage ZDO ID and compare item quantities **and**
quality, variant, crafter and custom data across the source and all cargo crates.

- With two peers, open an extra galleon container on one peer while the other
  owns/moves the ship more than 6 m. Check ownership is not seized, then close,
  reconnect and reopen; verify unchanged contents.
- Fill multiple extra containers, including distinct custom-data stacks. After
  scene reload (also covering hidden storage without a loaded scene instance),
  destroy the ship. Check combined source/crate contents equal the pre-destruction
  inventory and that repeated cleanup/reload does not produce another copy.
- In a controlled failure setup, make `CargoCrate_DO` unavailable or trigger an
  inventory round-trip rejection. Verify the log identifies the preserved source
  and that its stored bytes remain unchanged; do not automatically replay a
  quarantined recovery.
- Destroy with storage still in use or owned by the other peer. Verify the source
  is retained and no force-claim or duplicate cargo occurs.
- Separately inject a destination/source save failure, verify rollback restores
  total contents, and verify an unsuccessful rollback leaves the recovery marker
  and original backup for administrator inspection. Process/crash/network-loss
  durability is a separate check: local ZDO readback is not a disk/server ACK.
