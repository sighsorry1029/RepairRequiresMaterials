using System;
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
        AssemblyBuilder unsupported = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("UnsupportedArtisanContract") { Version = new Version(99, 0, 0, 0) }, AssemblyBuilderAccess.Run);
        bool rejected = false;
        try { resolve.Invoke(null, new object[] { unsupported }); }
        catch (TargetInvocationException error) when (error.InnerException is NotSupportedException) { rejected = true; }
        Require(rejected, "Production ResolveContract rejects an unreviewed version");
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
