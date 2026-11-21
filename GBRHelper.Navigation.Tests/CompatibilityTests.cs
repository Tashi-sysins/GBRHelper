using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using GBRHelper.Ipc;
using GBRHelper.Navigation;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NavigationTests;

public static class CompatibilityTests
{
    private static void Assert(bool ok, string reason = "assertion") { if (!ok) throw new Exception(reason); }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static PluginValidationReport Report() => new() { InternalName = "fixture" };
    public static async Task<int> Run()
    {
        var passed = 0; var failed = 0;
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
        }
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        var root = Path.Combine(AppContext.BaseDirectory, "compat-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "original.dll");
        using (var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("MemoryFixture", new Version(1, 0)), "MemoryFixture", ModuleKind.Dll))
        {
            var type = new TypeDefinition("GatherBuddy.AutoGather", "AutoGather", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, fixture.MainModule.TypeSystem.Object);
            fixture.MainModule.Types.Add(type);
            var method = new MethodDefinition("Sample", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, fixture.MainModule.TypeSystem.Int32);
            type.Methods.Add(method);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            fixture.Write(original);
        }
        var context = new ProbeContext();
        using var originalStream = File.OpenRead(original);
        var loaded = context.LoadFromStream(originalStream);
        var originalHash = Hash(original);
        await Test("R01 memory-loaded assembly passes product validation with its source path", () => Sync(() => {
            Assert(loaded.Location == ""); var report = Report();
            GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, original, originalHash, report);
            Assert(report.Validated && report.CheckedMethods == 1 && report.AssemblyLocation == "" && report.DiskMvid == report.LoadedMvid);
        }));
        await Test("R02 empty and relative paths fail with path-stage diagnostic", () => Sync(() => {
            foreach (var path in new[] { "", "relative.dll" })
            {
                var report = Report();
                Throws<InvalidOperationException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, path, originalHash, report));
                Assert(report.Stage == "パス取得" && !report.Validated && report.ActualHash == null);
            }
        }));
        await Test("R02 missing and locked files fail before hash validation", () => Sync(() => {
            var report = Report();
            Throws<FileNotFoundException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, Path.Combine(root, "missing.dll"), originalHash, report));
            Assert(report.Stage == "ファイル読込み" && !report.Validated);
            var lockedPath = Path.Combine(root, "locked.dll"); File.Copy(original, lockedPath);
            using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Throws<IOException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, lockedPath, originalHash, report));
            Assert(report.Stage == "ファイル読込み" && report.ActualHash == null);
        }));
        await Test("R03 hash mismatch is rejected for empty-Location assembly", () => Sync(() => {
            var report = Report();
            Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, original, new string('0', 64), report));
            Assert(report.Stage == "ハッシュ" && report.ActualHash == originalHash && !report.Validated);
        }));
        await Test("R04 matching disk hash cannot bypass different loaded MVID", () => Sync(() => {
            var path = Path.Combine(root, "different-mvid.dll");
            using (var file = AssemblyDefinition.ReadAssembly(original)) { file.MainModule.Mvid = Guid.NewGuid(); file.Write(path); }
            var report = Report();
            Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, path, Hash(path), report));
            Assert(report.Stage == "MVID" && report.DiskMvid != report.LoadedMvid && !report.Validated);
        }));
        await Test("R05 same MVID and accepted disk hash cannot bypass modified method IL", () => Sync(() => {
            var path = Path.Combine(root, "different-il.dll");
            using (var file = AssemblyDefinition.ReadAssembly(original))
            {
                file.MainModule.GetType("GatherBuddy.AutoGather.AutoGather").Methods[0].Body.Instructions[0].OpCode = OpCodes.Ldc_I4_2;
                file.Write(path);
            }
            var report = Report();
            Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, path, Hash(path), report));
            Assert(report.Stage == "IL/契約" && report.DiskMvid == report.LoadedMvid && !report.Validated);
        }));

        Entry NewEntry(string name = "GBR") => new(new object(), original) { InternalName = name };
        await Test("R07 selects loaded development entry and retains paired instance and file", () => Sync(() => {
            var installed = NewEntry(); installed.IsLoaded = false; installed.DllFile = new(Path.Combine(root, "old.dll"));
            var dev = new DevEntry(new object(), original) { InternalName = "GBR" };
            var binding = LoadedPluginBinding.Resolve(new object[] { installed, dev }, "GBR");
            Assert(ReferenceEquals(binding.Instance, dev.Current) && binding.DllPath == original && binding.IsCurrent);
            Throws<InvalidOperationException>(() => LoadedPluginBinding.Resolve(new[] { NewEntry(), NewEntry() }, "GBR"));
            Throws<InvalidOperationException>(() => LoadedPluginBinding.Resolve(new[] { installed }, "vnavmesh"));
        }));
        await Test("R07 unload instance assembly and path changes invalidate cached binding", () => Sync(() => {
            foreach (Action<Entry> change in new Action<Entry>[] {
                e => e.IsLoaded = false, e => e.Replace(new object()), e => e.Assembly = loaded,
                e => e.DllFile = new(Path.Combine(root, "another.dll")) })
            {
                var entry = NewEntry(); var binding = LoadedPluginBinding.Resolve(new[] { entry }, "GBR");
                change(entry); Assert(!binding.IsCurrent); Throws<InvalidOperationException>(binding.EnsureCurrent);
            }
            var reloaded = NewEntry(); var old = LoadedPluginBinding.Resolve(new[] { reloaded }, "GBR");
            reloaded.Replace(new object()); var next = LoadedPluginBinding.Resolve(new[] { reloaded }, "GBR");
            Assert(!old.IsCurrent && next.IsCurrent && !ReferenceEquals(old.Instance, next.Instance));
        }));
        await Test("R07 instance changing while reading DllFile is rejected", () => Sync(() => {
            var entry = NewEntry(); entry.OnPathRead = () => entry.Replace(new object());
            Throws<InvalidOperationException>(() => LoadedPluginBinding.Resolve(new[] { entry }, "GBR"));
            entry.OnPathRead = null; entry.DllFile = null;
            var stage = ""; Throws<InvalidOperationException>(() => LoadedPluginBinding.Resolve(new[] { entry }, "GBR", s => stage = s));
            Assert(stage == "パス取得");
        }));
        await Test("R07 R09 invalid binding prevents registration and reload rolls back new bridge", () => Sync(() => {
            var g = NewEntry(); var n = NewEntry("nav"); var gb = LoadedPluginBinding.Resolve(new[] { g }, "GBR");
            var nb = LoadedPluginBinding.Resolve(new[] { n }, "nav");
            var auto = new FakeAuto(); var env = new FakeEnvironment(); GbrNavigationBridge? created = null;
            Throws<InvalidOperationException>(() => LoadedPluginBinding.Connect(gb, nb, () => {
                created = new(auto, env, new(new FakeQueries())); n.Replace(new object()); return created;
            }));
            Assert(created?.HookCount == 0);
            auto.DoNodeMovement(new(new(42)), new()); auto.HandlePathfinding(); Assert(env.Attempts == 0 && auto.Moves == 1);
            var called = false;
            Throws<InvalidOperationException>(() => LoadedPluginBinding.Connect(gb, nb, () => { called = true; return new GbrNavigationBridge(auto, env, new(new FakeQueries())); }));
            Assert(!called);
        }));
        await Test("R09 registration failure reports method and previous hook count", () => Sync(() => {
            var installed = 0; var auto = new FakeAuto(); var env = new FakeEnvironment();
            var ex = Throws<HookRegistrationException>(() => new GbrNavigationBridge(auto, env, new(new FakeQueries()), (m, d) => {
                if (++installed == 5) throw new InvalidOperationException("injected"); return ManagedHookRuntime.CreateHook(m, d);
            }));
            Assert(ex.RegisteredBeforeFailure == 4 && ex.MethodName.EndsWith("StopPathfinding"));
            auto.DoNodeMovement(new(new(42)), new()); auto.HandlePathfinding(); Assert(env.Attempts == 0 && auto.Moves == 1);
        }));
        await Test("R09 reflected first-hook failure retains method diagnostic", () => Sync(() => {
            var diagnostic = new ConnectionDiagnostics(); diagnostic.Begin(); diagnostic.Stage = "フック登録";
            var error = Throws<TargetInvocationException>(() => new GbrNavigationBridge(new FakeAuto(), new FakeEnvironment(), new(new FakeQueries()),
                (_, _) => throw new InvalidOperationException("first hook failed")));
            diagnostic.Fail(error);
            Assert(diagnostic.FailedMethod?.EndsWith("DoNodeMovement") == true && diagnostic.RegisteredHooks == 0 && diagnostic.ActiveHooks == 0);
        }));
        await Test("R09 cleanup failure retains original invalidation reason", () => Sync(() => {
            var g = NewEntry(); var n = NewEntry("nav");
            var gb = LoadedPluginBinding.Resolve(new[] { g }, "GBR"); var nb = LoadedPluginBinding.Resolve(new[] { n }, "nav");
            var error = Throws<AggregateException>(() => LoadedPluginBinding.Connect(gb, nb, () => { n.IsLoaded = false; return new FailingCleanup(); }));
            Assert(error.InnerExceptions.Count == 2 && error.InnerExceptions[0].Message.Contains("nav") && error.InnerExceptions[1].Message == "cleanup failed");
        }));
        await Test("R09 repeated connect and detach do not multiply hooks or original calls", () => Sync(() => {
            for (var i = 0; i < 3; i++)
            {
                var auto = new FakeAuto(); var env = new FakeEnvironment();
                using (var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries())))
                {
                    Assert(bridge.HookCount == 7); auto.DoNodeMovement(new(new(42)), new()); auto.HandlePathfinding();
                    Assert(env.Attempts == 1 && auto.Moves == 1 && auto.NavigateCalls == 1);
                }
                auto.StopNavigation(); auto.DoNodeMovement(new(new(42)), new()); auto.HandlePathfinding();
                Assert(env.Attempts == 1 && auto.Moves == 2 && auto.NavigateCalls == 2);
            }
        }));
        await Test("R08 early failure remains copyable and duplicate failures are aggregated", () => Sync(() => {
            var diagnostic = new ConnectionDiagnostics(); diagnostic.Begin(); diagnostic.Stage = "GBR：パス取得";
            var error = new InvalidOperationException("source missing", new ArgumentException("inner detail"));
            Assert(diagnostic.Fail(error));
            var snapshot = diagnostic.ToText(); Assert(snapshot.Contains("source missing") && snapshot.Contains("inner detail") && diagnostic.RegisteredHooks == 0);
            Assert(!diagnostic.Fail(error) && diagnostic.RepeatedFailures == 2);
            diagnostic.Begin(); diagnostic.Stage = "GBR：パス取得"; Assert(!diagnostic.Fail(error) && diagnostic.RepeatedFailures == 3);
            diagnostic.Connected(7); Assert(diagnostic.Fail(error) && diagnostic.RepeatedFailures == 1);
        }));
        await Test("R08 trace storage failure does not erase original connection exception", async () => {
            var diagnostic = new ConnectionDiagnostics(); diagnostic.Begin(); diagnostic.Fail(new Exception("original connection failure"));
            var path = Path.Combine(root, "not-a-directory"); File.WriteAllText(path, "fixture");
            using var trace = new NavigationTraceWriter(path); trace.Record("connection-error", null, diagnostic.ToText()); trace.Dispose();
            await trace.Completion.WaitAsync(TimeSpan.FromSeconds(5)); diagnostic.StorageError = trace.Error;
            Assert(trace.Error.Length > 0 && diagnostic.ToText().Contains("original connection failure") && diagnostic.RegisteredHooks == 0);
        });
        await Test("R10 navigation release allowlist retains old release and accepts inspected 1.2.3.15 only", () => Sync(() => {
            Assert(GbrCompatibilityProfile.IsSupportedNavigationHash("889EBFF40D92DB2DF4B4C190F1D81B3971E538908E459F6297F53FEA017E3723"));
            Assert(GbrCompatibilityProfile.IsSupportedNavigationHash("4D94923DAEB56EA7A842108A581408B0F201F4AB35966065D974A9367D7EA288"));
            Assert(!GbrCompatibilityProfile.IsSupportedNavigationHash(originalHash));
            var report = Report();
            Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, original, GbrCompatibilityProfile.NavHash, report));
            Assert(report.Stage == "ハッシュ" && !report.Validated && report.CheckedMethods == 0);
        }));
        await Test("R10 missing navigation IPC provider is rejected before a zero-method validation can succeed", () => Sync(() => {
            using var fixture = AssemblyDefinition.ReadAssembly(original);
            Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateNavigationContract(fixture.MainModule));
        }));
        context.Unload();
        Console.WriteLine($"COMPATIBILITY RESULT {passed} PASS / {failed} FAIL");
        return failed == 0 ? 0 : 1;
    }

    public static void VerifyInstalled(string gbrPath, string navPath, string dalamudDirectory)
    {
        foreach (var (path, hash, isGbr) in new[] { (gbrPath, GbrCompatibilityProfile.GbrHash, true), (navPath, GbrCompatibilityProfile.NavHash, false) })
        {
            var context = new ProbeContext(Path.GetDirectoryName(gbrPath)!, Path.GetDirectoryName(navPath)!, dalamudDirectory);
            using var stream = File.OpenRead(path); var loaded = context.LoadFromStream(stream);
            Assert(loaded.Location == ""); var report = new PluginValidationReport { InternalName = isGbr ? "GatherBuddyReborn" : "vnavmesh" };
            GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, path, hash, report);
            if (isGbr) _ = GbrCompatibilityProfile.ValidateAutoGather(loaded);
            Console.WriteLine($"PASS R06 {report.InternalName}: empty Location, source={path}, hash={report.ActualHash}, MVID={report.LoadedMvid}, compared IL methods={report.CheckedMethods}, IPC contracts={report.CheckedContracts}, tuple={(isGbr ? "PASS" : "N/A")}");
            context.Unload();
        }
        Console.WriteLine("READ ONLY real installed DLLs; product validators used; no plugin initialization, game hooks or gathering executed.");
    }

    public static void VerifyNavigation(string navPath, string dalamudDirectory)
    {
        var context = new ProbeContext(Path.GetDirectoryName(navPath)!, dalamudDirectory);
        using var stream = File.OpenRead(navPath);
        var loaded = context.LoadFromStream(stream);
        Assert(loaded.Location == "");
        var report = new PluginValidationReport { InternalName = "vnavmesh" };
        GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, navPath, GbrCompatibilityProfile.NavHash, report);
        Assert(report.Validated && report.CheckedMethods > 0 && report.CheckedContracts == 15);
        Console.WriteLine($"PASS vnavmesh {loaded.GetName().Version}: SHA256={report.ActualHash}; MVID={report.LoadedMvid}; compared IL methods={report.CheckedMethods}; IPC contracts={report.CheckedContracts}; empty Location; no initialization");
        // Mutations are confined to a temporary fixture. Installed plugin files stay read-only.
        var temp = Path.Combine(Path.GetTempPath(), "GBRH-navigation-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var changed = Path.Combine(temp, "unknown.dll");
            using (var file = AssemblyDefinition.ReadAssembly(navPath))
            {
                var constructor = file.MainModule.GetType("Navmesh.IPCProvider").Methods.Single(m => m.IsConstructor && !m.IsStatic);
                constructor.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && i.Operand as string == "Nav.PathfindCancelable").Operand = "Nav.PathfindChanged";
                Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateNavigationContract(file.MainModule));
                file.Write(changed);
            }
            var rejected = new PluginValidationReport { InternalName = "vnavmesh" };
            Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(loaded, changed, GbrCompatibilityProfile.NavHash, rejected));
            Assert(rejected.Stage == "ハッシュ" && !rejected.Validated);
            Console.WriteLine("PASS unknown DLL rejected at hash stage; missing cancelable path IPC rejected at contract stage");
            using (var file = AssemblyDefinition.ReadAssembly(navPath))
            {
                var constructor = file.MainModule.GetType("Navmesh.IPCProvider").Methods.Single(m => m.IsConstructor && !m.IsStatic);
                var index = constructor.Body.Instructions.ToList().FindIndex(i => i.OpCode == OpCodes.Ldstr && i.Operand as string == "Nav.PathfindCancelable");
                var call = constructor.Body.Instructions.Skip(index + 1).First(i => i.Operand is GenericInstanceMethod m && m.Name == "RegisterFunc");
                ((GenericInstanceMethod)call.Operand).GenericArguments[4] = file.MainModule.TypeSystem.Int32;
                Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateNavigationContract(file.MainModule));
            }
            Console.WriteLine("PASS cancelable path IPC parameter-type change rejected");
            foreach (var changeMvid in new[] { true, false })
            {
                using var file = AssemblyDefinition.ReadAssembly(navPath);
                if (changeMvid) file.MainModule.Mvid = Guid.NewGuid();
                else
                {
                    var method = file.MainModule.GetType("Navmesh.NavmeshManager").Methods.Single(m => m.Name == "get_PathfindInProgress");
                    method.Body.Instructions.Clear();
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                }
                using var modified = new MemoryStream();
                file.Write(modified); modified.Position = 0;
                var modifiedContext = new ProbeContext(Path.GetDirectoryName(navPath)!, dalamudDirectory);
                try
                {
                    var modifiedLoaded = modifiedContext.LoadFromStream(modified);
                    var mismatch = new PluginValidationReport { InternalName = "vnavmesh" };
                    Throws<NotSupportedException>(() => GbrCompatibilityProfile.ValidateFileAndLoadedModule(modifiedLoaded, navPath, GbrCompatibilityProfile.NavHash, mismatch));
                    Assert(!mismatch.Validated && mismatch.Stage == (changeMvid ? "MVID" : "IL/契約"));
                    Console.WriteLine(changeMvid
                        ? "PASS accepted disk hash cannot bypass a different loaded MVID"
                        : "PASS accepted disk hash and matching MVID cannot bypass changed loaded navigation IL");
                }
                finally { modifiedContext.Unload(); }
            }
        }
        finally
        {
            File.Delete(Path.Combine(temp, "unknown.dll"));
            Directory.Delete(temp);
            context.Unload();
        }
    }

    public class Entry
    {
        private object instance;
        private FileInfo? dllFile;
        public string InternalName { get; set; } = "GBR";
        public bool IsLoaded { get; set; } = true;
        public Assembly? Assembly { get; set; }
        public Action? OnPathRead;
        public FileInfo? DllFile { get { OnPathRead?.Invoke(); return dllFile; } set => dllFile = value; }
        public object Current => instance;
        public Entry(object value, string path) { instance = value; Assembly = value.GetType().Assembly; dllFile = new(path); }
        public void Replace(object value) { instance = value; Assembly = value.GetType().Assembly; }
    }
    public sealed class DevEntry(object value, string path) : Entry(value, path);
    private sealed class FailingCleanup : IDisposable { public void Dispose() => throw new Exception("cleanup failed"); }
    private sealed class ProbeContext(params string[] roots) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var shared = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (shared != null) return shared;
            foreach (var root in roots)
            {
                var path = Path.Combine(root, name.Name + ".dll");
                if (File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return null;
        }
    }
}
