using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

internal static class Program
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static string[] _searchPaths = Array.Empty<string>();
    private static object? _boxedDefinition;
    private static int _checks;

    private static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--embedded-mono")
            return NativeMonoHost.Run(args);
        if (args.Length != 5 || args[0] != "--probe")
        {
            Console.Error.WriteLine("Use Run.ps1; this probe requires explicit original game, BepInEx, Artisan and optional RRM paths.");
            return 2;
        }
        _searchPaths = new[] { args[1], args[2], Path.GetDirectoryName(args[3])!, args[4] == "-" ? "" : Path.GetDirectoryName(args[4])! };
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try { return Check(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in _searchPaths)
        {
            string path = Path.Combine(directory, name);
            if (directory.Length != 0 && File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static int Check(string[] args)
    {
        Require(Type.GetType("Mono.Runtime") != null, "Probe is running on Mono");
        string gamePath = Path.Combine(args[1], "assembly_valheim.dll");
        PrintIdentity(gamePath);
        PrintIdentity(args[3]);
        PrintIdentity(typeof(Harmony).Assembly.Location);
        Require(File.ReadAllBytes(typeof(Harmony).Assembly.Location).SequenceEqual(File.ReadAllBytes(Path.Combine(args[2], "0Harmony.dll"))),
            "Loaded Harmony bytes match the requested BepInEx core");
        Assembly game = Assembly.LoadFrom(gamePath);
        Assembly artisan = Assembly.LoadFrom(args[3]);
        Type inventory = game.GetType("Inventory", true)!;
        Type container = game.GetType("Container", true)!;
        Require(container.GetMethod("Save", Any, null, Type.EmptyTypes, null)!.IsPrivate, "Container.Save is private in original DLL");
        Require(container.GetMethod("Load", Any, null, Type.EmptyTypes, null)!.IsPrivate, "Container.Load is private in original DLL");
        Require(inventory.GetMethod("MoveAll", Any, null, new[] { inventory }, null)!.IsPublic, "Inventory.MoveAll is public");
        Require(inventory.GetMethod("GetAllItems", Any, null, Type.EmptyTypes, null)!.IsPublic, "Inventory.GetAllItems is public");
        Require(inventory.GetField("m_onChanged", Any)!.IsPublic, "Inventory.m_onChanged is public");
        foreach (string name in new[] { "m_name", "m_bkg", "m_width", "m_height" })
            Require(container.GetField(name, Any)!.IsPublic, "Container." + name + " is public");
        Type itemData = game.GetType("ItemDrop+ItemData", true)!;
        Require(itemData.GetMethod("Clone", Any, null, Type.EmptyTypes, null)!.IsPublic, "ItemData.Clone is public");
        Type root = artisan.GetType("Artisan_Mastery.ShipHiddenContainerRoot", true)!;
        Type definition = artisan.GetType("Artisan_Mastery.ShipHiddenContainerSystem+StorageDef", true)!;
        Require(definition.IsValueType && !definition.IsNestedPublic, "StorageDef is a non-public value type");
        MethodInfo cleanup = root.GetMethod("CleanupStorage", Any)!;
        Require(cleanup.IsPrivate && cleanup.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { game.GetType("ZDO", true)!, typeof(string), definition }), "CleanupStorage signature matches the original contract");
        Require(root.GetMethod("UpdateLinkedStoragesPassiveFollow", Any)!.GetParameters().Length == 0, "Passive-follow target has no arguments");

        var harmony = new Harmony("RepairRequiresMaterials.ArtisanStorageChecks." + Guid.NewGuid().ToString("N"));
        try
        {
            // Generate a wrapper for the real private method, but never call its game code.
            MethodInfo capture = typeof(Program).GetMethod(nameof(CaptureDefinition), Any)!;
            harmony.Patch(cleanup, prefix: new HarmonyMethod(capture));
            Require(Harmony.GetPatchInfo(cleanup).Owners.Contains(harmony.Id), "Harmony generates a wrapper for original CleanupStorage with object def");
            harmony.Unpatch(cleanup, HarmonyPatchType.All, harmony.Id);
            CheckBoxing(harmony, definition, capture);

            if (args[4] == "-") Console.WriteLine("SKIP: production RRM checks (no -RepairDll supplied).");
            else CheckProduction(harmony, args[4], artisan, root);
        }
        finally { harmony.UnpatchSelf(); }
        Console.WriteLine("PASS: " + _checks + " contract checks. No Unity world, inventory transfer, ownership or durable save was executed.");
        return 0;
    }

    private static void CheckBoxing(Harmony harmony, Type definition, MethodInfo capture)
    {
        // The sentinel has the exact original value-type argument. Its body only throws;
        // unlike an actual Unity component method, invoking it is safe without Unity.
        AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("StorageDefBoxingSentinel"), AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Sentinel", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { definition });
        method.DefineParameter(1, ParameterAttributes.None, "def");
        method.SetImplementationFlags(MethodImplAttributes.NoInlining | MethodImplAttributes.NoOptimization);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        MethodInfo sentinel = type.CreateType()!.GetMethod("Run")!;
        harmony.Patch(sentinel, prefix: new HarmonyMethod(capture));
        object input = Activator.CreateInstance(definition, Any, null, new object[] { 3, "visual", "ShipContainerHiddenMedium_DO", "link", "name" }, null)!;
        _boxedDefinition = null;
        sentinel.Invoke(null, new[] { input });
        Require(_boxedDefinition != null && _boxedDefinition.GetType() == definition, "Harmony boxes the real StorageDef into object def");
        Require((int)definition.GetField("Slot", Any)!.GetValue(_boxedDefinition)! == 3, "Boxing preserves StorageDef fields");
        harmony.Unpatch(sentinel, HarmonyPatchType.All, harmony.Id);
    }

    private static bool CaptureDefinition(object def)
    {
        _boxedDefinition = def;
        return false;
    }

    private static void CheckProduction(Harmony harmony, string path, Assembly artisan, Type root)
    {
        PrintIdentity(path);
        Assembly repair = Assembly.LoadFrom(path);
        Type compat = repair.GetType("RepairRequiresMaterials.ArtisanStorageCompat", true)!;
        MethodInfo resolve = compat.GetMethod("ResolveContract", Any, null, new[] { typeof(Assembly) }, null)
            ?? throw new MissingMethodException(compat.FullName, "ResolveContract");
        Require(true, "Production ResolveContract hook exists");
        resolve.Invoke(null, new object[] { artisan });
        Require(true, "Production ResolveContract accepts this original Artisan DLL");
        // Forward the real types, but make any version-metadata query fail. This
        // tests version independence without modifying or relabelling input DLLs.
        resolve.Invoke(null, new object[] { new VersionUnreadableAssembly(artisan) });
        Require(true, "Production ResolveContract accepts the original structure without reading version metadata");
        AssemblyBuilder missingContract = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("MissingArtisanContract") { Version = new Version(99, 0, 0, 0) }, AssemblyBuilderAccess.Run);
        bool rejected = false;
        try { resolve.Invoke(null, new object[] { missingContract }); }
        catch (TargetInvocationException error) when (error.InnerException is TypeLoadException) { rejected = true; }
        Require(rejected, "Production ResolveContract rejects missing required types regardless of version");
        // Re-resolve after the negative case so it cannot contaminate later checks.
        resolve.Invoke(null, new object[] { artisan });
        foreach (var pair in new[] {
            new[] { "CleanupStorage", "CleanupPrefix" },
            new[] { "UpdateLinkedStoragesPassiveFollow", "FollowPrefix" },
            new[] { "EnsureStorageBackLink", "BacklinkPrefix" },
            new[] { "InstantiateExistingStorage", "InstantiatePrefix" },
            new[] { "RPC_RequestOpenHiddenContainer", "RequestPrefix" } })
        {
            MethodInfo original = root.GetMethod(pair[0], Any)!;
            MethodInfo prefix = compat.GetMethod(pair[1], Any)!;
            Require(prefix != null, "Production " + pair[1] + " exists");
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            Require(Harmony.GetPatchInfo(original).Owners.Contains(harmony.Id), "Production " + pair[1] + " wrapper generation succeeds");
            harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id);
        }
        MethodInfo bytes = compat.GetMethod("BytesEqual", Any)
            ?? throw new MissingMethodException(compat.FullName, "BytesEqual");
        Require(true, "Production BytesEqual helper exists");
        Require((bool)bytes.Invoke(null, new object?[] { new byte[] { 1, 2 }, new byte[] { 1, 2 } })!, "BytesEqual accepts independent equal byte arrays");
        Require(!(bool)bytes.Invoke(null, new object?[] { new byte[] { 1, 2 }, new byte[] { 1, 3 } })!, "BytesEqual rejects changed bytes");
        Require(!(bool)bytes.Invoke(null, new object?[] { new byte[] { 1, 2 }, new byte[] { 1 } })!, "BytesEqual rejects different lengths");
        Require(!(bool)bytes.Invoke(null, new object?[] { null, new byte[] { 1 } })!, "BytesEqual rejects missing persisted bytes");
        Require((bool)bytes.Invoke(null, new object?[] { null, null })!, "BytesEqual treats two absent byte arrays as equal");
        Require((bool)bytes.Invoke(null, new object?[] { Array.Empty<byte>(), new byte[0] })!, "BytesEqual accepts empty arrays");
        CheckInventoryRoundTrip(compat);
    }

    private static void CheckInventoryRoundTrip(Type compat)
    {
        MethodInfo method = compat.GetMethod("IsSupportedInventoryRoundTrip", Any, null,
            new[] { typeof(byte[]), typeof(byte[]) }, null)
            ?? throw new MissingMethodException(compat.FullName, "IsSupportedInventoryRoundTrip");
        Require(true, "Production inventory round-trip policy exists");
        bool Accept(byte[] original, byte[] serialized) => (bool)method.Invoke(null, new object[] { original, serialized })!;

        InventoryFixture empty = MakeInventoryFixture(109, Array.Empty<byte>());
        Require(Accept(empty.Bytes, ConvertFixture(empty)), "Empty 109 inventory migrates to 110");
        InventoryFixture source = MakeInventoryFixture(109, new byte[] { 1, 0, 1 });
        byte[] expected = ConvertFixture(source);
        byte[] originalSnapshot = (byte[])source.Bytes.Clone();
        byte[] expectedSnapshot = (byte[])expected.Clone();
        Require(Accept(source.Bytes, expected), "Multiple 109 items allow only header and old cheated-flag conversion");
        Require(source.Bytes.SequenceEqual(originalSnapshot) && expected.SequenceEqual(expectedSnapshot),
            "Round-trip policy leaves both source and serialized buffers unchanged");
        MethodInfo bytesEqual = compat.GetMethod("BytesEqual", Any)!;
        bool Same(byte[] first, byte[] second) => (bool)bytesEqual.Invoke(null, new object[] { first, second })!;
        Require(!Same(source.Bytes, expected), "Old source bytes differ from the equivalent current-format live snapshot");
        Require(Same(expected, (byte[])expected.Clone()), "Validated current-format snapshot matches equivalent live serialization");
        byte[] changedLive = (byte[])expected.Clone();
        changedLive[source.FieldOffsets["item 0 stack"]] ^= 1;
        Require(!Same(expected, changedLive), "Validated current-format snapshot still rejects stale or changed live contents");
        Require(Accept(source.Bytes, (byte[])source.Bytes.Clone()), "Unchanged 109 bytes remain accepted");
        InventoryFixture current = MakeInventoryFixture(110, new byte[] { 1, 0, 1 });
        Require(Accept(current.Bytes, (byte[])current.Bytes.Clone()), "Current 110 bytes preserve cheated flags");
        byte[] erased = (byte[])current.Bytes.Clone();
        erased[current.CheatedOffsets[0]] = 0;
        Require(!Accept(current.Bytes, erased), "Current 110 cheated flag cannot be erased");
        byte[] added = (byte[])current.Bytes.Clone();
        added[current.CheatedOffsets[1]] = 1;
        Require(!Accept(current.Bytes, added), "Current 110 cheated flag cannot be added");

        foreach (var field in source.FieldOffsets)
        {
            byte[] changed = (byte[])expected.Clone();
            changed[field.Value] ^= 1;
            Require(!Accept(source.Bytes, changed), "Migration rejects changed " + field.Key);
        }
        byte[] reordered = (byte[])expected.Clone();
        // Equal-length records 0 and 2 have different hashes and item data.
        int firstLength = source.RecordOffsets[1] - source.RecordOffsets[0];
        Array.Copy(expected, source.RecordOffsets[2], reordered, source.RecordOffsets[0], firstLength);
        Array.Copy(expected, source.RecordOffsets[0], reordered, source.RecordOffsets[2], firstLength);
        Require(!Accept(source.Bytes, reordered), "Migration rejects reordered inventory records");
        byte[] stillCheated = (byte[])expected.Clone();
        stillCheated[source.CheatedOffsets[0]] = 1;
        Require(!Accept(source.Bytes, stillCheated), "109 to 110 must follow native clearing of the old cheated flag");
        foreach (byte flags in new byte[] { 2, 128, 255 })
        {
            byte[] unknown = (byte[])source.Bytes.Clone();
            unknown[source.CheatedOffsets[0]] = flags;
            Require(!Accept(unknown, expected), "Migration rejects unknown old cheated bits " + flags);
        }
        foreach (int version in new[] { 108, 111, 999 })
        {
            byte[] unsupported = (byte[])source.Bytes.Clone();
            Array.Copy(BitConverter.GetBytes(version), unsupported, 4);
            Require(!Accept(unsupported, expected), "Migration rejects unsupported source format " + version);
        }
        byte[] future = (byte[])expected.Clone();
        Array.Copy(BitConverter.GetBytes(111), future, 4);
        Require(!Accept(source.Bytes, future), "Migration rejects unknown destination format");
        Require(!Accept(expected, source.Bytes), "Migration never downgrades 110 to 109");
        Require(!Accept(source.Bytes, expected.Take(expected.Length - 1).ToArray()), "Migration rejects different payload lengths");
        bool truncationsRejected = true;
        for (int length = 1; length < source.Bytes.Length; ++length)
            truncationsRejected &= !Accept(source.Bytes.Take(length).ToArray(), expected.Take(length).ToArray());
        Require(truncationsRejected, "Every nonempty truncated fixture prefix fails closed without escaping an exception");
        Require(!Accept(source.Bytes.Concat(new byte[] { 0 }).ToArray(), expected.Concat(new byte[] { 0 }).ToArray()),
            "Migration rejects unconsumed trailing bytes");
        byte[] badCountSource = (byte[])source.Bytes.Clone();
        byte[] badCountExpected = (byte[])expected.Clone();
        badCountSource[4] = badCountExpected[4] = 4;
        Require(!Accept(badCountSource, badCountExpected), "Migration rejects a count larger than available item records");
        badCountSource[4] = badCountExpected[4] = 2;
        Require(!Accept(badCountSource, badCountExpected), "Migration rejects a count smaller than available item records");
        InventoryFixture largeCustom = MakeInventoryFixture(109, new byte[] { 1 }, customCount: 128);
        Require(Accept(largeCustom.Bytes, ConvertFixture(largeCustom)), "Migration parses two-byte custom-data counts and UTF-8 strings");

        // Exercise the game's actual managed parser as well as the production policy.
        // No prefabs, ItemData.Save, Inventory.Load or Unity objects are required.
        Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "assembly_valheim");
        Type itemVersion = game.GetType("Version+Item", true)!;
        if (Enum.IsDefined(itemVersion, 110))
        {
            Type package = game.GetType("ZPackage", true)!;
            Type item = game.GetType("ItemDrop+ItemData", true)!;
            MethodInfo load = item.GetMethod("Load", Any, null, new[] { package, itemVersion }, null)!;
            bool ReadCheated(InventoryFixture fixture, int version)
            {
                object input = Activator.CreateInstance(package, new object[] { fixture.Bytes })!;
                package.GetMethod("SetPos", Any)!.Invoke(input, new object[] { 6 });
                object result = load.Invoke(null, new[] { input, Enum.ToObject(itemVersion, version) })!;
                object data = result.GetType().GetField("Item2")!.GetValue(result)!;
                return (bool)item.GetField("m_cheated", Any)!.GetValue(data)!;
            }
            Require(!ReadCheated(source, 109), "Original game parser clears cheated for item format 109");
            Require(ReadCheated(current, 110), "Original game parser preserves cheated for item format 110");
        }
        else Console.WriteLine("SKIP: native 110 cheated semantics (this original game predates item format 110).");
    }

    private sealed class InventoryFixture
    {
        internal byte[] Bytes = Array.Empty<byte>();
        internal readonly List<int> RecordOffsets = new();
        internal readonly List<int> CheatedOffsets = new();
        internal readonly Dictionary<string, int> FieldOffsets = new();
    }

    private static InventoryFixture MakeInventoryFixture(int version, byte[] cheated, int customCount = 2)
    {
        var fixture = new InventoryFixture();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(version);
        writer.Write((ushort)cheated.Length);
        for (int i = 0; i < cheated.Length; ++i)
        {
            fixture.RecordOffsets.Add((int)stream.Position);
            void Mark(string field) => fixture.FieldOffsets.Add("item " + i + " " + field, (int)stream.Position);
            bool optional = i % 2 == 0;
            Mark("durability"); writer.Write(12345 + i);
            Mark("grid x"); writer.Write((byte)i);
            Mark("grid y"); writer.Write((byte)2);
            Mark("world level"); writer.Write((byte)1);
            Mark("item flags"); writer.Write(optional ? (byte)255 : (byte)64);
            if (optional)
            {
                Mark("quality"); writer.Write((ushort)(3 + i));
                Mark("stack"); writer.Write((ushort)(17 + i));
                Mark("variant"); writer.Write(2 + i);
                Mark("crafter ID"); writer.Write(1234567890123L + i);
                Mark("crafter name"); writer.Write("장인-" + i);
            }
            Mark("prefab hash"); writer.Write(unchecked((int)0x89ABCDEF) + i);
            if (optional)
            {
                if (customCount < 128) writer.Write((byte)customCount);
                else { writer.Write((byte)((customCount >> 8) | 128)); writer.Write((byte)customCount); }
                for (int j = 0; j < customCount; ++j)
                {
                    if (j == 0) Mark("custom-data key");
                    writer.Write("socket-key-" + j);
                    if (j == 0) Mark("custom-data value");
                    writer.Write("보석-value-" + i + "-" + j);
                }
            }
            fixture.CheatedOffsets.Add((int)stream.Position);
            writer.Write(cheated[i]);
        }
        writer.Flush();
        fixture.Bytes = stream.ToArray();
        return fixture;
    }

    private static byte[] ConvertFixture(InventoryFixture fixture)
    {
        byte[] result = (byte[])fixture.Bytes.Clone();
        Array.Copy(BitConverter.GetBytes(110), result, 4);
        foreach (int offset in fixture.CheatedOffsets) result[offset] = 0;
        return result;
    }

    private sealed class VersionUnreadableAssembly : Assembly
    {
        private readonly Assembly _original;

        internal VersionUnreadableAssembly(Assembly original) => _original = original;

        public override Type GetType(string name, bool throwOnError) => _original.GetType(name, throwOnError)!;

        public override AssemblyName GetName() => throw new InvalidOperationException("Version metadata must not be queried.");

        public override AssemblyName GetName(bool copiedName) => throw new InvalidOperationException("Version metadata must not be queried.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
        Console.WriteLine("OK: " + label);
    }

    private static void PrintIdentity(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(path))
            Console.WriteLine("INPUT: " + Path.GetFullPath(path) + " | SHA256=" + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""));
    }
}
