using System;
using System.Collections;
using System.Reflection;

namespace GBRHelper.Ipc;

/// <summary>
/// GatherBuddyReborn の設定（自動採集の設定 AutoGatherConfig）をリフレクションで読み書きする窓口。
///
/// GBR の IPC には設定を変える口が無い（公開しているのは Version・Identify・IsAutoGatherEnabled・
/// GetAutoGatherStatusText・SetAutoGatherEnabled・IsAutoGatherWaiting の6つと、イベント2つ。
/// GatherBuddy/Plugin/GatherBuddyIpc.cs）。そのため GBR の本体へ直接届く。
///
/// 経路（すべて実ソースで確認）:
///   Dalamud.Service&lt;PluginManager&gt;.Get().InstalledPlugins（Dalamud Plugin/Internal/PluginManager.cs:186）
///     → InternalName が "GatherBuddyReborn" で読み込み済みのもの
///     → private フィールド instance（Plugin/Internal/Types/LocalPlugin.cs:47。開発版は基底型へ辿る）
///     → GBR の型 GatherBuddy.GatherBuddy の static Config（GatherBuddy.cs:50）
///     → Configuration.AutoGatherConfig（Config/Configuration.cs:72）→ 各プロパティ
///   保存は Configuration.Save()（:176）。
///
/// 【GBR が読み直されたとき】型も参照も無効になる。使うたびに instance が同じかを確かめ、違えば取り直す。
/// 型は必ず GBR が読み込んだアセンブリから取る（自分の文脈で解決すると別物になる）。
/// フレームワークのスレッドから呼ぶこと。
/// </summary>
public sealed class GbrConfigAccess
{
    public const string InternalName = "GatherBuddyReborn";

    private const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;

    private object? entry;
    private FieldInfo? instanceField;
    private object? plugin;
    private DateTime nextScan = DateTime.MinValue;

    /// <summary>直近の失敗。無ければ空。画面に出す。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>自動採集の設定の真偽値を読む。読めなければ null。</summary>
    public bool? GetBool(string property)
    {
        try
        {
            if (this.AutoGatherConfig(out _) is not { } agc)
                return null;

            var value = agc.GetType().GetProperty(property, PubInst)?.GetValue(agc);

            if (value is not bool b)
            {
                this.LastError = $"GBR の設定 {property} が見つかりません（GBR の版が変わった可能性）";
                return null;
            }

            this.LastError = string.Empty;
            return b;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の設定 {property} を読めません: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 自動採集の設定の真偽値を書き、GBR の設定ファイルへ保存する。
    /// true は「書いたあと読み直して、その値になっていた」こと。
    /// </summary>
    public bool SetBool(string property, bool value)
    {
        try
        {
            if (this.AutoGatherConfig(out var config) is not { } agc || config is null)
                return false;

            var prop = agc.GetType().GetProperty(property, PubInst);

            if (prop is null || prop.PropertyType != typeof(bool) || !prop.CanWrite)
            {
                this.LastError = $"GBR の設定 {property} を書けません（GBR の版が変わった可能性）";
                return false;
            }

            prop.SetValue(agc, value);
            config.GetType().GetMethod("Save", PubInst, null, Type.EmptyTypes, null)?.Invoke(config, null);

            // 書けたかは読み直して確かめる（「呼べた」と「変わった」は別物）。
            return this.GetBool(property) == value;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の設定 {property} を書けません: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>GBR の Configuration と AutoGatherConfig。届かなければ null。</summary>
    private object? AutoGatherConfig(out object? config)
    {
        config = null;

        if (this.Plugin() is not { } p)
            return null;

        config = p.GetType().GetProperty("Config", PubStatic)?.GetValue(null);

        if (config is null)
        {
            this.LastError = "GBR の設定（GatherBuddy.Config）に届きません";
            return null;
        }

        var agc = config.GetType().GetProperty("AutoGatherConfig", PubInst)?.GetValue(config);

        if (agc is null)
            this.LastError = "GBR の自動採集の設定（AutoGatherConfig）に届きません";

        return agc;
    }

    /// <summary>
    /// GBR の本体（GatherBuddy.GatherBuddy のインスタンス）。未導入・未読み込みなら null。
    /// 他のリフレクション層（GbrAutoGatherListAccess・GbrTimedAccess 等）からもこの経路を共用する。
    /// </summary>
    internal object? Plugin()
    {
        // 握っているものがまだ生きていればそれを使う。
        if (this.plugin is not null && this.entry is not null && this.instanceField is not null
            && ReferenceEquals(this.instanceField.GetValue(this.entry), this.plugin))
        {
            return this.plugin;
        }

        this.plugin = null;
        this.entry = null;
        this.instanceField = null;

        // 探し直しは間隔を空ける（見つからないときに毎回走らせない）。
        if (DateTime.UtcNow < this.nextScan)
            return null;

        this.nextScan = DateTime.UtcNow.AddSeconds(5);

        try
        {
            var dalamud = Svc.PluginInterface.GetType().Assembly;
            var service = dalamud.GetType("Dalamud.Service`1", throwOnError: true)!;
            var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true)!;
            var pm = service.MakeGenericType(pmType).GetMethod("Get", PubStatic)!.Invoke(null, null)!;
            var installed = (IEnumerable)pmType.GetProperty("InstalledPlugins", PubInst)!.GetValue(pm)!;

            foreach (var e in installed)
            {
                var et = e.GetType();

                if (et.GetProperty("InternalName")?.GetValue(e) as string != InternalName)
                    continue;

                if (et.GetProperty("IsLoaded")?.GetValue(e) is not true)
                    continue;

                FieldInfo? field = null;

                for (var t = et; t != null && field == null; t = t.BaseType)
                    field = t.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);

                if (field?.GetValue(e) is not { } instance)
                    continue;

                this.entry = e;
                this.instanceField = field;
                this.plugin = instance;
                return instance;
            }

            this.LastError = "GatherBuddyReborn が読み込まれていません";
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の本体に届きません: {ex.GetType().Name}: {ex.Message}";
        }

        return null;
    }
}
