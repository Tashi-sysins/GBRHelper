namespace GBRHelper.Ipc;

/// <summary>
/// Lifestream への窓口。帰還先の宿屋へ入るのに使う。
///
/// IPC 名は Lifestream の実ソースで確認済み
/// （Lifestream/IPC/IPCProvider.cs。EzIPC.Init(this) で前置詞は InternalName の "Lifestream"）。
///
///   [EzIPC] public void EnqueueLocalInnShortcut(int? innIndex)   … :327-331
///   [EzIPC] public bool IsBusy()                                 … :64-68
///   [EzIPC] public void Abort()                                  … :70-75
///
/// 【宿屋の機能で Lifestream がすること】（Tasks/Shortcuts/TaskPropertyShortcut.cs:242-343）
///   その都市の大エーテライトへテレポート → エーテライトに近づく → 都市内転送 →
///   決まった経路を歩く → 受付の NPC に話しかける → 宿屋に入る、までを作業列に積む。
///   経路の座標と受付の NPC は Lifestream の中に書かれている（:25-37）。
///
/// 【ホームワールドへ戻る版（EnqueueInnShortcut）を使わない理由】
///   あちらは他のワールドに居るとホームワールドへ移動してから宿屋へ向かう。
///   ワールドの判定はこちらで先に済ませ（RetainerAccess）、他のワールドでは向かわないので、
///   思いがけないワールド移動が起きない「今いるワールド」の版を使う。
///
/// 【IsBusy の性質】
///   宿屋の機能は作業列（TaskManager）に積まれるので、受け付けた直後から IsBusy が true になる。
///   void の IPC は「受け付けたか」を返さないので、IsBusy で確かめる。
///   （以前使っていた Teleport は作業列に積まれず、IsBusy が立たなかった。性質が違う）
/// </summary>
public sealed class LifestreamIpc : IpcGate
{
    public override string InternalName => "Lifestream";

    /// <summary>
    /// 今いるワールドの宿屋へ向かう作業を積んでもらう。
    ///
    /// true は「呼び出しで例外が出なかった」ことだけを意味する。
    /// 受け付けられたかは、直後に <see cref="TryIsBusy"/> で確かめること。
    /// </summary>
    /// <param name="innIndex">Lifestream の宿屋表の番号（InnDestination.LifestreamIndex）。null なら Lifestream に選ばせる。</param>
    public bool TryEnqueueLocalInn(int? innIndex)
        => this.TryAction("EnqueueLocalInnShortcut",
            () => this.Func<int?, object>("Lifestream.EnqueueLocalInnShortcut").InvokeAction(innIndex));

    /// <summary>Lifestream が何か処理中か。移動系の競合を避けるために見る。</summary>
    public bool TryIsBusy(out bool busy)
        => this.TryInvoke("IsBusy", () => this.Func<bool>("Lifestream.IsBusy").InvokeFunc(), out busy);

    /// <summary>Lifestream の処理を中断する。自分が頼んだ移動のときだけ呼ぶこと。</summary>
    public bool TryAbort()
        => this.TryAction("Abort", () => this.Func<object>("Lifestream.Abort").InvokeAction());
}
