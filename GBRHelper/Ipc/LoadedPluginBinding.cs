using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace GBRHelper.Ipc;

/// <summary>The instance and source file must come from the same loaded LocalPlugin entry.</summary>
public sealed class LoadedPluginBinding
{
    private readonly object entry;
    private readonly FieldInfo instanceField;
    public string InternalName { get; }
    public object Instance { get; }
    public Assembly Assembly { get; }
    public string DllPath { get; }
    private LoadedPluginBinding(object entry, FieldInfo field, object instance, string name, string path)
    {
        this.entry = entry; instanceField = field; Instance = instance;
        InternalName = name; Assembly = instance.GetType().Assembly; DllPath = path;
    }
    public static LoadedPluginBinding Resolve(IEnumerable entries, string internalName, Action<string>? stage = null)
    {
        stage?.Invoke("プラグイン取得");
        object? selected = null;
        foreach (var candidate in entries)
        {
            var type = candidate.GetType();
            if (type.GetProperty("InternalName")?.GetValue(candidate) as string != internalName
                || type.GetProperty("IsLoaded")?.GetValue(candidate) is not true) continue;
            if (selected != null)
                throw new InvalidOperationException($"{internalName}が複数読み込まれています。読み込み元を確認してください");
            selected = candidate;
        }
        if (selected == null) throw new InvalidOperationException($"{internalName}の読み込みを待っています");
        FieldInfo? field = null;
        for (var t = selected.GetType(); t != null && field == null; t = t.BaseType)
            field = t.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
        var instance = field?.GetValue(selected) ?? throw new InvalidOperationException($"{internalName}の実体を取得できません");
        stage?.Invoke("パス取得");
        var path = (selected.GetType().GetProperty("DllFile")?.GetValue(selected) as FileInfo)?.FullName;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{internalName}の元DLLパスを取得できません（DllFile）");
        var binding = new LoadedPluginBinding(selected, field!, instance, internalName, path);
        binding.EnsureCurrent();
        return binding;
    }
    public bool IsCurrent
    {
        get
        {
            try
            {
                var type = entry.GetType();
                return type.GetProperty("IsLoaded")?.GetValue(entry) is true
                    && type.GetProperty("InternalName")?.GetValue(entry) as string == InternalName
                    && ReferenceEquals(instanceField.GetValue(entry), Instance)
                    && ReferenceEquals(type.GetProperty("Assembly")?.GetValue(entry), Assembly)
                    && string.Equals((type.GetProperty("DllFile")?.GetValue(entry) as FileInfo)?.FullName,
                        DllPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
    public void EnsureCurrent()
    {
        if (!IsCurrent) throw new InvalidOperationException($"{InternalName}が再読み込みされました。接続をやり直します");
    }
    public static T Connect<T>(LoadedPluginBinding first, LoadedPluginBinding second, Func<T> create) where T : IDisposable
    {
        first.EnsureCurrent(); second.EnsureCurrent();
        var resource = create();
        try { first.EnsureCurrent(); second.EnsureCurrent(); return resource; }
        catch (Exception failure)
        {
            try { resource.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("接続が失効し、解除処理も失敗しました", failure, cleanup); }
            throw;
        }
    }
}
