using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace RepairRequiresMaterials;

// Separate from the anvil UI: these hooks run on whichever peer owns the ship.
// No compile-time Artisan reference, global Container patch, or automatic recovery.
internal static class ArtisanStorageCompat
{
    private const string PendingKey = "rrm_galleon_recovery_pending";
    private const string BackupKey = "rrm_galleon_recovery_original";
    private static MethodInfo _cleanup = null!, _follow = null!, _backlink = null!, _instantiate = null!, _request = null!;
    private static MethodInfo _findStorage = null!;
    private static FieldInfo _slot = null!, _link = null!, _prefab = null!, _visual = null!;
    private static Action<Container> _save = null!;
    private static readonly HashSet<ZDOID> Recovering = new();
    private static readonly HashSet<ZDOID> OwnershipWarnings = new();

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(ArtisanMasteryCompat.PluginGuid, out var plugin)
            || plugin.Instance == null) return;
        var installed = new List<(MethodInfo Target, MethodInfo Patch)>();
        try
        {
            ResolveContract(plugin.Instance.GetType().Assembly);
            _save = AccessTools.MethodDelegate<Action<Container>>(
                AccessTools.DeclaredMethod(typeof(Container), "Save", Type.EmptyTypes));
            Install(_backlink, nameof(BacklinkPrefix));
            Install(_instantiate, nameof(InstantiatePrefix));
            Install(_follow, nameof(FollowPrefix));
            Install(_request, nameof(RequestPrefix));
            Install(_cleanup, nameof(CleanupPrefix));
            RepairRequiresMaterialsPlugin.Log.LogInfo("Artisan Mastery hidden galleon storage protection enabled.");
        }
        catch (Exception exception)
        {
            foreach (var patch in installed) harmony.Unpatch(patch.Target, patch.Patch);
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Artisan storage protection was not enabled: {exception.GetBaseException().Message}");
        }

        void Install(MethodInfo target, string name)
        {
            MethodInfo patch = AccessTools.DeclaredMethod(typeof(ArtisanStorageCompat), name);
            installed.Add((target, patch));
            harmony.Patch(target, prefix: new HarmonyMethod(patch) { priority = Priority.First });
        }
    }

    // Kept independently callable for checks against both original optional-mod DLLs.
    internal static void ResolveContract(Assembly assembly)
    {
        System.Version? version = assembly.GetName().Version;
        if (version != new System.Version(1, 0, 5, 0) && version != new System.Version(1, 0, 6, 0))
            throw new NotSupportedException($"Unreviewed Artisan version {version}");
        Type root = assembly.GetType("Artisan_Mastery.ShipHiddenContainerRoot", true)!;
        Type def = assembly.GetType("Artisan_Mastery.ShipHiddenContainerSystem+StorageDef", true)!;
        if (!typeof(Component).IsAssignableFrom(root) || !def.IsValueType)
            throw new NotSupportedException("Unrecognized hidden storage types");
        _cleanup = Method(root, "CleanupStorage", typeof(void), typeof(ZDO), typeof(string), def);
        _follow = Method(root, "UpdateLinkedStoragesPassiveFollow", typeof(void));
        _backlink = Method(root, "EnsureStorageBackLink", typeof(void), def, typeof(ZNetView), typeof(string));
        _instantiate = Method(root, "InstantiateExistingStorage", typeof(GameObject), def, typeof(ZDO), typeof(Transform), typeof(bool));
        _request = Method(root, "RPC_RequestOpenHiddenContainer", typeof(void), typeof(long), typeof(int), typeof(long));
        _findStorage = Method(root, "FindStorageZDOByStableLink", typeof(ZDO), typeof(string), typeof(int));
        _slot = Field(def, "Slot", typeof(int));
        _link = Field(def, "LinkKey", typeof(string));
        _prefab = Field(def, "HiddenPrefabName", typeof(string));
        _visual = Field(def, "VisualPath", typeof(string));
        Field(root, "_shipNView", typeof(ZNetView));
        Field(root, "_nextPassiveFollowTime", typeof(float));
        Field(root, "_defs", def.MakeArrayType());
        Method(typeof(Container), "Save", typeof(void));
    }

    private static MethodInfo Method(Type type, string name, Type result, params Type[] args)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, args);
        if (method == null || method.IsStatic || method.ReturnType != result)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static FieldInfo Field(Type type, string name, Type valueType)
    {
        FieldInfo? field = AccessTools.DeclaredField(type, name);
        if (field == null || field.IsStatic || field.FieldType != valueType)
            throw new MissingFieldException(type.FullName, name);
        return field;
    }

    private static bool IsHiddenStorage(ZDO zdo)
    {
        int hash = zdo.GetPrefab();
        return hash == "ShipContainerHiddenLarge_DO".GetStableHashCode()
            || hash == "ShipContainerHiddenMedium_DO".GetStableHashCode()
            || hash == "ShipContainerHiddenSmall_DO".GetStableHashCode();
    }

    private static Container? LoadedContainer(ZDO zdo)
    {
        GameObject instance = ZNetScene.instance.FindInstance(zdo.m_uid);
        return instance != null ? instance.GetComponent<Container>() : null;
    }

    private static bool IsBusy(ZDO zdo)
    {
        Container? container = LoadedContainer(zdo);
        return zdo.GetInt(ZDOVars.s_inUse) != 0 || (container != null && container.IsInUse());
    }

    private static bool ProtectStorage(ZDO zdo) =>
        IsHiddenStorage(zdo) && (zdo.GetBool(PendingKey) || (!zdo.IsOwner() && IsBusy(zdo)));

    private static bool BacklinkPrefix(ZNetView storageNView)
    {
        if (storageNView == null || !storageNView.IsValid()) return true;
        return !ProtectStorage(storageNView.GetZDO());
    }

    private static void InstantiatePrefix(ZDO storageZdo, ref bool writeZdoPosition)
    {
        if (storageZdo != null && ProtectStorage(storageZdo)) writeZdoPosition = false;
    }

    private static bool FollowPrefix(Component __instance, ZNetView ____shipNView,
        ref float ____nextPassiveFollowTime, Array ____defs)
    {
        if (____shipNView == null || !____shipNView.IsValid() || !____shipNView.IsOwner()
            || Time.time < ____nextPassiveFollowTime) return false;
        ____nextPassiveFollowTime = Time.time + 1f;
        ZDO ship = ____shipNView.GetZDO();
        // Preserve Artisan's one-second/6m following policy, but never seize an open container.
        foreach (object def in ____defs)
        {
            ZDO? storage = ZDOMan.instance.GetZDO(ship.GetZDOID((string)_link.GetValue(def)));
            if (storage == null || !IsHiddenStorage(storage)) continue;
            if (!HasExpectedLink(storage, ship, ship.GetString("galleon_container_ship_stable_id"), def)) continue;
            Transform anchor = __instance.transform.Find((string)_visual.GetValue(def));
            if (anchor == null) continue;
            Vector3 position = anchor.position + Vector3.up * 0.15f;
            if (Vector3.Distance(storage.GetPosition(), position) < 6f) continue;
            if (ProtectStorage(storage))
            {
                if (OwnershipWarnings.Add(storage.m_uid))
                    Warn(storage, "skipped passive following: in use by another peer or recovery pending");
                continue;
            }
            if (!storage.IsOwner()) storage.SetOwner(ZDOMan.GetSessionID());
            storage.SetPosition(position);
        }
        return false;
    }

    // An interrupted transfer is quarantined, not replayed or exposed as a second copy.
    private static bool RequestPrefix(Component __instance, long requesterPeerId, int slot,
        ZNetView ____shipNView, Array ____defs)
    {
        if (____shipNView == null || !____shipNView.IsValid() || !____shipNView.IsOwner()) return true;
        ZDO ship = ____shipNView.GetZDO();
        foreach (object def in ____defs)
        {
            if ((int)_slot.GetValue(def) != slot) continue;
            ZDO? storage = ZDOMan.instance.GetZDO(ship.GetZDOID((string)_link.GetValue(def)));
            if (storage == null || !storage.GetBool(PendingKey))
            {
                string stableId = ship.GetString("galleon_container_ship_stable_id");
                storage = string.IsNullOrEmpty(stableId) ? null
                    : _findStorage.Invoke(__instance, new object[] { stableId, slot }) as ZDO;
            }
            if (storage == null || !storage.GetBool(PendingKey)) return true;
            Warn(storage, "open refused: interrupted recovery requires administrator inspection");
            ____shipNView.InvokeRPC(requesterPeerId, "HH_OpenHiddenContainerResponse", slot, ZDOID.None, false);
            return false;
        }
        return true;
    }

    private static bool CleanupPrefix(Component __instance, ZDO shipZdo, string stableShipId, object def)
    {
        int slot = (int)_slot.GetValue(def);
        string link = (string)_link.GetValue(def);
        try
        {
            if (!shipZdo.IsOwner()) return false;
            var candidates = new HashSet<ZDOID>();
            ZDOID linked = shipZdo.GetZDOID(link);
            if (!linked.IsNone()) candidates.Add(linked);
            if (!string.IsNullOrEmpty(stableShipId)
                && _findStorage.Invoke(__instance, new object[] { stableShipId, slot }) is ZDO recovered)
                candidates.Add(recovered.m_uid);
            bool complete = true;
            foreach (ZDOID id in candidates)
            {
                ZDO? storage = ZDOMan.instance.GetZDO(id);
                if (storage == null)
                {
                    complete = false;
                    RepairRequiresMaterialsPlugin.Log.LogWarning($"Galleon slot={slot}, storage={id}: missing storage; link retained.");
                    continue;
                }
                if (!HasExpectedLink(storage, shipZdo, stableShipId, def))
                {
                    Warn(storage, "unexpected prefab/backlink; data preserved");
                    complete = false;
                    continue;
                }
                complete &= RecoverStorage(storage, __instance.transform.position);
            }
            if (complete) shipZdo.RemoveZDOID(link);
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Galleon slot={slot}, ship={shipZdo.m_uid}: cleanup stopped, link retained: {exception.GetBaseException().Message}");
        }
        // Never fall back to Artisan's unconditional deletion after a failure.
        return false;
    }

    private static bool HasExpectedLink(ZDO storage, ZDO ship, string stableId, object def) =>
        IsHiddenStorage(storage) && storage.GetPrefab() == ((string)_prefab.GetValue(def)).GetStableHashCode()
        && !string.IsNullOrEmpty(stableId)
        && storage.GetString("galleon_parent_ship_stable_id") == stableId
        && storage.GetInt("galleon_container_slot", -1) == (int)_slot.GetValue(def)
        && storage.GetZDOID("galleon_parent_ship") == ship.m_uid;

    private static bool RecoverStorage(ZDO source, Vector3 position)
    {
        if (!Recovering.Add(source.m_uid)) return false;
        var crates = new List<Container>();
        byte[]? original = null;
        bool started = false, committed = false;
        try
        {
            if (source.GetBool(PendingKey)) throw new InvalidOperationException("interrupted recovery; not replaying it");
            if (!source.IsOwner() || IsBusy(source))
                throw new InvalidOperationException("storage is in use or owned by another peer");
            Container? live = LoadedContainer(source);
            Container template = ZNetScene.instance.GetPrefab(source.GetPrefab()).GetComponent<Container>();
            if (template == null) throw new InvalidOperationException("hidden container prefab unavailable");
            original = source.GetByteArray(ZDOVars.s_items)?.Clone() as byte[];
            var stagedSource = new Inventory(template.m_name, template.m_bkg, template.m_width, template.m_height);
            if (original != null)
            {
                stagedSource.Load(new ZPackage(original));
                // Load can silently skip unknown prefabs/clamp stacks. Refuse lossy decoding.
                if (!BytesEqual(original, Serialize(stagedSource)))
                    throw new InvalidOperationException("saved inventory did not round-trip exactly (missing/changed item or data format)");
            }
            if (live != null && live.GetInventory().NrOfItems() > 0
                && !BytesEqual(original, Serialize(live.GetInventory())))
                throw new InvalidOperationException("live inventory differs from saved inventory");
            if (stagedSource.NrOfItems() == 0)
            {
                ZDOMan.instance.DestroyZDO(source);
                return true;
            }
            GameObject cargo = ZNetScene.instance.GetPrefab("CargoCrate_DO");
            Container cargoTemplate = cargo != null ? cargo.GetComponent<Container>() : null!;
            if (cargo == null || cargoTemplate == null || cargoTemplate.m_width <= 0 || cargoTemplate.m_height <= 0)
                throw new InvalidOperationException("CargoCrate_DO container unavailable");
            List<byte[]> payloads = StageCrates(stagedSource, cargoTemplate);
            // Keep an independently saved source snapshot even if clearing/restoring s_items fails.
            // This is a recovery aid, not an atomic transaction across multiple network ZDOs.
            source.Set(BackupKey, original!);
            if (!BytesEqual(original, source.GetByteArray(BackupKey)))
                throw new InvalidOperationException("source backup readback mismatch");
            source.Set(PendingKey, true);
            started = true;
            foreach (byte[] payload in payloads)
            {
                GameObject obj = UnityEngine.Object.Instantiate(cargo, position + Vector3.up * 0.5f, Quaternion.identity);
                Container crate = obj.GetComponent<Container>();
                if (crate == null) throw new InvalidOperationException("spawned cargo crate has no container");
                crates.Add(crate);
                ZNetView view = crate.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || !view.IsOwner() || crate.IsInUse()
                    || crate.GetInventory() == null || crate.GetInventory().NrOfItems() != 0)
                    throw new InvalidOperationException("cargo crate is not empty and locally owned");
                WriteInventory(crate, payload);
            }
            if (!source.IsOwner() || IsBusy(source) || !BytesEqual(original, source.GetByteArray(ZDOVars.s_items)))
                throw new InvalidOperationException("source changed during recovery");
            for (int i = 0; i < crates.Count; ++i)
            {
                Container crate = crates[i];
                ZNetView view = crate.GetComponent<ZNetView>();
                RequireWritable(crate);
                if (!BytesEqual(payloads[i], view.GetZDO().GetByteArray(ZDOVars.s_items)))
                    throw new InvalidOperationException("cargo contents changed during recovery");
            }
            stagedSource.GetAllItems().Clear();
            byte[] empty = Serialize(stagedSource);
            if (live != null) WriteInventory(live, empty);
            else source.Set(ZDOVars.s_items, empty);
            if (!BytesEqual(source.GetByteArray(ZDOVars.s_items), empty))
                throw new InvalidOperationException("source clear could not be verified");
            committed = true;
            // Persisted empty source makes a repeated callback harmless even if deletion fails.
            ZDOMan.instance.DestroyZDO(source);
            RepairRequiresMaterialsPlugin.Log.LogInfo($"Galleon storage={source.m_uid}: recovered into {crates.Count} cargo crate(s).");
            return true;
        }
        catch (Exception exception)
        {
            if (started && !committed)
            {
                // Clear every staged copy before restoring the source. If any step cannot be
                // verified, retain the quarantine marker and never retry automatically.
                try
                {
                    foreach (Container crate in crates)
                    {
                        ZNetView view = crate.GetComponent<ZNetView>();
                        if (view == null || !view.IsValid() || !view.IsOwner() || crate.IsInUse())
                            throw new InvalidOperationException("rollback cargo is unavailable/in use");
                        var empty = new Inventory("", null, 1, 1);
                        WriteInventory(crate, Serialize(empty));
                    }
                    if (!source.IsOwner() || IsBusy(source)) throw new InvalidOperationException("rollback source ownership changed");
                    Container? live = LoadedContainer(source);
                    if (live != null && original != null) WriteInventory(live, original);
                    else if (original != null) source.Set(ZDOVars.s_items, original);
                    if (!BytesEqual(original, source.GetByteArray(ZDOVars.s_items)))
                        throw new InvalidOperationException("rollback source verification failed");
                    foreach (Container crate in crates) crate.GetComponent<ZNetView>().Destroy();
                    source.Set(PendingKey, false);
                    source.RemoveByteArray(BackupKey.GetStableHashCode());
                }
                catch (Exception rollback)
                {
                    Warn(source, $"recovery quarantined: {rollback.GetBaseException().Message}");
                }
            }
            Warn(source, $"{(committed ? "contents recovered; source deletion failed" : "data preserved/quarantined")}: {exception.GetBaseException().Message}");
            return committed;
        }
        finally { Recovering.Remove(source.m_uid); }
    }

    private static List<byte[]> StageCrates(Inventory source, Container template)
    {
        int capacity = checked(template.m_width * template.m_height);
        var result = new List<byte[]>();
        Inventory? crate = null;
        int index = 0;
        foreach (ItemDrop.ItemData item in source.GetAllItems())
        {
            if (index % capacity == 0)
            {
                if (crate != null) result.Add(Serialize(crate));
                if (result.Count >= 64) throw new InvalidOperationException("too many cargo crates required");
                crate = new Inventory(template.m_name, template.m_bkg, template.m_width, template.m_height);
            }
            // Do not merge stacks: variant/crafter/custom data belong to individual items.
            ItemDrop.ItemData copy = item.Clone();
            int slot = index++ % capacity;
            copy.m_gridPos = new Vector2i(slot % template.m_width, slot / template.m_width);
            crate!.GetAllItems().Add(copy);
        }
        if (crate != null) result.Add(Serialize(crate));
        return result;
    }

    private static void WriteInventory(Container container, byte[] payload)
    {
        RequireWritable(container);
        Inventory inventory = container.GetInventory();
        Action callbacks = inventory.m_onChanged;
        inventory.m_onChanged = null;
        try
        {
            inventory.Load(new ZPackage(payload));
            if (!BytesEqual(payload, Serialize(inventory))) throw new InvalidOperationException("cargo/source inventory lost data on load");
            RequireWritable(container);
            _save(container);
            if (!BytesEqual(payload, container.GetComponent<ZNetView>().GetZDO().GetByteArray(ZDOVars.s_items)))
                throw new InvalidOperationException("inventory save readback mismatch");
        }
        finally { inventory.m_onChanged = callbacks; }
    }

    private static void RequireWritable(Container container)
    {
        ZNetView view = container.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner() || IsBusy(view.GetZDO()))
            throw new InvalidOperationException("container is unavailable, in use, or owned by another peer");
    }

    private static byte[] Serialize(Inventory inventory)
    {
        var package = new ZPackage();
        inventory.Save(package);
        return package.GetArray();
    }

    private static bool BytesEqual(byte[]? first, byte[]? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first == null || second == null || first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; ++i) if (first[i] != second[i]) return false;
        return true;
    }

    private static void Warn(ZDO storage, string message) => RepairRequiresMaterialsPlugin.Log.LogWarning(
        $"Galleon slot={storage.GetInt("galleon_container_slot", -1)}, storage={storage.m_uid}, owner={storage.GetOwner()}: {message}");

    internal static void Shutdown()
    {
        Recovering.Clear();
        OwnershipWarnings.Clear();
    }
}
