using System.Reflection;
using System.Runtime.CompilerServices;
using GBRHelper.Ipc;
using GBRHelper.Navigation;
using GBRHelper.Translation;
using Mono.Cecil;

var pass = 0; var fail = 0;
void Assert(bool value, string message = "assertion") { if (!value) throw new Exception(message); }
void Test(string name, Action body)
{
    try { body(); pass++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { fail++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
var table = TranslationTable.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GbrJa.tsv")));
Test("dictionary has no duplicate keys or broken placeholders", () => Assert(table.ExactCount > 500 && table.TemplateCount > 0));
Test("new Material UI pages and settings have translations", () =>
{
    foreach (var text in new[] { "Browse", "PLAN", "Auto-Gather settings", "Interface settings", "Gathering settings",
        "Control size", "Text size", "Spacing", "Check retainer inventories", "Use AutoHook global preset",
        "Opt in to fishing data collection", "AutoRetainer threshold", "Search settings" })
        Assert(table.Translate(text) is { } ja && ja != text, text);
});
Test("new UI selects auto_gather and restores without toggling closed", () =>
{
    var window = new NewWindow(); var plugin = new FakePlugin(window);
    Assert(GbrWindowAccess.TryOpenAutoGather(plugin)); Assert(window.Page == "auto_gather" && window.Opens == 1);
    Assert(GbrWindowAccess.TryOpenAutoGather(plugin)); Assert(window.Opens == 2);
});
Test("legacy UI requests fallback without modifying the window", () => Assert(!GbrWindowAccess.TryOpenAutoGather(new FakePlugin(new LegacyWindow()))));
Test("new UI errors propagate instead of invoking a toggle fallback", () =>
{
    try { GbrWindowAccess.TryOpenAutoGather(new FakePlugin(new BrokenWindow())); throw new Exception("exception missing"); }
    catch (TargetInvocationException e) { Assert(e.InnerException?.Message == "fixture"); }
});
Test("new content children are inside GBR but other plugin windows are not", () =>
{
    Assert(WindowScope.IsInside(new Stack(10, 20, 30), 10));
    Assert(!WindowScope.IsInside(new Stack(40, 20, 30), 10));
    Assert(!WindowScope.IsInside(new Stack(), 10));
});
Func<Func<string, float, string>, string, float, string>? detour = null;
var inside = true; var released = 0; var calls = 0;
var adapter = new MaterialTextAdapter(typeof(FakeNavigation), () => inside, table.Translate,
    (_, d) => { detour = (Func<Func<string, float, string>, string, float, string>)d; return new Cleanup(() => released++); });
string Original(string text, float width) { calls++; return text[..Math.Min(text.Length, (int)width)]; }
Test("translate before clipping; shortened text keeps Japanese", () =>
{
    calls = 0; var result = detour!(Original, "Auto-Gather settings", 4);
    Assert(result == table.Translate("Auto-Gather settings")![..4] && calls == 1);
});
Test("other plugin text remains unchanged", () =>
{
    inside = false; calls = 0; Assert(detour!(Original, "Auto-Gather settings", 4) == "Auto" && calls == 1); inside = true;
});
Test("original error is not caught and retried", () =>
{
    calls = 0;
    try { detour!((_, _) => { calls++; throw new InvalidOperationException("draw failed"); }, "Browse", 20); }
    catch (InvalidOperationException) { Assert(calls == 1); return; }
    throw new Exception("original failure swallowed");
});
Test("detached adapter passes original text through", () =>
{
    adapter.Dispose(); calls = 0; Assert(released == 1 && detour!(Original, "Browse", 10) == "Browse" && calls == 1);
});
Test("translation failure still calls original exactly once", () =>
{
    using var broken = new MaterialTextAdapter(typeof(FakeNavigation), () => true, _ => throw new Exception("translation"),
        (_, d) => { detour = (Func<Func<string, float, string>, string, float, string>)d; return new Cleanup(() => { }); });
    calls = 0; Assert(detour!(Original, "Browse", 10) == "Browse" && calls == 1);
});
Test("managed adapter really attaches and detaches on .NET 10", () =>
{
    ManagedHookRuntime.Initialize(AppContext.BaseDirectory);
    using (var live = new MaterialTextAdapter(typeof(FakeNavigation), () => true, table.Translate, ManagedHookRuntime.CreateHook))
        Assert(FakeNavigation.Truncate("Browse", 100) == table.Translate("Browse"));
    Assert(FakeNavigation.Truncate("Browse", 100) == "Browse");
});

if (args.Length == 1)
{
    using var dll = AssemblyDefinition.ReadAssembly(args[0]);
    var module = dll.MainModule;
    TypeDefinition Type(string name) => module.GetType(name) ?? throw new Exception("missing type " + name);
    void Method(TypeDefinition type, string name, string result, params string[] parameters)
        => Assert(type.Methods.Count(m => m.Name == name && m.ReturnType.FullName == result
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)) == 1, type.Name + "." + name);
    Test("installed GBR 7.5.8.3 offers the new page entry point", () =>
    {
        Assert(dll.Name.Version.ToString() == "7.5.8.3");
        var ui = Type("GatherBuddy.Gui.Interface");
        Assert(ui.BaseType.FullName == "RebornMaterial.M3ShellWindow");
        Method(ui, "Open", "System.Void", "System.String");
        Assert(Type("GatherBuddy.GatherBuddy").Fields.Single(f => f.Name == "Interface").FieldType.FullName == ui.FullName);
    });
    Test("installed GBR IPC types and events still match helper subscriptions", () =>
    {
        var ipc = Type("GatherBuddy.Plugin.GatherBuddyIpc");
        Method(ipc, "Version", "System.Int32"); Method(ipc, "IsAutoGatherEnabled", "System.Boolean");
        Method(ipc, "IsAutoGatherWaiting", "System.Boolean"); Method(ipc, "GetAutoGatherStatusText", "System.String");
        Method(ipc, "SetAutoGatherEnabled", "System.Void", "System.Boolean");
        Assert(ipc.Fields.Single(f => f.Name == "AutoGatherEnabledChanged").FieldType.FullName == "System.Action`1<System.Boolean>");
    });
    Test("installed vendor approach adapter method and field types match", () =>
    {
        var vendor = Type("GatherBuddy.Vulcan.Vendors.VendorNavigator");
        foreach (var name in new[] { "HandlePendingLocalAethernetTeleport", "HandlePendingHousingShardTeleport", "CanIssueTeleportRequest", "CanIssueHousingEntryAction" })
            Method(vendor, name, "System.Boolean");
        Method(vendor, "TryGetNearestAethernetSource", "System.Boolean", "Dalamud.Game.ClientState.Objects.Types.IGameObject&", "System.Single&", "System.Single&");
        foreach (var (name, type) in new[] { ("_pendingAethernetName", "System.String"), ("_pendingHousingShardTeleportName", "System.String"),
            ("_pendingAethernetParentTerritoryId", "System.UInt32"), ("_teleportWaitStartTime", "System.DateTime") })
            Assert(vendor.Fields.Single(f => f.Name == name).FieldType.FullName == type, name);
        Assert(vendor.Properties.Single(p => p.Name == "IsActive").PropertyType.FullName == "System.Boolean");
    });
    Test("installed list add/delete/save and per-item toggles retain their contracts", () =>
    {
        var manager = Type("GatherBuddy.AutoGather.Lists.AutoGatherListsManager");
        var list = Type("GatherBuddy.AutoGather.Lists.AutoGatherList");
        Method(list, "Add", "System.Boolean", "GatherBuddy.Interfaces.IGatherable", "System.UInt32");
        Method(manager, "Save", "System.Void");
        Method(manager, "DeleteList", "System.Void", list.FullName);
        Method(manager, "RemoveItem", "System.Void", list.FullName, "System.Int32");
        Method(manager, "ChangeEnabled", "System.Void", list.FullName, "GatherBuddy.Interfaces.IGatherable", "System.Boolean");
        foreach (var name in new[] { "Name", "Description", "Enabled", "Fallback", "RemoveCompletedItems", "UsesRetainerInventory", "Items", "Quantities", "EnabledItems", "FolderPath", "Order" })
            Assert(list.Properties.Any(p => p.Name == name), name);
        Assert(manager.Methods.Any(m => m.Name == "AddList" && m.Parameters[0].ParameterType.FullName == list.FullName
            && m.Parameters.Skip(1).All(p => p.HasDefault)));
        var fs = Type("GatherBuddy.AutoGather.Lists.FileSystem`1");
        Assert(fs.Methods.Any(m => m.Name == "TryGetValue" && m.Parameters.Count == 2 && m.Parameters[1].IsOut));
        Assert(fs.NestedTypes.Single(t => t.Name == "Folder").Methods.Any(m => m.Name == "GetLeaves" && m.Parameters.Count == 0));
    });
    Test("installed reduction and item count hooks retain their signatures", () =>
    {
        var auto = Type("GatherBuddy.AutoGather.AutoGather");
        Method(auto, "ReduceItems", "System.Void", "System.Boolean", "System.Action");
        Method(auto, "HasReducibleItems", "System.Boolean");
        Method(Type("GatherBuddy.AutoGather.Extensions.GatherableExtensions"), "GetTotalCount", "System.Int32", "GatherBuddy.Interfaces.IGatherable", "System.Boolean");
        Assert(module.AssemblyReferences.Any(a => a.Name == "LuminaSupplemental.Excel"));
    });
}
Console.WriteLine($"RESULT {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;

sealed class FakePlugin(object ui) { public readonly object Interface = ui; }
sealed class NewWindow { public int Opens; public string Page = ""; public void Open(string id) { Page = id; Opens++; } }
sealed class LegacyWindow { }
sealed class BrokenWindow { public void Open(string id) => throw new InvalidOperationException("fixture"); }
readonly struct Stack(params uint[] ids) : WindowScope.IStack { public int Count => ids?.Length ?? 0; public uint IdAt(int index) => ids[index]; }
sealed class Cleanup(Action dispose) : IDisposable { public void Dispose() => dispose(); }
static class FakeNavigation
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static string Truncate(string text, float width) => text[..Math.Min(text.Length, (int)width)];
}
