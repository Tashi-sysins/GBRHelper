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
    public bool Validated { get; set; }
}

public static class GbrCompatibilityProfile
{
    public const string Contract = "GBRH-navigation/1";
    public const string GbrHash = "FFBCDEBC635580749CE61A70155FF53893C8BB511679D7288A245170128444E1";
    public const string NavHash = "889EBFF40D92DB2DF4B4C190F1D81B3971E538908E459F6297F53FEA017E3723";
    public static void ValidateFileAndLoadedModule(Assembly assembly, string dllPath, string expectedHash,
        PluginValidationReport report)
    {
        report.Validated = false; report.CheckedMethods = 0; report.DiskMvid = null; report.ActualHash = null;
        report.DllPath = dllPath; report.ExpectedHash = expectedHash;
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
        if (report.ActualHash != expectedHash) throw new NotSupportedException($"{report.InternalName}は未検証のDLLです。対応版を確認してください");
        using var stream = new MemoryStream(snapshot, writable: false);
        using var file = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream);
        report.Stage = "MVID";
        report.DiskMvid = file.MainModule.Mvid;
        if (report.DiskMvid != report.LoadedMvid) throw new NotSupportedException($"{report.InternalName}のディスクとロード中DLLが一致しません。再読み込みしてください");
        report.Stage = "IL/契約";
        // A replaced file with the same display version is not sufficient. Compare loaded method bodies to file bytes.
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(snapshot, writable: false));
        foreach (var token in file.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody
                     && (m.DeclaringType.FullName == "GatherBuddy.AutoGather.AutoGather" || m.DeclaringType.Name == "AdvancedUnstuck")))
        {
            var live = assembly.ManifestModule.ResolveMethod(token.MetadataToken.ToInt32());
            var bytes = live?.GetMethodBody()?.GetILAsByteArray();
            var disk = pe.GetMethodBody((int)token.RVA).GetILBytes();
            if (bytes == null || disk == null || !bytes.SequenceEqual(disk))
                throw new NotSupportedException($"{report.InternalName}のロード中のコード形状が異なります: {token.FullName}");
            report.CheckedMethods++;
        }
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
