using System;

namespace GBRHelper.Ipc;

/// <summary>
/// GatherBuddyReborn への窓口。自動採集の ON / OFF を読み書きする。
///
/// IPC 名は GBR の実ソースで確認済み（GatherBuddy/Plugin/GatherBuddyIpc.cs）。
/// 登録は EzIPC.Init(this, GatherBuddy.InternalName) で、
/// InternalName は "GatherBuddyReborn"（GatherBuddy.cs:43）。
/// EzIPC は「前置詞 + "." + メソッド名」で登録するため、完全名は次のとおり:
///
///     GatherBuddyReborn.IsAutoGatherEnabled     () -> bool
///     GatherBuddyReborn.SetAutoGatherEnabled    (bool) -> void
///     GatherBuddyReborn.IsAutoGatherWaiting     () -> bool
///     GatherBuddyReborn.GetAutoGatherStatusText () -> string
///     GatherBuddyReborn.Version                 () -> int
///     GatherBuddyReborn.AutoGatherEnabledChanged … イベント（Action&lt;bool&gt;）
///
/// 【void を返す IPC の引数について】
/// GBR 側は RegisterActionProvider で ICallGateProvider&lt;bool, object&gt; として登録している
/// （EzIpc.cs:191-211。引数1つの Action は &lt;T1, object&gt;）。
/// よって購読側も Func&lt;bool, object&gt; で受け、InvokeAction(value) で呼ぶ。
///
/// 【イベントの購読について】
/// AutoGatherEnabledChanged は Action&lt;bool&gt; なので、GBR 側は
/// ICallGateProvider&lt;bool, object&gt; の SendMessage(bool) で飛ばしている
/// （EzIpc.cs:385-396）。購読側は GetIpcSubscriber&lt;bool, object&gt; の Subscribe で受ける。
/// </summary>
public sealed class GatherBuddyIpc : IpcGate, IDisposable
{
    public override string InternalName => "GatherBuddyReborn";

    public override string DisplayName => "GatherBuddyReborn";

    private const string Prefix = "GatherBuddyReborn.";

    /// <summary>
    /// 自動採集の ON / OFF が変わったときに呼ばれる。引数は変更後の値。
    ///
    /// ポーリングではなくイベントで受ける理由:
    /// 「GBR を止めたらこちらも止める」を毎フレームの読み取りで実装すると、
    /// こちらが復帰のために ON へ戻した瞬間を取りこぼしたり、
    /// 逆に自分が OFF にした直後を「利用者が止めた」と誤認したりする。
    /// 変化そのものを受け取れば、いつ誰が変えたかを取り違えずに済む。
    /// </summary>
    public event Action<bool>? EnabledChanged;

    private Dalamud.Plugin.Ipc.ICallGateSubscriber<bool, object>? enabledChangedGate;

    /// <summary>
    /// イベントの購読を試みる。まだ GBR が起動していなければ false。
    ///
    /// 購読は1回だけ成功すればよいので、成功したら以後は呼ばれない
    /// （呼び出し側が購読済みかどうかを見る）。
    /// </summary>
    public bool TrySubscribeEnabledChanged()
    {
        if (this.enabledChangedGate != null)
            return true;

        if (!this.IsLoaded)
            return false;

        try
        {
            var gate = Svc.PluginInterface
                .GetIpcSubscriber<bool, object>(Prefix + "AutoGatherEnabledChanged");

            gate.Subscribe(this.OnEnabledChanged);
            this.enabledChangedGate = gate;

            Svc.Log.Information(
                "[GatherBuddyReborn] 自動採集の切り替え通知を購読しました");

            return true;
        }
        catch (Exception ex)
        {
            this.WarnThrottled(
                $"切り替え通知を購読できませんでした: {ex.Message}", "subscribe");
            return false;
        }
    }

    /// <summary>購読済みか。</summary>
    public bool Subscribed => this.enabledChangedGate != null;

    private void OnEnabledChanged(bool enabled)
    {
        try
        {
            this.EnabledChanged?.Invoke(enabled);
        }
        catch (Exception ex)
        {
            // 購読側で例外を出すと相手（GBR）の処理まで巻き込む。ここで止める。
            Svc.Log.Error($"[GatherBuddyReborn] 通知の処理で例外が出ました: {ex}");
        }
    }

    /// <summary>自動採集が有効か。読めなければ null。</summary>
    public bool? IsAutoGatherEnabled()
        => this.TryInvoke("IsAutoGatherEnabled",
            () => this.Func<bool>(Prefix + "IsAutoGatherEnabled").InvokeFunc(), out var enabled)
            ? enabled
            : null;

    /// <summary>
    /// 自動採集が「待機中」か。次の採集対象が湧くまで待っている状態。
    /// 読めなければ null。
    /// </summary>
    public bool? IsAutoGatherWaiting()
        => this.TryInvoke("IsAutoGatherWaiting",
            () => this.Func<bool>(Prefix + "IsAutoGatherWaiting").InvokeFunc(), out var waiting)
            ? waiting
            : null;

    /// <summary>自動採集の状態を表す文字列。画面に出すためだけに使う（分岐に使わない）。</summary>
    public string StatusText()
        => this.TryInvoke("GetAutoGatherStatusText",
            () => this.Func<string>(Prefix + "GetAutoGatherStatusText").InvokeFunc(), out var text)
            ? text ?? string.Empty
            : string.Empty;

    /// <summary>
    /// 自動採集を ON / OFF する。
    ///
    /// 戻り値の true は「例外が飛ばなかった」ことだけを意味する。
    /// 実際に切り替わったかは IsAutoGatherEnabled() で読み直して確かめること。
    /// </summary>
    public bool SetAutoGatherEnabled(bool enabled)
        => this.TryAction("SetAutoGatherEnabled",
            () => this.Func<bool, object>(Prefix + "SetAutoGatherEnabled").InvokeAction(enabled));

    /// <summary>GBR の IPC 版。互換性の確認に使う。読めなければ null。</summary>
    public int? Version()
        => this.TryInvoke("Version",
            () => this.Func<int>(Prefix + "Version").InvokeFunc(), out var v)
            ? v
            : null;

    public void Dispose()
    {
        if (this.enabledChangedGate == null)
            return;

        try
        {
            this.enabledChangedGate.Unsubscribe(this.OnEnabledChanged);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GatherBuddyReborn] 購読を解除できませんでした: {ex.Message}");
        }

        this.enabledChangedGate = null;
    }
}
