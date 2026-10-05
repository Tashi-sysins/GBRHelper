namespace GBRHelper.Ipc;

/// <summary>
/// AutoRetainer への窓口。ベンチャーが回収できるか、回収が終わったかを知るのに使う。
///
/// 【IPC 名の前置詞について。ここを間違えると全滅する】
///
/// AutoRetainer 側の登録はこうなっている（AutoRetainer.dll を逆コンパイルして確認）:
///
///     EzIPC.Init(this, Svc.PluginInterface.InternalName + ".PluginState");
///     [EzIPC(null, true, ...)] public bool IsBusy()
///
/// EzIPC は「前置詞 + "." + メソッド名」で登録する。属性の第1引数が null なので
/// メソッド名が使われる。つまり実際に登録されている名前は:
///
///     AutoRetainer.PluginState.IsBusy
///
/// ECommons.IPC の AutoRetainerIPC.cs には [EzIPC("PluginState.IsBusy")] と
/// 書いてあるが、これは「属性に書く値」であって完全な名前ではない。
/// ECommons が購読するときに InternalName（"AutoRetainer"）を頭に足すので、
/// 結果は同じ AutoRetainer.PluginState.IsBusy になる。
///
/// こちらは ECommons を使わず素の GetIpcSubscriber を呼ぶため、
/// 前置詞まで含めた完全な名前を自分で書く必要がある。
/// これを落とすと、全ての IPC が IpcNotReadyError になる。
/// </summary>
public sealed class AutoRetainerIpc : IpcGate
{
    public override string InternalName => "AutoRetainer";

    /// <summary>IPC 名の前置詞。AutoRetainer 側が InternalName + ".PluginState" で登録している。</summary>
    private const string Prefix = "AutoRetainer.PluginState.";

    /// <summary>
    /// AutoRetainer が何か処理をしている最中か。
    /// ベンチャーの受け取り・再送も「処理中」に含まれる。
    /// </summary>
    public bool TryIsBusy(out bool busy)
        => this.TryInvoke(Prefix + "IsBusy",
            () => this.Func<bool>(Prefix + "IsBusy").InvokeFunc(), out busy);

    /// <summary>
    /// このキャラクターに、いま回収できるベンチャーを持つリテイナーが居るか。
    ///
    /// AutoRetainer 内部では Utils.AnyRetainersAvailableCurrentChara() を呼んでおり、
    /// 有効にしているリテイナーのうち残り時間が UnsyncCompensation（既定 -5 秒）
    /// 以下のものがあるかを見ている。キャラクターの特定も AutoRetainer 側が行う。
    /// </summary>
    public bool TryAnyRetainersAvailable(out bool available)
        => this.TryInvoke(Prefix + "AreAnyRetainersAvailableForCurrentChara",
            () => this.Func<bool>(Prefix + "AreAnyRetainersAvailableForCurrentChara").InvokeFunc(),
            out available);

    /// <summary>ベンチャーを回収できるかの判定結果。</summary>
    public enum VentureState
    {
        /// <summary>まだ判断できない。時間を置いてもう一度聞くべき状態。</summary>
        Unknown,

        /// <summary>回収できるベンチャーがある。</summary>
        Collectable,

        /// <summary>回収できるベンチャーは無い。</summary>
        None,
    }

    /// <summary>
    /// 回収できるベンチャーがあるか。
    ///
    /// AreAnyRetainersAvailableForCurrentChara に聞く。
    /// AutoDuty も呼び鈴の前でこれを使って「AutoRetainer に仕事があるか」を見ている。
    ///
    /// この IPC を選んだ理由（GetClosestRetainerVentureSecondsRemaining をやめた理由）:
    ///
    ///   ・戻り値が素の bool。long? のような Nullable を跨がないので、
    ///     値の受け渡しで失敗する余地が無い。
    ///     残り秒数を聞く方は実機で「取得に失敗しました」になった。
    ///
    ///   ・キャラクターの判定を AutoRetainer 側が自分で行う。
    ///     こちらが ContentId を読める状態かどうかに左右されない。
    ///     （エリア移動中は ContentId が 0 になるため、自分で渡す方式は脆い）
    ///
    ///   ・「回収できる」の線引きが AutoRetainer 本体と完全に同じになる
    ///     （内部では UnsyncCompensation を使った判定。既定 -5 秒）。
    ///
    /// 「無い」と「まだ分からない」は区別して返す。
    /// ここを一緒にすると、読めなかった一瞬のせいで
    /// 実際は回収できるのに「ベンチャーがありません」と誤判定する。
    /// </summary>
    public VentureState CheckCollectableVenture()
    {
        if (!this.IsLoaded)
            return VentureState.Unknown;

        if (!this.TryAnyRetainersAvailable(out var available))
            return VentureState.Unknown;

        return available ? VentureState.Collectable : VentureState.None;
    }

    /// <summary>
    /// 処理を中断させる。
    /// こちらから呼び鈴を閉じる前に呼び、AutoRetainer が動いたままにならないようにする。
    /// </summary>
    public bool TryAbort()
        => this.TryAction(Prefix + "AbortAllTasks",
            () => this.Func<object>(Prefix + "AbortAllTasks").InvokeAction());
}
