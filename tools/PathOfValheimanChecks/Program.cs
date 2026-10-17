using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using HarmonyLib;

internal static class Program
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static string[] _searchPaths = Array.Empty<string>();
    private static int _checks;
    private static int _postfixCalls;
    private static MethodInfo? _haveRepairableItems;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AnimatorHashStub(IntPtr span);
    private static readonly AnimatorHashStub HashStub = _ => 1;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);

    private static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--embedded-mono") return NativeMonoHost.Run(args);
        if (args.Length != 5 || args[0] != "--probe")
        {
            Console.Error.WriteLine("Use Run.ps1 with original game and PathOfValheiman DLL paths.");
            return 2;
        }
        _searchPaths = new[] { args[1], args[2], Path.GetDirectoryName(args[3])!, args[4] == "-" ? "" : Path.GetDirectoryName(args[4])! };
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try { return Check(args); }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in _searchPaths)
        {
            string path = Path.Combine(directory, file);
            if (directory.Length != 0 && File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static int Check(string[] args)
    {
        Require(Type.GetType("Mono.Runtime") != null, "Running on original Mono");
        // Isolated Mono has no Unity engine. Player's static fields need Animator hashes
        // even though the interaction body is skipped. This test-only native stub does
        // not change input assemblies or participate in the patch-of-patch assertion.
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(HashStub));
        Console.WriteLine("ISOLATED FIXTURE: Unity Animator.StringToHash internal call returns a fixed sentinel hash.");
        string gamePath = Path.Combine(args[1], "assembly_valheim.dll");
        PrintIdentity(gamePath);
        PrintIdentity(args[3]);
        PrintIdentity(typeof(Harmony).Assembly.Location);
        Require(File.ReadAllBytes(typeof(Harmony).Assembly.Location).SequenceEqual(File.ReadAllBytes(Path.Combine(args[2], "0Harmony.dll"))), "Harmony bytes match requested BepInEx core");
        Assembly game = Assembly.LoadFrom(gamePath);
        Assembly pov = Assembly.LoadFrom(args[3]);
        Type station = game.GetType("CraftingStation", true)!;
        Type humanoid = game.GetType("Humanoid", true)!;
        Type gui = game.GetType("InventoryGui", true)!;
        Type item = game.GetType("ItemDrop+ItemData", true)!;
        Type skillType = game.GetType("Skills+SkillType", true)!;
        MethodInfo interaction = station.GetMethod("Interact", Any, null, new[] { humanoid, typeof(bool), typeof(bool) }, null)!;
        MethodInfo postfix = pov.GetType("PassiveTree.CraftSmith+Station_Interact", true)!.GetMethod("Postfix", Any)!;
        Require(interaction != null && interaction.IsPublic && interaction.ReturnType == typeof(bool), "Original CraftingStation.Interact public signature matches");
        Require(postfix.IsPrivate && postfix.IsStatic && postfix.ReturnType == typeof(void) && postfix.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { station, humanoid, typeof(bool) }), "PoV private Auto Repair postfix signature matches");
        _haveRepairableItems = gui.GetMethod("HaveRepairableItems", Any, null, Type.EmptyTypes, null)!;
        var instructions = PatchProcessor.GetOriginalInstructions(postfix);
        Require(instructions.Count(i => i.Calls(_haveRepairableItems)) == 1, "PoV Auto Repair has exactly one HaveRepairableItems call");
        Require(instructions.Count(i => i.Calls(gui.GetMethod("RepairOneItem", Any, null, Type.EmptyTypes, null)!)) == 1, "PoV Auto Repair has exactly one RepairOneItem call");
        var replaced = ReplaceAvailability(instructions).ToList();
        Require(replaced.Count == instructions.Count && replaced.Count(i => i.Calls(_haveRepairableItems)) == 0 && replaced.Count(i => i.Calls(typeof(Program).GetMethod(nameof(BatchRepair), Any)!)) == 1, "Availability-call replacement preserves instruction count and only substitutes one call");
        MethodInfo highStation = pov.GetType("PassiveTree.RepairHighStation", true)!.GetMethod("Postfix", Any)!;
        Require(highStation.IsPrivate && highStation.IsStatic && highStation.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { item, typeof(bool).MakeByRefType() }), "PoV special-station postfix signature matches");
        Type plugin = pov.GetType("PassiveTree.Plugin", true)!;
        FieldInfo disableSkills = plugin.GetField("DisableVanillaSkills", Any)!;
        Require(disableSkills.IsPublic && disableSkills.IsStatic && disableSkills.FieldType.IsGenericType && disableSkills.FieldType.GetGenericTypeDefinition().FullName == "BepInEx.Configuration.ConfigEntry`1" && disableSkills.FieldType.GetGenericArguments().Single() == typeof(bool), "PoV skill-freeze setting is public static ConfigEntry<bool>");
        Require(game.GetType("Character", true)!.GetMethod("GetSkillFactor", Any, null, new[] { skillType }, null)!.IsPublic, "Character.GetSkillFactor is a public original game API");
        MethodInfo factor = game.GetType("Skills", true)!.GetMethod("GetSkillFactor", Any, null, new[] { skillType }, null)!;
        MethodInfo level = game.GetType("Skills", true)!.GetMethod("GetSkillLevel", Any, null, new[] { skillType }, null)!;
        Require(PatchProcessor.GetOriginalInstructions(factor).Any(i => i.Calls(level)), "Original skill factor derives from GetSkillLevel, honoring PoV effective-level patch");

        var foreignHarmony = new Harmony("RRM.PathOfValheimanChecks.External." + Guid.NewGuid().ToString("N"));
        var bridgeHarmony = new Harmony("RRM.PathOfValheimanChecks.Bridge." + Guid.NewGuid().ToString("N"));
        try
        {
            foreignHarmony.Patch(interaction, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SkipGameInteraction), Any)), postfix: new HarmonyMethod(postfix));
            object instance = FormatterServices.GetUninitializedObject(station);
            // repeat=true is the first PoV guard and returns before any Unity-native access.
            Require((bool)interaction!.Invoke(instance, new object?[] { null, true, false })!, "Real Interact Harmony wrapper is primed with original PoV postfix and game body skipped");
            bridgeHarmony.Patch(postfix, transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(ReplacePostfixWithSentinel), Any)));
            _postfixCalls = 0;
            interaction.Invoke(instance, new object?[] { null, true, false });
            Require(_postfixCalls == 1, "Already-generated and JIT-primed Interact wrapper reaches subsequently transpiled external postfix");
            bridgeHarmony.Unpatch(postfix, HarmonyPatchType.All, bridgeHarmony.Id);
            interaction.Invoke(instance, new object?[] { null, true, false });
            Require(_postfixCalls == 1, "Unpatching bridge restores external postfix while keeping foreign patch installed");
            bridgeHarmony.Patch(postfix, transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(ReplaceAvailability), Any)));
            Require(Harmony.GetPatchInfo(postfix).Owners.Contains(bridgeHarmony.Id), "Real PoV postfix accepts targeted availability-call replacement under original Mono");
            interaction.Invoke(instance, new object?[] { null, true, false });
            Require(_postfixCalls == 1, "Targeted replacement retains repeat guard and does not run batch for repeated interaction");
            bridgeHarmony.Unpatch(postfix, HarmonyPatchType.All, bridgeHarmony.Id);
            if (args[4] != "-") CheckProduction(bridgeHarmony, interaction, instance, postfix, highStation, args[4]);
        }
        finally { bridgeHarmony.UnpatchSelf(); foreignHarmony.UnpatchSelf(); }
        Console.WriteLine("PASS: " + _checks + " original-Mono contract checks. No Unity world, live plugin initialization or repair transaction was executed.");
        return 0;
    }

    private static void CheckProduction(Harmony harmony, MethodInfo interaction, object station, MethodInfo postfix, MethodInfo highStation, string path)
    {
        PrintIdentity(path);
        Assembly repair = Assembly.LoadFrom(path);
        Type compat = repair.GetType("RepairRequiresMaterials.PathOfValheimanCompat", true)!;
        MethodInfo transpiler = compat.GetMethod("ReplaceAutoRepairLoop", Any)!;
        MethodInfo batch = compat.GetMethod("RepairAffordableItems", Any)!;
        var input = PatchProcessor.GetOriginalInstructions(postfix);
        var rewritten = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { input })!).ToList();
        Require(rewritten.Count(i => i.Calls(_haveRepairableItems!)) == 0 && rewritten.Count(i => i.Calls(batch)) == 1, "Production transpiler substitutes the exact PoV availability call");
        bool rejectsUnknown = false;
        try { transpiler.Invoke(null, new object[] { new List<CodeInstruction>() }); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejectsUnknown = true; }
        Require(rejectsUnknown, "Production transpiler rejects missing or changed loop shape");
        harmony.Patch(postfix, transpiler: new HarmonyMethod(transpiler));
        Require(Harmony.GetPatchInfo(postfix).Owners.Contains(harmony.Id), "Production transpiler generates a valid real-PoV wrapper under original Mono");
        interaction.Invoke(station, new object?[] { null, true, false });
        Require(_postfixCalls == 1, "Actual Interact wrapper with production transpiler preserves PoV repeat guard");
        Console.WriteLine("SKIP: batch hook execution needs Unity Object lifetime and a real local player; service logic has separate isolated tests.");
        FieldInfo eligibility = compat.GetField("_repairEligibility", Any)!;
        MethodInfo delegateFactory = typeof(AccessTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "MethodDelegate" && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(MethodInfo));
        Delegate predicate = (Delegate)delegateFactory.MakeGenericMethod(eligibility.FieldType)
            .Invoke(null, new object?[] { highStation, null, true })!;
        object?[] parameters = { null, false };
        predicate.DynamicInvoke(parameters);
        Require(!(bool)parameters[1]!, "Original private PoV eligibility method can be cached as production delegate without publicizing");
        eligibility.SetValue(null, predicate);
        compat.GetField("_autoRepairFailureLogged", Any)!.SetValue(null, true);
        compat.GetField("_stationFailureLogged", Any)!.SetValue(null, true);
        compat.GetMethod("Shutdown", Any)!.Invoke(null, null);
        Require(eligibility.GetValue(null) == null && compat.GetField("_disableVanillaSkills", Any)!.GetValue(null) == null
            && !(bool)compat.GetField("_autoRepairFailureLogged", Any)!.GetValue(null)!
            && !(bool)compat.GetField("_stationFailureLogged", Any)!.GetValue(null)!, "Production shutdown clears cached predicate and diagnostic state");
        harmony.Unpatch(postfix, transpiler);
        CheckConflictingTranspiler(harmony, interaction, station, postfix, transpiler);
        CheckTooltipText(harmony, repair);
    }

    private static void CheckConflictingTranspiler(Harmony harmony, MethodInfo interaction, object station, MethodInfo postfix, MethodInfo productionTranspiler)
    {
        var foreign = new Harmony("RRM.PathOfValheimanChecks.Conflict." + Guid.NewGuid().ToString("N"));
        try
        {
            var rewrite = new HarmonyMethod(typeof(Program).GetMethod(nameof(ReplacePostfixWithSentinel), Any)) { priority = Priority.First };
            foreign.Patch(postfix, transpiler: rewrite);
            bool rejected = false;
            try { harmony.Patch(postfix, transpiler: new HarmonyMethod(productionTranspiler)); }
            catch (Exception) { rejected = true; }
            Require(rejected, "Production hook rejects another transpiler removing its expected loop");
            harmony.Unpatch(postfix, productionTranspiler);
            Patches state = Harmony.GetPatchInfo(postfix);
            Require(state.Transpilers.Any(patch => patch.owner == foreign.Id) && state.Transpilers.All(patch => patch.PatchMethod != productionTranspiler), "Targeted cleanup removes failed production hook and preserves foreign transpiler");
            int previous = _postfixCalls;
            interaction.Invoke(station, new object?[] { null, true, false });
            Require(_postfixCalls == previous + 1, "Real Interact wrapper remains callable after failed-hook cleanup");
        }
        finally { harmony.Unpatch(postfix, productionTranspiler); foreign.UnpatchSelf(); }
    }

    private static void CheckTooltipText(Harmony harmony, Assembly repair)
    {
        Type localization = Assembly.LoadFrom(Path.Combine(_searchPaths[0], "assembly_guiutils.dll")).GetType("Localization", true)!;
        MethodInfo getter = localization.GetProperty("instance", Any)!.GetGetMethod()!;
        harmony.Patch(getter, transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(ReturnNull), Any)));
        Type translations = repair.GetType("RepairRequiresMaterials.RepairRequiresMaterialsLocalization", true)!;
        try
        {
            translations.GetMethod("Initialize", Any)!.Invoke(null, null);
            MethodInfo append = repair.GetType("RepairRequiresMaterials.CraftingSkillTooltipText", true)!.GetMethod("Append", Any)!;
            string Normal(float? level, string original = "Crafting description", bool enabled = true)
                => (string)append.Invoke(null, new object?[] { original, enabled, 10f, 30f, enabled ? 25f : 0f, enabled ? 50f : 0f, level })!;
            string absent = Normal(null);
            Require(!absent.Contains("Path of Valheiman"), "Tooltip has no freeze note when optional level is absent");
            Require(Normal(0f).Contains("Effective Crafting level: 0."), "Tooltip displays frozen effective level zero");
            string withLevel = Normal(37f);
            Require(withLevel.Contains("Effective Crafting level: 37."), "Tooltip displays the provided effective level including gear bonuses");
            Require(Normal(37f, withLevel) == withLevel, "Repeated tooltip append does not duplicate heading or freeze note");
            Require(Normal(37f, "Crafting description", false) == "Crafting description", "Disabled RRM skill bonuses leave original tooltip unchanged");
        }
        finally { translations.GetMethod("Shutdown", Any)!.Invoke(null, null); harmony.Unpatch(getter, HarmonyPatchType.All, harmony.Id); }
    }

    private static bool SkipGameInteraction(ref bool __result) { __result = true; return false; }
    private static void RecordPostfix() { _postfixCalls++; }
    private static bool BatchRepair(object gui) { _postfixCalls++; return false; }

    private static IEnumerable<CodeInstruction> ReplacePostfixWithSentinel(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Call, typeof(Program).GetMethod(nameof(RecordPostfix), Any));
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static IEnumerable<CodeInstruction> ReturnNull(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Ldnull);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static IEnumerable<CodeInstruction> ReplaceAvailability(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_haveRepairableItems!))
            {
                var replacement = new CodeInstruction(instruction) { opcode = OpCodes.Call, operand = typeof(Program).GetMethod(nameof(BatchRepair), Any) };
                yield return replacement;
            }
            else yield return instruction;
        }
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }

    private static void PrintIdentity(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(path))
            Console.WriteLine(AssemblyName.GetAssemblyName(path).FullName + " | SHA256 " + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") + " | " + path);
    }
}
