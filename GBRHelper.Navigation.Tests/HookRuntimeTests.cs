using GBRHelper.Navigation;
using System.Runtime.Loader;

namespace NavigationTests;

public static class HookRuntimeTests
{
    public static void Run(string directory)
    {
        var identity = ManagedHookRuntime.Description;
        var contexts = AssemblyLoadContext.Default.Assemblies.Count(a => a.GetName().Name?.StartsWith("MonoMod.") == true);
        ManagedHookRuntime.Initialize(directory);
        if (identity != ManagedHookRuntime.Description || contexts != AssemblyLoadContext.Default.Assemblies.Count(a => a.GetName().Name?.StartsWith("MonoMod.") == true))
            throw new Exception("Hook runtime duplicated on reconnect");
        Console.WriteLine("PASS H01 reconnect reuses dependency context");
        foreach (var path in new[] { "", "relative" })
        {
            try { ManagedHookRuntime.Initialize(path); throw new Exception("Expected invalid path rejection"); }
            catch (ArgumentException) { }
        }
        Console.WriteLine("PASS H02 no empty/relative hook dependency path");
        var root = Path.Combine(AppContext.BaseDirectory, "compat-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { ManagedHookRuntime.Initialize(root); throw new Exception("Expected missing library rejection"); }
        catch (FileNotFoundException) { }
        Console.WriteLine("PASS H03 missing dependencies fail even after successful initialization");
        File.WriteAllText(Path.Combine(root, "Mono.Cecil.dll"), "invalid package");
        try { ManagedHookRuntime.Initialize(root); throw new Exception("Expected mismatch rejection"); }
        catch (InvalidOperationException e) when (e.Message.Contains("Mono.Cecil")) { }
        Console.WriteLine("PASS H04 altered dependencies cannot reuse a cached runtime");
    }
}
