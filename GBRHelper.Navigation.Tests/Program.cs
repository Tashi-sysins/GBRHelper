using System.Reflection;
using System.Runtime.CompilerServices;
using GBRHelper.Navigation;

if (args.Length > 0 && args[0] == "--verify-loaded")
{
    NavigationTests.CompatibilityTests.VerifyInstalled(args[1], args[2], args[3]);
    return;
}

if (args.Length > 0 && args[0] == "--verify-navigation")
{
    NavigationTests.CompatibilityTests.VerifyNavigation(args[1], args[2]);
    return;
}

if (args.Length > 0 && args[0] == "--verify-installed")
{
    var known = new[] { GBRHelper.Navigation.GbrCompatibilityProfile.GbrHash, GBRHelper.Navigation.GbrCompatibilityProfile.NavHash };
    for (var i = 0; i < 2; i++)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[i + 1])));
        if (i == 0 ? !GbrCompatibilityProfile.IsSupportedGbrHash(hash) : !GbrCompatibilityProfile.IsSupportedNavigationHash(hash))
            throw new Exception("Unexpected installed DLL hash");
        using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(args[i + 1]);
        Console.WriteLine($"FILE {asm.Name.Name} {asm.Name.Version} MVID={asm.MainModule.Mvid} SHA256={hash}");
        if (i == 0)
        {
            var type = asm.MainModule.GetType("GatherBuddy.AutoGather.AutoGather");
            var navigate = type.Methods.Single(m => m.Name == "Navigate");
            var handle = type.Methods.Single(m => m.Name == "HandlePathfinding");
            var combined = navigate.Body.Instructions.Count(x => x.Operand is Mono.Cecil.MethodReference r && r.Name == "FindCombinedPath");
            var direct = navigate.Body.Instructions.Count(x => x.Operand is Mono.Cecil.FieldReference f && f.Name == "PathfindCancelable");
            var fallback = handle.Body.Instructions.Count(x => x.Operand is Mono.Cecil.FieldReference f && f.Name == "PathfindCancelable");
            if (combined != 2 || direct != 1 || fallback != 1) throw new Exception("Four task entry points differ");
            Console.WriteLine("PASS installed binary metadata: direct=1 combined=2 fallback=1");
            Console.WriteLine(type.Fields.Single(f => f.Name == "_navState").FieldType.FullName);
            foreach (var name in new[] { "DoNodeMovement", "Navigate", "HandlePathfinding", "StopPathfinding", "StopNavigation", "GetCorrectedDestination", "set_Enabled" })
                Console.WriteLine(type.Methods.Single(m => m.Name == name).FullName);
        }
        else Console.WriteLine($"PASS navigation IPC contracts={GbrCompatibilityProfile.ValidateNavigationContract(asm.MainModule)}");
    }
    Console.WriteLine("READ ONLY disk inspection; game runtime hook/gathering NOT RUN");
    return;
}

if (args.Length > 0 && args[0] == "--inspect")
{
    using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(args[1]);
    foreach (var type in asm.MainModule.Types.Where(t => t.FullName == "GatherBuddy.AutoGather.AutoGather" || t.Name == "AdvancedUnstuck"))
    {
        Console.WriteLine(type.FullName);
        foreach (var m in type.Methods.Where(m => new[] { "DoNodeMovement", "Navigate", "HandlePathfinding", "StopNavigation", "StopPathfinding", "GetCorrectedDestination", "Check", "AbortAutoGather" }.Contains(m.Name)))
        {
            Console.WriteLine(m.FullName);
            foreach (var il in m.Body.Instructions) Console.WriteLine(il);
        }
    }
    return;
}
ManagedHookRuntime.Initialize(args.Length == 2 && args[0] == "--hook-directory" ? args[1] : AppContext.BaseDirectory);
NavigationTests.Suite.TestDataDirectory = Path.Combine(args.Length == 2 && args[0] == "--hook-directory" ? args[1] : AppContext.BaseDirectory, "TestData");
Console.WriteLine($"HOOK RUNTIME {ManagedHookRuntime.Description}; Runtime={Environment.Version}");
NavigationTests.HookRuntimeTests.Run(args.Length == 2 && args[0] == "--hook-directory" ? args[1] : AppContext.BaseDirectory);
var target = typeof(Probe).GetMethod(nameof(Probe.Run))!;
var calls = 0;
using (var h = ManagedHookRuntime.CreateHook(target, (Action<Action<object>, object>)((orig, self) => { calls++; orig(self); })))
{
    var p = new Probe(); p.Run();
    if (calls != 1 || p.Calls != 1) throw new Exception("Hook passthrough failed");
    h.Dispose(); p.Run();
    if (calls != 1 || p.Calls != 2) throw new Exception("Hook detach failed");
}
Console.WriteLine("PASS managed instance hook: object receiver, original exactly once, detach (.NET 10)");
var existing = await NavigationTests.Suite.Run();
var compatibility = await NavigationTests.CompatibilityTests.Run();
Environment.ExitCode = existing == 0 && compatibility == 0 ? 0 : 1;
public sealed class Probe
{
    public int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Run() => Calls++;
}
