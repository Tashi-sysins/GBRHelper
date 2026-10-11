using System;
using System.Text.Json;

namespace GBRHelper.Navigation;

/// <summary>Available even if compatibility checks or trace storage fail.</summary>
public sealed class ConnectionDiagnostics
{
    private string lastFailureKey = "";
    public string Stage { get; set; } = "未接続";
    public PluginValidationReport Gbr { get; private set; } = new() { InternalName = "GatherBuddyReborn" };
    public PluginValidationReport Navigation { get; private set; } = new() { InternalName = "vnavmesh" };
    public string? FailedMethod { get; private set; }
    public string? HookRuntime { get; set; }
    public int RegisteredHooks { get; set; }
    public int? ActiveHooks { get; set; } = 0;
    public string Summary { get; private set; } = "";
    public int RepeatedFailures { get; private set; }
    public string? Exception { get; private set; }
    public string? CleanupError { get; set; }
    public string? StorageError { get; set; }
    public DateTimeOffset Updated { get; private set; }
    public void Begin()
    {
        Stage = "プラグイン取得"; RegisteredHooks = 0; ActiveHooks = 0; FailedMethod = null; Exception = null; CleanupError = null; Summary = "";
        HookRuntime = null;
        Gbr = new() { InternalName = "GatherBuddyReborn" };
        Navigation = new() { InternalName = "vnavmesh" };
        Updated = DateTimeOffset.UtcNow;
    }
    public bool Fail(Exception error)
    {
        Exception = error.ToString(); Updated = DateTimeOffset.UtcNow;
        Summary = error switch
        {
            System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException => "対象DLLが見つかりません。プラグインの読み込み元を確認してください",
            UnauthorizedAccessException or System.IO.IOException => "対象DLLを読めません。更新中でないか、アクセスできるか確認してください",
            InvalidOperationException or NotSupportedException => error.Message,
            _ => "接続処理に失敗しました。診断をコピーして詳細を確認してください",
        };
        for (var current = error; current != null; current = current.InnerException)
            if (current is HookRegistrationException hook)
            { FailedMethod = hook.MethodName; RegisteredHooks = hook.RegisteredBeforeFailure; Summary = "移動処理への接続に失敗しました。診断で対象メソッドを確認してください"; }
        var key = Stage + "|" + Gbr.DllPath + "|" + Gbr.LoadedMvid + "|" + Navigation.DllPath
            + "|" + Navigation.LoadedMvid + "|" + Exception;
        if (key == lastFailureKey) { RepeatedFailures++; return false; }
        lastFailureKey = key; RepeatedFailures = 1; return true;
    }
    public void Connected(int hooks)
    {
        Stage = "接続済み"; RegisteredHooks = hooks; ActiveHooks = hooks; Exception = null; RepeatedFailures = 0; Summary = "";
        lastFailureKey = ""; Updated = DateTimeOffset.UtcNow;
    }
    public string ToText() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
