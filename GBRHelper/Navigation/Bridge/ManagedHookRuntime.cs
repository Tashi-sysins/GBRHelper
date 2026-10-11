using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace GBRHelper.Navigation;

/// <summary>
/// Dalamud's memory loader shadow-copies native libraries, including the absolute clrjit path
/// opened by MonoMod.Core. That uninitialized copy returns a null JIT object. Keep only the
/// packaged detour dependencies in the default BCL context, whose native resolution uses the real
/// JIT. MonoMod's generated assemblies also resolve their support types from that context.
/// </summary>
public static class ManagedHookRuntime
{
    // These are the Release assets selected by packages.lock.json, not GBR/vnavmesh assemblies.
    private static readonly (string Name, string Hash)[] Assets =
    [
        ("Mono.Cecil", "831DCA77470D85CB6FFBEA3072DAA7A3DF5B7C9FCFD9C3F43674A9BE99D4BFCF"),
        ("Mono.Cecil.Mdb", "28CB367972BDC1CD43E4006306AF2FD96D37F4ED4B239EE90E1DC7237A93AF7F"),
        ("Mono.Cecil.Pdb", "A332332633FBCB20E8D50E49B4DB7BD1557721417122CF0C5F4C42F2332391D0"),
        ("Mono.Cecil.Rocks", "BF992F3DCE364EBCC3200FA7832EF07E20B4E2DBC3A8A6213CE44E3D239DB984"),
        ("MonoMod.ILHelpers", "FDD0E3538340FD78B8F521E62B8CAC1EBB7683AC1F27F6AAEDFC1044B14BF4BB"),
        ("MonoMod.Backports", "AC3F32BFD44AAB83ABF71ABDFF6DDE548D57B7C0F8A1FE6D8964E348B4EEAFB1"),
        ("MonoMod.Utils", "987ADECB948EE6D6D0D82BA588F8BA41E7C3D38A0EFCC46A5A14A4343333EF29"),
        ("MonoMod.Iced", "6F6355BCF21159073C777F36B51F3EDAE7177502B530B8EBA97DAF32F00C239E"),
        ("MonoMod.Core", "E8825F6974D496F4E158D2B02D79E49B04261705B806829C604B1DD867C55D15"),
        ("MonoMod.RuntimeDetour", "FB1029F51A93CC5FCD5A1E1AC7E87D8FB6C03DBD893AAEE684F7DF69E834C155"),
    ];
    private static ConstructorInfo? hookConstructor;
    public static string Description { get; private set; } = "未初期化";

    public static void Initialize(string dependencyDirectory)
    {
        if (string.IsNullOrWhiteSpace(dependencyDirectory) || !Path.IsPathFullyQualified(dependencyDirectory))
            throw new ArgumentException("接続ライブラリの配置先には絶対パスが必要です", nameof(dependencyDirectory));
        // Validate all bytes before loading process-lifetime dependencies. Do not silently fall back
        // to another plugin's library or reuse stale dependencies after a broken deployment.
        var snapshots = new List<byte[]>();
        foreach (var asset in Assets)
        {
            var data = File.ReadAllBytes(Path.Combine(dependencyDirectory, asset.Name + ".dll"));
            if (Convert.ToHexString(SHA256.HashData(data)) != asset.Hash)
                throw new InvalidOperationException($"接続ライブラリ {asset.Name} の内容が対応版と一致しません。GBRHelper一式の配置を確認してください");
            snapshots.Add(data);
        }
        // MonoMod's JIT hook is process-wide. Default survives plugin reloads and avoids both
        // Dalamud's native shadow copies and generated non-collectible assemblies resolving
        // support types to another plugin's collectible MonoMod.Utils. No resolver is replaced.
        lock (string.Intern("GBRHelper.ManagedHooks.Default.v1"))
        {
            var context = AssemblyLoadContext.Default;
            // Refuse incompatible resident versions before loading any missing assets.
            for (var i = 0; i < Assets.Length; i++)
            {
                var loaded = context.Assemblies.SingleOrDefault(a => a.GetName().Name == Assets[i].Name);
                if (loaded != null)
                {
                    using var stream = new MemoryStream(snapshots[i], writable: false);
                    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                    var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                    var expectedMvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
                    if (loaded.ManifestModule.ModuleVersionId != expectedMvid)
                        throw new InvalidOperationException($"異なる版の {Assets[i].Name} が既に読み込まれています。ゲームの再起動後に確認してください");
                }
            }
            for (var i = 0; i < Assets.Length; i++)
            {
                if (context.Assemblies.Any(a => a.GetName().Name == Assets[i].Name)) continue;
                using var stream = new MemoryStream(snapshots[i], writable: false);
                context.LoadFromStream(stream);
            }
            var assembly = context.Assemblies.Single(a => a.GetName().Name == "MonoMod.RuntimeDetour");
            hookConstructor = assembly.GetType("MonoMod.RuntimeDetour.Hook", throwOnError: true)!
                .GetConstructor([typeof(MethodBase), typeof(Delegate)])
                ?? throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate)");
            Description = $"{context.Name}; RuntimeDetour={assembly.GetName().Version}; MVID={assembly.ManifestModule.ModuleVersionId}";
        }
    }

    public static IDisposable CreateHook(MethodInfo method, Delegate hook)
    {
        var constructor = hookConstructor ?? throw new InvalidOperationException("接続ライブラリが初期化されていません");
        var context = AssemblyLoadContext.GetLoadContext(constructor.DeclaringType!.Assembly)!;
        // Keep dynamic support assemblies in the same context as the validated libraries.
        using var scope = context.EnterContextualReflection();
        try { return new OwnedHook((IDisposable)constructor.Invoke([method, hook]), context); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed class OwnedHook(IDisposable hook, AssemblyLoadContext context) : IDisposable
    {
        public void Dispose()
        {
            using var scope = context.EnterContextualReflection();
            hook.Dispose();
        }
    }
}
