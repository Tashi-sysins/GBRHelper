using System;

namespace GBRHelper.Features;

/// <summary>
/// 宿屋へ向かう段の判断。ゲームにも Lifestream にも触らない（状態を受け取って、次にすることを返すだけ）。
/// 実際に頼む・止めるのは RelayController が行う。判断をここへ切り出したのは、ゲーム無しで試せるようにするため。
///
/// 【Lifestream の宿屋機能の性質】（Lifestream/Tasks/Shortcuts/TaskPropertyShortcut.cs を読んで確認）
///   ・IPC は void。忙しいときは「Lifestream is busy」とチャットに出して何もせずに戻る（:41-45）。
///     → 頼んだ直後に IsBusy が立ったかで、受け付けられたかを確かめる。
///     受け付けると同じフレームの中で作業列に積まれるので、直後に読めば分かる。
///   ・作業列の各段は 30 秒で打ち切られ、全体が中止される
///     （ECommons の TaskManager の既定：TimeLimitMS = 30000・AbortOnTimeout = true）。
///     宿屋が未解放でも、止まり続けずに IsBusy が落ちる。
///   ・最後の段は「暗転が始まったら終わり」（:326-334）。
///     IsBusy が落ちた瞬間はまだエリアが変わっていないことがある。
///     → 「終わったのに宿屋に居ない」を、すぐに失敗にしない（SettleGrace）。
///
/// 【時間の使い方】
///   時間は「諦めるまでの上限」にだけ使う。進むのは必ず状態の変化を見てから
///   （宿屋に居る・IsBusy が落ちた・エリア移動が終わった）。
/// </summary>
public sealed class InnTrip
{
    /// <summary>次にすること。</summary>
    public enum Action
    {
        /// <summary>待つ。何もしない。</summary>
        Wait,

        /// <summary>Lifestream に宿屋への移動を頼む。頼んだら <see cref="OnRequested"/> を呼ぶ。</summary>
        Request,

        /// <summary>宿屋に着いた。呼び鈴へ向かってよい。</summary>
        Arrived,

        /// <summary>諦める。</summary>
        Fail,
    }

    /// <param name="Action">次にすること。</param>
    /// <param name="Detail">画面に出す説明。分岐には使わない。</param>
    /// <param name="AbortLifestream">
    /// 諦めるときに Lifestream の作業を止めるべきか。自分が頼んだ移動が走っている可能性があるときだけ true。
    /// 頼む前（別の誰かの作業）には立てない。
    /// </param>
    public readonly record struct Decision(Action Action, string Detail, bool AbortLifestream = false);

    /// <summary>判断に使う、いまの状態。</summary>
    /// <param name="InInn">
    /// いま行き先の宿屋の部屋に居るか。
    /// 回収（RelayController）ではどの宿屋でもよい（どの部屋にも呼び鈴があるため）。
    /// 検証（InnTestRunner）では指定した宿屋だけを数える（違う宿屋に着いたら失敗として見せるため）。
    /// </param>
    /// <param name="Transitioning">
    /// エリア移動・会話・読み込みの最中か。この間は「終わったのに着いていない」と数えない。
    /// </param>
    /// <param name="LifestreamLoaded">Lifestream が導入済みか。</param>
    /// <param name="LifestreamBusy">Lifestream の IsBusy。読めなければ null（「終わった」とは扱わない）。</param>
    /// <param name="Gathering">採集ノードを開いているか。</param>
    /// <param name="UnsafeReason">いま動くと危ない理由（戦闘中など）。無ければ null。</param>
    /// <param name="DestinationReady">
    /// アクセス済みエーテライトの一覧が読めるか（＝テレポートできる場所に居るか）。
    /// コンテンツの中では一覧が空になり、テレポートもできない。
    /// </param>
    public readonly record struct Input(
        bool InInn,
        bool Transitioning,
        bool LifestreamLoaded,
        bool? LifestreamBusy,
        bool Gathering,
        string? UnsafeReason,
        bool DestinationReady);

    /// <summary>頼んでから宿屋に入るまでの上限。テレポート・都市内転送・徒歩・会話を合わせても通常 1 分ほど。</summary>
    public static readonly TimeSpan TravelLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Lifestream の作業が終わってから、宿屋に着くのを待つ上限。
    /// 最後の段は暗転の開始で終わるため、その直後はまだエリアが変わっていない。
    /// </summary>
    public static readonly TimeSpan SettleGrace = TimeSpan.FromSeconds(10);

    /// <summary>Lifestream の状態が読めないまま待つ上限。</summary>
    public static readonly TimeSpan UnknownLimit = TimeSpan.FromSeconds(30);

    /// <summary>頼む前に、Lifestream が別の作業（利用者や他のプラグインの頼みごと）を終えるのを待つ上限。</summary>
    public static readonly TimeSpan OtherWorkLimit = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 頼む前に、アクセス済みエーテライトの一覧が読めるようになるのを待つ上限。
    ///
    /// 読めない場所（コンテンツの中）では、自動採集を止める前に回収を見送っている
    /// （RelayController.CanStartCollection）。ここへ来るのは止めた直後に読めなくなったときだけなので、
    /// 長くは待たない。上限が無いと、自動採集を止めたまま待ち続ける。
    /// </summary>
    public static readonly TimeSpan DestinationLimit = TimeSpan.FromSeconds(60);

    /// <summary>受け付けられなかったとき、頼み直すまでの間隔。</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>頼む回数の上限。受け付けられないまま頼み続けない。</summary>
    public const int MaxRequests = 3;

    private bool requested;
    private DateTime requestedAt;
    private DateTime nextRequestAt = DateTime.MinValue;
    private DateTime? idleSince;
    private DateTime? unknownSince;
    private DateTime? otherWorkSince;
    private DateTime? destinationWaitSince;
    private int attempts;

    /// <summary>
    /// 自分の頼んだ移動を Lifestream が受け付けたか。
    /// 途中で止めるとき、Lifestream を止めてよいかの判断に使う（頼んでいなければ触らない）。
    /// </summary>
    public bool Requested => this.requested;

    /// <summary>
    /// 頼んだあと、「Lifestream は終わった・移動の印も無い・まだ宿屋ではない」が続いた最長の時間（暗転の隙間）。
    /// SettleGrace（10秒）が足りているかを実機で確かめるために記録へ出す。
    /// </summary>
    public TimeSpan LongestSettleWait { get; private set; }

    /// <summary>新しい回を始める。</summary>
    public void Reset()
    {
        this.LongestSettleWait = TimeSpan.Zero;
        this.requested = false;
        this.requestedAt = default;
        this.nextRequestAt = DateTime.MinValue;
        this.idleSince = null;
        this.unknownSince = null;
        this.otherWorkSince = null;
        this.destinationWaitSince = null;
        this.attempts = 0;
    }

    /// <summary>
    /// 頼んだ結果を伝える。
    /// </summary>
    /// <param name="accepted">頼んだ直後に IsBusy が立ったか。</param>
    /// <param name="now">いまの時刻（UTC）。</param>
    public void OnRequested(bool accepted, DateTime now)
    {
        this.attempts++;

        if (!accepted)
        {
            this.nextRequestAt = now + RetryInterval;
            return;
        }

        this.requested = true;
        this.requestedAt = now;
        this.idleSince = null;
        this.unknownSince = null;
    }

    /// <summary>毎フレーム呼ぶ。次にすることを返す。</summary>
    public Decision Step(Input input, DateTime now)
        => this.requested ? this.StepAfterRequest(input, now) : this.StepBeforeRequest(input, now);

    private Decision StepBeforeRequest(Input input, DateTime now)
    {
        // 既に宿屋に居る（「いますぐ回収」を宿屋で押した等）。頼まずに呼び鈴へ。
        if (input.InInn && !input.Transitioning)
            return Arrived("宿屋に居ます。呼び鈴へ向かいます");

        if (!input.LifestreamLoaded)
            return Fail("Lifestream が見つかりません");

        // 自動採集を OFF にした時点で、最後のノードがまだ開いていることがある。
        // その最中に動くと、採り終わっていない分を捨てることになる。
        if (input.Gathering)
            return Wait("最後の採集が終わるのを待っています");

        if (input.UnsafeReason is { } reason)
            return Wait($"宿屋へ向かうのを待っています（{reason}）");

        // テレポートできる場所か。読めないまま待ち続けると、自動採集を止めたままになる。
        if (!input.DestinationReady)
        {
            this.destinationWaitSince ??= now;

            return now - this.destinationWaitSince.Value > DestinationLimit
                ? Fail($"エーテライトの一覧を {DestinationLimit.TotalSeconds:F0} 秒読めませんでした"
                       + "（コンテンツの中など、テレポートできない場所の可能性があります）")
                : Wait("エーテライトの一覧を確認しています");
        }

        this.destinationWaitSince = null;

        if (input.LifestreamBusy is null)
        {
            this.unknownSince ??= now;

            return now - this.unknownSince.Value > UnknownLimit
                ? Fail($"Lifestream の状態を {UnknownLimit.TotalSeconds:F0} 秒読めませんでした")
                : Wait("Lifestream の状態を確認しています");
        }

        this.unknownSince = null;

        // 別の作業中に頼むと「忙しい」で断られる。終わるのを待つ。
        // これは自分の頼みごとではないので、諦めるときも止めない。
        if (input.LifestreamBusy == true)
        {
            this.otherWorkSince ??= now;

            return now - this.otherWorkSince.Value > OtherWorkLimit
                ? Fail($"Lifestream が別の作業中のまま {OtherWorkLimit.TotalSeconds:F0} 秒たちました")
                : Wait("Lifestream が別の作業中です。終わるのを待っています");
        }

        this.otherWorkSince = null;

        if (this.attempts >= MaxRequests)
        {
            return Fail(
                $"Lifestream が宿屋への移動を受け付けませんでした（{MaxRequests} 回頼みました）");
        }

        if (now < this.nextRequestAt)
            return Wait("宿屋への移動を頼み直すのを待っています");

        return new Decision(Action.Request, "Lifestream に宿屋への移動を頼みます");
    }

    private Decision StepAfterRequest(Input input, DateTime now)
    {
        if (now - this.requestedAt > TravelLimit)
        {
            return Fail(
                $"宿屋へ {TravelLimit.TotalMinutes:F0} 分以内に入れませんでした",
                abort: true);
        }

        var waited = (now - this.requestedAt).TotalSeconds;

        if (input.InInn && !input.Transitioning)
        {
            // Lifestream の作業が残っているうちは呼び鈴へ向かわない。
            // 会話の送りなどと、こちらの操作が取り合いになる。
            if (input.LifestreamBusy == true)
                return Wait("宿屋に入りました。Lifestream の作業が終わるのを待っています");

            return Arrived("宿屋に入りました");
        }

        if (input.Transitioning || input.LifestreamBusy == true)
        {
            this.idleSince = null;
            this.unknownSince = null;
            return Wait($"Lifestream が宿屋へ向かっています（{waited:F0} 秒）");
        }

        // 読めないのは「終わった」ではない。ここで失敗にすると、読めなかった一瞬で諦めることになる。
        if (input.LifestreamBusy is null)
        {
            this.unknownSince ??= now;

            return now - this.unknownSince.Value > UnknownLimit
                ? Fail($"Lifestream の状態を {UnknownLimit.TotalSeconds:F0} 秒読めませんでした", abort: true)
                : Wait("Lifestream の状態を確認しています");
        }

        this.unknownSince = null;

        // Lifestream は終わったが、まだ宿屋ではない。
        // 暗転が始まった直後はエリア移動の印がまだ立っていないことがあるので、少し見守る。
        this.idleSince ??= now;

        if (now - this.idleSince.Value > this.LongestSettleWait)
            this.LongestSettleWait = now - this.idleSince.Value;

        if (now - this.idleSince.Value <= SettleGrace)
            return Wait("宿屋に入ったか確かめています");

        return Fail(
            "Lifestream の作業が終わりましたが、宿屋に入っていません"
            + "（宿屋が未解放か、Lifestream が途中で中止した可能性があります。"
            + "/xllog の Lifestream の記録を見てください）");
    }

    private static Decision Wait(string detail) => new(Action.Wait, detail);

    private static Decision Arrived(string detail) => new(Action.Arrived, detail);

    private static Decision Fail(string detail, bool abort = false) => new(Action.Fail, detail, abort);
}
