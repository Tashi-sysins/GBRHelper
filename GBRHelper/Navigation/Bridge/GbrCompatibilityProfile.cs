using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using Mono.Cecil;

namespace GBRHelper.Navigation;

public sealed class PluginValidationReport
{
    public string InternalName { get; set; } = "";
    public string Stage { get; set; } = "プラグイン取得";
    public string? DllPath { get; set; }
    public string? AssemblyName { get; set; }
    public string? AssemblyLocation { get; set; }
    public Guid? LoadedMvid { get; set; }
    public Guid? DiskMvid { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }
    public int CheckedMethods { get; set; }
    public int CheckedContracts { get; set; }
    public bool Validated { get; set; }
}

public static class GbrCompatibilityProfile
{
    public const string Contract = "GBRH-navigation/1";
    public const string GbrHash = "FFBCDEBC635580749CE61A70155FF53893C8BB511679D7288A245170128444E1";
    // 7.5.8.3: installed release inspected against source 3fa7874 and the navigation/vendor contracts.
    public const string Gbr7583Hash = "5A2FD7FB98787545D70276B344DC4F08152484E1912B43FFAE99001859BEFF93";
    public static bool IsSupportedGbrHash(string hash) => hash is GbrHash or Gbr7583Hash;
    public const string NavHash = "889EBFF40D92DB2DF4B4C190F1D81B3971E538908E459F6297F53FEA017E3723";
    public const string Nav12315Hash = "4D94923DAEB56EA7A842108A581408B0F201F4AB35966065D974A9367D7EA288";
    // Each entry is an inspected release binary, not a version-number wildcard.
    public static bool IsSupportedNavigationHash(string hash) => hash is NavHash or Nav12315Hash;
    private static readonly HashSet<string> NavigationTypes = new(StringComparer.Ordinal)
    {
        "Navmesh.IPCProvider", "Navmesh.NavmeshManager", "Navmesh.NavmeshQuery",
        "Navmesh.AsyncMoveRequest", "Navmesh.Movement.FollowPath",
    };

    // Return type first for Func, followed by arguments, matching RegisterFunc<TRet,T1,...>.
    internal static readonly IReadOnlyDictionary<string, string[]> NavigationIpcContracts = new Dictionary<string, string[]>
    {
        ["Nav.IsReady"] = ["System.Boolean"],
        ["Nav.BuildProgress"] = ["System.Single"],
        ["Nav.Reload"] = ["System.Boolean"],
        ["Nav.IsAutoLoad"] = ["System.Boolean"],
        ["Nav.PathfindCancelable"] = ["System.Threading.Tasks.Task`1<System.Collections.Generic.List`1<System.Numerics.Vector3>>", "System.Numerics.Vector3", "System.Numerics.Vector3", "System.Boolean", "System.Threading.CancellationToken"],
        ["Nav.PathfindInProgress"] = ["System.Boolean"],
        ["Query.Mesh.NearestPoint"] = ["System.Nullable`1<System.Numerics.Vector3>", "System.Numerics.Vector3", "System.Single", "System.Single"],
        ["Path.MoveTo"] = ["System.Void", "System.Collections.Generic.List`1<System.Numerics.Vector3>", "System.Boolean"],
        ["Path.Stop"] = ["System.Void"],
        ["Path.IsRunning"] = ["System.Boolean"],
        ["Path.ListWaypoints"] = ["System.Collections.Generic.List`1<System.Numerics.Vector3>"],
        ["Path.GetTolerance"] = ["System.Single"],
        ["SimpleMove.PathfindAndMoveTo"] = ["System.Boolean", "System.Numerics.Vector3", "System.Boolean"],
        ["SimpleMove.PathfindAndMoveCloseTo"] = ["System.Boolean", "System.Numerics.Vector3", "System.Boolean", "System.Single"],
        ["SimpleMove.PathfindInProgress"] = ["System.Boolean"],
    };

    internal static int ValidateNavigationContract(Mono.Cecil.ModuleDefinition module)
    {
        var provider = module.GetType("Navmesh.IPCProvider") ?? throw new NotSupportedException("vnavmeshのIPC登録型が見つかりません");
        var constructor = provider.Methods.SingleOrDefault(m => m.IsConstructor && !m.IsStatic && m.HasBody)
            ?? throw new NotSupportedException("vnavmeshのIPC登録処理が一致しません");
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? name = null;
        foreach (var instruction in constructor.Body.Instructions)
        {
            if (instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr) name = instruction.Operand as string;
            if (instruction.Operand is not MethodReference method || method.DeclaringType.FullName != provider.FullName
                || method.Name is not ("RegisterFunc" or "RegisterAction")) continue;
            if (name != null && NavigationIpcContracts.ContainsKey(name))
            {
                var arguments = (method as GenericInstanceMethod)?.GenericArguments.Select(t => t.FullName).ToArray() ?? [];
                var signature = method.Name == "RegisterAction" ? new[] { "System.Void" }.Concat(arguments).ToArray() : arguments;
                if (!found.TryAdd(name, signature)) throw new NotSupportedException("vnavmeshのIPC登録が重複しています: " + name);
            }
            name = null;
        }
        foreach (var (key, signature) in NavigationIpcContracts)
            if (!found.TryGetValue(key, out var actual) || !actual.SequenceEqual(signature))
                throw new NotSupportedException("vnavmeshのIPC契約が一致しません: " + key);
        return found.Count;
    }

    private static IEnumerable<Mono.Cecil.TypeDefinition> Descendants(Mono.Cecil.TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Descendants)) yield return nested;
    }
    public static void ValidateFileAndLoadedModule(Assembly assembly, string dllPath, string expectedHash,
        PluginValidationReport report)
    {
        var navigation = expectedHash is NavHash or Nav12315Hash;
        var gathering = expectedHash is GbrHash or Gbr7583Hash;
        report.Validated = false; report.CheckedMethods = report.CheckedContracts = 0; report.DiskMvid = null; report.ActualHash = null;
        report.DllPath = dllPath; report.ExpectedHash = navigation ? NavHash + " / " + Nav12315Hash
            : gathering ? GbrHash + " / " + Gbr7583Hash : expectedHash;
        report.AssemblyName = assembly.FullName; report.AssemblyLocation = assembly.Location;
        report.LoadedMvid = assembly.ManifestModule.ModuleVersionId;
        report.Stage = "パス取得";
        if (string.IsNullOrWhiteSpace(dllPath) || !Path.IsPathFullyQualified(dllPath))
            throw new InvalidOperationException($"{report.InternalName}の元DLLパスを取得できません。読み込み元を確認してください");
        report.Stage = "ファイル読込み";
        // Hash, metadata and IL all read the same disk generation.
        var snapshot = File.ReadAllBytes(dllPath);
        report.Stage = "ハッシュ";
        report.ActualHash = Convert.ToHexString(SHA256.HashData(snapshot));
        if (navigation ? !IsSupportedNavigationHash(report.ActualHash)
            : gathering ? !IsSupportedGbrHash(report.ActualHash) : report.ActualHash != expectedHash)
            throw new NotSupportedException($"{report.InternalName}は未検証のDLLです。対応版を確認してください");
        using var stream = new MemoryStream(snapshot, writable: false);
        using var file = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream);
        report.Stage = "MVID";
        report.DiskMvid = file.MainModule.Mvid;
        if (report.DiskMvid != report.LoadedMvid) throw new NotSupportedException($"{report.InternalName}のディスクとロード中DLLが一致しません。再読み込みしてください");
        report.Stage = "IL/契約";
        if (navigation) report.CheckedContracts = ValidateNavigationContract(file.MainModule);
        // A replaced file with the same display version is not sufficient. Compare loaded method bodies to file bytes.
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(snapshot, writable: false));
        var selected = navigation
            ? file.MainModule.Types.Where(t => NavigationTypes.Contains(t.FullName)).SelectMany(Descendants)
            : file.MainModule.Types.Where(t => t.FullName is "GatherBuddy.AutoGather.AutoGather"
                or "GatherBuddy.Vulcan.Vendors.VendorNavigator" || t.Name == "AdvancedUnstuck");
        foreach (var token in selected.SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var live = assembly.ManifestModule.ResolveMethod(token.MetadataToken.ToInt32());
            var bytes = live?.GetMethodBody()?.GetILAsByteArray();
            var disk = pe.GetMethodBody((int)token.RVA).GetILBytes();
            if (bytes == null || disk == null || !bytes.SequenceEqual(disk))
                throw new NotSupportedException($"{report.InternalName}のロード中のコード形状が異なります: {token.FullName}");
            report.CheckedMethods++;
        }
        if (navigation && report.CheckedMethods == 0) throw new NotSupportedException("vnavmeshのロード中コードを検証できません");
        report.Validated = true;
    }
    public static Type ValidateAutoGather(Assembly a)
    {
        var type = a.GetType("GatherBuddy.AutoGather.AutoGather", true)!;
        _ = new NavStateAccessor(type);
        foreach (var name in new[] { "DoNodeMovement", "Navigate", "HandlePathfinding", "StopPathfinding", "StopNavigation", "GetCorrectedDestination" })
            if (type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Count(m => m.Name == name) != 1)
                throw new NotSupportedException("未対応のメソッド署名: " + name);
        return type;
    }
}
