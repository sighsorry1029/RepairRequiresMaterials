using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class NativeMonoHost
{
    private const string MonoDll = "mono-2.0-bdwgc.dll";
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetDllDirectory(string path);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_set_dirs(string assemblies, string config);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_set_assemblies_path(string path);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_config_parse(string path);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_domain_assembly_open(IntPtr domain, string file);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int count, IntPtr args);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_jit_cleanup(IntPtr domain);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_get_runtime_build_info();

    internal static int Run(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        IntPtr domain = IntPtr.Zero;
        IntPtr argv = IntPtr.Zero;
        IntPtr[] strings = Array.Empty<IntPtr>();
        try
        {
            string runtime = Path.Combine(args[1], "MonoBleedingEdge");
            if (IntPtr.Size != 8 || !SetDllDirectory(Path.Combine(runtime, "EmbedRuntime")))
                throw new InvalidOperationException("A 64-bit child and original Mono runtime are required.");
            string executable = Assembly.GetExecutingAssembly().Location;
            mono_set_dirs(args[2], Path.Combine(runtime, "etc"));
            mono_set_assemblies_path(string.Join(Path.PathSeparator.ToString(), new[] { args[2], args[3], Path.GetDirectoryName(executable), Path.GetDirectoryName(args[4]), args[5] == "-" ? "" : Path.GetDirectoryName(args[5]) }));
            mono_config_parse(Path.Combine(runtime, "etc", "mono", "config"));
            Console.WriteLine("Original Mono: " + Marshal.PtrToStringAnsi(mono_get_runtime_build_info()));
            domain = mono_jit_init_version("ArtisanStorageChecks", "v4.0.30319");
            if (domain == IntPtr.Zero) throw new InvalidOperationException("Mono initialization failed.");
            IntPtr assembly = mono_domain_assembly_open(domain, executable);
            if (assembly == IntPtr.Zero) throw new InvalidOperationException("Mono could not load the harness.");
            strings = new[] { executable, "--probe", args[2], args[3], args[4], args[5] }.Select(Marshal.StringToHGlobalAnsi).ToArray();
            argv = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
            for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
            return mono_jit_exec(domain, assembly, strings.Length, argv);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Mono host failed: " + error);
            return 1;
        }
        finally
        {
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
            foreach (IntPtr value in strings) Marshal.FreeHGlobal(value);
            if (domain != IntPtr.Zero) mono_jit_cleanup(domain);
        }
    }
}
